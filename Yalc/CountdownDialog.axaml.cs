using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace YetAnotherLosslessCutter;

/// <summary>
/// Modal dialog that runs a tick-down clock and closes itself when it hits zero.
/// Used to give the user a grace window to cancel before a destructive action
/// (e.g. PC shutdown after the queue completes).
/// </summary>
public partial class CountdownDialog : Window
{
    private DispatcherTimer? _timer;
    private int _remaining;
    private string _messageTemplate = "{0}";

    public CountdownDialog()
    {
        InitializeComponent();
        Closed += (_, _) => _timer?.Stop();
    }

    /// <summary>
    /// Show the dialog; returns true if the user cancelled, false if the countdown
    /// elapsed without intervention. Use the boolean to decide whether to proceed.
    /// </summary>
    /// <param name="messageTemplate">Format string with one <c>{0}</c> placeholder
    /// for the remaining seconds, e.g. <c>"Closing in {0}s."</c>.</param>
    public static Task<bool> ShowAsync(Window owner, string title, string messageTemplate, int seconds)
    {
        var dlg = new CountdownDialog
        {
            Title = title,
            _remaining = Math.Max(1, seconds),
            _messageTemplate = messageTemplate,
        };
        dlg.UpdateMessage();

        dlg._timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        dlg._timer.Tick += (_, _) =>
        {
            dlg._remaining--;
            if (dlg._remaining <= 0)
            {
                dlg._timer!.Stop();
                dlg.Close(false);
            }
            else dlg.UpdateMessage();
        };
        dlg._timer.Start();

        return dlg.ShowDialog<bool>(owner);
    }

    private void UpdateMessage() =>
        MessageBlock.Text = string.Format(_messageTemplate, _remaining);

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        Close(true);
    }
}
