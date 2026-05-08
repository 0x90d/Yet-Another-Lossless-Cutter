using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FFmpeg.AutoGen;
using YetAnotherLosslessCutter.Controls;

namespace YetAnotherLosslessCutter.Cutting;

/// <summary>
/// In-process thumbnail extractor backed by the FFmpeg shared libraries via
/// <see cref="FFmpeg.AutoGen"/>. Replaces the previous mpv-based extractor +
/// per-frame ffmpeg.exe fallback with a single code path that:
///
///   • Opens the file once (avformat_open_input + find_stream_info).
///   • Per requested timestamp: <c>av_seek_frame BACKWARD</c>, flush codec,
///     decode forward until <c>pts &gt;= target</c>, then <c>sws_scale</c>
///     into a fixed BGRA tile sized for the timeline strip.
///   • Letterboxes when the source aspect doesn't match the tile aspect, so
///     thumbs render correctly without the renderer having to know aspect.
///
/// MPEG-TS reliability: the decode-forward loop tolerates a bounded run of
/// AVERROR_INVALIDDATA / EINVAL packets right after a seek (the demuxer can
/// hand us partial fragments before the next keyframe). Pattern lifted from
/// VideoDuplicateFinder's <c>VideoStreamDecoder</c> — same workaround for the
/// same family of bug.
///
/// Class is NOT marked <c>unsafe</c> at the class level — that would forbid
/// <c>await</c> in any method. Pointer-using fields and helpers carry the
/// modifier individually; the async surface stays clean.
/// </summary>
public sealed class NativeFfmpegThumbnailExtractor : IDisposable
{
    public const int TileWidth = 160;
    public const int TileHeight = 90;

    private unsafe AVFormatContext* _fmtCtx;
    private unsafe AVCodecContext* _codecCtx;
    private unsafe AVPacket* _packet;
    private unsafe AVFrame* _frame;
    // Holds the most recent successfully-decoded frame whose pts < target. If we
    // hit EOF / a packet-budget bail before reaching the target, this is what we
    // return — better than nothing, and for end-of-file timestamps it IS the
    // correct answer (the file's reported duration often exceeds the last
    // playable frame on .ts streams).
    private unsafe AVFrame* _fallbackFrame;
    private unsafe SwsContext* _sws;

    private int _videoStreamIndex = -1;
    private AVRational _streamTimeBase;

    // Reusable scratch — stride * TileHeight bytes. Avalonia's Bitmap ctor
    // copies from the pointer, so we can reuse this buffer across calls.
    private byte[]? _bgraBuffer;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private string? _loadedPath;

    public double SourceFps { get; private set; }
    public TimeSpan SourceDuration { get; private set; }
    public string? LoadedPath => _loadedPath;

    static NativeFfmpegThumbnailExtractor()
    {
        NativeFfmpegLoader.Ensure();
    }

    public Task LoadFileAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativeFfmpegThumbnailExtractor));
        if (!NativeFfmpegLoader.Available)
            throw new InvalidOperationException(NativeFfmpegLoader.FailureReason ?? "FFmpeg unavailable");

        _gate.Wait(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            LoadInternal(path);
        }
        catch
        {
            CloseInternal();
            _loadedPath = null;
            throw;
        }
        finally { _gate.Release(); }
    }, ct);

    private unsafe void LoadInternal(string path)
    {
        CloseInternal();

        AVFormatContext* fmt = null;
        int err = ffmpeg.avformat_open_input(&fmt, path, null, null);
        if (err < 0) throw new InvalidOperationException("avformat_open_input: " + AvError(err));
        _fmtCtx = fmt;

        err = ffmpeg.avformat_find_stream_info(_fmtCtx, null);
        if (err < 0) throw new InvalidOperationException("avformat_find_stream_info: " + AvError(err));

        AVCodec* codec = null;
        int idx = ffmpeg.av_find_best_stream(_fmtCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
        if (idx < 0) throw new InvalidOperationException("no video stream");
        _videoStreamIndex = idx;
        _streamTimeBase = _fmtCtx->streams[idx]->time_base;

        _codecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecCtx == null) throw new InvalidOperationException("avcodec_alloc_context3 failed");
        err = ffmpeg.avcodec_parameters_to_context(_codecCtx, _fmtCtx->streams[idx]->codecpar);
        if (err < 0) throw new InvalidOperationException("avcodec_parameters_to_context: " + AvError(err));

        // Decode-speed tuning. All three are safe over a network drive — none of
        // them increase I/O or read-ahead; they only relax CPU work the decoder
        // would otherwise do for output we'll downscale to 160×90 anyway.
        //
        //  • thread_count=0 lets libavcodec pick (typically logical CPU count).
        //    FRAME|SLICE combines pipelined frame threading with intra-frame
        //    parallelism — biggest single win on H.264/HEVC 4K (~3-4× on local
        //    files). Network files see less because I/O can dominate, but the
        //    speedup is never negative: av_read_frame stays serial.
        //  • AV_CODEC_FLAG2_FAST skips a few error-resilience checks. Any
        //    artifacts it might introduce are invisible at 160×90.
        //  • skip_loop_filter=ALL skips in-loop deblocking. ~15-25% on its own,
        //    visually irrelevant at thumbnail size.
        _codecCtx->thread_count = 0;
        _codecCtx->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
        _codecCtx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
        _codecCtx->skip_loop_filter = AVDiscard.AVDISCARD_ALL;

        err = ffmpeg.avcodec_open2(_codecCtx, codec, null);
        if (err < 0) throw new InvalidOperationException("avcodec_open2: " + AvError(err));

        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _fallbackFrame = ffmpeg.av_frame_alloc();
        if (_packet == null || _frame == null || _fallbackFrame == null)
            throw new InvalidOperationException("alloc failed");

        // Prefer avg_frame_rate; fall back to r_frame_rate when avg is zero
        // (some TS streams report 0/0 for avg).
        var stream = _fmtCtx->streams[idx];
        var fps = ffmpeg.av_q2d(stream->avg_frame_rate);
        if (fps <= 0) fps = ffmpeg.av_q2d(stream->r_frame_rate);
        SourceFps = fps;

        SourceDuration = _fmtCtx->duration > 0
            ? TimeSpan.FromSeconds(_fmtCtx->duration / (double)ffmpeg.AV_TIME_BASE)
            : TimeSpan.Zero;

        _loadedPath = path;
    }

    /// <summary>
    /// Returns the decoded frame at <paramref name="timeSec"/>, or <c>null</c> when
    /// no frame could be produced (typically: seeking past the last keyframe of a
    /// .ts stream, or audio dropout regions). Returns <c>null</c> when the extractor
    /// has been disposed too — fire-and-forget callers (e.g., per-segment thumbnail
    /// loads after silence detection creates many segments) inherently race against
    /// shutdown, and turning that into an exception just produces noise rather than
    /// catching a real bug.
    /// </summary>
    public Task<Bitmap?> ExtractFrameAsync(double timeSec, CancellationToken ct = default) => Task.Run<Bitmap?>(() =>
    {
        if (_disposed) return null;
        // OperationCanceledException is the expected unwind path when a newer view-pan
        // supersedes an in-flight extraction. We swallow it INSIDE the Task.Run lambda
        // so it doesn't surface as a "user-unhandled" exception in the debugger every
        // time follow-playhead or rapid zooming cancels a frame mid-decode. Callers
        // already treat a null result as "skip this cell" (no frame extracted), and
        // outer cancellation propagates via the next ct.ThrowIfCancellationRequested()
        // in awaited code — that one fires in normal async user-code context and is
        // caught cleanly.
        try { _gate.Wait(ct); }
        catch (OperationCanceledException) { return null; }
        try
        {
            if (ct.IsCancellationRequested) return null;
            return ExtractInternal(timeSec, ct);
        }
        catch (OperationCanceledException) { return null; }
        finally { _gate.Release(); }
    }, ct);

    /// <summary>
    /// Convert seconds → absolute stream PTS. Two adjustments matter:
    /// <list type="bullet">
    ///   <item>av_seek_frame's timestamp is in stream time_base units, not AV_TIME_BASE.</item>
    ///   <item>Stream PTS aren't guaranteed to start at 0 — TS files from live-stream
    ///     recorders often have non-zero start_time (the recorder dumped raw PES that
    ///     already had elapsed PTS). Without this offset, "seek to 0.5s from start"
    ///     actually targets a PTS before any frame in the file, making BACKWARD + ANY
    ///     both fail and blanking the front of the strip.</item>
    /// </list>
    /// </summary>
    private unsafe long ComputeTargetPts(double timeSec)
    {
        long targetPts = (long)(timeSec * _streamTimeBase.den / (double)_streamTimeBase.num);
        var startTime = _fmtCtx->streams[_videoStreamIndex]->start_time;
        if (startTime != ffmpeg.AV_NOPTS_VALUE)
            targetPts += startTime;
        return targetPts;
    }

    private unsafe Bitmap? ExtractInternal(double timeSec, CancellationToken ct)
    {
        if (_fmtCtx == null) throw new InvalidOperationException("Call LoadFileAsync first");

        long targetPts = ComputeTargetPts(timeSec);

        // Three-tier seek: prefer the keyframe ≤ target (BACKWARD), fall back to
        // any frame ≤ target (ANY), and finally to the first keyframe ≥ target
        // (FORWARD via flags=0). FORWARD is the saver when the file has no
        // decodable content before the requested time — instead of blanking the
        // cell we land on the next available keyframe and use that.
        int err = ffmpeg.av_seek_frame(_fmtCtx, _videoStreamIndex, targetPts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (err < 0) err = ffmpeg.av_seek_frame(_fmtCtx, _videoStreamIndex, targetPts, ffmpeg.AVSEEK_FLAG_ANY);
        if (err < 0) err = ffmpeg.av_seek_frame(_fmtCtx, _videoStreamIndex, targetPts, 0);
        if (err < 0) return null;

        ffmpeg.avcodec_flush_buffers(_codecCtx);
        // Reset the fallback slot at the start of every extraction. We'll fill it
        // with the most recent decoded frame whose pts < target, in case the loop
        // hits EOF before reaching the target (very common for .ts where the
        // file's reported duration overshoots the last decodable frame by 1-3s).
        ffmpeg.av_frame_unref(_fallbackFrame);

        const int maxIterations = 10_000;
        // Stream-recorder TS files routinely have a long run of partial / fragmented
        // packets before the first valid keyframe (the recorder started mid-stream).
        // 64 bails out before reaching usable content; 512 has been enough on every
        // sample file tested without making truly-corrupt files hang noticeably.
        const int maxBadPackets = 512;
        int badPackets = 0;
        bool gotFrame = false;
        bool hasFallback = false;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();

            // Read packets until we get one for our video stream (or hit EOF /
            // a tolerable run of bad packets). Other streams (audio, subs) are
            // silently skipped.
            int readErr = 0;
            while (true)
            {
                ffmpeg.av_packet_unref(_packet);
                readErr = ffmpeg.av_read_frame(_fmtCtx, _packet);
                if (readErr == ffmpeg.AVERROR_EOF) break;
                if (readErr == ffmpeg.AVERROR_INVALIDDATA)
                {
                    if (++badPackets > maxBadPackets) break;
                    continue;
                }
                if (readErr < 0) throw new InvalidOperationException("av_read_frame: " + AvError(readErr));
                if (_packet->stream_index == _videoStreamIndex) break;
            }
            if (readErr == ffmpeg.AVERROR_EOF)
            {
                // Drain: with FRAME threading the decoder buffers up to thread_count
                // frames internally before producing output, so on EOF the target
                // frame may still be sitting in the queue. Send NULL to flush, then
                // pull frames out the same way the normal path does.
                ffmpeg.avcodec_send_packet(_codecCtx, null);
                while (true)
                {
                    int dErr = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
                    if (dErr < 0) break; // EAGAIN or EOF — decoder drained.
                    long dPts = _frame->best_effort_timestamp;
                    if (dPts == ffmpeg.AV_NOPTS_VALUE) dPts = _frame->pts;
                    if (dPts == ffmpeg.AV_NOPTS_VALUE || dPts >= targetPts)
                    {
                        gotFrame = true;
                        break;
                    }
                    ffmpeg.av_frame_unref(_fallbackFrame);
                    ffmpeg.av_frame_ref(_fallbackFrame, _frame);
                    hasFallback = true;
                    ffmpeg.av_frame_unref(_frame);
                }
                break;
            }
            if (badPackets > maxBadPackets) break;

            int sendErr = ffmpeg.avcodec_send_packet(_codecCtx, _packet);
            ffmpeg.av_packet_unref(_packet);
            if (sendErr == ffmpeg.AVERROR_INVALIDDATA || sendErr == ffmpeg.AVERROR(ffmpeg.EINVAL))
            {
                if (++badPackets > maxBadPackets) break;
                continue;
            }
            if (sendErr < 0) throw new InvalidOperationException("avcodec_send_packet: " + AvError(sendErr));

            int recvErr = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
            if (recvErr == ffmpeg.AVERROR(ffmpeg.EAGAIN)) continue;
            if (recvErr < 0) throw new InvalidOperationException("avcodec_receive_frame: " + AvError(recvErr));

            long pts = _frame->best_effort_timestamp;
            if (pts == ffmpeg.AV_NOPTS_VALUE) pts = _frame->pts;

            // Stop once we've decoded a frame at-or-past the target.
            if (pts == ffmpeg.AV_NOPTS_VALUE || pts >= targetPts)
            {
                gotFrame = true;
                break;
            }
            // pts < target. Save as fallback in case EOF cuts us off; av_frame_ref
            // is a refcount bump on the underlying buffer — cheap. Then unref _frame
            // so the next receive can write into a clean slot.
            ffmpeg.av_frame_unref(_fallbackFrame);
            ffmpeg.av_frame_ref(_fallbackFrame, _frame);
            hasFallback = true;
            ffmpeg.av_frame_unref(_frame);
        }

        if (gotFrame) return ScaleAndConvert(_frame);
        if (hasFallback) return ScaleAndConvert(_fallbackFrame);
        return null;
    }

    private unsafe Bitmap ScaleAndConvert(AVFrame* src)
    {
        int srcW = src->width;
        int srcH = src->height;
        if (srcW <= 0 || srcH <= 0) throw new InvalidOperationException("invalid frame dimensions");

        // Letterbox: scale source to fit inside tile, preserving aspect, with
        // even dst dims (some sws targets dislike odd widths/heights).
        var scale = Math.Min((double)TileWidth / srcW, (double)TileHeight / srcH);
        int dstW = Math.Max(2, ((int)Math.Round(srcW * scale)) & ~1);
        int dstH = Math.Max(2, ((int)Math.Round(srcH * scale)) & ~1);
        int dstX = (TileWidth - dstW) / 2;
        int dstY = (TileHeight - dstH) / 2;

        // BGRA8888 = Avalonia's preferred raw-pixel format on Windows.
        _sws = ffmpeg.sws_getCachedContext(_sws,
            srcW, srcH, (AVPixelFormat)src->format,
            dstW, dstH, AVPixelFormat.AV_PIX_FMT_BGRA,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_sws == null) throw new InvalidOperationException("sws_getCachedContext failed");

        const int bytesPerPixel = 4;
        int stride = TileWidth * bytesPerPixel;
        int needed = stride * TileHeight;
        if (_bgraBuffer == null || _bgraBuffer.Length < needed)
            _bgraBuffer = new byte[needed];
        // Black letterbox background.
        Array.Clear(_bgraBuffer, 0, needed);

        fixed (byte* pBuf = _bgraBuffer)
        {
            // Aim sws_scale at the inner letterbox rect.
            byte* dstStart = pBuf + dstY * stride + dstX * bytesPerPixel;
            var dstData = new byte_ptrArray4 { [0] = dstStart };
            var dstLinesize = new int_array4 { [0] = stride };

            ffmpeg.sws_scale(_sws,
                src->data, src->linesize,
                0, srcH,
                dstData, dstLinesize);

            // Avalonia copies pixels from the IntPtr internally, so reusing
            // _bgraBuffer across calls is safe.
            return new Bitmap(
                PixelFormat.Bgra8888,
                AlphaFormat.Premul,
                (IntPtr)pBuf,
                new PixelSize(TileWidth, TileHeight),
                new Vector(96, 96),
                stride);
        }
    }

    public async Task<FrameSet> ExtractRangeAsync(
        double startSec, double endSec, int count,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (endSec <= startSec) throw new ArgumentException("endSec must exceed startSec");
        count = Math.Clamp(count, 16, 600);

        var span = endSec - startSec;
        var step = span / count;
        var bitmaps = new List<Bitmap>(count);
        var times = new List<double>(count);

        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = Math.Clamp(startSec + i * step + step * 0.5, 0, endSec - 0.05);
            // Null return = expected miss (e.g. .ts past last keyframe). Skip
            // without padding so the renderer's distance cap leaves the cell
            // blank rather than smearing a duplicate of a nearby frame.
            // Catch is for unexpected setup failures (codec err, etc.).
            try
            {
                var bmp = await ExtractFrameAsync(t, ct);
                if (bmp != null)
                {
                    bitmaps.Add(bmp);
                    times.Add(t);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* unexpected — skip */ }
            progress?.Report((double)(i + 1) / count);
        }

        if (bitmaps.Count == 0)
            throw new InvalidOperationException("No frames extracted");
        return new FrameSet(bitmaps, times, startSec, endSec);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _gate.Wait(2000); } catch { }
        try { CloseInternal(); }
        finally { try { _gate.Release(); } catch { } }
        _gate.Dispose();
    }

    private unsafe void CloseInternal()
    {
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_fallbackFrame != null) { var p = _fallbackFrame; ffmpeg.av_frame_free(&p); _fallbackFrame = null; }
        if (_frame != null) { var p = _frame; ffmpeg.av_frame_free(&p); _frame = null; }
        if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_codecCtx != null) { var p = _codecCtx; ffmpeg.avcodec_free_context(&p); _codecCtx = null; }
        if (_fmtCtx != null) { var p = _fmtCtx; ffmpeg.avformat_close_input(&p); _fmtCtx = null; }
        _videoStreamIndex = -1;
        SourceFps = 0;
        SourceDuration = TimeSpan.Zero;
    }

    private static unsafe string AvError(int code)
    {
        const int bufSize = 1024;
        var buf = stackalloc byte[bufSize];
        ffmpeg.av_strerror(code, buf, (ulong)bufSize);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? $"err {code}";
    }
}
