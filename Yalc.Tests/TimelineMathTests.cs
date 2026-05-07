using System;
using YetAnotherLosslessCutter.Controls;
using Xunit;

namespace YetAnotherLosslessCutter.Tests;

/// <summary>
/// Tests for the pure-math helpers extracted from <see cref="TimelineControl"/>.
/// These lock in the current geometry so refactors / cleanups can't silently break
/// scrubbing, zoom, pan, snap, or duration handling.
/// </summary>
public class TimelineMathTests
{
    private const double Eps = 1e-6;
    private const double MinView = TimelineMath.DefaultMinViewDuration;

    // ---- TimeToX / XToTime ----

    [Theory]
    [InlineData(0.0, 0.0, 100.0, 1000.0, 0.0)]      // start
    [InlineData(50.0, 0.0, 100.0, 1000.0, 500.0)]   // midpoint
    [InlineData(100.0, 0.0, 100.0, 1000.0, 1000.0)] // end
    [InlineData(25.0, 20.0, 30.0, 1000.0, 500.0)]   // zoomed view, midpoint of [20,30]
    public void TimeToX_MapsTimeToPixel(double t, double vs, double ve, double w, double expectedX)
    {
        Assert.Equal(expectedX, TimelineMath.TimeToX(t, vs, ve, w), 4);
    }

    [Fact]
    public void TimeToX_AndXToTime_RoundTrip()
    {
        const double vs = 12.5, ve = 87.5, w = 1000, dur = 100;
        for (var px = 0.0; px <= w; px += 100)
        {
            var t = TimelineMath.XToTime(px, vs, ve, w, dur);
            var px2 = TimelineMath.TimeToX(t, vs, ve, w);
            Assert.Equal(px, px2, 4);
        }
    }

    [Fact]
    public void XToTime_ClampsToDuration()
    {
        // Pixel x past the right edge would map past Duration; XToTime clamps.
        var t = TimelineMath.XToTime(2000, 0, 100, 1000, 100);
        Assert.Equal(100, t, 4);
    }

    [Fact]
    public void XToTime_ClampsBelowZero()
    {
        var t = TimelineMath.XToTime(-50, 0, 100, 1000, 100);
        Assert.Equal(0, t, 4);
    }

    // ---- PanBy ----

    [Fact]
    public void PanBy_SimpleSlide()
    {
        var (s, e) = TimelineMath.PanBy(10, viewStart: 20, viewEnd: 50, duration: 100);
        Assert.Equal(30, s, 4);
        Assert.Equal(60, e, 4);
    }

    [Fact]
    public void PanBy_ClampsToZero()
    {
        // Trying to pan left past the start clamps to ViewStart=0.
        var (s, e) = TimelineMath.PanBy(-100, viewStart: 20, viewEnd: 50, duration: 100);
        Assert.Equal(0, s, 4);
        Assert.Equal(30, e, 4); // width preserved (50-20=30)
    }

    [Fact]
    public void PanBy_ClampsToDurationEnd()
    {
        // Pan right past the end clamps so ViewEnd == Duration.
        var (s, e) = TimelineMath.PanBy(1000, viewStart: 20, viewEnd: 50, duration: 100);
        Assert.Equal(70, s, 4);
        Assert.Equal(100, e, 4);
    }

    [Fact]
    public void PanBy_ZeroDurationIsNoop()
    {
        var (s, e) = TimelineMath.PanBy(50, viewStart: 0, viewEnd: 0, duration: 0);
        Assert.Equal(0, s, 4);
        Assert.Equal(0, e, 4);
    }

    // ---- ZoomAround ----

    [Fact]
    public void ZoomAround_PivotStaysAtSameRelativePosition()
    {
        // Zoom in by 0.5 around t=50: view [0,100] → [25, 75], pivot stayed at center.
        var (s, e) = TimelineMath.ZoomAround(50, 0.5, 0, 100, 100, MinView);
        Assert.Equal(25, s, 4);
        Assert.Equal(75, e, 4);
    }

    [Fact]
    public void ZoomAround_ClampsAtZero()
    {
        // Zoom OUT by 3× around t=10 from a tight view [5,15]. New width would be 30,
        // but with pivot only 10s in, the natural newStart goes negative — clamp to 0
        // and let newEnd shift right to preserve the target width.
        var (s, e) = TimelineMath.ZoomAround(10, 3.0, 5, 15, 100, MinView);
        Assert.Equal(0, s, 4);
        Assert.Equal(30, e, 4);
    }

    [Fact]
    public void ZoomAround_ClampsAtDuration()
    {
        // Symmetric case: zoom out by 3× near the end of the file → newEnd would
        // overshoot Duration. Clamp newEnd to Duration and shift newStart left to
        // preserve the target width.
        var (s, e) = TimelineMath.ZoomAround(95, 3.0, 90, 100, 100, MinView);
        Assert.Equal(70, s, 4);
        Assert.Equal(100, e, 4);
    }

    [Fact]
    public void ZoomAround_RejectsBelowMinView()
    {
        // Asking for a 0.001-second view should be rejected — return input unchanged.
        var (s, e) = TimelineMath.ZoomAround(50, 0.00001, 0, 100, 100, MinView);
        Assert.Equal(0, s, 4);
        Assert.Equal(100, e, 4);
    }

    [Fact]
    public void ZoomAround_ZoomOutFromZoomedView()
    {
        // From [40, 60] (zoomed), zoom out by 2x around the center → [30, 70].
        var (s, e) = TimelineMath.ZoomAround(50, 2.0, 40, 60, 100, MinView);
        Assert.Equal(30, s, 4);
        Assert.Equal(70, e, 4);
    }

    // ---- ClampViewToDuration (OnDurationChanged) ----

    [Fact]
    public void ClampViewToDuration_FirstNonZeroFitsToView()
    {
        var (s, e) = TimelineMath.ClampViewToDuration(0, 0, 100, MinView);
        Assert.Equal(0, s, 4);
        Assert.Equal(100, e, 4);
    }

    [Fact]
    public void ClampViewToDuration_PreservesZoomOnReReport()
    {
        // User zoomed in to [20,30] on a 100s file; mpv re-reports duration as 100.5
        // (refinement). Should preserve the zoom, not reset to [0, 100.5].
        var (s, e) = TimelineMath.ClampViewToDuration(20, 30, 100.5, MinView);
        Assert.Equal(20, s, 4);
        Assert.Equal(30, e, 4);
    }

    [Fact]
    public void ClampViewToDuration_ShrinksToFitSmallerNewDuration()
    {
        // Existing view [20, 80] but new duration is 50 → ViewEnd shrinks to 50.
        var (s, e) = TimelineMath.ClampViewToDuration(20, 80, 50, MinView);
        Assert.Equal(20, s, 4);
        Assert.Equal(50, e, 4);
    }

    [Fact]
    public void ClampViewToDuration_PullsStartIfNeeded()
    {
        // Existing view [60, 80] but new duration is 50 — start would be past end.
        // Should clamp start so [start, end] still has at least minViewDuration.
        var (s, e) = TimelineMath.ClampViewToDuration(60, 80, 50, MinView);
        Assert.Equal(50, e, 4);
        Assert.True(s >= 0 && s <= e - MinView + Eps, $"start {s} should be ≤ end - minView ({e - MinView})");
    }

    [Fact]
    public void ClampViewToDuration_ZeroDurationCollapses()
    {
        var (s, e) = TimelineMath.ClampViewToDuration(20, 30, 0, MinView);
        Assert.Equal(0, s, 4);
        Assert.Equal(0, e, 4);
    }

    // ---- SnapTime ----

    [Fact]
    public void SnapTime_NoTargetsReturnsT()
    {
        var t = TimelineMath.SnapTime(50, viewDuration: 100, width: 1000,
            targets: Array.Empty<double>(), snapHitPx: 8);
        Assert.Equal(50, t, 4);
    }

    [Fact]
    public void SnapTime_SnapsToWithinWindow()
    {
        // ViewDuration=100, width=1000 → 0.1 sec/px. snapHitPx=8 → snap window 0.8s.
        var t = TimelineMath.SnapTime(20.5, viewDuration: 100, width: 1000,
            targets: new[] { 20.0, 30.0 }, snapHitPx: 8);
        Assert.Equal(20.0, t, 4);
    }

    [Fact]
    public void SnapTime_OutsideWindowDoesNotSnap()
    {
        // Same window (0.8s); 22 is 2s away from 20 — no snap.
        var t = TimelineMath.SnapTime(22, viewDuration: 100, width: 1000,
            targets: new[] { 20.0, 30.0 }, snapHitPx: 8);
        Assert.Equal(22, t, 4);
    }

    [Fact]
    public void SnapTime_PicksClosestWhenTwoInRange()
    {
        // 20.4 is closer to 20 (0.4) than to 21 (0.6) — should pick 20.
        var t = TimelineMath.SnapTime(20.4, viewDuration: 100, width: 1000,
            targets: new[] { 20.0, 21.0 }, snapHitPx: 8);
        Assert.Equal(20.0, t, 4);
    }

    [Fact]
    public void SnapTime_ZoomedInTightensWindow()
    {
        // ViewDuration=1, width=1000 → 0.001 sec/px. snapHitPx=8 → window 0.008s.
        // 20.05 is 0.05s from 20 — outside the tight window; no snap.
        var t = TimelineMath.SnapTime(20.05, viewDuration: 1, width: 1000,
            targets: new[] { 20.0 }, snapHitPx: 8);
        Assert.Equal(20.05, t, 4);
    }
}
