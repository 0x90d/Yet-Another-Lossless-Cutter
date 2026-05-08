using YetAnotherLosslessCutter.Navigation;
using Xunit;

namespace YetAnotherLosslessCutter.Tests;

/// <summary>
/// Tests for the keyframe-extraction parser and the binary-search navigation
/// over the resulting array. The I/O wrapper (subprocess ffprobe) is not tested
/// here — only the pure logic.
/// </summary>
public class KeyframeIndexTests
{
    // ---- Parser ----

    [Fact]
    public void Parse_FilterToKeyframes()
    {
        var lines = new[]
        {
            "0.000000,K__",
            "0.040000,___",
            "0.080000,___",
            "1.000000,K__",
            "1.040000,___",
        };
        var result = KeyframeParser.Parse(lines);
        Assert.Equal(new[] { 0.0, 1.0 }, result);
    }

    [Fact]
    public void Parse_EmptyInput()
    {
        Assert.Empty(KeyframeParser.Parse(System.Array.Empty<string>()));
    }

    [Fact]
    public void Parse_SkipsMalformedLines()
    {
        var lines = new[]
        {
            "",
            "garbage",
            "1.000,K__",
            ",K__",       // missing pts
            "not-a-number,K__",
            "2.000,K__",
        };
        var result = KeyframeParser.Parse(lines);
        Assert.Equal(new[] { 1.0, 2.0 }, result);
    }

    [Fact]
    public void Parse_SortsResult()
    {
        // ffprobe normally emits in order, but defensive sort lets the consumer
        // assume a sorted array for binary search.
        var lines = new[]
        {
            "5.0,K__",
            "1.0,K__",
            "3.0,K__",
        };
        Assert.Equal(new[] { 1.0, 3.0, 5.0 }, KeyframeParser.Parse(lines));
    }

    [Fact]
    public void Parse_FlagsWithoutKMarkerSkipped()
    {
        // P-frames, B-frames, etc.
        var lines = new[]
        {
            "0.0,___",
            "0.5,P__",
            "1.0,K__",
        };
        Assert.Equal(new[] { 1.0 }, KeyframeParser.Parse(lines));
    }

    // ---- Index navigation ----

    private static KeyframeIndex BuildIndex(params double[] times)
    {
        var idx = new KeyframeIndex();
        idx.Load(times);
        return idx;
    }

    [Fact]
    public void NextAfter_EmptyIndex_ReturnsNull()
    {
        var idx = new KeyframeIndex();
        Assert.Null(idx.NextAfter(5.0));
    }

    [Fact]
    public void PrevBefore_EmptyIndex_ReturnsNull()
    {
        var idx = new KeyframeIndex();
        Assert.Null(idx.PrevBefore(5.0));
    }

    [Fact]
    public void NextAfter_BetweenKeyframes_ReturnsNext()
    {
        var idx = BuildIndex(0, 1, 2, 3);
        Assert.Equal(2.0, idx.NextAfter(1.5));
    }

    [Fact]
    public void NextAfter_ExactMatch_ReturnsTheFollowing()
    {
        // Don't return the keyframe you're sitting on — that'd "stick".
        var idx = BuildIndex(0, 1, 2, 3);
        Assert.Equal(2.0, idx.NextAfter(1.0));
    }

    [Fact]
    public void NextAfter_BeforeFirst_ReturnsFirst()
    {
        var idx = BuildIndex(1, 2, 3);
        Assert.Equal(1.0, idx.NextAfter(0.5));
    }

    [Fact]
    public void NextAfter_AtOrPastLast_ReturnsNull()
    {
        var idx = BuildIndex(1, 2, 3);
        Assert.Null(idx.NextAfter(3.0));
        Assert.Null(idx.NextAfter(10.0));
    }

    [Fact]
    public void PrevBefore_BetweenKeyframes_ReturnsPrev()
    {
        var idx = BuildIndex(0, 1, 2, 3);
        Assert.Equal(1.0, idx.PrevBefore(1.5));
    }

    [Fact]
    public void PrevBefore_ExactMatch_ReturnsThePreceding()
    {
        var idx = BuildIndex(0, 1, 2, 3);
        Assert.Equal(1.0, idx.PrevBefore(2.0));
    }

    [Fact]
    public void PrevBefore_AtOrBeforeFirst_ReturnsNull()
    {
        var idx = BuildIndex(1, 2, 3);
        Assert.Null(idx.PrevBefore(1.0));
        Assert.Null(idx.PrevBefore(0.5));
    }

    [Fact]
    public void PrevBefore_PastLast_ReturnsLast()
    {
        var idx = BuildIndex(1, 2, 3);
        Assert.Equal(3.0, idx.PrevBefore(10.0));
    }

    [Fact]
    public void Load_DefensivelySorts()
    {
        var idx = BuildIndex(5, 1, 3, 2, 4);
        Assert.Equal(2.0, idx.NextAfter(1.0));
        Assert.Equal(4.0, idx.PrevBefore(5.0));
    }

    [Fact]
    public void Clear_DropsIndex()
    {
        var idx = BuildIndex(1, 2, 3);
        idx.Clear();
        Assert.False(idx.IsLoaded);
        Assert.Equal(0, idx.Count);
        Assert.Null(idx.NextAfter(0.0));
    }
}
