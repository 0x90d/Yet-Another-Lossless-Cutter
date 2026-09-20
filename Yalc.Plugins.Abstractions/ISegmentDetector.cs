using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace YetAnotherLosslessCutter.Plugins;

/// <summary>
/// Adds a button to the main window's action row that scans the loaded file and
/// proposes segments. Behaves like the built-in "↓ silence" detector: existing
/// segments are replaced (after confirmation), the result is a single undo step,
/// clicking the button again while it runs cancels, and switching to another file
/// cancels too.
/// </summary>
public interface ISegmentDetector
{
    /// <summary>Button caption, e.g. "↓ silence".</summary>
    string ButtonLabel { get; }

    string ToolTip { get; }

    /// <summary>
    /// Scan <see cref="SegmentDetectorContext.SourceFile"/>. Runs on the UI thread up to
    /// its first await — push heavy work onto the thread pool. <paramref name="status"/>
    /// writes to the status bar; <paramref name="ct"/> fires on cancel or file change.
    /// </summary>
    Task<SegmentDetectorResult> DetectAsync(
        SegmentDetectorContext ctx, IProgress<string> status, CancellationToken ct);

    /// <summary>
    /// Segments this detector already knows for <paramref name="sourceFile"/> — e.g. from
    /// an earlier batch scan. Applied automatically when the file loads, as one undo step.
    /// Runs on the UI thread during file load, so it must be cheap and must not do I/O
    /// beyond a cache lookup. Null (the default) means "nothing known, don't touch the
    /// timeline"; a result with no segments means "scanned, found nothing".
    /// </summary>
    SegmentDetectorResult? GetCachedSegments(string sourceFile) => null;
}

public sealed class SegmentDetectorContext
{
    public string SourceFile { get; init; } = "";
    public double DurationSeconds { get; init; }
}

public sealed class SegmentDetectorResult
{
    public IReadOnlyList<DetectedSegment> Segments { get; init; } = Array.Empty<DetectedSegment>();

    /// <summary>One line for the status bar once the scan finishes.</summary>
    public string Summary { get; init; } = "";
}

public readonly record struct DetectedSegment(double StartSeconds, double EndSeconds);
