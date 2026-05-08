using System.Collections.Generic;

namespace YetAnotherLosslessCutter.Help;

public sealed record HotkeyEntry(string Keys, string Description);

public sealed record HotkeyGroup(string Title, IReadOnlyList<HotkeyEntry> Entries);

/// <summary>
/// Single source of truth for the F1 help dialog. Keep entries in sync with the
/// actual handlers in <see cref="MainWindow.OnKeyDown"/> and
/// <see cref="Controls.TimelineControl.OnKeyDown"/> / OnPointerWheelChanged.
///
/// When customizable-hotkeys lands later, the handlers should read from this
/// registry directly so drift becomes impossible — for now it's a curated list.
/// </summary>
public static class HotkeyHelp
{
    public static readonly IReadOnlyList<HotkeyGroup> All = new HotkeyGroup[]
    {
        new("Playback", new HotkeyEntry[]
        {
            new("Space", "Play / pause"),
            new("← / →", "Seek ±1 second"),
            new(", / .", "Step one frame back / forward"),
            new("Alt + ← / →", "Step to previous / next keyframe (≈)"),
            new("L", "Loop segment under playhead (toggle)"),
        }),
        new("Segments", new HotkeyEntry[]
        {
            new("S", "Set in-point at playhead"),
            new("E", "Set out-point at playhead"),
            new("A", "Add new segment at playhead"),
            new("C", "Enqueue all segments and start cutting"),
            new("Ctrl + Z", "Undo last segment edit"),
            new("Ctrl + Y  /  Ctrl + Shift + Z", "Redo"),
        }),
        new("Timeline (when focused)", new HotkeyEntry[]
        {
            new("Home / End", "Playhead to file start / end"),
            new("+ / −", "Zoom in / out around playhead"),
            new("0", "Zoom to fit (whole file)"),
            new("Wheel", "Seek ±60 s"),
            new("Ctrl + Wheel", "Zoom around cursor"),
            new("Shift + Wheel", "Pan timeline horizontally"),
            new("Middle-drag", "Pan timeline"),
        }),
        new("Mouse on timeline", new HotkeyEntry[]
        {
            new("Click strip", "Seek playhead"),
            new("Drag in segment header", "Move segment"),
            new("Drag segment edge handle", "Resize segment"),
            new("Double-click segment", "Play segment from start"),
            new("Right-click", "Context menu / auto-seek"),
        }),
        new("Other", new HotkeyEntry[]
        {
            new("F1", "Show this help"),
        }),
    };
}
