using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

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
        // Probe the avcodec DLL whose major matches FFmpeg.AutoGen's binding — sourced
        // from AutoGen's own version map so the probe automatically tracks future
        // AutoGen bumps. One DLL is enough; BtbN's archive ships them as a set.
        var ffmpegMissing = !File.Exists(Path.Combine(dir, AvcodecDllName()));
        return new MissingDeps(libmpvMissing, ffmpegMissing);
    }

    // Probe filename derived from FFmpeg.AutoGen's binding map so the dep check stays
    // in sync with the bindings across version bumps. Exposed to tests.
    internal static string AvcodecDllName() => $"avcodec-{ffmpeg.LibraryVersionMap["avcodec"]}.dll";

    private static MissingDeps DetectUnix()
    {
        var libmpvMissing = !TryLoadAnywhere(LibmpvSoName());
        var ffmpegMissing = !TryLoadAnywhere(AvcodecSoName());
        return new MissingDeps(libmpvMissing, ffmpegMissing);
    }

    /// <summary>
    /// Try the bare name first (uses the loader's default search path) and then fall
    /// back to absolute Homebrew paths. macOS dyld doesn't search <c>/opt/homebrew/lib</c>
    /// or <c>/usr/local/lib</c> by default, so a <c>brew install</c> of mpv/ffmpeg
    /// would otherwise look "missing" to a bare TryLoad.
    /// </summary>
    private static bool TryLoadAnywhere(string fileName)
    {
        if (TryLoad(fileName)) return true;
        foreach (var path in HomebrewPaths.CandidatesFor(fileName))
        {
            if (TryLoad(path)) return true;
        }
        return false;
    }

    private static bool TryLoad(string name)
    {
        try { return NativeLibrary.TryLoad(name, out _); }
        catch { return false; }
    }

    private static string LibmpvSoName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libmpv.2.dylib" : "libmpv.so.2";

    // libavcodec.so.NN / libavcodec.NN.dylib — major is sourced from FFmpeg.AutoGen's
    // version map so the probe automatically tracks future AutoGen bumps.
    internal static string AvcodecSoName()
    {
        var major = ffmpeg.LibraryVersionMap["avcodec"];
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? $"libavcodec.{major}.dylib" : $"libavcodec.so.{major}";
    }
}
