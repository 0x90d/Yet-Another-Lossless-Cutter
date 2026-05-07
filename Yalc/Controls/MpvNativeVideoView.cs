using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using YetAnotherLosslessCutter.Mpv;

namespace YetAnotherLosslessCutter.Controls;

/// <summary>
/// Windows + Linux X11 rendering path: native control host whose HWND/XID is
/// handed to libmpv via the <c>wid</c> option. mpv embeds itself and renders
/// directly. Used by <see cref="MpvVideoView"/> on those platforms — macOS uses
/// <see cref="MpvOpenGlVideoView"/> instead.
/// </summary>
public class MpvNativeVideoView : NativeControlHost
{
    public static readonly StyledProperty<MpvPlayer?> PlayerProperty =
        AvaloniaProperty.Register<MpvNativeVideoView, MpvPlayer?>(nameof(Player));

    public MpvPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    private IntPtr _hwnd;

    static MpvNativeVideoView()
    {
        PlayerProperty.Changed.AddClassHandler<MpvNativeVideoView>((view, _) => view.AttachIfReady());
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        _hwnd = handle.Handle;
        AttachIfReady();
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _hwnd = IntPtr.Zero;
        base.DestroyNativeControlCore(control);
    }

    private void AttachIfReady()
    {
        if (_hwnd == IntPtr.Zero || Player == null) return;
        if (!Player.IsInitialized)
            Player.Initialize(_hwnd);
    }
}
