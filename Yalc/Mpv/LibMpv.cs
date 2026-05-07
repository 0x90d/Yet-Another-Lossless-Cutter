using System;
using System.Runtime.InteropServices;

namespace YetAnotherLosslessCutter.Mpv;

internal enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
    ByteArray = 9,
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    Idle = 11,
    Tick = 14,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;
}

internal static class LibMpv
{
    private const string Dll = "libmpv-2";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_create")]
    public static extern IntPtr Create();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_initialize")]
    public static extern int Initialize(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_terminate_destroy")]
    public static extern void TerminateDestroy(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_option_string", CharSet = CharSet.Ansi)]
    public static extern int SetOptionString(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_property_string", CharSet = CharSet.Ansi)]
    public static extern int SetPropertyString(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_property")]
    public static extern int SetProperty(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, MpvFormat format, ref double data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    public static extern int GetProperty(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, MpvFormat format, out double data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    public static extern int GetProperty(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, MpvFormat format, out long data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_command")]
    public static extern int Command(IntPtr ctx, IntPtr args);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_command_string", CharSet = CharSet.Ansi)]
    public static extern int CommandString(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string args);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_observe_property")]
    public static extern int ObserveProperty(IntPtr ctx, ulong replyUserdata, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, MpvFormat format);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_wait_event")]
    public static extern IntPtr WaitEvent(IntPtr ctx, double timeout);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_wakeup")]
    public static extern void Wakeup(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_wakeup_callback")]
    public static extern void SetWakeupCallback(IntPtr ctx, WakeupCallback cb, IntPtr d);

    public delegate void WakeupCallback(IntPtr d);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_error_string")]
    public static extern IntPtr ErrorString(int error);

    public static string ErrorToString(int error)
    {
        var ptr = ErrorString(error);
        return ptr == IntPtr.Zero ? "unknown" : Marshal.PtrToStringAnsi(ptr) ?? "unknown";
    }

    public static int CommandArgs(IntPtr ctx, params string[] args)
    {
        var utf8 = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++)
                utf8[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            utf8[args.Length] = IntPtr.Zero;
            var pin = GCHandle.Alloc(utf8, GCHandleType.Pinned);
            try
            {
                return Command(ctx, pin.AddrOfPinnedObject());
            }
            finally
            {
                pin.Free();
            }
        }
        finally
        {
            for (var i = 0; i < args.Length; i++)
                if (utf8[i] != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(utf8[i]);
        }
    }

    // ---- Render API (libmpv 1.x render_gl.h) ----
    // Used on macOS where the wid-based embedding doesn't work; on Windows / Linux X11
    // the wid path is preferred and the render API stays unused.

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_create")]
    public static extern int RenderContextCreate(out IntPtr renderCtx, IntPtr mpv, IntPtr paramsArray);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_render")]
    public static extern int RenderContextRender(IntPtr renderCtx, IntPtr paramsArray);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_free")]
    public static extern void RenderContextFree(IntPtr renderCtx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_set_update_callback")]
    public static extern void RenderContextSetUpdateCallback(IntPtr renderCtx, RenderUpdateCallback cb, IntPtr cbCtx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_report_swap")]
    public static extern void RenderContextReportSwap(IntPtr renderCtx);

    public delegate void RenderUpdateCallback(IntPtr cbCtx);
    public delegate IntPtr GetProcAddressFunc(IntPtr ctx, IntPtr name);
}

internal enum MpvRenderParamType
{
    Invalid = 0,
    ApiType = 1,
    OpenGlInitParams = 2,
    OpenGlFbo = 3,
    FlipY = 4,
    Depth = 5,
    IccProfile = 6,
    AmbientLight = 7,
    X11Display = 8,
    WlDisplay = 9,
    AdvancedControl = 10,
    NextFrameInfo = 11,
    BlockForTargetTime = 12,
    SkipRendering = 13,
    DrmDisplay = 14,
    DrmOsdSize = 15,
    DrmDisplayV2 = 16,
    SwSize = 17,
    SwFormat = 18,
    SwStride = 19,
    SwPointer = 20,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvRenderParam
{
    public MpvRenderParamType Type;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlInitParams
{
    public IntPtr GetProcAddress;     // void* (*)(void* ctx, const char* name)
    public IntPtr GetProcAddressCtx;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvOpenGlFbo
{
    public int Fbo;
    public int Width;
    public int Height;
    public int InternalFormat;
}
