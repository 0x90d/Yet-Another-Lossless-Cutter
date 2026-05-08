using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace YetAnotherLosslessCutter.Help;

public partial class HotkeyHelpDialog : Window
{
    public HotkeyHelpDialog()
    {
        InitializeComponent();
        PopulateBody();
    }

    /// <summary>
    /// Build the help body from <see cref="HotkeyHelp.All"/>. Each group becomes a
    /// section header followed by a 2-column grid of (keys, description). Done in
    /// code-behind because the registry is small and a DataTemplate would add more
    /// XAML than it removes.
    /// </summary>
    private void PopulateBody()
    {
        foreach (var group in HotkeyHelp.All)
        {
            Body.Children.Add(new TextBlock { Text = group.Title, Classes = { "section" } });

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            };
            for (var i = 0; i < group.Entries.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var entry = group.Entries[i];

                var keys = new TextBlock { Text = entry.Keys, Classes = { "keyCol" } };
                Grid.SetRow(keys, i);
                Grid.SetColumn(keys, 0);
                grid.Children.Add(keys);

                var desc = new TextBlock { Text = entry.Description, Classes = { "descCol" } };
                Grid.SetRow(desc, i);
                Grid.SetColumn(desc, 1);
                grid.Children.Add(desc);
            }
            Body.Children.Add(grid);
        }
    }

    /// <summary>Esc closes the dialog (matches the Close button).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape || e.Key == Key.F1)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
