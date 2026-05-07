using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace YetAnotherLosslessCutter.Cutting;

/// <summary>
/// One-time initialization for the FFmpeg.AutoGen P/Invoke bindings.
///
/// FFmpeg.AutoGen resolves <c>avcodec-NN.dll</c> et al at runtime by walking
/// <see cref="ffmpeg.RootPath"/>, then PATH. We point it at our exe directory
/// (where csproj's content-copy puts the shared libraries) so the bundled
/// DLLs always win over whatever happens to be on the user's PATH.
/// </summary>
internal static class NativeFfmpegLoader
{
    private static readonly object _gate = new();
    private static bool _initialized;
    private static bool _available;
    private static string? _failureReason;

    /// <summary>
    /// True if the FFmpeg shared libraries were found and loaded. Callers should
    /// gate native paths on this and fall back gracefully when false.
    /// </summary>
    public static bool Available
    {
        get { Ensure(); return _available; }
    }

    /// <summary>Human-readable reason when <see cref="Available"/> is false.</summary>
    public static string? FailureReason
    {
        get { Ensure(); return _failureReason; }
    }

    public static void Ensure()
    {
        if (_initialized) return;
        lock (_gate)
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                ffmpeg.RootPath = baseDir;

                // Trigger one symbol resolution so we fail fast here (with a clean
                // exception) rather than from inside an extraction call later.
                _ = ffmpeg.av_version_info();
                _available = true;
            }
            catch (Exception ex)
            {
                _available = false;
                _failureReason = BuildFailureReason(ex);
            }
        }
    }

    private static string BuildFailureReason(Exception ex)
    {
        string libs;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            libs = "avcodec-62.dll, avformat-62.dll, avutil-60.dll, swscale-9.dll, " +
                   "swresample-6.dll, avfilter-11.dll, avdevice-62.dll";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            libs = "libavcodec.62.dylib, libavformat.62.dylib, libavutil.60.dylib, " +
                   "libswscale.9.dylib, libswresample.6.dylib, libavfilter.11.dylib, " +
                   "libavdevice.62.dylib (install via Homebrew: `brew install ffmpeg`)";
        else
            libs = "libavcodec.so.62, libavformat.so.62, libavutil.so.60, " +
                   "libswscale.so.9, libswresample.so.6, libavfilter.so.11, " +
                   "libavdevice.so.62 (install via your package manager: `apt install ffmpeg`, " +
                   "`dnf install ffmpeg`, `pacman -S ffmpeg`)";

        return "FFmpeg shared libraries not loadable. Required: " + libs +
               ". Underlying error: " + ex.Message;
    }
}
