using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace YetAnotherLosslessCutter.Cutting;

/// <summary>
/// Locates ffmpeg and ffprobe executables. No side effects — callers handle the
/// "missing" case explicitly. The original used a static constructor that popped
/// a MessageBox on failure; that was both untestable and a bad failure path.
/// </summary>
public static class FfmpegLocator
{
    private static string? _ffmpegPath;
    private static string? _ffprobePath;
    private static IReadOnlyList<string> _ffmpegSearchedPaths = Array.Empty<string>();

    /// <summary>Returns the discovered ffmpeg path, or null if not found.</summary>
    public static string? FfmpegPath
    {
        get
        {
            if (_ffmpegPath != null) return _ffmpegPath;
            _ffmpegPath = Find("ffmpeg", out var searched);
            _ffmpegSearchedPaths = searched;
            return _ffmpegPath;
        }
    }

    /// <summary>Returns the discovered ffprobe path, or null if not found.</summary>
    public static string? FfprobePath => _ffprobePath ??= Find("ffprobe", out _);

    /// <summary>
    /// Concrete paths that the most recent <see cref="FfmpegPath"/> resolution checked.
    /// Surfaced in the "ffmpeg not found" error so the user can see exactly where to
    /// drop the binary instead of guessing what "alongside the executable" means.
    /// </summary>
    public static IReadOnlyList<string> FfmpegSearchedPaths
    {
        get { _ = FfmpegPath; return _ffmpegSearchedPaths; }
    }

    private static string Exe(string name) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? name + ".exe" : name;

    private static string? Find(string toolName, out IReadOnlyList<string> searchedPaths)
    {
        var exeName = Exe(toolName);
        var exeDir = AppContext.BaseDirectory;
        var searched = new List<string>();

        string? Probe(string candidate)
        {
            searched.Add(candidate);
            return File.Exists(candidate) ? candidate : null;
        }

        // 1. {app}/bin/<tool>
        var hit = Probe(Path.Combine(exeDir, "bin", exeName));
        if (hit != null) { searchedPaths = searched; return hit; }

        // 2. Next to executable
        hit = Probe(Path.Combine(exeDir, exeName));
        if (hit != null) { searchedPaths = searched; return hit; }

        // 3. PATH. Skip empty entries — Path.Combine("", "ffmpeg.exe") returns the
        // bare filename, and File.Exists then resolves it against the current
        // working directory. That's how we ended up returning a CWD-resolved path
        // when something irrelevant happened to have ffmpeg.exe (e.g. an MSBuild
        // bin folder that ships its own copy). Also require absolute results so
        // callers always get a deterministic path.
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (!Path.IsPathRooted(candidate)) continue;
                hit = Probe(candidate);
                if (hit != null) { searchedPaths = searched; return hit; }
            }
            catch { /* skip unreadable PATH entries */ }
        }

        searchedPaths = searched;
        return null;
    }
}
