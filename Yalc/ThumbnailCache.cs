using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace YetAnotherLosslessCutter;

/// <summary>
/// On-disk cache for queue-item thumbnails. Without this, restoring the queue from
/// <c>queue.json</c> on launch leaves every item with a blank thumbnail until the
/// user happens to open the source file again — which is rarely.
///
/// Cache lives at <c>queue-thumbs/&lt;sha1&gt;.png</c> next to <c>Yalc.exe</c>. Key is a
/// SHA-1 of <c>"{SourceFile}|{CutFromSeconds}"</c> so two cuts of the same source at
/// different timestamps don't collide. Files are PNG via Avalonia's <see cref="Bitmap.Save(string)"/>
/// which always emits PNG regardless of extension.
/// </summary>
public static class ThumbnailCache
{
    private static readonly string _cacheDir = Path.Combine(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
        "queue-thumbs");

    public static Bitmap? TryLoad(VideoSegment seg)
    {
        try
        {
            var path = PathFor(seg);
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return new Bitmap(fs);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(VideoSegment seg, Bitmap bitmap)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            bitmap.Save(PathFor(seg));
        }
        catch
        {
            // Disk-full / permission issue — non-fatal, thumbnail just won't survive restart.
        }
    }

    /// <summary>
    /// Delete cache files that don't correspond to any current queue item. Called from
    /// the queue-save debounce so the cache stays bounded as items get cut and removed.
    /// </summary>
    public static void Prune(IEnumerable<VideoSegment> currentQueue)
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) return;
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var seg in currentQueue)
            {
                if (seg.MarkedForDeletion) continue;
                keep.Add(KeyFor(seg) + ".png");
            }
            foreach (var file in Directory.EnumerateFiles(_cacheDir, "*.png"))
            {
                if (!keep.Contains(Path.GetFileName(file)))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch
        {
            // Cache directory missing or unreadable — nothing to do.
        }
    }

    private static string PathFor(VideoSegment seg)
        => Path.Combine(_cacheDir, KeyFor(seg) + ".png");

    private static string KeyFor(VideoSegment seg)
    {
        // R-format ensures CutFromSeconds round-trips exactly, so cache lookups match
        // even after a queue load/save cycle that goes through doubles.
        var input = $"{seg.SourceFile}|{seg.CutFromSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
