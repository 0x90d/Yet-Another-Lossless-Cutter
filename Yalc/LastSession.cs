using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YetAnotherLosslessCutter;

/// <summary>
/// Crash-recovery snapshot of the file playlist. Written each time
/// <see cref="MainWindow"/> loads a new file (so the on-disk view always reflects
/// the current playlist + index), deleted on graceful shutdown. If it survives
/// to the next launch the previous session crashed, and we offer to resume —
/// re-loading the full playlist starting at the saved index, not just the one
/// file that happened to be loaded.
/// </summary>
public sealed class LastSession
{
    public List<string> Files { get; set; } = new();
    public int CurrentIndex { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LastSession))]
internal partial class LastSessionJsonContext : JsonSerializerContext { }

public static class LastSessionStore
{
    // Per-install: lastsession.json sits next to Yalc.exe so a debug build, AOT
    // build, and portable copy each have their own crash-recovery snapshot. Earlier
    // versions used %LOCALAPPDATA%/YALC/lastsession.json; that path is migrated
    // once on first launch (see Load).
    private static readonly string _path = Path.Combine(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
        "lastsession.json");

    private static readonly string _localAppDataLegacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YALC",
        "lastsession.json");

    /// <summary>
    /// Path of the legacy <c>lastfile.txt</c> next to the executable. Read once at
    /// startup if <c>lastsession.json</c> doesn't exist, so users upgrading from a
    /// pre-playlist build don't lose recovery on their first crash after the upgrade.
    /// </summary>
    private static readonly string _legacyPath = Path.Combine(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
        "lastfile.txt");

    public static void Save(IReadOnlyList<string> files, int currentIndex)
    {
        try
        {
            var snapshot = new LastSession
            {
                Files = new List<string>(files),
                CurrentIndex = currentIndex,
            };
            var json = JsonSerializer.Serialize(snapshot, LastSessionJsonContext.Default.LastSession);
            File.WriteAllText(_path, json);
            // Remove the legacy single-path file if it exists — superseded.
            if (File.Exists(_legacyPath)) { try { File.Delete(_legacyPath); } catch { } }
        }
        catch { /* recovery is best-effort */ }
    }

    public static LastSession? Load()
    {
        try
        {
            MigrateLocalAppDataLegacyIfNeeded();
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize(json, LastSessionJsonContext.Default.LastSession);
            }
            // Older fallback: lastfile.txt next to the exe — single path.
            if (File.Exists(_legacyPath))
            {
                var legacy = File.ReadAllText(_legacyPath).Trim();
                if (!string.IsNullOrEmpty(legacy))
                    return new LastSession { Files = new List<string> { legacy }, CurrentIndex = 0 };
            }
        }
        catch { /* corrupt — fall through */ }
        return null;
    }

    public static void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        try { if (File.Exists(_legacyPath)) File.Delete(_legacyPath); } catch { }
    }

    /// <summary>
    /// One-shot migration from the previous shared %LOCALAPPDATA%/YALC/lastsession.json
    /// to the per-install location. Same logic as <c>QueuePersistence.MigrateLegacyIfNeeded</c>:
    /// run only when there's a legacy file AND no current file.
    /// </summary>
    private static void MigrateLocalAppDataLegacyIfNeeded()
    {
        try
        {
            if (File.Exists(_path)) return;
            if (!File.Exists(_localAppDataLegacyPath)) return;
            File.Move(_localAppDataLegacyPath, _path);
        }
        catch { /* permission issue — leave both alone */ }
    }
}
