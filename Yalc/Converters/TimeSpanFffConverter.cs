using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace YetAnotherLosslessCutter.Converters;

/// <summary>
/// Two-way binding converter between <see cref="TimeSpan"/> and the user-facing
/// <c>hh:mm:ss.fff</c> string. Used by the segment-list inline time text-entry
/// so users can type exact timestamps instead of dragging at frame precision.
/// On invalid input, returns <see cref="BindingOperations.DoNothing"/> so the
/// model isn't updated and the original value snaps back when the user tabs out.
/// </summary>
public sealed class TimeSpanFffConverter : IValueConverter
{
    public static readonly TimeSpanFffConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
            return ts.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s))
            return BindingOperations.DoNothing;

        s = s.Trim();
        // Strict format first, then progressively looser formats so users can type
        // shorthand like "1:23.5" or "01:23".
        if (TimeSpan.TryParseExact(s, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var t)) return t;
        if (TimeSpan.TryParseExact(s, @"hh\:mm\:ss",      CultureInfo.InvariantCulture, out t)) return t;
        if (TimeSpan.TryParseExact(s, @"h\:mm\:ss\.fff",  CultureInfo.InvariantCulture, out t)) return t;
        if (TimeSpan.TryParseExact(s, @"h\:mm\:ss",       CultureInfo.InvariantCulture, out t)) return t;
        if (TimeSpan.TryParseExact(s, @"m\:ss\.fff",      CultureInfo.InvariantCulture, out t)) return t;
        if (TimeSpan.TryParseExact(s, @"m\:ss",           CultureInfo.InvariantCulture, out t)) return t;
        if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out t)) return t;
        return BindingOperations.DoNothing;
    }
}
