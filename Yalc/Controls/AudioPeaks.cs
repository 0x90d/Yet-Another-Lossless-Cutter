using System;

namespace YetAnotherLosslessCutter.Controls;

/// <summary>
/// Pre-computed peak amplitudes (0..1) over fixed-size time buckets across a file's
/// audio. The timeline renders a translucent waveform overlay using these — fast lookup
/// by time, no per-render audio decode.
/// </summary>
public sealed class AudioPeaks
{
    public float[] Peaks { get; }
    public double StartTime { get; }
    public double EndTime { get; }

    public AudioPeaks(float[] peaks, double startTime, double endTime)
    {
        Peaks = peaks;
        StartTime = startTime;
        EndTime = endTime;
    }

    public double Duration => EndTime - StartTime;

    /// <summary>
    /// Returns the maximum peak amplitude across buckets that overlap
    /// <c>[t1, t2]</c>. 0 if the range is empty / outside coverage.
    /// </summary>
    public float MaxInRange(double t1, double t2)
    {
        if (Peaks.Length == 0) return 0;
        var dur = Duration;
        if (dur <= 0) return 0;

        if (t1 < StartTime) t1 = StartTime;
        if (t2 > EndTime) t2 = EndTime;
        if (t2 <= t1) return 0;

        var i1 = (int)Math.Floor((t1 - StartTime) / dur * Peaks.Length);
        var i2 = (int)Math.Ceiling((t2 - StartTime) / dur * Peaks.Length);
        if (i1 < 0) i1 = 0;
        if (i2 > Peaks.Length) i2 = Peaks.Length;
        if (i1 >= i2) return 0;

        float max = 0;
        for (var i = i1; i < i2; i++)
        {
            if (Peaks[i] > max) max = Peaks[i];
        }
        return max;
    }
}
