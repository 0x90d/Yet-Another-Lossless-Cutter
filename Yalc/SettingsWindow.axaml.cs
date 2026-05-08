using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
}
