using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace YetAnotherLosslessCutter.Mpv;

public sealed class MpvPlayer : IDisposable
{
    private IntPtr _handle;
    private Thread? _eventThread;
    private volatile bool _running;
    private readonly Dictionary<ulong, string> _observed = new();
    private ulong _nextObserveId = 1;

    // Render API state (used on macOS — wid path leaves these zeroed).
    private IntPtr _renderCtx;
    private bool _useRenderApi;
    // mpv with vo=libmpv refuses to start video output until the render context
    // is created. The GL view doesn't create that context until its first
    // OnOpenGlRender — which can lag behind the user picking a file. Hold the
    // last-requested loadfile path here and replay it once the render context
    // exists. Latest LoadFile call wins; previous queued path is overwritten.
    private string? _pendingLoadFile;
    // Pinned delegates kept alive for the render context's lifetime so the
    // GC doesn't reclaim them while libmpv still holds the function pointers.
    private LibMpv.GetProcAddressFunc? _getProcAddressDelegate;
    private LibMpv.RenderUpdateCallback? _renderUpdateDelegate;
    private Action? _renderUpdateAction;
    private GCHandle _selfHandle;

    public event Action<double>? TimePosChanged;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PauseChanged;
    public event Action? FileLoaded;
    public event Action? EndFile;
    /// <summary>
    /// Fires after a seek when the new frame is decoded and ready to display.
    /// Used by auto-repeat seeking to advance only after the user can actually see
    /// the prior frame, instead of running on a fixed timer that decouples from
    /// decode latency.
    /// </summary>
    public event Action? PlaybackRestart;

    public bool IsInitialized => _handle != IntPtr.Zero;
    public bool HasRenderContext => _renderCtx != IntPtr.Zero;

    /// <summary>
    /// Initialize using the wid-embedding path. Used on Windows + Linux X11 where
    /// mpv embeds itself into a host HWND/XID and renders directly. On macOS the
    /// wid path doesn't work — use the parameterless overload + the render API.
    /// </summary>
    public void Initialize(IntPtr hwnd)
    {
        InitializeCore(widValue: hwnd.ToInt64().ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Initialize for the render-API path (macOS). The caller is responsible for
    /// later calling <see cref="CreateRenderContext"/> on a thread with a current
    /// OpenGL context. Sets <c>vo=libmpv</c> so mpv pushes frames through the
    /// render context instead of trying to embed.
    /// </summary>
    public void Initialize()
    {
        InitializeCore(widValue: null);
    }

    private void InitializeCore(string? widValue)
    {
        if (_handle != IntPtr.Zero) throw new InvalidOperationException("Already initialized");

        _useRenderApi = widValue is null;

        _handle = LibMpv.Create();
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed");

        if (widValue != null)
        {
            // Embed into the supplied HWND/XID. Must be set before mpv_initialize.
            Check(LibMpv.SetOptionString(_handle, "wid", widValue));
            // Modern renderer; falls back to gpu. wid path drives display directly.
            LibMpv.SetOptionString(_handle, "vo", "gpu-next");
        }
        else
        {
            // Render-API path: vo=libmpv tells mpv to push frames through a
            // render context that the host (the GL view) drives.
            LibMpv.SetOptionString(_handle, "vo", "libmpv");
        }

        LibMpv.SetOptionString(_handle, "hwdec", "auto-safe");      // hw decode where safe
        LibMpv.SetOptionString(_handle, "keep-open", "always");     // don't close on EOF

        // macOS arm64 hardened-runtime kills the process with "Code Signature
        // Invalid" when libluajit performs its W→X JIT page transitions. mpv's
        // bundled scripts (stats / console / auto_profiles / select / commands /
        // context_menu / positioning / ytdl_hook) all run through luajit, so
        // suppress their auto-load. We drive the UI ourselves and don't surface
        // any of these features, so nothing user-visible is lost.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            LibMpv.SetOptionString(_handle, "load-osd-console", "no");
            LibMpv.SetOptionString(_handle, "load-stats-overlay", "no");
            LibMpv.SetOptionString(_handle, "load-auto-profiles", "no");
            LibMpv.SetOptionString(_handle, "load-select", "no");
            LibMpv.SetOptionString(_handle, "load-commands", "no");
            LibMpv.SetOptionString(_handle, "load-context-menu", "no");
            LibMpv.SetOptionString(_handle, "load-positioning", "no");
            LibMpv.SetOptionString(_handle, "ytdl", "no");
        }

        // Editor-style: load files paused on the first frame. User clicks Play to play.
        LibMpv.SetOptionString(_handle, "pause", "yes");

        // We drive the UI; disable mpv's own input/OSC.
        LibMpv.SetOptionString(_handle, "input-default-bindings", "no");
        LibMpv.SetOptionString(_handle, "input-vo-keyboard", "no");
        LibMpv.SetOptionString(_handle, "osc", "no");
        LibMpv.SetOptionString(_handle, "input-cursor", "no");
        LibMpv.SetOptionString(_handle, "cursor-autohide", "no");

        // Smooth scrubbing: high-resolution seek by default.
        LibMpv.SetOptionString(_handle, "hr-seek", "yes");
        LibMpv.SetOptionString(_handle, "hr-seek-framedrop", "no");

        // Logs. The render-API path is still being validated on macOS — enable
        // verbose terminal output there so a user running from a tty can see
        // what mpv is actually doing (file load, codec selection, render
        // context errors). The wid path stays quiet.
        if (widValue != null)
        {
            LibMpv.SetOptionString(_handle, "terminal", "no");
            LibMpv.SetOptionString(_handle, "msg-level", "all=warn");
        }
        else
        {
            LibMpv.SetOptionString(_handle, "terminal", "yes");
            LibMpv.SetOptionString(_handle, "msg-level", "all=v");
        }

        Check(LibMpv.Initialize(_handle));

        // Property change subscriptions
        Observe("time-pos", MpvFormat.Double);
        Observe("duration", MpvFormat.Double);
        Observe("pause", MpvFormat.Flag);

        _running = true;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    /// <summary>
    /// Create the render context after Initialize(). Must be called on a thread that
    /// has a current OpenGL context (Avalonia's <c>OpenGlControlBase</c> render thread).
    /// <paramref name="getProcAddress"/> resolves OpenGL function pointers — typically
    /// a thin wrapper around Avalonia's <c>GlInterface.GetProcAddress</c>.
    /// </summary>
    public void CreateRenderContext(Func<string, IntPtr> getProcAddress)
    {
        EnsureInit();
        if (_renderCtx != IntPtr.Zero) throw new InvalidOperationException("Render context already created");
        if (getProcAddress is null) throw new ArgumentNullException(nameof(getProcAddress));

        // Pin a strong reference to ourselves so the libmpv callback (running on
        // a non-GC thread) has a stable user-data pointer to identify us.
        if (!_selfHandle.IsAllocated)
            _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);

        _getProcAddressDelegate = (_, namePtr) =>
        {
            var name = Marshal.PtrToStringUTF8(namePtr);
            return name == null ? IntPtr.Zero : getProcAddress(name);
        };
        var procAddrFnPtr = Marshal.GetFunctionPointerForDelegate(_getProcAddressDelegate);

        var initParams = new MpvOpenGlInitParams
        {
            GetProcAddress = procAddrFnPtr,
            GetProcAddressCtx = IntPtr.Zero,
        };

        var initParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenGlInitParams>());
        var apiTypePtr = Marshal.StringToCoTaskMemUTF8("opengl");
        try
        {
            Marshal.StructureToPtr(initParams, initParamsPtr, fDeleteOld: false);

            // Param array terminated by {Invalid, NULL}.
            var paramArray = new MpvRenderParam[]
            {
                new() { Type = MpvRenderParamType.ApiType, Data = apiTypePtr },
                new() { Type = MpvRenderParamType.OpenGlInitParams, Data = initParamsPtr },
                new() { Type = MpvRenderParamType.Invalid, Data = IntPtr.Zero },
            };

            var pin = GCHandle.Alloc(paramArray, GCHandleType.Pinned);
            try
            {
                Check(LibMpv.RenderContextCreate(out _renderCtx, _handle, pin.AddrOfPinnedObject()));
            }
            finally
            {
                pin.Free();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(initParamsPtr);
            Marshal.FreeCoTaskMem(apiTypePtr);
        }

        // Now that mpv has a video output, replay any LoadFile that arrived early.
        var pending = _pendingLoadFile;
        _pendingLoadFile = null;
        if (pending != null)
            Check(LibMpv.CommandArgs(_handle, "loadfile", pending));
    }

    /// <summary>
    /// Set a callback fired by libmpv whenever a new frame is ready to render. The
    /// callback runs on libmpv's internal thread — implementations should bounce to
    /// the UI thread (e.g. via Dispatcher) before calling
    /// <c>RequestNextFrameRendering()</c>.
    /// </summary>
    public void SetRenderUpdateCallback(Action onUpdate)
    {
        if (_renderCtx == IntPtr.Zero) throw new InvalidOperationException("Render context not created");
        _renderUpdateAction = onUpdate;
        _renderUpdateDelegate = _ => _renderUpdateAction?.Invoke();
        LibMpv.RenderContextSetUpdateCallback(_renderCtx,
            _renderUpdateDelegate,
            _selfHandle.IsAllocated ? GCHandle.ToIntPtr(_selfHandle) : IntPtr.Zero);
    }

    /// <summary>
    /// Render the next decoded frame into the given OpenGL framebuffer. Called from
    /// the GL render thread when libmpv signals a new frame is ready and Avalonia
    /// hands us its FBO. <paramref name="internalFormat"/> may be 0 to let libmpv
    /// assume <c>GL_RGBA8</c>.
    /// </summary>
    public void RenderFrame(int fbo, int width, int height, int internalFormat = 0)
    {
        if (_renderCtx == IntPtr.Zero) return;

        var fboParam = new MpvOpenGlFbo
        {
            Fbo = fbo,
            Width = width,
            Height = height,
            InternalFormat = internalFormat,
        };

        var fboPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenGlFbo>());
        // Avalonia's default framebuffer is bottom-up; mpv defaults to top-down. flip_y=1
        // tells mpv to render flipped so the result appears upright in our coordinate space.
        var flipYPtr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.StructureToPtr(fboParam, fboPtr, fDeleteOld: false);
            Marshal.WriteInt32(flipYPtr, 1);

            var paramArray = new MpvRenderParam[]
            {
                new() { Type = MpvRenderParamType.OpenGlFbo, Data = fboPtr },
                new() { Type = MpvRenderParamType.FlipY, Data = flipYPtr },
                new() { Type = MpvRenderParamType.Invalid, Data = IntPtr.Zero },
            };

            var pin = GCHandle.Alloc(paramArray, GCHandleType.Pinned);
            try
            {
                LibMpv.RenderContextRender(_renderCtx, pin.AddrOfPinnedObject());
            }
            finally
            {
                pin.Free();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(fboPtr);
            Marshal.FreeHGlobal(flipYPtr);
        }
    }

    /// <summary>
    /// Free the render context. Must be called on a thread with a current GL context
    /// (libmpv may release GL resources during teardown). The mpv handle itself is
    /// kept alive — caller can still dispose it.
    /// </summary>
    public void FreeRenderContext()
    {
        var ctx = Interlocked.Exchange(ref _renderCtx, IntPtr.Zero);
        if (ctx != IntPtr.Zero)
            LibMpv.RenderContextFree(ctx);

        _renderUpdateAction = null;
        _renderUpdateDelegate = null;
        _getProcAddressDelegate = null;
    }

    private void Observe(string name, MpvFormat format)
    {
        var id = _nextObserveId++;
        _observed[id] = name;
        LibMpv.ObserveProperty(_handle, id, name, format);
    }

    public void LoadFile(string path)
    {
        EnsureInit();
        if (_useRenderApi && _renderCtx == IntPtr.Zero)
        {
            // Render context isn't up yet — issuing loadfile now would make mpv
            // bail with "No render context set" and refuse to retry. Stash the
            // path; CreateRenderContext drains it.
            _pendingLoadFile = path;
            return;
        }
        Check(LibMpv.CommandArgs(_handle, "loadfile", path));
    }

    /// <summary>
    /// Stop playback and release the currently-loaded file. Lets the caller delete or
    /// move the source file without hitting a Windows file-lock from mpv's open handle.
    /// </summary>
    public void Stop()
    {
        EnsureInit();
        // If a loadfile was queued waiting for the render context, cancel it —
        // the caller wanted playback gone, not deferred.
        _pendingLoadFile = null;
        LibMpv.CommandArgs(_handle, "stop");
    }

    /// <summary>
    /// Enable or disable the audio stream selection. <c>aid=no</c> tells mpv to
    /// skip audio entirely — used for files with truncated audio where mpv would
    /// otherwise re-scan the file looking for audio packets on every seek past
    /// the audio EOF (laggy scrubbing). The value persists across <see cref="LoadFile"/>
    /// calls until changed.
    /// </summary>
    public void SetAudioEnabled(bool on)
    {
        EnsureInit();
        LibMpv.SetPropertyString(_handle, "aid", on ? "auto" : "no");
    }

    public void Play()
    {
        EnsureInit();
        LibMpv.SetPropertyString(_handle, "pause", "no");
    }

    public void Pause()
    {
        EnsureInit();
        LibMpv.SetPropertyString(_handle, "pause", "yes");
    }

    public void TogglePause()
    {
        EnsureInit();
        LibMpv.CommandArgs(_handle, "cycle", "pause");
    }

    public void SeekAbsolute(double seconds, bool exact = true)
    {
        EnsureInit();
        var mode = exact ? "absolute+exact" : "absolute+keyframes";
        LibMpv.CommandArgs(_handle, "seek", seconds.ToString("R", CultureInfo.InvariantCulture), mode);
    }

    public void FrameStep()
    {
        EnsureInit();
        LibMpv.CommandArgs(_handle, "frame-step");
    }

    public void FrameBackStep()
    {
        EnsureInit();
        LibMpv.CommandArgs(_handle, "frame-back-step");
    }

    /// <summary>
    /// Seek by <paramref name="seconds"/> from current position and snap to the
    /// keyframe at-or-before the target. Used for next-/prev-keyframe navigation
    /// so the user can see where a lossless cut will actually land.
    /// Note: mpv's keyframe seek snaps to the keyframe ≤ target time, so the delta
    /// must exceed the file's GOP size to advance. ~1s suits typical stream/screen
    /// recordings (1–2s GOPs); files with longer GOPs may appear to "stick" and
    /// would benefit from a precomputed keyframe index instead.
    /// </summary>
    public void SeekKeyframeRelative(double seconds)
    {
        EnsureInit();
        LibMpv.CommandArgs(_handle, "seek",
            seconds.ToString("R", CultureInfo.InvariantCulture), "relative+keyframes");
    }

    public double Duration
    {
        get
        {
            if (_handle == IntPtr.Zero) return 0;
            return LibMpv.GetProperty(_handle, "duration", MpvFormat.Double, out double v) == 0 ? v : 0;
        }
    }

    public double TimePos
    {
        get
        {
            if (_handle == IntPtr.Zero) return 0;
            return LibMpv.GetProperty(_handle, "time-pos", MpvFormat.Double, out double v) == 0 ? v : 0;
        }
    }

    /// <summary>
    /// Returns chapter start times (seconds) from the loaded file, or empty if the
    /// file has no chapters. Reads <c>chapters</c> (count) + <c>chapter-list/N/time</c>
    /// individually — no node-array marshalling needed. Stream-recorder .ts files
    /// typically have no chapters; mainstream MKV/MP4s usually do.
    /// </summary>
    public IReadOnlyList<double> GetChapterTimes()
    {
        if (_handle == IntPtr.Zero) return Array.Empty<double>();
        if (LibMpv.GetProperty(_handle, "chapters", MpvFormat.Int64, out long count) != 0 || count <= 0)
            return Array.Empty<double>();
        var result = new List<double>((int)count);
        for (var i = 0; i < count; i++)
        {
            var name = "chapter-list/" + i.ToString(CultureInfo.InvariantCulture) + "/time";
            if (LibMpv.GetProperty(_handle, name, MpvFormat.Double, out double t) == 0)
                result.Add(t);
        }
        return result;
    }

    private void EventLoop()
    {
        try
        {
            while (_running)
            {
                var evPtr = LibMpv.WaitEvent(_handle, 0.1);
                // Re-check _running after wait — if Dispose woke us up, the evPtr's memory
                // may be on the verge of being freed; safest to bail before reading it.
                if (!_running) break;
                if (evPtr == IntPtr.Zero) continue;

                var ev = System.Runtime.InteropServices.Marshal.PtrToStructure<MpvEvent>(evPtr);
                switch (ev.EventId)
                {
                    case MpvEventId.None:
                        continue;
                    case MpvEventId.Shutdown:
                        _running = false;
                        break;
                    case MpvEventId.FileLoaded:
                        FileLoaded?.Invoke();
                        break;
                    case MpvEventId.EndFile:
                        EndFile?.Invoke();
                        break;
                    case MpvEventId.PlaybackRestart:
                        PlaybackRestart?.Invoke();
                        break;
                    case MpvEventId.PropertyChange:
                        HandlePropertyChange(ev);
                        break;
                }
            }
        }
        finally
        {
            // Event thread owns destruction. Dispose only signals shutdown; it does NOT
            // call TerminateDestroy — that would invalidate evPtr in flight on this thread.
            var h = System.Threading.Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (h != IntPtr.Zero) LibMpv.TerminateDestroy(h);
        }
    }

    private void HandlePropertyChange(MpvEvent ev)
    {
        if (!_observed.TryGetValue(ev.ReplyUserdata, out var name)) return;

        // Event data layout: mpv_event_property { name, format, data }
        // We read the format and value from the event Data pointer.
        var dataPtr = ev.Data;
        if (dataPtr == IntPtr.Zero) return;

        // sizeof name pointer + format int (with padding) + data pointer
        var ptrSize = IntPtr.Size;
        var formatOffset = ptrSize;
        var format = (MpvFormat)System.Runtime.InteropServices.Marshal.ReadInt32(dataPtr, formatOffset);
        var valuePtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(dataPtr, formatOffset + ptrSize);
        if (valuePtr == IntPtr.Zero) return;

        switch (name)
        {
            case "time-pos" when format == MpvFormat.Double:
                TimePosChanged?.Invoke(System.Runtime.InteropServices.Marshal.PtrToStructure<double>(valuePtr));
                break;
            case "duration" when format == MpvFormat.Double:
                DurationChanged?.Invoke(System.Runtime.InteropServices.Marshal.PtrToStructure<double>(valuePtr));
                break;
            case "pause" when format == MpvFormat.Flag:
                PauseChanged?.Invoke(System.Runtime.InteropServices.Marshal.ReadInt32(valuePtr) != 0);
                break;
        }
    }

    private void EnsureInit()
    {
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("MpvPlayer not initialized");
    }

    private static void Check(int code)
    {
        if (code < 0)
            throw new InvalidOperationException("mpv error: " + LibMpv.ErrorToString(code));
    }

    public void Dispose()
    {
        // Render context (if any) must be freed before terminate_destroy. The GL
        // view is expected to call FreeRenderContext() from its OnOpenGlDeinit
        // first — this is a defense-in-depth fallback for paths where Dispose
        // happens to fire first (shouldn't, but cheap to guard).
        if (_renderCtx != IntPtr.Zero)
        {
            try { LibMpv.RenderContextFree(_renderCtx); } catch { }
            _renderCtx = IntPtr.Zero;
        }

        // Signal the event thread to exit. It calls TerminateDestroy in its finally block —
        // doing it from this thread would race with mpv_wait_event's evPtr read.
        _running = false;
        var handle = _handle;
        if (handle != IntPtr.Zero)
        {
            try { LibMpv.Wakeup(handle); } catch { /* may already be destroyed */ }
        }
        try { _eventThread?.Join(2000); } catch { }

        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }
}
