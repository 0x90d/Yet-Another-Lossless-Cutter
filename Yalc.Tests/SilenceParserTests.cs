using YetAnotherLosslessCutter.Detectors;
using Xunit;

namespace YetAnotherLosslessCutter.Tests;

/// <summary>
/// Tests for the pure parser of ffmpeg silencedetect output and its inversion to
/// speech ranges. Locks in:
/// <list type="bullet">
/// <item>Robustness to surrounding ffmpeg log noise (banner lines, progress lines).</item>
/// <item>Trailing-silence handling (silence_start with no closing silence_end).</item>
/// <item>Edge cases for inversion (no silences, leading silence, trailing silence,
/// adjacent silences, overlapping silences, min-speech-duration filter).</item>
/// </list>
/// </summary>
public class SilenceParserTests
{
    private const double Eps = 1e-6;

    // ---- Parse ----

    [Fact]
    public void Parse_WellFormedPair()
    {
        var lines = new[]
        {
            "[silencedetect @ 0x55c4d8] silence_start: 12.345",
            "[silencedetect @ 0x55c4d8] silence_end: 14.567 | silence_duration: 2.222",
        };
        var result = SilenceParser.Parse(lines, fileDurationSeconds: 100);
        Assert.Single(result);
        Assert.Equal(12.345, result[0].StartSeconds, 6);
        Assert.Equal(14.567, result[0].EndSeconds, 6);
    }

    [Fact]
    public void Parse_MultipleIntervals()
    {
        var lines = new[]
        {
            "ffmpeg version 6.1 ...",
            "[silencedetect @ 0x...] silence_start: 1.0",
            "[silencedetect @ 0x...] silence_end: 2.0 | silence_duration: 1.0",
            "frame=  120 fps= 30 q=-1.0",
            "[silencedetect @ 0x...] silence_start: 5.5",
            "[silencedetect @ 0x...] silence_end: 7.5 | silence_duration: 2.0",
        };
        var result = SilenceParser.Parse(lines, fileDurationSeconds: 100);
        Assert.Equal(2, result.Count);
        Assert.Equal(1.0, result[0].StartSeconds, 6);
        Assert.Equal(2.0, result[0].EndSeconds, 6);
        Assert.Equal(5.5, result[1].StartSeconds, 6);
        Assert.Equal(7.5, result[1].EndSeconds, 6);
    }

    [Fact]
    public void Parse_TrailingSilenceUsesFileDuration()
    {
        // ffmpeg emits silence_start near end-of-file but never closes it because
        // EOF arrived during silence. Parser must close at the known file duration.
        var lines = new[]
        {
            "[silencedetect @ 0x...] silence_start: 95.0",
        };
        var result = SilenceParser.Parse(lines, fileDurationSeconds: 100.0);
        Assert.Single(result);
        Assert.Equal(95.0, result[0].StartSeconds, 6);
        Assert.Equal(100.0, result[0].EndSeconds, 6);
    }

    [Fact]
    public void Parse_TrailingSilencePastFileDuration_IsDropped()
    {
        // If silence_start came AFTER the reported file duration (clock skew or
        // probe disagreement), don't emit a backwards interval.
        var lines = new[]
        {
            "[silencedetect @ 0x...] silence_start: 105.0",
        };
        var result = SilenceParser.Parse(lines, fileDurationSeconds: 100.0);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_IgnoresEmptyLinesAndUnrelatedNoise()
    {
        var lines = new[]
        {
            "",
            "Input #0, mov,mp4,m4a,3gp,3g2,mj2",
            "  Stream #0:0: Audio: aac, 48000 Hz, stereo",
            "[silencedetect @ 0x...] silence_start: 3.14",
            "[silencedetect @ 0x...] silence_end: 4.15 | silence_duration: 1.01",
            "size=    1024kB time=00:01:00.00",
        };
        var result = SilenceParser.Parse(lines, fileDurationSeconds: 60);
        Assert.Single(result);
        Assert.Equal(3.14, result[0].StartSeconds, 6);
    }

    [Fact]
    public void Parse_EndWithoutStart_IsIgnored()
    {
        var lines = new[]
        {
            "[silencedetect @ 0x...] silence_end: 4.0 | silence_duration: 1.0",
        };
        var result = SilenceParser.Parse(lines, fileDurationSeconds: 100);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_StartAfterStart_LatestStartWins()
    {
        // Defensive: two starts in a row means we lost the close event for the first.
        // Parser should adopt the later start (closer to actual silence end).
        var lines = new[]
        {
            "[silencedetect @ 0x...] silence_start: 1.0",
            "[silencedetect @ 0x...] silence_start: 5.0",
            "[silencedetect @ 0x...] silence_end: 7.0 | silence_duration: 2.0",
        };
        var result = SilenceParser.Parse(lines, fileDurationSeconds: 100);
        Assert.Single(result);
        Assert.Equal(5.0, result[0].StartSeconds, 6);
        Assert.Equal(7.0, result[0].EndSeconds, 6);
    }

    [Fact]
    public void Parse_ZeroOrNegativeDuration_IsDropped()
    {
        // silence_end must be strictly greater than silence_start.
        var lines = new[]
        {
            "[silencedetect @ 0x...] silence_start: 5.0",
            "[silencedetect @ 0x...] silence_end: 5.0 | silence_duration: 0.0",
        };
        var result = SilenceParser.Parse(lines, fileDurationSeconds: 100);
        Assert.Empty(result);
    }

    // ---- InvertToSpeech ----

    [Fact]
    public void InvertToSpeech_NoSilences_OneSpanOfWholeFile()
    {
        var result = SilenceParser.InvertToSpeech(
            silences: System.Array.Empty<SilenceInterval>(),
            fileDurationSeconds: 60.0);
        Assert.Single(result);
        Assert.Equal(0.0, result[0].FromSeconds, 6);
        Assert.Equal(60.0, result[0].ToSeconds, 6);
    }

    [Fact]
    public void InvertToSpeech_MiddleSilenceProducesTwoSpans()
    {
        var silences = new[] { new SilenceInterval(20, 30) };
        var result = SilenceParser.InvertToSpeech(silences, fileDurationSeconds: 60);
        Assert.Equal(2, result.Count);
        Assert.Equal((0.0, 20.0), result[0]);
        Assert.Equal((30.0, 60.0), result[1]);
    }

    [Fact]
    public void InvertToSpeech_LeadingSilence_StartsAtSilenceEnd()
    {
        var silences = new[] { new SilenceInterval(0, 5) };
        var result = SilenceParser.InvertToSpeech(silences, fileDurationSeconds: 60);
        Assert.Single(result);
        Assert.Equal((5.0, 60.0), result[0]);
    }

    [Fact]
    public void InvertToSpeech_TrailingSilence_EndsAtSilenceStart()
    {
        var silences = new[] { new SilenceInterval(50, 60) };
        var result = SilenceParser.InvertToSpeech(silences, fileDurationSeconds: 60);
        Assert.Single(result);
        Assert.Equal((0.0, 50.0), result[0]);
    }

    [Fact]
    public void InvertToSpeech_EntireFileSilent_NoSpans()
    {
        var silences = new[] { new SilenceInterval(0, 60) };
        var result = SilenceParser.InvertToSpeech(silences, fileDurationSeconds: 60);
        Assert.Empty(result);
    }

    [Fact]
    public void InvertToSpeech_OverlappingSilences_Merged()
    {
        var silences = new[]
        {
            new SilenceInterval(10, 20),
            new SilenceInterval(15, 25), // overlaps the first
        };
        var result = SilenceParser.InvertToSpeech(silences, fileDurationSeconds: 60);
        Assert.Equal(2, result.Count);
        Assert.Equal((0.0, 10.0), result[0]);
        Assert.Equal((25.0, 60.0), result[1]);
    }

    [Fact]
    public void InvertToSpeech_DropsSpansBelowMinDuration()
    {
        // 0.5s gap between adjacent silences — below the 1s minimum, should be dropped.
        var silences = new[]
        {
            new SilenceInterval(10, 20),
            new SilenceInterval(20.5, 30),
        };
        var result = SilenceParser.InvertToSpeech(silences,
            fileDurationSeconds: 60, minSpeechDurationSeconds: 1.0);
        Assert.Equal(2, result.Count);
        Assert.Equal((0.0, 10.0), result[0]);
        Assert.Equal((30.0, 60.0), result[1]);
    }

    [Fact]
    public void InvertToSpeech_UnorderedSilencesAreSorted()
    {
        var silences = new[]
        {
            new SilenceInterval(50, 55),
            new SilenceInterval(20, 30),
        };
        var result = SilenceParser.InvertToSpeech(silences, fileDurationSeconds: 60);
        Assert.Equal(3, result.Count);
        Assert.Equal((0.0, 20.0), result[0]);
        Assert.Equal((30.0, 50.0), result[1]);
        Assert.Equal((55.0, 60.0), result[2]);
    }
}
