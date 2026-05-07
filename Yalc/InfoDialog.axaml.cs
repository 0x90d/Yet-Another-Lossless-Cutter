using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace YetAnotherLosslessCutter;

/// <summary>
/// Single-button informational modal — for cases where we want the user to
/// acknowledge something important (e.g. "this file's audio is truncated and
/// scrubbing past it will be slow") without offering a yes/no choice. Sibling
/// of <see cref="ConfirmDialog"/>; same dark-themed look so the two feel like
/// one family.
/// </summary>
public partial class InfoDialog : Window
{
    public InfoDialog()
    {
        InitializeComponent();
    }

    /// <summary>Shows the dialog and resolves once the user dismisses it.</summary>
    /// <param name="icon">Leading glyph — e.g. <c>⚠</c>, <c>ℹ</c>, <c>❌</c>.</param>
    public static Task ShowAsync(Window owner, string title, string message, string icon = "⚠")
    {
        var dlg = new InfoDialog { Title = title };
        dlg.IconBlock.Text = icon;
        dlg.MessageBlock.Text = message;
        return dlg.ShowDialog(owner);
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close();
}
