namespace YetAnotherLosslessCutter.Plugins;

/// <summary>
/// Adds a button to the main window's top bar, next to "Open folder…", for a plugin
/// action that isn't tied to the file currently open — a batch job over a folder, say.
/// Per-file scanning belongs on <see cref="ISegmentDetector"/> instead, which puts its
/// button in the action row with the other per-file controls.
/// </summary>
public interface IToolbarCommand
{
    string Label { get; }

    string ToolTip { get; }

    /// <summary>Called on the UI thread when the button is clicked.</summary>
    void Execute();
}
