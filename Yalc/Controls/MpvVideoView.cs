using System;
using Avalonia;
using Avalonia.Controls;
using YetAnotherLosslessCutter.Mpv;

namespace YetAnotherLosslessCutter.Controls;

/// <summary>
/// OS-aware host for the libmpv video surface. Hosts <see cref="MpvNativeVideoView"/>
/// on Windows + Linux (wid embedding) and <see cref="MpvOpenGlVideoView"/> on macOS
/// (render API). XAML and consuming code only see this single control with a
/// <see cref="Player"/> property.
/// </summary>
public class MpvVideoView : Decorator
{
    public static readonly StyledProperty<MpvPlayer?> PlayerProperty =
        AvaloniaProperty.Register<MpvVideoView, MpvPlayer?>(nameof(Player));

    public MpvPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    public MpvVideoView()
    {
        if (OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("[MpvVideoView] macOS branch — creating MpvOpenGlVideoView");
            var view = new MpvOpenGlVideoView();
            view.Bind(MpvOpenGlVideoView.PlayerProperty, this.GetObservable(PlayerProperty));
            Child = view;
        }
        else
        {
            var view = new MpvNativeVideoView();
            view.Bind(MpvNativeVideoView.PlayerProperty, this.GetObservable(PlayerProperty));
            Child = view;
        }
    }
}
