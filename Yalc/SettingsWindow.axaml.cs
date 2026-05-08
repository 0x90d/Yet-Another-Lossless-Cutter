using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using YetAnotherLosslessCutter.Hotkeys;
using YetAnotherLosslessCutter.NativeDeps;
using YetAnotherLosslessCutter.Plugins;

namespace YetAnotherLosslessCutter;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // Settings.Instance is already wired as DataContext via x:Static in XAML.
        AppendPluginContributions();
        AppendPluginManager();
        AppendNativeDepsCard();
        UpdateOutputTemplatePreview();
        BuildHotkeysList();

        // Re-render the hotkey list whenever bindings change (e.g., the user just
        // edited one and we need to surface the new chord + recompute conflicts).
        // Detach on close so reopening this dialog doesn't accumulate subscriptions.
        Settings.Instance.HotkeyBindingsChanged += BuildHotkeysList;
        Closed += (_, _) => Settings.Instance.HotkeyBindingsChanged -= BuildHotkeysList;
    }

    private void OutputTemplateBox_TextChanged(object? sender, TextChangedEventArgs e)
        => UpdateOutputTemplatePreview();

    /// <summary>
    /// Render a sample filename using the current template so the user sees the
    /// effect live as they edit. Sample data is fixed and synthetic — independent
    /// of any currently-loaded file so the preview is meaningful even before the
    /// user opens anything.
    /// </summary>
    private void UpdateOutputTemplatePreview()
    {
        if (OutputTemplatePreview == null || OutputTemplateBox == null) return;
        var ctx = new OutputTemplate.Context(
            Name: "myvideo",
            Ext: ".mp4",
            StartTime: new TimeSpan(0, 0, 1, 23, 456),
            EndTime: new TimeSpan(0, 0, 2, 34, 789),
            Now: DateTime.Now,
            Index: 1);
        var rendered = OutputTemplate.Render(OutputTemplateBox.Text, ctx);
        var sanitized = OutputTemplate.SanitizeFileName(rendered);
        OutputTemplatePreview.Text = "→ " + sanitized;
    }

    /// <summary>
    /// Adds a Border.card section per registered <see cref="ISettingsContribution"/>,
    /// matching the visual style of the built-in groups so plugin settings feel native.
    /// Order: built-in groups first, plugin groups appended in registration order.
    /// </summary>
    private void AppendPluginContributions()
    {
        foreach (var contribution in PluginHost.Get<ISettingsContribution>())
        {
            var content = contribution.BuildContent();
            var stack = new StackPanel { Spacing = 0 };
            stack.Children.Add(new TextBlock
            {
                Text = contribution.Title,
                Classes = { "section" },
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#f0f0f0")),
                Margin = new Thickness(0, 0, 0, 8),
            });
            stack.Children.Add(content);

            var card = new Border
            {
                Classes = { "card" },
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 12),
                Margin = new Thickness(0, 0, 0, 10),
                Child = stack,
            };
            PluginsRoot.Children.Add(card);
        }
    }

    /// <summary>
    /// Appends a "Plugins" card listing every plugin discovered at startup.
    /// Read-only — to disable a plugin, remove its folder from <c>Plugins/</c>
    /// and rebuild. Skipped if no plugins are present.
    /// </summary>
    private void AppendPluginManager()
    {
        if (PluginRegistry.All.Count == 0) return;

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(new TextBlock
        {
            Text = "Loaded plugins",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#f0f0f0")),
            Margin = new Thickness(0, 0, 0, 8),
        });

        foreach (var p in PluginRegistry.All)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "• " + p.DisplayName,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#dddddd")),
                Margin = new Thickness(0, 2),
            });
        }

        var card = new Border
        {
            Classes = { "card" },
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack,
        };
        PluginsRoot.Children.Add(card);
    }

    /// <summary>
    /// Appends a "Native components" card with a button that re-opens the first-run
    /// download dialog with both libmpv + FFmpeg flagged as missing. Useful for
    /// repairing a corrupted install or testing the downloader during development —
    /// without needing to manually delete files from the bin folder.
    /// </summary>
    private void AppendNativeDepsCard()
    {
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(new TextBlock
        {
            Text = "Native components",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#f0f0f0")),
            Margin = new Thickness(0, 0, 0, 4),
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Re-download libmpv and FFmpeg if your install is broken or you want to refresh to the latest upstream build.",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#888888")),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        });
        var btn = new Button
        {
            Content = "Reinstall native components…",
            Padding = new Thickness(20, 6),
        };
        btn.Click += async (_, _) => await NativeDepsDialog.ShowForceAsync(this);
        stack.Children.Add(btn);

        var card = new Border
        {
            Classes = { "card" },
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack,
        };
        PluginsRoot.Children.Add(card);
    }

    private async void PickOutputDir_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose output folder",
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            Settings.Instance.OutputDirectory = path;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Render one row per catalog action, grouped by Category. Each row shows
    /// description, current chord (clickable to rebind), and a per-row "Reset"
    /// button. Rows whose chord collides with another row are visually flagged.
    /// </summary>
    private void BuildHotkeysList()
    {
        if (HotkeysRoot == null) return;
        HotkeysRoot.Children.Clear();

        // Compute current chord per action using the same overlay rules the
        // dispatcher uses, so what's displayed matches what'll fire.
        var chords = new Dictionary<string, KeyChord>();
        foreach (var action in HotkeyCatalog.All)
        {
            var c = action.DefaultBinding;
            if (Settings.Instance.HotkeyBindings.TryGetValue(action.Id, out var s))
            {
                if (string.IsNullOrWhiteSpace(s) ||
                    string.Equals(s, "(unbound)", StringComparison.OrdinalIgnoreCase))
                    c = KeyChord.Unbound;
                else if (KeyChord.TryParse(s, out var p)) c = p;
            }
            chords[action.Id] = c;
        }

        // Conflict detection: any chord used by 2+ actions is flagged on every
        // affected row, so the user can clear the duplicate themselves.
        var counts = new Dictionary<KeyChord, int>();
        foreach (var (_, c) in chords)
            if (!c.IsEmpty) counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1;

        var lastCategory = string.Empty;
        foreach (var action in HotkeyCatalog.All)
        {
            if (!string.Equals(action.Category, lastCategory, StringComparison.Ordinal))
            {
                HotkeysRoot.Children.Add(new TextBlock
                {
                    Text = action.Category,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#dddddd")),
                    Margin = new Thickness(0, 10, 0, 4),
                });
                lastCategory = action.Category;
            }

            var chord = chords[action.Id];
            var isConflict = !chord.IsEmpty && counts[chord] > 1;
            var isOverridden = chord != action.DefaultBinding;

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,160,Auto"),
                Margin = new Thickness(0, 2),
            };
            grid.Children.Add(new TextBlock
            {
                Text = action.Description,
                Foreground = new SolidColorBrush(Color.Parse("#f0f0f0")),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var chordButton = new Button
            {
                Content = chord.ToString(),
                FontFamily = new FontFamily("Consolas,Menlo,Courier New"),
                Padding = new Thickness(8, 4),
                MinHeight = 24,
                Tag = action,
            };
            if (isConflict)
            {
                chordButton.Background = new SolidColorBrush(Color.Parse("#7a2e2e"));
                chordButton.Foreground = new SolidColorBrush(Color.Parse("#ffd0d0"));
                ToolTip.SetTip(chordButton, "This chord conflicts with another action — click to rebind.");
            }
            chordButton.Click += HotkeyChord_Click;
            Grid.SetColumn(chordButton, 1);
            grid.Children.Add(chordButton);

            var resetButton = new Button
            {
                Content = "Reset",
                Padding = new Thickness(8, 4),
                FontSize = 11,
                MinHeight = 24,
                Margin = new Thickness(6, 0, 0, 0),
                IsEnabled = isOverridden,
                Tag = action,
            };
            ToolTip.SetTip(resetButton, isOverridden
                ? $"Restore default ({action.DefaultBinding})"
                : "Already at default");
            resetButton.Click += HotkeyReset_Click;
            Grid.SetColumn(resetButton, 2);
            grid.Children.Add(resetButton);

            HotkeysRoot.Children.Add(grid);
        }
    }

    private async void HotkeyChord_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HotkeyAction action }) return;
        var current = ChordForAction(action.Id);
        var dlg = new KeyCaptureDialog(action.Description, current);
        await dlg.ShowDialog(this);
        if (dlg.Result is { } picked)
        {
            // (unbound) goes through the same setter as a real chord — the dispatcher
            // interprets the storage string. This keeps Settings.json consistent.
            Settings.Instance.SetHotkeyBinding(action.Id, picked.IsEmpty ? "(unbound)" : picked.ToString());
        }
    }

    private void HotkeyReset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HotkeyAction action }) return;
        // null = remove the override entirely so the default takes over.
        Settings.Instance.SetHotkeyBinding(action.Id, null);
    }

    private void ResetAllHotkeys_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var action in HotkeyCatalog.All)
            Settings.Instance.SetHotkeyBinding(action.Id, null);
    }

    private static KeyChord ChordForAction(string actionId)
    {
        foreach (var action in HotkeyCatalog.All)
        {
            if (action.Id != actionId) continue;
            if (Settings.Instance.HotkeyBindings.TryGetValue(actionId, out var s))
            {
                if (string.IsNullOrWhiteSpace(s) ||
                    string.Equals(s, "(unbound)", StringComparison.OrdinalIgnoreCase))
                    return KeyChord.Unbound;
                if (KeyChord.TryParse(s, out var p)) return p;
            }
            return action.DefaultBinding;
        }
        return KeyChord.Unbound;
    }
}
