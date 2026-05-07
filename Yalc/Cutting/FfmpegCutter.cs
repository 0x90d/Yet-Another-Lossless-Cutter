using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YetAnotherLosslessCutter.Cutting;

/// <summary>
/// Runs ffmpeg to cut a <see cref="VideoSegment"/> via stream-copy. Handles:
///   * Whole-file shortcut (if cut spans the entire source, just byte-copy)
///   * .ts container quirk (some .ts files are MP4-in-TS — retry as .mp4 on failure)
///   * Cancellation (kills the ffmpeg process tree)
///   * Structured progress via -progress pipe:1 (no fragile stderr regex)
/// </summary>
public sealed class FfmpegCutter
{
    private readonly Settings _settings;

    public FfmpegCutter(Settings settings) { _settings = settings; }

    public Task CutAsync(VideoSegment segment,
        IProgress<double>? progress = null,
        ProcessPriorityClass priority = ProcessPriorityClass.Normal,
        CancellationToken ct = default)
    {
        var output = segment.ComputeOutputFile(_settings);
        return CutToPathAsync(segment, output, priority, progress, ct);
    }

    private async Task CutToPathAsync(VideoSegment segment, string outputPath,
        ProcessPriorityClass priority, IProgress<double>? progress, CancellationToken ct)
    {
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        // Whole-file cut: skip ffmpeg entirely, byte-copy. Faster, simpler, no
        // transcode. Tolerate up to 100ms drift on each end so manually-dragged
        // handles that landed "visually" at the start/end of the file (but not
        // exactly tick-equal) still take the fast path. 100ms is below typical
        // user-perceptible cut precision and well above floating-point drift.
        var startSlack = TimeSpan.FromMilliseconds(100);
        if (segment.CutFrom <= startSlack &&
            segment.MaxDuration - segment.CutTo <= startSlack)
        {
            await FileCopyWithProgress(segment.SourceFile, outputPath, progress, ct);
            return;
        }

        var ffmpeg = FfmpegLocator.FfmpegPath
            ?? throw new FfmpegException(-1, "(not started)",
                "ffmpeg.exe not found. Searched:\n  " +
                string.Join("\n  ", FfmpegLocator.FfmpegSearchedPaths));

        var args = BuildCutArgs(segment, outputPath);
        var stderr = new StringBuilder();

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var p = new Process { StartInfo = psi };

        var stdoutDone = new TaskCompletionSource();
        var stderrDone = new TaskCompletionSource();

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) { stdoutDone.TrySetResult(); return; }
            ParseProgressLine(e.Data, segment.CutDuration, progress);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) { stderrDone.TrySetResult(); return; }
            stderr.AppendLine(e.Data);
        };

        p.Start();
        try { p.PriorityClass = priority; } catch { /* may throw on non-Windows or due to permissions; non-fatal */ }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
        {
            await p.WaitForExitAsync(CancellationToken.None);
        }
        await Task.WhenAll(stdoutDone.Task, stderrDone.Task);

        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

        if (p.ExitCode != 0)
        {
            var commandLog = ffmpeg + " " + string.Join(' ', args);

            // .ts container retry: occasionally a .ts file is actually MP4 with a .ts extension.
            // ffmpeg refuses; renaming the OUTPUT to .mp4 lets it pick a compatible muxer.
            var srcExt = Path.GetExtension(segment.SourceFile);
            var outExt = Path.GetExtension(outputPath);
            if (srcExt.Equals(".ts", StringComparison.OrdinalIgnoreCase) &&
                !outExt.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                var mp4Output = Path.ChangeExtension(outputPath, ".mp4");
                await CutToPathAsync(segment, mp4Output, priority, progress, ct);
                return;
            }

            throw new FfmpegException(p.ExitCode, commandLog, stderr.ToString());
        }

        progress?.Report(1.0);
    }

    private List<string> BuildCutArgs(VideoSegment segment, string outputPath)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-progress", "pipe:1",
            // -ss BEFORE -i: fast input seek (keyframe). With -c copy we keep keyframe alignment;
            // -avoid_negative_ts make_zero shifts timestamps so the output starts at 0.
            "-ss", FormatTime(segment.CutFrom),
            "-i", LongPath(segment.SourceFile),
            "-t", FormatTime(segment.CutDuration),
            "-avoid_negative_ts", "make_zero",
        };

        if (_settings.RemoveAudio)
        {
            args.Add("-an");
        }
        else
        {
            args.Add("-c:a"); args.Add("copy");
        }

        args.Add("-c:v"); args.Add("copy");
        args.Add("-c:s"); args.Add("copy");

        // FIX vs WPF original: that code did `if (!IncludeAllStreams) -map 0` which is inverted.
        // -map 0 means "include every stream from input 0". When the user toggles
        // "Include All Streams" ON, that's exactly what they want.
        if (_settings.IncludeAllStreams)
        {
            args.Add("-map"); args.Add("0");
        }

        args.Add("-map_metadata"); args.Add("0");
        args.Add("-ignore_unknown");

        args.Add("-y");
        args.Add(LongPath(outputPath));
        return args;
    }

    private static void ParseProgressLine(string line, TimeSpan duration, IProgress<double>? progress)
    {
        // -progress pipe:1 emits key=value lines. We want out_time_us (microseconds, unambiguous).
        if (progress == null) return;
        var eq = line.IndexOf('=');
        if (eq <= 0) return;
        var key = line.AsSpan(0, eq);
        var value = line.AsSpan(eq + 1);
        if (!key.SequenceEqual("out_time_us")) return;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)) return;
        var elapsedSec = us / 1_000_000.0;
        var pct = duration.TotalSeconds > 0 ? elapsedSec / duration.TotalSeconds : 0;
        progress.Report(Math.Clamp(pct, 0, 1));
    }

    /// <summary>
    /// Concat-merges already-cut segments into a single output. Uses ffmpeg's concat demuxer,
    /// which requires all inputs to share codec parameters — fine for stream-copy outputs.
    /// </summary>
    public static async Task MergeAsync(string outputPath, IReadOnlyList<VideoSegment> segments,
        Settings settings, CancellationToken ct = default)
    {
        var ffmpeg = FfmpegLocator.FfmpegPath
            ?? throw new FfmpegException(-1, "(not started)",
                "ffmpeg.exe not found. Searched:\n  " +
                string.Join("\n  ", FfmpegLocator.FfmpegSearchedPaths));

        var manifest = new StringBuilder();
        foreach (var seg in segments)
        {
            var p = seg.ComputeOutputFile(settings);
            if (!File.Exists(p)) continue;
            // concat demuxer: lines look like  file '/abs/path.mp4'
            // Single quotes inside paths must be backslash-escaped.
            manifest.Append("file '").Append(p.Replace("'", @"\'")).Append("'\n");
        }

        var manifestPath = Path.Combine(Path.GetTempPath(), $"yalc_concat_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(manifestPath, manifest.ToString(), ct);

        try
        {
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("concat");
            psi.ArgumentList.Add("-safe"); psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(manifestPath);
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-map_metadata"); psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add(LongPath(outputPath));

            using var p = Process.Start(psi)!;
            var stderrTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
            using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
            {
                await p.WaitForExitAsync(CancellationToken.None);
            }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (p.ExitCode != 0)
            {
                var stderr = await stderrTask;
                throw new FfmpegException(p.ExitCode, "ffmpeg -f concat ... " + outputPath, stderr);
            }
        }
        finally
        {
            try { File.Delete(manifestPath); } catch { }
        }
    }

    private static async Task FileCopyWithProgress(string source, string dest,
        IProgress<double>? progress, CancellationToken ct)
    {
        var info = new FileInfo(source);
        var total = info.Length;
        const int bufferSize = 1024 * 1024;
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize, useAsync: true);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize, useAsync: true);
        var buf = new byte[bufferSize];
        long copied = 0;
        int n;
        while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            copied += n;
            if (total > 0) progress?.Report((double)copied / total);
        }
        progress?.Report(1.0);
    }

    private static string FormatTime(TimeSpan t) =>
        t.ToString("hh\\:mm\\:ss\\.fff", CultureInfo.InvariantCulture);

    /// <summary>Apply Windows long-path prefix to bypass MAX_PATH=260, when applicable.</summary>
    private static string LongPath(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return path;
        if (path.StartsWith(@"\\?\")) return path;
        if (path.StartsWith(@"\\")) return path;   // UNC — leave alone
        if (path.Length < 240) return path;        // headroom; only prefix when actually needed
        return @"\\?\" + path;
    }
}
