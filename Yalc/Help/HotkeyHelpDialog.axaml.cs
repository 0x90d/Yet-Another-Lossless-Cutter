using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using YetAnotherLosslessCutter.Hotkeys;

namespace YetAnotherLosslessCutter.Help;

public partial class HotkeyHelpDialog : Window
{
    private readonly HotkeyDispatcher? _dispatcher;

    /// <summary>Parameterless ctor for the XAML loader / design tools.</summary>
    public HotkeyHelpDialog() : this(null) { }

    /// <summary>
    /// Pass the live dispatcher so the dialog reflects the user's current bindings
    /// (rebinding via Settings → Hotkeys updates the dialog the next time it opens).
    /// Falls back to catalog defaults when null (design-time / preview).
    /// </summary>
    public HotkeyHelpDialog(HotkeyDispatcher? dispatcher)
    {
        _dispatcher = dispatcher;
        InitializeComponent();
        PopulateBody();
    }

    /// <summary>
    /// Build the help body from the catalog actions, formatting each chord via the
    /// live dispatcher so the displayed keys match what's actually bound. Mouse-only
    /// gestures and the still-fixed timeline shortcuts are appended at the end as
    /// static groups (they aren't user-customizable yet).
    /// </summary>
    private void PopulateBody()
    {
        // Group catalog actions by Category, preserving the catalog order within
        // each group so playback / segments / other render in a predictable layout.
        var groups = new Dictionary<string, List<HotkeyAction>>();
        var order = new List<string>();
        foreach (var action in HotkeyCatalog.All)
        {
            if (!groups.TryGetValue(action.Category, out var list))
            {
                list = new List<HotkeyAction>();
                groups[action.Category] = list;
                order.Add(action.Category);
            }
            list.Add(action);
        }

        foreach (var category in order)
        {
            Body.Children.Add(new TextBlock { Text = category, Classes = { "section" } });
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            };
            for (var i = 0; i < groups[category].Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var action = groups[category][i];
                var chord = _dispatcher?.ChordFor(action.Id) ?? action.DefaultBinding;

                var keys = new TextBlock { Text = chord.ToString(), Classes = { "keyCol" } };
                Grid.SetRow(keys, i);
                Grid.SetColumn(keys, 0);
                grid.Children.Add(keys);

                var desc = new TextBlock { Text = action.Description, Classes = { "descCol" } };
                Grid.SetRow(desc, i);
                Grid.SetColumn(desc, 1);
                grid.Children.Add(desc);
            }
            Body.Children.Add(grid);
        }

        // Static rows for things not in the customizable catalog yet — timeline
        // shortcuts (focus-scoped) and mouse gestures.
        AppendStaticGroup("Timeline (when focused)", new (string, string)[]
        {
            ("Home / End", "Playhead to file start / end"),
            ("+ / −",      "Zoom in / out around playhead"),
            ("0",          "Zoom to fit (whole file)"),
            ("Wheel",      "Seek ±60 s"),
            ("Ctrl + Wheel",  "Zoom around cursor"),
            ("Shift + Wheel", "Pan timeline horizontally"),
            ("Middle-drag",   "Pan timeline"),
        });
        AppendStaticGroup("Mouse on timeline", new (string, string)[]
        {
            ("Click strip",                "Seek playhead"),
            ("Drag in segment header",     "Move segment"),
            ("Drag segment edge handle",   "Resize segment"),
            ("Double-click segment",       "Play segment from start"),
            ("Right-click",                "Context menu / auto-seek"),
        });
    }

    private void AppendStaticGroup(string title, (string keys, string desc)[] rows)
    {
        Body.Children.Add(new TextBlock { Text = title, Classes = { "section" } });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var k = new TextBlock { Text = rows[i].keys, Classes = { "keyCol" } };
            Grid.SetRow(k, i);
            Grid.SetColumn(k, 0);
            grid.Children.Add(k);
            var d = new TextBlock { Text = rows[i].desc, Classes = { "descCol" } };
            Grid.SetRow(d, i);
            Grid.SetColumn(d, 1);
            grid.Children.Add(d);
        }
        Body.Children.Add(grid);
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
