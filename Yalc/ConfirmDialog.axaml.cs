using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace YetAnotherLosslessCutter;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <summary>Show a yes/no confirmation. Returns true on OK, false on Cancel/close.</summary>
    public static async Task<bool> ShowAsync(Window owner, string title, string message)
    {
        var dlg = new ConfirmDialog { Title = title };
        dlg.MessageBlock.Text = message;
        return await dlg.ShowDialog<bool>(owner);
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
