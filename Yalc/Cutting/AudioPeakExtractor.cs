using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using YetAnotherLosslessCutter.Controls;

namespace YetAnotherLosslessCutter.Cutting;

/// <summary>
/// Extracts peak-amplitude buckets from a file's audio by piping ffmpeg's mono f32le
/// output and bucketing in-process. Sidesteps the FFmpeg.AutoGen audio decode +
/// resample dance — ffmpeg already does both perfectly when invoked as a child
/// process, and parsing raw little-endian floats is trivial.
/// </summary>
public static class AudioPeakExtractor
{
    /// <summary>
    /// Decode the file's audio at <paramref name="sampleRate"/> Hz mono and produce
    /// <paramref name="bucketCount"/> peak buckets covering the full duration.
    /// Returns null if ffmpeg isn't available, the duration is unknown, or the file
    /// has no audio. Cancellation kills the ffmpeg process.
    /// </summary>
    public static async Task<AudioPeaks?> ExtractAsync(
        string path,
        double durationSec,
        int bucketCount = 2000,
        int sampleRate = 8000,
        CancellationToken ct = default)
    {
        if (durationSec <= 0 || bucketCount < 16) return null;
        var ffmpeg = FfmpegLocator.FfmpegPath;
        if (ffmpeg == null) return null;

        // Samples-per-bucket. Capped to a reasonable minimum so very short files
        // still produce a usable waveform; if the file is too short to fill all
        // buckets at the chosen sample rate, lower the rate adaptively.
        var totalSamples = (long)Math.Round(sampleRate * durationSec);
        if (totalSamples < bucketCount * 4)
        {
            // ~4 samples per bucket minimum — keep peaks meaningful.
            sampleRate = (int)Math.Max(1000, Math.Round(bucketCount * 4 / Math.Max(0.001, durationSec)));
            totalSamples = (long)Math.Round(sampleRate * durationSec);
        }
        var samplesPerBucket = (int)Math.Max(1, totalSamples / bucketCount);

        var args = string.Format(CultureInfo.InvariantCulture,
            "-hide_banner -loglevel error -i \"{0}\" -vn -ac 1 -ar {1} -f f32le -",
            path, sampleRate);

        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc == null) return null;

            // Drain stderr so a chatty ffmpeg never blocks the pipe. We only really
            // care if ffmpeg exits non-zero AND we got no samples — then there was
            // no decodable audio.
            _ = Task.Run(() => { try { proc.StandardError.ReadToEnd(); } catch { } }, ct);

            var peaks = new float[bucketCount];
            var byteBuf = new byte[samplesPerBucket * 4];
            var stream = proc.StandardOutput.BaseStream;

            for (var i = 0; i < bucketCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                var totalRead = 0;
                while (totalRead < byteBuf.Length)
                {
                    var read = await stream.ReadAsync(
                        byteBuf.AsMemory(totalRead, byteBuf.Length - totalRead), ct);
                    if (read == 0) break; // EOF
                    totalRead += read;
                }

                var actualSamples = totalRead / 4;
                if (actualSamples == 0) break;

                float max = 0;
                var span = byteBuf.AsSpan(0, actualSamples * 4);
                for (var j = 0; j < actualSamples; j++)
                {
                    var v = BitConverter.ToSingle(span.Slice(j * 4, 4));
                    if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                    var abs = v < 0 ? -v : v;
                    if (abs > max) max = abs;
                }
                peaks[i] = max > 1f ? 1f : max;
            }

            return new AudioPeaks(peaks, 0, durationSec);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (IOException)
        {
            // Pipe broke — usually means ffmpeg exited (e.g., file has no audio).
            return null;
        }
        finally
        {
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
                try { proc.Dispose(); } catch { }
            }
        }
    }
}
