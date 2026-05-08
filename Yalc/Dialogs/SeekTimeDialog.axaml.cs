using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using YetAnotherLosslessCutter.Converters;

namespace YetAnotherLosslessCutter.Dialogs;

/// <summary>
/// Modal that asks the user for an absolute timestamp and (on OK) returns it as
/// a TimeSpan. Reuses <see cref="TimeSpanFffConverter"/> for parsing so the
/// accepted formats match the segment-list inline editor.
/// </summary>
public partial class SeekTimeDialog : Window
{
    /// <summary>What the user picked, or null on cancel / invalid input.</summary>
    public TimeSpan? Result { get; private set; }

    public SeekTimeDialog() : this(TimeSpan.Zero) { }

    public SeekTimeDialog(TimeSpan currentPosition)
    {
        InitializeComponent();
        TimeBox.Text = currentPosition.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        // Open with the time pre-selected so the user can immediately overtype.
        Opened += (_, _) => { TimeBox.Focus(); TimeBox.SelectAll(); };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            Cancel_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var text = TimeBox.Text;
        var parsed = TimeSpanFffConverter.Instance.ConvertBack(
            text, typeof(TimeSpan), null, CultureInfo.InvariantCulture);
        if (parsed is TimeSpan ts && ts >= TimeSpan.Zero)
        {
            Result = ts;
            Close();
        }
        // Invalid input — leave the dialog open so the user can fix it. Could
        // surface an error label here, but the placeholder hint should be enough.
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
