using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace YetAnotherLosslessCutter.Controls;

/// <summary>
/// A set of video thumbnail frames covering [<see cref="StartTime"/>, <see cref="EndTime"/>].
///
/// Each bitmap carries its own actual capture time in <see cref="Times"/> — we don't
/// assume even spacing because seeks can fail on some files (e.g. MPEG-TS with audio
/// dropouts), and a "fill the gap with the last successful frame" fallback would just
/// produce visually-identical cells across most of the timeline.
/// </summary>
public sealed record FrameSet(
    IReadOnlyList<Bitmap> Bitmaps,
    IReadOnlyList<double> Times,
    double StartTime,
    double EndTime)
{
    /// <summary>
    /// Approximate density — duration of [<see cref="StartTime"/>, <see cref="EndTime"/>]
    /// divided by frame count. Used by the multi-layer cache to pick which layer is finest.
    /// </summary>
    public double SecondsPerFrame =>
        Bitmaps.Count <= 1 ? (EndTime - StartTime) : (EndTime - StartTime) / Bitmaps.Count;

    public bool Covers(double t) => t >= StartTime && t <= EndTime;

    /// <summary>
    /// Returns the bitmap whose timestamp is closest to <paramref name="t"/>, but only
    /// if it's within <paramref name="maxDistanceSeconds"/> — otherwise null. The cap
    /// prevents drawing a stale duplicate across a region of the timeline that we
    /// failed to extract frames for.
    /// </summary>
    public Bitmap? PickNearest(double t, double maxDistanceSeconds = double.PositiveInfinity)
    {
        if (Bitmaps.Count == 0) return null;
        var bestIdx = 0;
        var bestDist = Math.Abs(Times[0] - t);
        for (var i = 1; i < Times.Count; i++)
        {
            var d = Math.Abs(Times[i] - t);
            if (d < bestDist) { bestDist = d; bestIdx = i; }
        }
        return bestDist <= maxDistanceSeconds ? Bitmaps[bestIdx] : null;
    }
}
