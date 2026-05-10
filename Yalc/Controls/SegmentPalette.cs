using System.Collections.Generic;
using Avalonia.Media;

namespace YetAnotherLosslessCutter.Controls;

/// <summary>
/// Color palette for segments. The same color is used in the timeline overlay AND
/// in the segment list border, so the user can match a list entry to its band on
/// the timeline at a glance.
/// </summary>
public static class SegmentPalette
{
    private static readonly Color[] _colors =
    [
        Color.FromRgb(0x16, 0xea, 0x16), // green   (default for segment 0)
        Color.FromRgb(0xff, 0x9a, 0x16), // orange
        Color.FromRgb(0xea, 0x16, 0xea), // magenta
        Color.FromRgb(0x16, 0xc8, 0xea), // cyan
        Color.FromRgb(0xea, 0xea, 0x16), // yellow
        Color.FromRgb(0xea, 0x4e, 0x4e), // red
        Color.FromRgb(0x86, 0x16, 0xea), // purple
        Color.FromRgb(0x16, 0xea, 0x86), // mint
    ];

    public static int Count => _colors.Length;

    public static Color GetColor(int colorIndex)
    {
        var n = _colors.Length;
        // Handle negative modulo correctly.
        var i = ((colorIndex % n) + n) % n;
        return _colors[i];
    }

    /// <summary>
    /// Pick a palette slot not currently occupied by any existing segment, so a new
    /// segment is visually distinct from all surviving ones. Falls back to the next
    /// sequential index only when all palette slots are in use.
    /// </summary>
    public static int PickUnusedIndex(IEnumerable<int> existingIndices)
    {
        var n = _colors.Length;
        var used = new bool[n];
        var count = 0;
        foreach (var idx in existingIndices)
        {
            var slot = ((idx % n) + n) % n;
            used[slot] = true;
            count++;
        }
        for (var i = 0; i < n; i++)
            if (!used[i]) return i;
        return count;
    }

    public static IBrush GetBrush(int colorIndex) => new SolidColorBrush(GetColor(colorIndex));

    public static IBrush GetFillBrush(int colorIndex)
    {
        var c = GetColor(colorIndex);
        return new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B));
    }
}
