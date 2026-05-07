using System;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace YetAnotherLosslessCutter.Cutting;

/// <summary>
/// Probes a media file for codec / dimensions / bitrate / duration via the
/// FFmpeg shared libraries (in-process), filling a <see cref="MediaInfo"/>.
///
/// Replaces the previous <c>ffprobe.exe</c> spawn — same observable behavior
/// (returns null on probe failure or when libs are missing) but no subprocess
/// or JSON parse on every file load.
/// </summary>
public static class MediaInfoProbe
{
    public static Task<MediaInfo?> ProbeAsync(string filePath, CancellationToken ct = default)
        => Task.Run<MediaInfo?>(() => ProbeSync(filePath, ct), ct);

    private static unsafe MediaInfo? ProbeSync(string filePath, CancellationToken ct)
    {
        if (!NativeFfmpegLoader.Available) return null;
        ct.ThrowIfCancellationRequested();

        AVFormatContext* fmt = null;
        try
        {
            int err = ffmpeg.avformat_open_input(&fmt, filePath, null, null);
            if (err < 0) return null;
            err = ffmpeg.avformat_find_stream_info(fmt, null);
            if (err < 0) return null;

            string? vCodec = null, aCodec = null, aLayout = null;
            int width = 0, height = 0, sampleRate = 0;
            double fps = 0;
            long vBitrate = 0, aBitrate = 0, overallBitrate = 0;
            bool vBitrateEstimated = false, hasAudio = false;
            var audioDuration = TimeSpan.Zero;

            for (var i = 0; i < fmt->nb_streams; i++)
            {
                var stream = fmt->streams[i];
                var par = stream->codecpar;
                if (par == null) continue;

                if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && vCodec == null)
                {
                    vCodec = NameOf(par->codec_id);
                    width = par->width;
                    height = par->height;
                    fps = ffmpeg.av_q2d(stream->avg_frame_rate);
                    if (fps <= 0) fps = ffmpeg.av_q2d(stream->r_frame_rate);
                    vBitrate = par->bit_rate;
                }
                else if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && aCodec == null)
                {
                    hasAudio = true;
                    aCodec = NameOf(par->codec_id);
                    aBitrate = par->bit_rate;
                    sampleRate = par->sample_rate;

                    // Per-stream duration in time_base units. AV_NOPTS_VALUE means
                    // unknown, in which case we leave audioDuration at zero and
                    // the caller falls back to the format duration.
                    if (stream->duration != ffmpeg.AV_NOPTS_VALUE && stream->duration > 0)
                    {
                        audioDuration = TimeSpan.FromSeconds(
                            stream->duration * ffmpeg.av_q2d(stream->time_base));
                    }

                    // ch_layout.nb_channels gives a usable channel count; we render
                    // a friendly label rather than parsing the channel mask.
                    var nbChannels = par->ch_layout.nb_channels;
                    aLayout = nbChannels switch
                    {
                        1 => "mono",
                        2 => "stereo",
                        6 => "5.1",
                        8 => "7.1",
                        _ => nbChannels > 0 ? $"{nbChannels} ch" : null,
                    };
                }
            }

            overallBitrate = fmt->bit_rate;
            if (vBitrate <= 0 && overallBitrate > 0)
            {
                vBitrate = overallBitrate - Math.Max(0, aBitrate);
                vBitrateEstimated = true;
            }

            var duration = fmt->duration > 0
                ? TimeSpan.FromSeconds(fmt->duration / (double)ffmpeg.AV_TIME_BASE)
                : TimeSpan.Zero;

            return new MediaInfo
            {
                VideoCodec = vCodec,
                Width = width,
                Height = height,
                Fps = fps,
                VideoBitrate = vBitrate,
                VideoBitrateEstimated = vBitrateEstimated,
                OverallBitrate = overallBitrate,
                Duration = duration,
                HasAudio = hasAudio,
                AudioCodec = aCodec,
                AudioBitrate = aBitrate,
                AudioSampleRate = sampleRate,
                AudioChannelLayout = aLayout,
                AudioDuration = audioDuration,
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        }
    }

    // FFmpeg.AutoGen 8.x marshals avcodec_get_name's char* return to a managed
    // string for us — no PtrToStringAnsi dance needed.
    private static string? NameOf(AVCodecID id) => ffmpeg.avcodec_get_name(id);
}
