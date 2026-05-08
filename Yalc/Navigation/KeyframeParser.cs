using System;
using System.Collections.Generic;
using System.Globalization;

namespace YetAnotherLosslessCutter.Navigation;

/// <summary>
/// Pure parser for ffprobe's CSV packet output. Extracts the timestamps of every
/// keyframe (packets whose flags string contains <c>K</c>). Kept I/O-free so the
/// extraction logic is unit-testable without spawning a subprocess.
///
/// Expected ffprobe invocation:
/// <c>ffprobe -v error -select_streams v:0 -show_entries packet=pts_time,flags -of csv=p=0 FILE</c>
/// produces lines like <c>12.345,K_</c> (keyframe) or <c>12.345,_</c> (non-key).
/// </summary>
public static class KeyframeParser
{
    /// <summary>
    /// Parse ffprobe stdout into a sorted array of keyframe timestamps (seconds).
    /// Lines without a <c>K</c> flag are skipped, as are malformed lines.
    /// </summary>
    public static double[] Parse(IEnumerable<string> lines)
    {
        var times = new List<double>();
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            var commaIdx = line.IndexOf(',');
            if (commaIdx <= 0) continue;

            // Keyframe iff the flags portion contains 'K'. ffprobe emits flags like
            // "K__" (key, no-discard, no-corrupt) or "___" — uppercase-K is reliable.
            var flags = line.AsSpan(commaIdx + 1);
            var isKey = false;
            for (var i = 0; i < flags.Length; i++)
            {
                if (flags[i] == 'K') { isKey = true; break; }
            }
            if (!isKey) continue;

            var ptsSpan = line.AsSpan(0, commaIdx);
            if (double.TryParse(ptsSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                times.Add(t);
        }

        times.Sort();
        return times.ToArray();
    }
}
