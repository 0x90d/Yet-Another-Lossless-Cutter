using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using YetAnotherLosslessCutter.Cutting;

namespace YetAnotherLosslessCutter.Navigation;

/// <summary>
/// In-memory list of keyframe timestamps for the currently-loaded file. Populated
/// in the background by an ffprobe scan; callers query <see cref="NextAfter"/> /
/// <see cref="PrevBefore"/> for keyframe-precise seeks. While the scan is running
/// the index is empty and callers should fall back to mpv's approximate
/// seek-with-keyframes mode.
/// </summary>
public sealed class KeyframeIndex
{
    private double[] _times = Array.Empty<double>();

    /// <summary>True once a successful scan has populated the index.</summary>
    public bool IsLoaded { get; private set; }

    public int Count => _times.Length;

    /// <summary>Returns the smallest keyframe time strictly greater than <paramref name="t"/>, or null.</summary>
    public double? NextAfter(double t)
    {
        if (_times.Length == 0) return null;
        var idx = Array.BinarySearch(_times, t);
        if (idx < 0) idx = ~idx;     // insertion point = first element > t
        else idx++;                  // exact match — advance past it
        return idx < _times.Length ? _times[idx] : null;
    }

    /// <summary>Returns the largest keyframe time strictly less than <paramref name="t"/>, or null.</summary>
    public double? PrevBefore(double t)
    {
        if (_times.Length == 0) return null;
        var idx = Array.BinarySearch(_times, t);
        if (idx < 0) idx = ~idx - 1; // insertion point - 1 = last element < t
        else idx--;                  // exact match — back off one
        return idx >= 0 ? _times[idx] : null;
    }

    /// <summary>
    /// Drop the current index. Call from the host on file change so a stale set
    /// doesn't hang around while the new scan runs.
    /// </summary>
    public void Clear()
    {
        _times = Array.Empty<double>();
        IsLoaded = false;
    }

    /// <summary>
    /// Replace the index with a precomputed timestamp array. Used by tests and by
    /// (future) on-disk caches that skip the ffprobe scan. Defensively re-sorts.
    /// </summary>
    public void Load(IReadOnlyList<double> sortedTimes)
    {
        var arr = new double[sortedTimes.Count];
        for (var i = 0; i < sortedTimes.Count; i++) arr[i] = sortedTimes[i];
        Array.Sort(arr);
        _times = arr;
        IsLoaded = true;
    }

    /// <summary>
    /// Scan <paramref name="videoPath"/> with ffprobe and replace the index with the
    /// resulting keyframe timestamps. Throws if ffprobe is unavailable or the scan
    /// fails; callers can ignore the exception and fall back to approximate mode.
    /// </summary>
    public async Task LoadAsync(string videoPath, CancellationToken ct = default)
    {
        var ffprobe = FfmpegLocator.FfprobePath;
        if (string.IsNullOrEmpty(ffprobe))
            throw new InvalidOperationException("ffprobe not available");
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("Source file not found.", videoPath);

        var args = new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
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

        _times = KeyframeParser.Parse(stdoutLines);
        IsLoaded = true;
    }
}
