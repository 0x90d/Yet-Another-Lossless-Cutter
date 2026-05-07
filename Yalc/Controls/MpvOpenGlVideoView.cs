using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        PlayerProperty.Changed.AddClassHandler<MpvOpenGlVideoView>((view, e) =>
        {
            Console.Error.WriteLine($"[gl-view] PlayerProperty changed -> {e.NewValue}");
            if (e.NewValue is MpvPlayer p && !p.IsInitialized)
                p.Initialize();
            view.RequestNextFrameRendering();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Console.Error.WriteLine($"[gl-view] AttachedToVisualTree, bounds={Bounds}, requesting first render");
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        Console.Error.WriteLine($"[gl-view] OnOpenGlInit, bounds={Bounds}");
        base.OnOpenGlInit(gl);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        var player = Player;
        Console.Error.WriteLine($"[gl-view] OnOpenGlRender fb={fb} bounds={Bounds} player={(player is null ? "null" : "set")}");
        if (player is null) return;

        if (!player.IsInitialized) player.Initialize();

        if (!player.HasRenderContext)
        {
            Console.Error.WriteLine("[gl-view] creating mpv render context");
            player.CreateRenderContext(gl.GetProcAddress);
            player.SetRenderUpdateCallback(() =>
                Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render));
            _attached = player;
            Console.Error.WriteLine("[gl-view] render context created");
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var w = (int)Math.Round(Bounds.Width * scaling);
        var h = (int)Math.Round(Bounds.Height * scaling);
        if (w > 0 && h > 0)
            player.RenderFrame(fb, w, h);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        Console.Error.WriteLine("[gl-view] OnOpenGlDeinit");
        _attached?.FreeRenderContext();
        _attached = null;
    }
}
