using System;
using System.Text;

namespace YetAnotherLosslessCutter.Cutting;

/// <summary>
/// Codec / dimension / bitrate summary for a media file. Populated by
/// <see cref="MediaInfoProbe"/>. All fields nullable / zero-defaulted to handle
/// files where ffprobe couldn't determine some values.
/// </summary>
public sealed class MediaInfo
{
    public string? VideoCodec { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public long VideoBitrate { get; init; }
    public bool VideoBitrateEstimated { get; init; }
    public long OverallBitrate { get; init; }

    /// <summary>
    /// Authoritative file duration, from ffprobe's <c>format.duration</c>. mpv can
    /// mis-report this for streaming containers (.ts) where audio and video stream
    /// durations diverge — prefer this when available.
    /// </summary>
    public TimeSpan Duration { get; init; }

    public bool HasAudio { get; init; }
    public string? AudioCodec { get; init; }
    public long AudioBitrate { get; init; }
    public int AudioSampleRate { get; init; }
    public string? AudioChannelLayout { get; init; }

    /// <summary>
    /// Duration of the audio stream specifically. Often differs from <see cref="Duration"/>
    /// for live-recorded streams — the recorder keeps writing video after the
    /// audio source drops, leaving an audio-truncated file. mpv re-scans for audio
    /// on every seek past this point, which manifests as laggy scrubbing past the
    /// audio EOF. Zero when the file has no audio at all.
    /// </summary>
    public TimeSpan AudioDuration { get; init; }

    public static string FormatBitrate(long bitrate) =>
        bitrate <= 0 ? "N/A" : $"{bitrate / 1000} kb/s";

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Resolution: {Width}x{Height}");
        sb.AppendLine($"Video Codec: {VideoCodec ?? "N/A"}");
        sb.AppendLine($"FPS: {Fps:F2}");
        sb.AppendLine($"{(VideoBitrateEstimated ? "Video Bitrate (est.)" : "Video Bitrate")}: {FormatBitrate(VideoBitrate)}");
        sb.AppendLine($"Overall Bitrate: {FormatBitrate(OverallBitrate)}");
        sb.AppendLine();
        if (HasAudio)
        {
            sb.AppendLine($"Audio Codec: {AudioCodec ?? "N/A"}");
            sb.AppendLine($"Audio Bitrate: {FormatBitrate(AudioBitrate)}");
            sb.AppendLine($"Sample Rate: {(AudioSampleRate > 0 ? $"{AudioSampleRate} Hz" : "N/A")}");
            sb.AppendLine($"Channels: {AudioChannelLayout ?? "N/A"}");
        }
        else
        {
            sb.AppendLine("Audio: None");
        }
        return sb.ToString();
    }
}
