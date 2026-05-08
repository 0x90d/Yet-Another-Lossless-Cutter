using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Input;

namespace YetAnotherLosslessCutter.Hotkeys;

/// <summary>
/// A modifiers + key combination. Round-trips to a stable display/storage string
/// like <c>Ctrl+Shift+Z</c> or <c>Alt+Left</c> so settings persistence and the
/// UI can use the same format.
/// </summary>
public readonly record struct KeyChord(Key Key, KeyModifiers Modifiers)
{
    /// <summary>Modifiers-less chord on a single key.</summary>
    public KeyChord(Key key) : this(key, KeyModifiers.None) { }

    /// <summary>True if this chord has no key (used as a "no binding" marker).</summary>
    public bool IsEmpty => Key == Key.None;

    /// <summary>Empty / unbound chord. Displayed as "(unbound)".</summary>
    public static KeyChord Unbound => new(Key.None, KeyModifiers.None);

    /// <summary>
    /// Format as <c>Ctrl+Shift+Z</c> — modifiers in canonical order (Ctrl, Shift,
    /// Alt, Meta), then the key's friendly name.
    /// </summary>
    public override string ToString()
    {
        if (IsEmpty) return "(unbound)";
        var sb = new StringBuilder();
        if ((Modifiers & KeyModifiers.Control) != 0) sb.Append("Ctrl+");
        if ((Modifiers & KeyModifiers.Shift) != 0) sb.Append("Shift+");
        if ((Modifiers & KeyModifiers.Alt) != 0) sb.Append("Alt+");
        if ((Modifiers & KeyModifiers.Meta) != 0) sb.Append("Meta+");
        sb.Append(KeyName(Key));
        return sb.ToString();
    }

    /// <summary>
    /// Parse a chord written in the same format <see cref="ToString"/> produces.
    /// Modifier order is flexible (<c>Shift+Ctrl+Z</c> works too); modifier and
    /// key names are case-insensitive.
    /// </summary>
    public static bool TryParse(string? s, out KeyChord result)
    {
        result = Unbound;
        if (string.IsNullOrWhiteSpace(s)) return false;

        var parts = s.Trim().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var modifiers = KeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= KeyModifiers.Control; break;
                case "shift":   modifiers |= KeyModifiers.Shift; break;
                case "alt":     modifiers |= KeyModifiers.Alt; break;
                case "meta":
                case "win":
                case "cmd":     modifiers |= KeyModifiers.Meta; break;
                default: return false;
            }
        }

        if (!TryParseKey(parts[^1], out var key)) return false;
        result = new KeyChord(key, modifiers);
        return true;
    }

    /// <summary>
    /// Friendly name for a <see cref="Key"/>. Maps Avalonia's enum names that aren't
    /// user-friendly (<c>OemComma</c>, <c>D5</c>, <c>OemOpenBrackets</c>) to the actual
    /// printed character.
    /// </summary>
    private static string KeyName(Key k) => k switch
    {
        Key.OemComma            => ",",
        Key.OemPeriod           => ".",
        Key.OemOpenBrackets     => "[",
        Key.OemCloseBrackets    => "]",
        Key.OemPipe             => "\\",
        Key.OemBackslash        => "\\",
        Key.OemMinus            => "-",
        Key.OemPlus             => "=",
        Key.OemQuestion         => "/",
        Key.OemSemicolon        => ";",
        Key.OemQuotes           => "'",
        Key.OemTilde            => "`",
        Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4",
        Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
        Key.NumPad0 => "Num0", Key.NumPad1 => "Num1", Key.NumPad2 => "Num2",
        Key.NumPad3 => "Num3", Key.NumPad4 => "Num4", Key.NumPad5 => "Num5",
        Key.NumPad6 => "Num6", Key.NumPad7 => "Num7", Key.NumPad8 => "Num8",
        Key.NumPad9 => "Num9",
        Key.Return  => "Enter",
        Key.Escape  => "Esc",
        _ => k.ToString(),
    };

    private static readonly Dictionary<string, Key> _keyNameMap = BuildKeyNameMap();

    private static Dictionary<string, Key> BuildKeyNameMap()
    {
        var m = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
        // Reverse-map all the friendly names to their Key enum values.
        foreach (Key k in Enum.GetValues<Key>())
        {
            var name = KeyName(k);
            // Multiple Keys can map to the same display ("\\" → OemPipe and OemBackslash).
            // Keep the first; the explicit overrides below pick the canonical one.
            if (!m.ContainsKey(name)) m[name] = k;
            // Also accept the raw enum name so config files written by hand still work.
            if (!m.ContainsKey(k.ToString())) m[k.ToString()] = k;
        }
        // Canonical mappings for ambiguous display strings.
        m["\\"] = Key.OemPipe;
        m["Enter"] = Key.Return;
        m["Esc"] = Key.Escape;
        return m;
    }

    private static bool TryParseKey(string name, out Key key) =>
        _keyNameMap.TryGetValue(name, out key);
}
