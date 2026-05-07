using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace YetAnotherLosslessCutter.NativeDeps;

/// <summary>
/// Where Homebrew installs dylibs on macOS. macOS dyld doesn't search these prefixes
/// by default (unlike <c>/usr/lib</c>), so any <c>NativeLibrary.TryLoad("libmpv.2.dylib")</c>
/// call from a <c>brew install mpv</c> machine fails unless we probe these paths
/// explicitly. <c>/opt/homebrew</c> is the Apple Silicon prefix; <c>/usr/local</c> is
/// the Intel prefix. Linux Homebrew (<c>/home/linuxbrew/.linuxbrew</c>) is included
/// for completeness even though Linux distro packages typically land in <c>/usr/lib</c>.
/// </summary>
internal static class HomebrewPaths
{
    public static readonly string[] LibDirs =
    {
        "/opt/homebrew/lib",
        "/usr/local/lib",
        "/home/linuxbrew/.linuxbrew/lib",
    };

    /// <summary>
    /// Yields candidate absolute paths for a base library name (e.g. <c>libmpv.2.dylib</c>),
    /// covering all known Homebrew lib directories that exist on disk.
    /// </summary>
    public static IEnumerable<string> CandidatesFor(string fileName)
    {
        foreach (var dir in LibDirs)
        {
            if (Directory.Exists(dir))
                yield return Path.Combine(dir, fileName);
        }
    }

    /// <summary>
    /// First lib directory that contains <paramref name="probeFile"/>, or null. Useful
    /// to set <c>FFmpeg.AutoGen.ffmpeg.RootPath</c> to the right brew prefix.
    /// </summary>
    public static string? FindDirContaining(string probeFile)
    {
        foreach (var dir in LibDirs)
        {
            if (File.Exists(Path.Combine(dir, probeFile)))
                return dir;
        }
        return null;
    }

    public static bool IsRelevant => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
                                     RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
}
