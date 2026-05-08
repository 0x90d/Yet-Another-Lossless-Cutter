using Avalonia.Input;
using YetAnotherLosslessCutter.Hotkeys;
using Xunit;

namespace YetAnotherLosslessCutter.Tests;

/// <summary>
/// Tests for the chord struct that backs hotkey persistence and the F1/Settings UI.
/// Locks in the round-trip between display strings and (Key, Modifiers) pairs so
/// settings.json stays compatible across releases.
/// </summary>
public class KeyChordTests
{
    [Theory]
    [InlineData(Key.Z, KeyModifiers.Control, "Ctrl+Z")]
    [InlineData(Key.Z, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+Z")]
    [InlineData(Key.Left, KeyModifiers.Alt, "Alt+Left")]
    [InlineData(Key.Space, KeyModifiers.None, "Space")]
    [InlineData(Key.OemComma, KeyModifiers.None, ",")]
    [InlineData(Key.OemPeriod, KeyModifiers.None, ".")]
    [InlineData(Key.OemOpenBrackets, KeyModifiers.None, "[")]
    [InlineData(Key.OemCloseBrackets, KeyModifiers.None, "]")]
    [InlineData(Key.D5, KeyModifiers.None, "5")]
    [InlineData(Key.F1, KeyModifiers.None, "F1")]
    [InlineData(Key.Return, KeyModifiers.None, "Enter")]
    [InlineData(Key.Escape, KeyModifiers.None, "Esc")]
    public void ToString_Formats(Key key, KeyModifiers mods, string expected)
    {
        Assert.Equal(expected, new KeyChord(key, mods).ToString());
    }

    [Fact]
    public void Empty_DisplaysAsUnbound()
    {
        Assert.Equal("(unbound)", KeyChord.Unbound.ToString());
    }

    [Theory]
    [InlineData("Ctrl+Z", Key.Z, KeyModifiers.Control)]
    [InlineData("ctrl+z", Key.Z, KeyModifiers.Control)]
    [InlineData("Shift+Ctrl+Z", Key.Z, KeyModifiers.Control | KeyModifiers.Shift)]   // order-tolerant
    [InlineData("Alt+Left", Key.Left, KeyModifiers.Alt)]
    [InlineData("Space", Key.Space, KeyModifiers.None)]
    [InlineData(",", Key.OemComma, KeyModifiers.None)]
    [InlineData("F1", Key.F1, KeyModifiers.None)]
    [InlineData("Enter", Key.Return, KeyModifiers.None)]
    [InlineData("Esc", Key.Escape, KeyModifiers.None)]
    public void TryParse_ParsesValidStrings(string input, Key expectedKey, KeyModifiers expectedMods)
    {
        Assert.True(KeyChord.TryParse(input, out var chord));
        Assert.Equal(expectedKey, chord.Key);
        Assert.Equal(expectedMods, chord.Modifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("FakeKey")]
    [InlineData("Ctrl+")]
    [InlineData("Frobnicate+Z")]
    public void TryParse_RejectsGarbage(string input)
    {
        Assert.False(KeyChord.TryParse(input, out _));
    }

    [Theory]
    [InlineData(Key.Z, KeyModifiers.Control)]
    [InlineData(Key.Z, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(Key.Space, KeyModifiers.None)]
    [InlineData(Key.OemComma, KeyModifiers.None)]
    [InlineData(Key.D7, KeyModifiers.None)]
    public void RoundTrip(Key key, KeyModifiers mods)
    {
        var original = new KeyChord(key, mods);
        Assert.True(KeyChord.TryParse(original.ToString(), out var parsed));
        Assert.Equal(original, parsed);
    }
}
