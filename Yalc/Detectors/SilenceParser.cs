using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace YetAnotherLosslessCutter.Detectors;

/// <summary>
/// One contiguous span of audio classified as silent by ffmpeg's silencedetect filter.
/// </summary>
public readonly record struct SilenceInterval(double StartSeconds, double EndSeconds);

/// <summary>
/// Pure parsing helpers for ffmpeg's silencedetect output. Kept I/O-free so the
/// extraction logic is unit-testable without spawning a subprocess.
/// </summary>
public static class SilenceParser
{
    /// <summary>
    /// Parse silencedetect output lines into pairs of silent intervals. ffmpeg may
    /// emit a final unpaired silence_start when silence runs to end-of-file; if so,
    /// the trailing run is closed at <paramref name="fileDurationSeconds"/>.
    /// </summary>
    public static IReadOnlyList<SilenceInterval> Parse(IEnumerable<string> stderrLines, double fileDurationSeconds)
    {
        var results = new List<SilenceInterval>();
        double? pendingStart = null;
        foreach (var line in stderrLines)
        {
            if (line == null) continue;

            var startIdx = line.IndexOf("silence_start: ", StringComparison.Ordinal);
            if (startIdx >= 0)
            {
                var rest = line.AsSpan(startIdx + "silence_start: ".Length);
                if (TryParseLeadingDouble(rest, out var v)) pendingStart = v;
                continue;
            }

            var endIdx = line.IndexOf("silence_end: ", StringComparison.Ordinal);
            if (endIdx >= 0 && pendingStart.HasValue)
            {
                var rest = line.AsSpan(endIdx + "silence_end: ".Length);
                if (TryParseLeadingDouble(rest, out var v))
                {
                    if (v > pendingStart.Value)
                        results.Add(new SilenceInterval(pendingStart.Value, v));
                    pendingStart = null;
                }
            }
        }

        // Trailing silence with no closing event — close at file end if known.
        if (pendingStart.HasValue && fileDurationSeconds > pendingStart.Value)
            results.Add(new SilenceInterval(pendingStart.Value, fileDurationSeconds));

        return results;
    }

    /// <summary>
    /// Invert silence intervals into the complementary "speech" ranges across
    /// [0, fileDurationSeconds]. Overlapping or out-of-order silences are tolerated.
    /// Speech ranges shorter than <paramref name="minSpeechDurationSeconds"/> are
    /// dropped — useful so a tiny gap between two adjacent silence runs doesn't
    /// produce a 0.05s "segment" of nothing.
    /// </summary>
    public static IReadOnlyList<(double FromSeconds, double ToSeconds)> InvertToSpeech(
        IReadOnlyList<SilenceInterval> silences,
        double fileDurationSeconds,
        double minSpeechDurationSeconds = 0.0)
    {
        var ordered = silences
            .Where(s => s.EndSeconds > s.StartSeconds)
            .OrderBy(s => s.StartSeconds)
            .ToList();

        var result = new List<(double, double)>();
        var cursor = 0.0;
        foreach (var s in ordered)
        {
            if (s.StartSeconds > cursor)
            {
                var from = cursor;
                var to = Math.Min(s.StartSeconds, fileDurationSeconds);
                if (to - from >= minSpeechDurationSeconds && to > from)
                    result.Add((from, to));
            }
            cursor = Math.Max(cursor, s.EndSeconds);
        }

        if (cursor < fileDurationSeconds)
        {
            var from = cursor;
            var to = fileDurationSeconds;
            if (to - from >= minSpeechDurationSeconds)
                result.Add((from, to));
        }

        return result;
    }

    private static bool TryParseLeadingDouble(ReadOnlySpan<char> span, out double value)
    {
        // ffmpeg emits "12.345" or "12.345 | silence_duration: ...". Take the first
        // run of digits / dot / minus / exponent characters.
        var i = 0;
        while (i < span.Length && (char.IsDigit(span[i]) || span[i] is '.' or '-' or '+' or 'e' or 'E')) i++;
        var slice = span[..i];
        return double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
