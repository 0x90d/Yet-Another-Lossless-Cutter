using System;
using YetAnotherLosslessCutter;
using Xunit;

namespace YetAnotherLosslessCutter.Tests;

/// <summary>
/// Tests for the output-filename template renderer. Locks in the supported tokens,
/// the case-insensitive matching, the unknown-token passthrough, and the malformed
/// brace handling.
/// </summary>
public class OutputTemplateTests
{
    private static OutputTemplate.Context Sample(
        string name = "myfile",
        string ext = ".mp4",
        TimeSpan? start = null,
        TimeSpan? end = null,
        DateTime? now = null,
        int index = 1) => new(
            name,
            ext,
            // Construct via integer ms to avoid double-precision drift in {start} format.
            start ?? new TimeSpan(0, 0, 1, 23, 456),
            end   ?? new TimeSpan(0, 0, 2, 34, 789),
            now ?? new DateTime(2026, 5, 8, 14, 30, 45, DateTimeKind.Local),
            index);

    [Fact]
    public void DefaultTemplate_MatchesLegacyHardcodedFormat()
    {
        // The pre-templating code produced "{stem}-{HH.mm.ss.fff}-{HH.mm.ss.fff}{ext}".
        // The default template must still produce that exact shape so existing users
        // see no change.
        var result = OutputTemplate.Render(OutputTemplate.Default, Sample());
        Assert.Equal("myfile-00.01.23.456-00.02.34.789.mp4", result);
    }

    [Fact]
    public void EmptyTemplate_FallsBackToDefault()
    {
        var result = OutputTemplate.Render("", Sample());
        Assert.Equal("myfile-00.01.23.456-00.02.34.789.mp4", result);
    }

    [Fact]
    public void NullTemplate_FallsBackToDefault()
    {
        var result = OutputTemplate.Render(null, Sample());
        Assert.Equal("myfile-00.01.23.456-00.02.34.789.mp4", result);
    }

    [Fact]
    public void NameAndExt_AreSubstituted()
    {
        var result = OutputTemplate.Render("{name}{ext}", Sample());
        Assert.Equal("myfile.mp4", result);
    }

    [Fact]
    public void Duration_IsEndMinusStart()
    {
        var ctx = Sample(start: TimeSpan.FromSeconds(10), end: TimeSpan.FromSeconds(40));
        var result = OutputTemplate.Render("{duration}", ctx);
        Assert.Equal("00.00.30.000", result);
    }

    [Fact]
    public void DateTimeTokens_UseProvidedNow()
    {
        var ctx = Sample(now: new DateTime(2030, 1, 15, 9, 5, 7, DateTimeKind.Local));
        Assert.Equal("2030-01-15", OutputTemplate.Render("{date}", ctx));
        Assert.Equal("09-05-07", OutputTemplate.Render("{time}", ctx));
        Assert.Equal("2030-01-15_09-05-07", OutputTemplate.Render("{datetime}", ctx));
    }

    [Fact]
    public void Index_IsZeroPaddedThreeDigits()
    {
        Assert.Equal("001", OutputTemplate.Render("{index}", Sample(index: 1)));
        Assert.Equal("042", OutputTemplate.Render("{index}", Sample(index: 42)));
        Assert.Equal("999", OutputTemplate.Render("{index}", Sample(index: 999)));
    }

    [Fact]
    public void TokensAreCaseInsensitive()
    {
        var result = OutputTemplate.Render("{NAME}-{Start}{ext}", Sample());
        Assert.Equal("myfile-00.01.23.456.mp4", result);
    }

    [Fact]
    public void UnknownToken_LeftInPlace()
    {
        // Hints to the user that they typo'd the token name, rather than silently
        // producing a broken filename.
        var result = OutputTemplate.Render("{name}-{nonsense}{ext}", Sample());
        Assert.Equal("myfile-{nonsense}.mp4", result);
    }

    [Fact]
    public void UnclosedBrace_PassedThrough()
    {
        var result = OutputTemplate.Render("{name}-{start", Sample());
        Assert.Equal("myfile-{start", result);
    }

    [Fact]
    public void LiteralCharsBetweenTokens_Preserved()
    {
        var result = OutputTemplate.Render("[cut] {name} @ {start}{ext}", Sample());
        Assert.Equal("[cut] myfile @ 00.01.23.456.mp4", result);
    }

    [Fact]
    public void NegativeStartTime_ClampsToZero()
    {
        // Defensive — a misuse should not produce a "-00.00.01.000" filename.
        var ctx = Sample(start: TimeSpan.FromSeconds(-5));
        Assert.Equal("00.00.00.000", OutputTemplate.Render("{start}", ctx));
    }

    // ---- Custom resolver (plugin-fed tokens) ----

    [Fact]
    public void CustomResolver_ProvidesUnknownToken()
    {
        var result = OutputTemplate.Render("{name}-{model}{ext}", Sample(),
            t => t == "model" ? "alice" : null);
        Assert.Equal("myfile-alice.mp4", result);
    }

    [Fact]
    public void CustomResolver_DoesNotOverrideBuiltInTokens()
    {
        // Built-in tokens take priority — a plugin can't shadow {name}.
        var result = OutputTemplate.Render("{name}", Sample(),
            t => "should-not-appear");
        Assert.Equal("myfile", result);
    }

    [Fact]
    public void CustomResolver_NullReturn_LeavesTokenInPlace()
    {
        var result = OutputTemplate.Render("{nope}", Sample(), t => null);
        Assert.Equal("{nope}", result);
    }

    [Fact]
    public void CustomResolver_EmptyReturn_LeavesTokenInPlace()
    {
        // Empty == "I don't have a value, defer" so a plugin returning "" is treated
        // the same as null. Avoids accidentally substituting whitespace.
        var result = OutputTemplate.Render("{nope}", Sample(), t => "");
        Assert.Equal("{nope}", result);
    }

    // ---- SanitizeFileName ----

    [Fact]
    public void SanitizeFileName_PassesCleanNamesUnchanged()
    {
        Assert.Equal("myfile-00.01.23.456-00.02.34.789.mp4",
            OutputTemplate.SanitizeFileName("myfile-00.01.23.456-00.02.34.789.mp4"));
    }

    [Fact]
    public void SanitizeFileName_ReplacesInvalidChars()
    {
        // Pick chars that are invalid on every platform: '<', '>', '|'. Avoid
        // platform-specific ones (':' is valid on Linux) so the test runs the same
        // everywhere.
        var dirty = "my<bad>file|name.mp4";
        var clean = OutputTemplate.SanitizeFileName(dirty);
        Assert.Equal("my_bad_file_name.mp4", clean);
    }
}
