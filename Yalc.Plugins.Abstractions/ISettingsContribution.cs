using Avalonia.Controls;

namespace YetAnotherLosslessCutter.Plugins;

/// <summary>
/// Lets a plugin contribute its own card to the Settings window. Each contribution
/// renders as a labelled section, identical in style to the core's built-in groups.
/// Persistence is the plugin's responsibility — it can use whatever JSON / file
/// scheme makes sense for its own settings.
/// </summary>
public interface ISettingsContribution
{
    /// <summary>Human-readable section title (e.g. "Camshow router").</summary>
    string Title { get; }

    /// <summary>
    /// Build the inner content control for the settings card. Called once per
    /// SettingsWindow open. The plugin owns the returned control's lifecycle.
    /// </summary>
    Control BuildContent();
}
