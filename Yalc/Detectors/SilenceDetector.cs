using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using YetAnotherLosslessCutter.Cutting;

namespace YetAnotherLosslessCutter.Detectors;

/// <summary>
/// Runs ffmpeg's <c>silencedetect</c> filter over a video file and parses the result
/// into a list of silent intervals. The parsing itself is in <see cref="SilenceParser"/>;
/// this class only handles the subprocess + cancellation + progress.
/// </summary>
public sealed class SilenceDetector
{
    /// <summary>
    /// Detect silent intervals in the audio stream of <paramref name="videoPath"/>.
    /// </summary>
    /// <param name="videoPath">Source file. Must contain an audio stream.</param>
    /// <param name="thresholdDb">Below this dBFS counts as silent (negative; -30 typical).</param>
    /// <param name="minSilenceSeconds">Minimum silence run length to count as a cut point.</param>
    /// <param name="fileDurationSeconds">Total file duration; used to close any trailing silence.</param>
    /// <param name="progress">Optional 0..1 progress sink (driven by ffmpeg's time= lines).</param>
    public async Task<IReadOnlyList<SilenceInterval>> DetectAsync(
        string videoPath,
        double thresholdDb,
        double minSilenceSeconds,
        double fileDurationSeconds,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("Source file not found.", videoPath);

        var ffmpeg = FfmpegLocator.FfmpegPath;
        if (string.IsNullOrEmpty(ffmpeg))
            throw new InvalidOperationException(
                "ffmpeg not available. Install via Settings → Reinstall native components.");

        var filter = $"silencedetect=n={thresholdDb.ToString("R", CultureInfo.InvariantCulture)}dB" +
                     $":d={minSilenceSeconds.ToString("R", CultureInfo.InvariantCulture)}";

        var args = new[]
        {
            "-hide_banner",
            "-i", videoPath,
            "-vn",                      // skip video — silencedetect operates on audio only
            "-af", filter,
            "-f", "null", "-"
        };

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };

        var stderrLines = new List<string>();
        var stdoutDone = new TaskCompletionSource();
        var stderrDone = new TaskCompletionSource();

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) stdoutDone.TrySetResult();
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) { stderrDone.TrySetResult(); return; }
            stderrLines.Add(e.Data);
            if (progress != null && fileDurationSeconds > 0)
                ReportProgress(e.Data, fileDurationSeconds, progress);
        };

        p.Start();
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
            var tail = stderrLines.Count > 6
                ? string.Join('\n', stderrLines.GetRange(stderrLines.Count - 6, 6))
                : string.Join('\n', stderrLines);
            throw new InvalidOperationException(
                $"ffmpeg silencedetect exited with code {p.ExitCode}:\n{tail}");
        }

        progress?.Report(1.0);
        return SilenceParser.Parse(stderrLines, fileDurationSeconds);
    }

    /// <summary>
    /// Parse ffmpeg's periodic "time=hh:mm:ss.ms" stderr lines and report a 0..1
    /// fraction. Best-effort — silencedetect runs at decode speed so the host shouldn't
    /// rely on this for tight progress bars, but it's enough to keep a long-file
    /// scan from looking frozen.
    /// </summary>
    private static void ReportProgress(string line, double duration, IProgress<double> progress)
    {
        var idx = line.IndexOf("time=", StringComparison.Ordinal);
        if (idx < 0) return;
        var rest = line.AsSpan(idx + "time=".Length);

        // hh:mm:ss.ms
        var end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] is ':' or '.')) end++;
        var slice = rest[..end];

        // Split on ':' manually — ReadOnlySpan doesn't have a Split.
        var firstColon = slice.IndexOf(':');
        if (firstColon < 0) return;
        var secondColon = slice[(firstColon + 1)..].IndexOf(':');
        if (secondColon < 0) return;

        if (!double.TryParse(slice[..firstColon], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return;
        if (!double.TryParse(slice.Slice(firstColon + 1, secondColon), NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return;
        if (!double.TryParse(slice[(firstColon + 1 + secondColon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return;

        var seconds = h * 3600 + m * 60 + s;
        progress.Report(Math.Clamp(seconds / duration, 0.0, 0.99));
    }
}
