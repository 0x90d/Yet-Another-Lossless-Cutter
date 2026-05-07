using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using YetAnotherLosslessCutter.Mpv;

namespace YetAnotherLosslessCutter.Controls;

/// <summary>
/// macOS rendering path: hosts an OpenGL surface and drives it with libmpv's render
/// API. mpv's <c>wid</c> embedding doesn't work on macOS NSView, so instead of
/// embedding mpv into a native control we let mpv push frames into an FBO that
/// Avalonia owns. Used only on macOS — Windows and Linux X11 keep the simpler
/// <see cref="MpvNativeVideoView"/> path.
/// </summary>
public class MpvOpenGlVideoView : OpenGlControlBase
{
    public static readonly StyledProperty<MpvPlayer?> PlayerProperty =
        AvaloniaProperty.Register<MpvOpenGlVideoView, MpvPlayer?>(nameof(Player));

    public MpvPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    private MpvPlayer? _attached;

    static MpvOpenGlVideoView()
    {
        // When Player is assigned (after OnOpenGlInit), kick a render so we attach
        // the render context on the GL thread. If GL isn't initialized yet, this
        // is a cheap no-op and OnOpenGlRender will run anyway when Avalonia is ready.
        PlayerProperty.Changed.AddClassHandler<MpvOpenGlVideoView>((view, _) =>
            view.RequestNextFrameRendering());
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        var player = Player;
        if (player is null) return;

        if (!player.IsInitialized) player.Initialize();

        if (!player.HasRenderContext)
        {
            player.CreateRenderContext(gl.GetProcAddress);
            // mpv calls this on its internal thread when a new frame is ready;
            // bounce to the UI thread before asking Avalonia to schedule a render.
            player.SetRenderUpdateCallback(() =>
                Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render));
            _attached = player;
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var w = (int)Math.Round(Bounds.Width * scaling);
        var h = (int)Math.Round(Bounds.Height * scaling);
        if (w > 0 && h > 0)
            player.RenderFrame(fb, w, h);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        // Render context owns GL resources — must be released while a GL context is current.
        _attached?.FreeRenderContext();
        _attached = null;
    }
}
