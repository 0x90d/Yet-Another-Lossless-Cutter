using System.Collections.Generic;
using Avalonia.Input;
using YetAnotherLosslessCutter.Hotkeys;
using Xunit;

namespace YetAnotherLosslessCutter.Tests;

/// <summary>
/// Tests for the chord→handler routing layer. The catalog and key types stay
/// AOT-clean; this gates the override-application logic so user customizations
/// don't silently lose actions or fire the wrong handler.
/// </summary>
public class HotkeyDispatcherTests
{
    [Fact]
    public void DefaultBindings_HandleCatalogChords()
    {
        var d = new HotkeyDispatcher();
        var fired = "";
        d.RegisterHandler(HotkeyCatalog.PlayPause, () => fired = "play");
        d.RegisterHandler(HotkeyCatalog.SetIn, () => fired = "in");
        d.ApplyBindings(null);

        Assert.True(d.Handle(new KeyChord(Key.Space)));
        Assert.Equal("play", fired);

        Assert.True(d.Handle(new KeyChord(Key.S)));
        Assert.Equal("in", fired);
    }

    [Fact]
    public void Handle_UnboundChord_ReturnsFalse()
    {
        var d = new HotkeyDispatcher();
        d.ApplyBindings(null);
        Assert.False(d.Handle(new KeyChord(Key.F12))); // not in the catalog
    }

    [Fact]
    public void UserOverride_ReplacesDefaultChord()
    {
        var d = new HotkeyDispatcher();
        var fired = false;
        d.RegisterHandler(HotkeyCatalog.PlayPause, () => fired = true);
        d.ApplyBindings(new Dictionary<string, string>
        {
            { HotkeyCatalog.PlayPause, "Ctrl+P" }
        });

        Assert.False(d.Handle(new KeyChord(Key.Space)));    // old chord no longer bound
        Assert.True(d.Handle(new KeyChord(Key.P, KeyModifiers.Control)));
        Assert.True(fired);
    }

    [Fact]
    public void UnparsableOverride_FallsBackToDefault()
    {
        var d = new HotkeyDispatcher();
        d.RegisterHandler(HotkeyCatalog.PlayPause, () => { });
        d.ApplyBindings(new Dictionary<string, string>
        {
            { HotkeyCatalog.PlayPause, "wat+Z" }
        });
        Assert.True(d.Handle(new KeyChord(Key.Space))); // default Space still works
    }

    [Fact]
    public void UnboundOverride_RemovesBinding()
    {
        var d = new HotkeyDispatcher();
        d.RegisterHandler(HotkeyCatalog.PlayPause, () => { });
        d.ApplyBindings(new Dictionary<string, string>
        {
            { HotkeyCatalog.PlayPause, "(unbound)" }
        });
        Assert.False(d.Handle(new KeyChord(Key.Space)));
        Assert.True(d.ChordFor(HotkeyCatalog.PlayPause).IsEmpty);
    }

    [Fact]
    public void ChordFor_ReturnsCurrentBinding()
    {
        var d = new HotkeyDispatcher();
        d.ApplyBindings(null);
        Assert.Equal(new KeyChord(Key.Space), d.ChordFor(HotkeyCatalog.PlayPause));
    }

    [Fact]
    public void Conflict_LaterCatalogEntryWins()
    {
        // Bind two actions to the same chord — the action declared later in the
        // catalog wins. Predictable rather than ambiguous; the Settings UI is
        // expected to surface the conflict before the user gets to this state.
        var d = new HotkeyDispatcher();
        var who = "";
        d.RegisterHandler(HotkeyCatalog.PlayPause, () => who = "play");
        d.RegisterHandler(HotkeyCatalog.SetIn, () => who = "in");
        d.ApplyBindings(new Dictionary<string, string>
        {
            { HotkeyCatalog.PlayPause, "Ctrl+1" },
            { HotkeyCatalog.SetIn, "Ctrl+1" }, // SetIn comes later in the catalog
        });
        d.Handle(new KeyChord(Key.D1, KeyModifiers.Control));
        Assert.Equal("in", who);
    }
}
