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
        // Initialize the player eagerly the moment it's assigned — the mpv handle
        // doesn't need a GL context, just a UI thread. The render *context* still
        // attaches lazily on the GL thread (see OnOpenGlRender) because that does
        // need a current GL context. Without this eager init the user could click
        // Open before any frame rendered and hit "MpvPlayer not initialized".
        PlayerProperty.Changed.AddClassHandler<MpvOpenGlVideoView>((view, e) =>
        {
            if (e.NewValue is MpvPlayer p && !p.IsInitialized)
                p.Initialize();
            view.RequestNextFrameRendering();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Avalonia's OpenGlControlBase doesn't render unless asked — without this
        // request OnOpenGlRender never fires until something else (resize, focus
        // change, etc.) provokes it. mpv with vo=libmpv refuses to start playback
        // until our render context exists, so we have to force the first render
        // up front rather than waiting for the user's first interaction.
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        var player = Player;
        if (player is null) return;

        // Defensive: should already be initialized by the property-changed handler.
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
