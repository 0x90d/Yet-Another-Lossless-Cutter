using System.Collections.Generic;
using Avalonia.Input;

namespace YetAnotherLosslessCutter.Hotkeys;

/// <summary>
/// Single source of truth for the catalog of hotkey actions. Lists every action the
/// user can bind a key to, in the order the F1 help and Settings → Hotkeys page
/// should display them.
///
/// Only main-window actions live here. The timeline control's own hotkeys
/// (Home / End / + / − / 0, wheel zoom, etc.) are scoped to the focused timeline
/// and not currently customizable.
/// </summary>
public static class HotkeyCatalog
{
    // Action IDs — stable strings persisted in Settings.json. Never rename.
    public const string PlayPause       = "playPause";
    public const string SeekBack        = "seekBack";
    public const string SeekForward     = "seekForward";
    public const string FrameBack       = "frameBack";
    public const string FrameForward    = "frameForward";
    public const string KeyframeBack    = "keyframeBack";
    public const string KeyframeForward = "keyframeForward";
    public const string LoopToggle      = "loopToggle";
    public const string FrameCapture    = "frameCapture";
    public const string SpeedDown       = "speedDown";
    public const string SpeedUp         = "speedUp";
    public const string SpeedReset      = "speedReset";
    public const string SetIn           = "setIn";
    public const string SetOut          = "setOut";
    public const string AddSegment      = "addSegment";
    public const string EnqueueAndStart = "enqueueAndStart";
    public const string Undo            = "undo";
    public const string Redo            = "redo";
    public const string RedoAlt         = "redoAlt";
    public const string HelpDialog      = "helpDialog";

    public static readonly IReadOnlyList<HotkeyAction> All = new HotkeyAction[]
    {
        // ─── Playback ───────────────────────────────────────────────────────
        new(PlayPause,       "Playback", "Play / pause",
            new KeyChord(Key.Space)),
        new(SeekBack,        "Playback", "Seek 1 second back",
            new KeyChord(Key.Left)),
        new(SeekForward,     "Playback", "Seek 1 second forward",
            new KeyChord(Key.Right)),
        new(FrameBack,       "Playback", "Step one frame back",
            new KeyChord(Key.OemComma)),
        new(FrameForward,    "Playback", "Step one frame forward",
            new KeyChord(Key.OemPeriod)),
        new(KeyframeBack,    "Playback", "Step to previous keyframe",
            new KeyChord(Key.Left, KeyModifiers.Alt)),
        new(KeyframeForward, "Playback", "Step to next keyframe",
            new KeyChord(Key.Right, KeyModifiers.Alt)),
        new(LoopToggle,      "Playback", "Loop segment under playhead (toggle)",
            new KeyChord(Key.L)),
        new(FrameCapture,    "Playback", "Save current frame as PNG",
            new KeyChord(Key.P)),
        new(SpeedDown,       "Playback", "Slow down playback",
            new KeyChord(Key.OemOpenBrackets)),
        new(SpeedUp,         "Playback", "Speed up playback",
            new KeyChord(Key.OemCloseBrackets)),
        new(SpeedReset,      "Playback", "Reset playback speed",
            new KeyChord(Key.OemPipe)),

        // ─── Segments ───────────────────────────────────────────────────────
        new(SetIn,           "Segments", "Set in-point at playhead",
            new KeyChord(Key.S)),
        new(SetOut,          "Segments", "Set out-point at playhead",
            new KeyChord(Key.E)),
        new(AddSegment,      "Segments", "Add new segment at playhead",
            new KeyChord(Key.A)),
        new(EnqueueAndStart, "Segments", "Enqueue all segments and start cutting",
            new KeyChord(Key.C)),
        new(Undo,            "Segments", "Undo last segment edit",
            new KeyChord(Key.Z, KeyModifiers.Control)),
        new(Redo,            "Segments", "Redo",
            new KeyChord(Key.Y, KeyModifiers.Control)),
        new(RedoAlt,         "Segments", "Redo (alternate)",
            new KeyChord(Key.Z, KeyModifiers.Control | KeyModifiers.Shift)),

        // ─── Other ──────────────────────────────────────────────────────────
        new(HelpDialog,      "Other",    "Show keyboard shortcuts",
            new KeyChord(Key.F1)),
    };
}
