using System;
using System.Collections.Generic;

namespace YetAnotherLosslessCutter.Controls;

/// <summary>
/// Pure-math helpers for the timeline control. Extracted so they can be unit-tested
/// without an Avalonia render context. <see cref="TimelineControl"/> instance methods
/// delegate to these — keep the two in lock-step.
/// </summary>
public static class TimelineMath
{
    public const double DefaultMinViewDuration = 0.1;

    /// <summary>Pixel x for time t, given the current view window and pixel width.</summary>
    public static double TimeToX(double t, double viewStart, double viewEnd, double width)
    {
        var viewDur = Math.Max(DefaultMinViewDuration, viewEnd - viewStart);
        if (viewDur <= 0) return 0;
        return (t - viewStart) / viewDur * width;
    }

    /// <summary>Time for a pixel x, clamped to <c>[0, duration]</c>.</summary>
    public static double XToTime(double x, double viewStart, double viewEnd, double width, double duration)
    {
        if (width <= 0) return viewStart;
        var viewDur = Math.Max(DefaultMinViewDuration, viewEnd - viewStart);
        return Math.Clamp(viewStart + x / width * viewDur, 0, duration);
    }

    /// <summary>
    /// Slide the view window by <paramref name="seconds"/>, keeping its width and
    /// clamping to <c>[0, duration]</c>. Returns the new (start, end).
    /// </summary>
    public static (double newStart, double newEnd) PanBy(double seconds, double viewStart, double viewEnd, double duration)
    {
        if (duration <= 0) return (viewStart, viewEnd);
        var dur = viewEnd - viewStart;
        var newStart = Math.Clamp(viewStart + seconds, 0, Math.Max(0, duration - dur));
        return (newStart, newStart + dur);
    }

    /// <summary>
    /// Zoom by <paramref name="factor"/> around <paramref name="pivotTime"/>: factor &lt; 1
    /// zooms in (tighter view). The pivot time stays at the same screen X. Clamped to
    /// <c>[0, duration]</c> and a min-view of <paramref name="minViewDuration"/>;
    /// returns the input unchanged if the result would violate the min-view.
    /// </summary>
    public static (double newStart, double newEnd) ZoomAround(
        double pivotTime, double factor,
        double viewStart, double viewEnd,
        double duration, double minViewDuration)
    {
        if (duration <= 0) return (viewStart, viewEnd);
        var newStart = pivotTime - (pivotTime - viewStart) * factor;
        var newEnd = pivotTime + (viewEnd - pivotTime) * factor;

        if (newStart < 0)
        {
            newEnd = Math.Min(duration, newEnd - newStart);
            newStart = 0;
        }
        if (newEnd > duration)
        {
            newStart = Math.Max(0, newStart - (newEnd - duration));
            newEnd = duration;
        }
        if (newEnd - newStart < minViewDuration) return (viewStart, viewEnd);
        return (newStart, newEnd);
    }

    /// <summary>
    /// Compute the new view window when Duration changes. Empty → fit-to-view on
    /// first non-zero value; otherwise clamp the existing window into the new range
    /// (preserves zoom across mpv duration re-reports).
    /// </summary>
    public static (double newStart, double newEnd) ClampViewToDuration(
        double viewStart, double viewEnd,
        double newDuration, double minViewDuration)
    {
        if (newDuration <= 0) return (0, 0);
        if (viewEnd <= 0) return (0, newDuration); // first time
        if (viewEnd > newDuration) viewEnd = newDuration;
        if (viewStart > viewEnd - minViewDuration)
            viewStart = Math.Max(0, viewEnd - minViewDuration);
        return (viewStart, viewEnd);
    }

    /// <summary>
    /// Find the nearest entry in <paramref name="targets"/> to <paramref name="t"/>
    /// within a window of <paramref name="snapHitPx"/> pixels (converted to time
    /// units via the current pixels-per-second). Returns <paramref name="t"/> if
    /// no target is within range.
    /// </summary>
    public static double SnapTime(double t, double viewDuration, double width,
        IReadOnlyList<double> targets, double snapHitPx)
    {
        if (width <= 0 || targets.Count == 0) return t;
        var snapWindow = snapHitPx * viewDuration / width;
        var bestT = t;
        var bestDist = double.MaxValue;
        foreach (var target in targets)
        {
            var d = Math.Abs(target - t);
            if (d <= snapWindow && d < bestDist) { bestDist = d; bestT = target; }
        }
        return bestT;
    }
}
