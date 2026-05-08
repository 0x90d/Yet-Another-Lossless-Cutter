using System;
using System.ComponentModel;
using System.Collections.Generic;
using YetAnotherLosslessCutter;
using Xunit;

namespace YetAnotherLosslessCutter.Tests;

/// <summary>
/// Tests for the two pure helpers on <see cref="VideoSegment"/>:
/// <list type="bullet">
/// <item><see cref="VideoSegment.SetCutTimes"/> — atomic bound-pair set that bypasses
/// the per-setter clamp dance. The whole point is correctness in cases where the
/// individual property setters would pin a bound mid-update.</item>
/// <item><see cref="VideoSegment.ShouldLoopBack"/> — A-B loop natural-progression
/// guard. Prevents re-snapping when the user manually scrubs past the segment end.</item>
/// </list>
/// </summary>
public class VideoSegmentTests
{
    private const double Eps = 1e-6;

    private static VideoSegment Make(double maxSeconds = 100)
    {
        return new VideoSegment { MaxDuration = TimeSpan.FromSeconds(maxSeconds) };
    }

    // ---- SetCutTimes ----

    [Fact]
    public void SetCutTimes_BasicAssign()
    {
        var s = Make();
        s.SetCutTimes(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));
        Assert.Equal(5.0, s.CutFromSeconds, 6);
        Assert.Equal(20.0, s.CutToSeconds, 6);
    }

    [Fact]
    public void SetCutTimes_SwapsWhenFromGreaterThanTo()
    {
        var s = Make();
        s.SetCutTimes(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5));
        Assert.Equal(5.0, s.CutFromSeconds, 6);
        Assert.Equal(20.0, s.CutToSeconds, 6);
    }

    [Fact]
    public void SetCutTimes_ClampsNegativeFromToZero()
    {
        var s = Make();
        s.SetCutTimes(TimeSpan.FromSeconds(-5), TimeSpan.FromSeconds(10));
        Assert.Equal(0.0, s.CutFromSeconds, 6);
        Assert.Equal(10.0, s.CutToSeconds, 6);
    }

    [Fact]
    public void SetCutTimes_ClampsToBeyondMaxDuration()
    {
        var s = Make(maxSeconds: 50);
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(200));
        Assert.Equal(10.0, s.CutFromSeconds, 6);
        Assert.Equal(50.0, s.CutToSeconds, 6);
    }

    [Fact]
    public void SetCutTimes_NoMaxDurationSet_DoesNotClampUpward()
    {
        // When MaxDuration is zero (no file context yet), don't clamp to 0 — that
        // would collapse the segment. Leave the upper bound alone.
        var s = new VideoSegment(); // MaxDuration default = TimeSpan.Zero
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(50));
        Assert.Equal(10.0, s.CutFromSeconds, 6);
        Assert.Equal(50.0, s.CutToSeconds, 6);
    }

    [Fact]
    public void SetCutTimes_TranslatePastCurrentRange()
    {
        // The whole reason this method exists. Setting CutFromSeconds and
        // CutToSeconds individually would clamp the new from to <= current to
        // (= 20), pinning it. SetCutTimes must move both atomically.
        var s = Make();
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        s.SetCutTimes(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60));
        Assert.Equal(50.0, s.CutFromSeconds, 6);
        Assert.Equal(60.0, s.CutToSeconds, 6);
    }

    [Fact]
    public void SetCutTimes_TranslatePastCurrentRangeBackward()
    {
        var s = Make();
        s.SetCutTimes(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60));
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        Assert.Equal(10.0, s.CutFromSeconds, 6);
        Assert.Equal(20.0, s.CutToSeconds, 6);
    }

    [Fact]
    public void SetCutTimes_NoChangeDoesNotFirePropertyChanged()
    {
        var s = Make();
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        var fired = new List<string?>();
        s.PropertyChanged += (_, e) => fired.Add(e.PropertyName);
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        Assert.Empty(fired);
    }

    [Fact]
    public void SetCutTimes_OnlyChangedBoundFiresPropertyChanged()
    {
        var s = Make();
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        var fired = new List<string?>();
        s.PropertyChanged += (_, e) => fired.Add(e.PropertyName);
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(25)); // only To changes
        Assert.Contains("CutTo", fired);
        Assert.Contains("CutToSeconds", fired);
        Assert.Contains("CutDuration", fired);
        Assert.DoesNotContain("CutFrom", fired);
        Assert.DoesNotContain("CutFromSeconds", fired);
    }

    // ---- ShouldLoopBack ----

    [Fact]
    public void ShouldLoopBack_NaturalCrossEnd_ReturnsTrue()
    {
        var s = Make();
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        // Last report at 19.9, current 20.0 — natural advance, just hit end.
        Assert.True(s.ShouldLoopBack(lastPos: 19.9, currentPos: 20.0));
    }

    [Fact]
    public void ShouldLoopBack_BeforeEnd_ReturnsFalse()
    {
        var s = Make();
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        Assert.False(s.ShouldLoopBack(lastPos: 14.9, currentPos: 15.0));
    }

    [Fact]
    public void ShouldLoopBack_ManualSeekPastEnd_ReturnsFalse()
    {
        // delta of 25 seconds is way larger than maxNaturalDelta (1s default), so
        // this looks like a deliberate scrub past the end, not playback crossing it.
        var s = Make();
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        Assert.False(s.ShouldLoopBack(lastPos: 5.0, currentPos: 30.0));
    }

    [Fact]
    public void ShouldLoopBack_LastPosOutsideSegment_ReturnsFalse()
    {
        // User seeked into the end of the segment from outside. Without this guard
        // they'd get yanked back to the start unexpectedly.
        var s = Make();
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        Assert.False(s.ShouldLoopBack(lastPos: 5.0, currentPos: 20.0));
    }

    [Fact]
    public void ShouldLoopBack_BackwardSeek_ReturnsFalse()
    {
        var s = Make();
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        // Negative delta — playback never reports backwards naturally.
        Assert.False(s.ShouldLoopBack(lastPos: 19.9, currentPos: 15.0));
    }

    [Fact]
    public void ShouldLoopBack_ZeroDelta_ReturnsFalse()
    {
        // Same-time report (mpv emits these when paused at the segment end).
        // We don't want to enter an infinite seek loop just because the player
        // is sitting on the end frame.
        var s = Make();
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        Assert.False(s.ShouldLoopBack(lastPos: 20.0, currentPos: 20.0));
    }

    [Fact]
    public void ShouldLoopBack_WithinEndTolerance_ReturnsTrue()
    {
        // mpv's TimePosChanged tick rate doesn't always land exactly on CutTo;
        // anything within the end tolerance counts as a natural cross.
        var s = Make();
        s.SetCutTimes(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        Assert.True(s.ShouldLoopBack(lastPos: 19.9, currentPos: 19.96));
    }
}
