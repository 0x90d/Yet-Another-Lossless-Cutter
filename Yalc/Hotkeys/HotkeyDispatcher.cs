using System;
using System.Collections.Generic;

namespace YetAnotherLosslessCutter.Hotkeys;

/// <summary>
/// Maps key chords to action handlers. The host registers a handler delegate per
/// action ID at startup; the chord-to-action mapping comes from
/// <see cref="HotkeyCatalog"/> defaults overlaid with user customizations from
/// settings. Reload the user customizations any time the user re-binds a chord.
/// </summary>
public sealed class HotkeyDispatcher
{
    private readonly Dictionary<string, Action> _handlers = new();
    private readonly Dictionary<KeyChord, string> _chordToAction = new();
    private readonly Dictionary<string, KeyChord> _actionToChord = new();

    /// <summary>
    /// Register the handler for an action ID. Should be called once per action at
    /// startup, before <see cref="ApplyBindings"/>.
    /// </summary>
    public void RegisterHandler(string actionId, Action handler)
    {
        _handlers[actionId] = handler;
    }

    /// <summary>
    /// Replace the chord→action map. <paramref name="userOverrides"/> may contain
    /// chord strings keyed by action ID; entries that fail to parse fall back to
    /// the catalog default. Actions whose user override is empty / "(unbound)"
    /// have no chord assigned. Last-write wins on conflicts (later catalog entries
    /// override earlier ones if both end up with the same chord).
    /// </summary>
    public void ApplyBindings(IReadOnlyDictionary<string, string>? userOverrides)
    {
        _chordToAction.Clear();
        _actionToChord.Clear();

        foreach (var action in HotkeyCatalog.All)
        {
            var chord = action.DefaultBinding;
            if (userOverrides != null && userOverrides.TryGetValue(action.Id, out var s))
            {
                if (string.IsNullOrWhiteSpace(s) || string.Equals(s, "(unbound)", StringComparison.OrdinalIgnoreCase))
                    chord = KeyChord.Unbound;
                else if (KeyChord.TryParse(s, out var parsed))
                    chord = parsed;
                // unparseable: silently fall back to default
            }

            _actionToChord[action.Id] = chord;
            if (!chord.IsEmpty)
            {
                // On chord conflict the later registration wins. The Settings UI is
                // the right place to surface the conflict to the user; here we just
                // honor the most recent intent so behavior is at least predictable.
                _chordToAction[chord] = action.Id;
            }
        }
    }

    /// <summary>Try to invoke the handler bound to <paramref name="chord"/>.</summary>
    /// <returns>True if a handler ran (caller should mark the event Handled).</returns>
    public bool Handle(KeyChord chord)
    {
        if (!_chordToAction.TryGetValue(chord, out var actionId)) return false;
        if (!_handlers.TryGetValue(actionId, out var handler)) return false;
        handler();
        return true;
    }

    /// <summary>Current chord bound to an action, or <see cref="KeyChord.Unbound"/>.</summary>
    public KeyChord ChordFor(string actionId) =>
        _actionToChord.TryGetValue(actionId, out var c) ? c : KeyChord.Unbound;
}
