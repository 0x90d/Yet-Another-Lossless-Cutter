using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using YetAnotherLosslessCutter.Cutting;

namespace YetAnotherLosslessCutter.Navigation;

/// <summary>
/// Keyframe timestamps for a bounded window of the currently-loaded file, populated
/// on demand by an ffprobe scan. Callers check <see cref="Covers"/> and then query
/// <see cref="NextAfter"/> / <see cref="PrevBefore"/> for keyframe-precise seeks;
/// outside the cached window they should fall back to mpv's approximate
/// seek-with-keyframes mode and kick off a fresh <see cref="LoadWindowAsync"/>.
///
/// The window exists because ffprobe has no way to enumerate an index without
/// reading media data — <c>-show_entries packet=…</c> demuxes every packet, so
/// indexing a whole file costs one complete read of the source (a 46 GB file on a
/// network share takes ~13 minutes and saturates the link while mpv is trying to
/// seek through the same file). The only consumer is prev/next-keyframe stepping,
/// which never looks further than a few GOPs from the playhead, so a window around
/// the requested position answers every real query for a fraction of the I/O.
///
/// ffmpeg's <c>-discard nokey</c> would let the demuxer skip non-key packets, but
/// the mov demuxer desyncs its <c>ctts</c> composition-offset walk when it skips
/// samples and reports keyframe pts values that drift by one to three frames — not
/// usable for a lossless cutter, where these timestamps become cut points.
/// </summary>
public sealed class KeyframeIndex
{
    /// <summary>
    /// Seconds scanned either side of the requested position by
    /// <see cref="LoadWindowAsync"/>. Wide enough to cover many steps before the
    /// user walks off the edge, small enough that the scan stays bounded on a huge
    /// file (~90 s of an 8K 70 Mbit/s source is well under a GB, versus 46 GB for
    /// the whole thing).
    /// </summary>
    public const double WindowRadiusSeconds = 45;

    /// <summary>
    /// Times plus the range they're authoritative for, swapped in as one reference so
    /// a UI-thread reader can never see timestamps from one scan paired with the
    /// bounds of another. Null until the first successful load.
    /// </summary>
    private sealed class Snapshot(double[] times, double start, double end)
    {
        public double[] Times { get; } = times;
        public double Start { get; } = start;
        public double End { get; } = end;
    }

    private Snapshot? _snapshot;

    /// <summary>True once a successful scan has populated the index.</summary>
    public bool IsLoaded => Volatile.Read(ref _snapshot) != null;

    public int Count => Volatile.Read(ref _snapshot)?.Times.Length ?? 0;

    /// <summary>
    /// True when the cached window is authoritative at <paramref name="t"/> — i.e. every
    /// keyframe adjacent to <paramref name="t"/> was seen by the scan. A null answer from
    /// <see cref="NextAfter"/> / <see cref="PrevBefore"/> while this is true means the
    /// neighbouring keyframe lies outside the window, not that none exists.
    /// </summary>
    public bool Covers(double t)
    {
        var s = Volatile.Read(ref _snapshot);
        return s != null && t >= s.Start && t <= s.End;
    }

    /// <summary>Returns the smallest keyframe time strictly greater than <paramref name="t"/>, or null.</summary>
    public double? NextAfter(double t)
    {
        var times = Volatile.Read(ref _snapshot)?.Times;
        if (times == null || times.Length == 0) return null;
        var idx = Array.BinarySearch(times, t);
        if (idx < 0) idx = ~idx;     // insertion point = first element > t
        else idx++;                  // exact match — advance past it
        return idx < times.Length ? times[idx] : null;
    }

    /// <summary>Returns the largest keyframe time strictly less than <paramref name="t"/>, or null.</summary>
    public double? PrevBefore(double t)
    {
        var times = Volatile.Read(ref _snapshot)?.Times;
        if (times == null || times.Length == 0) return null;
        var idx = Array.BinarySearch(times, t);
        if (idx < 0) idx = ~idx - 1; // insertion point - 1 = last element < t
        else idx--;                  // exact match — back off one
        return idx >= 0 ? times[idx] : null;
    }

    /// <summary>
    /// Drop the current window. Call from the host on file change so a stale set
    /// doesn't hang around and get treated as covering the new file.
    /// </summary>
    public void Clear() => Volatile.Write(ref _snapshot, null);

    /// <summary>
    /// Replace the index with a precomputed timestamp array covering the whole file.
    /// Used by tests and by (future) on-disk caches that skip the ffprobe scan.
    /// Defensively re-sorts.
    /// </summary>
    public void Load(IReadOnlyList<double> sortedTimes)
    {
        var arr = new double[sortedTimes.Count];
        for (var i = 0; i < sortedTimes.Count; i++) arr[i] = sortedTimes[i];
        Array.Sort(arr);
        Volatile.Write(ref _snapshot,
            new Snapshot(arr, double.NegativeInfinity, double.PositiveInfinity));
    }

    /// <summary>
    /// Scan <see cref="WindowRadiusSeconds"/> either side of <paramref name="centerSeconds"/>
    /// in <paramref name="videoPath"/> and replace the index with the keyframes found there.
    /// Throws if ffprobe is unavailable or the scan fails; callers can ignore the
    /// exception and fall back to approximate mode.
    /// </summary>
    public async Task LoadWindowAsync(string videoPath, double centerSeconds,
        CancellationToken ct = default)
    {
        var ffprobe = FfmpegLocator.FfprobePath;
        if (string.IsNullOrEmpty(ffprobe))
            throw new InvalidOperationException("ffprobe not available");
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("Source file not found.", videoPath);

        // Clamp to the start of the file but keep the far edge where it was, so a
        // request near t=0 still scans a full window's worth ahead.
        var start = Math.Max(0, centerSeconds - WindowRadiusSeconds);
        var length = centerSeconds - start + WindowRadiusSeconds;

        // "START%+LENGTH" — seek to START, read until LENGTH seconds past it. ffprobe
        // seeks to the keyframe at or before START, so real coverage is a little wider
        // than requested; the bounds we record stay conservative.
        var interval = string.Create(CultureInfo.InvariantCulture, $"{start:0.###}%+{length:0.###}");

        var args = new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-read_intervals", interval,
            "-show_entries", "packet=pts_time,flags",
            "-of", "csv=p=0",
            videoPath,
        };

        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };

        var stdoutLines = new List<string>();
        var stdoutDone = new TaskCompletionSource();
        var stderrDone = new TaskCompletionSource();

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) { stdoutDone.TrySetResult(); return; }
            stdoutLines.Add(e.Data);
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) stderrDone.TrySetResult(); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
        {
            await p.WaitForExitAsync(CancellationToken.None);
        }
        await Task.WhenAll(stdoutDone.Task, stderrDone.Task);

        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe exited with code {p.ExitCode}");

        // Bound the window by the keyframes actually seen, not by what we asked for.
        // ffprobe backs up to the keyframe at or before START, and stops a little short
        // of START+LENGTH — so the requested span overstates coverage at both ends.
        // Claiming the wider range would let PrevBefore confidently return a keyframe
        // two GOPs back when the real one sits in the unscanned sliver past the edge.
        // Between the first and last keyframe emitted, every keyframe is present.
        var times = KeyframeParser.Parse(stdoutLines);
        Volatile.Write(ref _snapshot, times.Length == 0
            ? null
            : new Snapshot(times, times[0], times[^1]));
    }
}
