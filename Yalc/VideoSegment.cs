using System;
using System.IO;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using YetAnotherLosslessCutter.Controls;
using YetAnotherLosslessCutter.Plugins;

namespace YetAnotherLosslessCutter;

/// <summary>
/// A cut segment of a video file. Pure data — no UI references, no side-effecting setters,
/// no thumbnail-on-model (UI fetches and caches thumbnails separately).
/// </summary>
public sealed class VideoSegment : Segment
{
    /// <summary>Soft-delete marker. Used by the queue runner to skip segments the user
    /// removed mid-flight, without disturbing in-progress work.</summary>
    [JsonIgnore]
    public bool MarkedForDeletion;

    private string? _failureReason;
    /// <summary>
    /// Populated when <see cref="Segment.Status"/> becomes <see cref="ProgressStatus.Failed"/>.
    /// Holds a short human-readable reason (typically the ffmpeg stderr tail). Surfaced
    /// in the queue item tooltip and persisted with the queue so the user can still see
    /// why a previous run failed after relaunching.
    /// </summary>
    public string? FailureReason
    {
        get => _failureReason;
        set => Set(ref _failureReason, value);
    }

    private Bitmap? _thumbnail;
    /// <summary>
    /// Frame at CutFrom, populated externally by the UI (we don't auto-fetch here to
    /// avoid the original WPF code's anti-pattern of triggering async IO from a setter).
    /// Excluded from JSON because Bitmap isn't serializable and thumbnails are
    /// regenerated per session anyway.
    /// </summary>
    [JsonIgnore]
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    private int _colorIndex;
    /// <summary>
    /// Tag color for visual distinction. The timeline overlay and the segment list
    /// border share the same color so you can match list entries to bands on the
    /// timeline. See <see cref="SegmentPalette"/> for the actual colors.
    /// </summary>
    public int ColorIndex
    {
        get => _colorIndex;
        set
        {
            if (Set(ref _colorIndex, value))
            {
                OnPropertyChanged(nameof(Color));
                OnPropertyChanged(nameof(Brush));
                OnPropertyChanged(nameof(FillBrush));
            }
        }
    }

    [JsonIgnore]
    public Color Color => SegmentPalette.GetColor(_colorIndex);

    [JsonIgnore]
    public IBrush Brush => SegmentPalette.GetBrush(_colorIndex);

    [JsonIgnore]
    public IBrush FillBrush => SegmentPalette.GetFillBrush(_colorIndex);

    private string _sourceFile = string.Empty;
    public string SourceFile
    {
        get => _sourceFile;
        set
        {
            if (Set(ref _sourceFile, value))
                OnPropertyChanged(nameof(SourceFileName));
        }
    }

    [JsonIgnore]
    public string SourceFileName => Path.GetFileName(_sourceFile);

    private TimeSpan _maxDuration;
    /// <summary>Total duration of the source video. Setting this clamps CutTo to fit.</summary>
    [JsonPropertyName("SourceDuration")]
    public TimeSpan MaxDuration
    {
        get => _maxDuration;
        set
        {
            if (!Set(ref _maxDuration, value)) return;
            if (_cutTo > _maxDuration || _cutTo == TimeSpan.Zero)
            {
                _cutTo = _maxDuration;
                OnPropertyChanged(nameof(CutTo));
                OnPropertyChanged(nameof(CutToSeconds));
                OnPropertyChanged(nameof(CutDuration));
            }
        }
    }

    private TimeSpan _cutFrom;
    public TimeSpan CutFrom
    {
        get => _cutFrom;
        set
        {
            value = Clamp(value, TimeSpan.Zero, _cutTo > TimeSpan.Zero ? _cutTo : _maxDuration);
            if (!Set(ref _cutFrom, value)) return;
            OnPropertyChanged(nameof(CutFromSeconds));
            OnPropertyChanged(nameof(CutDuration));
        }
    }

    private TimeSpan _cutTo;
    public TimeSpan CutTo
    {
        get => _cutTo;
        set
        {
            // CutTo can never go below CutFrom or above MaxDuration.
            value = Clamp(value, _cutFrom, _maxDuration);
            if (!Set(ref _cutTo, value)) return;
            OnPropertyChanged(nameof(CutToSeconds));
            OnPropertyChanged(nameof(CutDuration));
        }
    }

    [JsonIgnore]
    public TimeSpan CutDuration => _cutTo - _cutFrom;

    /// <summary>
    /// Decide whether playback has just naturally crossed this segment's end and the
    /// host should seek back to its start (A-B loop). True only when the previous
    /// reported position was inside the segment, the current position has reached or
    /// passed the end within <paramref name="endTolerance"/>, and the delta between
    /// reports is small enough to indicate continuous playback rather than a manual
    /// scrub. Pure — does not seek; the host calls <c>SeekAbsolute</c> on a true return.
    /// </summary>
    public bool ShouldLoopBack(double lastPos, double currentPos,
        double endTolerance = 0.05, double maxNaturalDelta = 1.0)
    {
        var delta = currentPos - lastPos;
        if (delta <= 0 || delta >= maxNaturalDelta) return false;
        if (lastPos < CutFromSeconds) return false;
        return currentPos >= CutToSeconds - endTolerance;
    }

    /// <summary>
    /// Atomically set both bounds, clamping each to the file extents but not to each
    /// other. Used by undo/redo where the target state is already known-valid but
    /// might require a transient cross-over (e.g., translating the segment past the
    /// current range, where setting the bounds individually would clamp).
    /// </summary>
    public void SetCutTimes(TimeSpan from, TimeSpan to)
    {
        if (from < TimeSpan.Zero) from = TimeSpan.Zero;
        if (_maxDuration > TimeSpan.Zero && to > _maxDuration) to = _maxDuration;
        if (from > to) (from, to) = (to, from);

        var fromChanged = _cutFrom != from;
        var toChanged = _cutTo != to;
        if (!fromChanged && !toChanged) return;

        _cutFrom = from;
        _cutTo = to;
        if (fromChanged)
        {
            OnPropertyChanged(nameof(CutFrom));
            OnPropertyChanged(nameof(CutFromSeconds));
        }
        if (toChanged)
        {
            OnPropertyChanged(nameof(CutTo));
            OnPropertyChanged(nameof(CutToSeconds));
        }
        OnPropertyChanged(nameof(CutDuration));
    }

    /// <summary>
    /// Slide the segment along the timeline keeping its duration fixed. Atomic: avoids
    /// the clamp dance when calling CutFrom/CutTo setters individually (each setter
    /// clamps against the *current* other bound, which can pin the value during a drag).
    /// </summary>
    public void MoveTo(TimeSpan newStart)
    {
        var duration = CutDuration;
        var maxStart = _maxDuration - duration;
        if (maxStart < TimeSpan.Zero) maxStart = TimeSpan.Zero;
        if (newStart < TimeSpan.Zero) newStart = TimeSpan.Zero;
        if (newStart > maxStart) newStart = maxStart;

        var newEnd = newStart + duration;
        if (newStart == _cutFrom && newEnd == _cutTo) return;

        _cutFrom = newStart;
        _cutTo = newEnd;
        OnPropertyChanged(nameof(CutFrom));
        OnPropertyChanged(nameof(CutTo));
        OnPropertyChanged(nameof(CutFromSeconds));
        OnPropertyChanged(nameof(CutToSeconds));
    }

    /// <summary>Convenience accessor for UI/timeline code that works in fractional seconds.</summary>
    [JsonIgnore]
    public double CutFromSeconds
    {
        get => _cutFrom.TotalSeconds;
        set => CutFrom = TimeSpan.FromSeconds(value);
    }

    /// <summary>Convenience accessor for UI/timeline code that works in fractional seconds.</summary>
    [JsonIgnore]
    public double CutToSeconds
    {
        get => _cutTo.TotalSeconds;
        set => CutTo = TimeSpan.FromSeconds(value);
    }

    /// <summary>
    /// Computes the output file path for this cut. Pure — does NOT create directories
    /// (the original WPF version had Directory.CreateDirectory in this getter, which is a
    /// side effect callers can't see). Caller is responsible for ensuring the directory
    /// exists before writing.
    /// </summary>
    public string ComputeOutputFile(Settings settings)
    {
        if (string.IsNullOrEmpty(_sourceFile))
            throw new InvalidOperationException("SourceFile not set");

        var ext = Path.GetExtension(_sourceFile);
        var stem = Path.GetFileNameWithoutExtension(_sourceFile);
        var suffix = $"-{_cutFrom:hh\\.mm\\.ss\\.fff}-{_cutTo:hh\\.mm\\.ss\\.fff}{ext}";

        var outputDir = settings.SaveToSourceFolder
            ? Path.GetDirectoryName(_sourceFile) ?? string.Empty
            : settings.OutputDirectory;

        // Let any registered output-path plugin transform the directory (e.g. route
        // per-model into subfolders). Plugins are passed the SaveToSourceFolder flag and
        // typically short-circuit when it's on. Multiple plugins compose left-to-right.
        var pathContext = new OutputPathContext { SaveToSourceFolder = settings.SaveToSourceFolder };
        foreach (var plugin in PluginHost.Get<IOutputPathPlugin>())
            outputDir = plugin.TransformOutputDirectory(_sourceFile, outputDir, pathContext);

        return Path.Combine(outputDir, $"{stem}{suffix}");
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (max < min) max = min;
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
