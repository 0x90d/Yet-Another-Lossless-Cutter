using System;
using System.IO;
using System.Runtime.InteropServices;

namespace YetAnotherLosslessCutter.NativeDeps;

/// <summary>
/// Detects whether the two native dependencies (libmpv + FFmpeg shared libraries) are
/// available on this machine. Detection strategy is platform-specific:
/// <list type="bullet">
///   <item>Windows — file presence in <see cref="AppContext.BaseDirectory"/>. The
///   auto-downloader places them there.</item>
///   <item>Linux / macOS — <see cref="NativeLibrary.TryLoad(string)"/> against the
///   platform-specific shared object name. Hits whatever search path the loader uses
///   (system lib dirs, LD_LIBRARY_PATH, etc.) so a package-manager install is
///   detected automatically.</item>
/// </list>
/// </summary>
internal static class NativeDepsCheck
{
    public sealed record MissingDeps(bool Libmpv, bool Ffmpeg)
    {
        public bool AnyMissing => Libmpv || Ffmpeg;
    }

    public static MissingDeps Detect()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return DetectWindows();
        return DetectUnix();
    }

    private static MissingDeps DetectWindows()
    {
        var dir = AppContext.BaseDirectory;
        // libmpv is one DLL; check for it directly.
        var libmpvMissing = !File.Exists(Path.Combine(dir, "libmpv-2.dll"));
        // FFmpeg.AutoGen 8.0.0 binds against FFmpeg 8.x — the avcodec major is 62.
        // One probe DLL is enough; if avcodec-62 is missing the others would be too
        // (BtbN's archive ships them as a set or not at all).
        var ffmpegMissing = !File.Exists(Path.Combine(dir, "avcodec-62.dll"));
        return new MissingDeps(libmpvMissing, ffmpegMissing);
    }

    private static MissingDeps DetectUnix()
    {
        var libmpvMissing = !TryLoad(LibmpvSoName());
        var ffmpegMissing = !TryLoad(AvcodecSoName());
        return new MissingDeps(libmpvMissing, ffmpegMissing);
    }

    private static bool TryLoad(string name)
    {
        try { return NativeLibrary.TryLoad(name, out _); }
        catch { return false; }
    }

    private static string LibmpvSoName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libmpv.2.dylib" : "libmpv.so.2";

    // libavcodec.so.NN / libavcodec.NN.dylib — major 62 matches FFmpeg.AutoGen 8.0.
    private static string AvcodecSoName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libavcodec.62.dylib" : "libavcodec.so.62";
}
