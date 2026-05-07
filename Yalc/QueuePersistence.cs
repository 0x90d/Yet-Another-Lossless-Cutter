using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YetAnotherLosslessCutter;

/// <summary>
/// Save/load of the processing queue across app sessions.
///
/// Serialized to <c>queue.json</c> next to <c>Yalc.exe</c> as a list of
/// <see cref="QueueItemDto"/>. Only Waiting and Failed items are persisted —
/// Finished/Cancelled items are dropped, Running items are flushed back to
/// Waiting (the previous session was killed mid-cut).
///
/// Per-install location matches <c>Settings.json</c>, so a debug build, an AOT
/// build, and a portable copy on a USB stick each have their own queue. (Earlier
/// versions used %LOCALAPPDATA%/YALC/queue.json, which was shared across all
/// installs — see <see cref="MigrateLegacyIfNeeded"/> for the one-time migration.)
///
/// Avalonia <see cref="Avalonia.Media.Imaging.Bitmap"/>/<see cref="Avalonia.Media.IBrush"/>
/// types on <see cref="VideoSegment"/> are non-serializable; that's why we use a flat DTO
/// with primitive fields and rehydrate in <see cref="ToVideoSegment"/>.
/// </summary>
public static class QueuePersistence
{
    private static readonly string _queuePath = Path.Combine(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
        "queue.json");

    private static readonly string _legacyQueuePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YALC",
        "queue.json");

    public static List<VideoSegment> Load()
    {
        try
        {
            MigrateLegacyIfNeeded();
            if (!File.Exists(_queuePath)) return new List<VideoSegment>();
            var json = File.ReadAllText(_queuePath);
            var dtos = JsonSerializer.Deserialize(json, QueueJsonContext.Default.ListQueueItemDto)
                       ?? new List<QueueItemDto>();
            return dtos.Select(d => d.ToVideoSegment()).ToList();
        }
        catch
        {
            // Corrupt queue file — fall back to empty rather than crash.
            return new List<VideoSegment>();
        }
    }

    public static void Save(IEnumerable<VideoSegment> segments)
    {
        try
        {
            var keep = segments
                .Where(s => s is { MarkedForDeletion: false }
                            && (s.Status == ProgressStatus.Waiting
                             || s.Status == ProgressStatus.Failed
                             || s.Status == ProgressStatus.Running))
                .Select(QueueItemDto.From)
                .ToList();

            if (keep.Count == 0)
            {
                if (File.Exists(_queuePath)) File.Delete(_queuePath);
                return;
            }

            var json = JsonSerializer.Serialize(keep, QueueJsonContext.Default.ListQueueItemDto);
            File.WriteAllText(_queuePath, json);
        }
        catch
        {
            // Disk-full / permission issue — non-fatal. Worst case: queue not preserved.
        }
    }

    /// <summary>
    /// One-shot migration from the old shared %LOCALAPPDATA%/YALC/queue.json to the
    /// per-install location. Runs only when there's a legacy file AND no current
    /// file — first install sees the legacy queue once, then the legacy file is
    /// deleted so subsequent installs / builds start clean.
    /// </summary>
    private static void MigrateLegacyIfNeeded()
    {
        try
        {
            if (File.Exists(_queuePath)) return;
            if (!File.Exists(_legacyQueuePath)) return;
            File.Move(_legacyQueuePath, _queuePath);
        }
        catch
        {
            // Permission issue — leave both files alone. Either we read from the new
            // path (currently empty) or do nothing; user keeps the legacy file as backup.
        }
    }
}

public sealed class QueueItemDto
{
    public string SourceFile { get; set; } = string.Empty;
    public double MaxDurationSeconds { get; set; }
    public double CutFromSeconds { get; set; }
    public double CutToSeconds { get; set; }
    public int ColorIndex { get; set; }
    public ProgressStatus Status { get; set; }
    public string? FailureReason { get; set; }

    public static QueueItemDto From(VideoSegment s) => new()
    {
        SourceFile = s.SourceFile,
        MaxDurationSeconds = s.MaxDuration.TotalSeconds,
        CutFromSeconds = s.CutFromSeconds,
        CutToSeconds = s.CutToSeconds,
        ColorIndex = s.ColorIndex,
        // A Running item from a previous session never finished — flush back to Waiting
        // so the user can either retry or remove it.
        Status = s.Status == ProgressStatus.Running ? ProgressStatus.Waiting : s.Status,
        FailureReason = s.FailureReason,
    };

    public VideoSegment ToVideoSegment()
    {
        // Failed items reset to Waiting on launch — the typical reason for a previous
        // failure (ffmpeg missing, disk full, output dir gone) is something the user
        // either fixed or wants to retry. Clearing FailureReason too so the tooltip
        // doesn't show last session's stale error after a successful retry.
        var status = Status == ProgressStatus.Failed ? ProgressStatus.Waiting : Status;
        var failureReason = Status == ProgressStatus.Failed ? null : FailureReason;

        var seg = new VideoSegment
        {
            SourceFile = SourceFile,
            // Order matters — MaxDuration first so the CutFrom/CutTo setters can clamp
            // against the file's actual length. Same ordering as MainWindow.AddSegmentInternal.
            MaxDuration = TimeSpan.FromSeconds(MaxDurationSeconds),
            ColorIndex = ColorIndex,
            Status = status,
            FailureReason = failureReason,
            Progress = 0,
        };
        seg.CutFromSeconds = CutFromSeconds;
        seg.CutToSeconds = CutToSeconds;
        return seg;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<QueueItemDto>))]
[JsonSerializable(typeof(QueueItemDto))]
internal partial class QueueJsonContext : JsonSerializerContext { }
