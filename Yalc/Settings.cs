using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace YetAnotherLosslessCutter;

// Source-generated JSON metadata for Settings — keeps NativeAOT happy by avoiding
// reflection-based serialization. UseStringEnumConverter writes FilePickerSortMode
// as its name (e.g. "Newest") so the file stays stable across enum reordering.
// AllowNamedFloatingPointLiterals lets WindowLeft/WindowTop round-trip when
// they're double.NaN (the default sentinel for "no recorded position"); without
// this, every save threw ArgumentException because plain JSON forbids NaN.
// List<string> is registered explicitly: under NativeAOT the source generator
// needs the closed generic to be reachable from a [JsonSerializable] attribute,
// otherwise serialize throws at runtime (the load path doesn't exercise it
// when "RecentFiles": [] is empty, which is why the bug only surfaces on save).
[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(List<string>))]
internal partial class SettingsJsonContext : JsonSerializerContext { }

/// <summary>
/// File-picker sort mode. Replaces the original "six mutually-exclusive bools" pattern
/// with a single enum — eliminates the radio-button logic in code and the silent-bug
/// risk where two flags could be true at once.
/// </summary>
public enum FilePickerSortMode
{
    None,
    Newest,
    Oldest,
    Smallest,
    Random,
    MostFiles,
    LeastFiles,
}

/// <summary>
/// Application settings. Persists to Settings.json next to the executable.
/// Auto-saves on any property change (debounced 300ms) — no need to call SaveSettings()
/// manually as the original required.
/// </summary>
public sealed class Settings : ViewModelBase
{
    private static Settings? _instance;
    public static Settings Instance => _instance ??= Load();

    private static readonly string _settingsLocation = Path.Combine(
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
        "Settings.json");

    private static Settings Load()
    {
        var settings = new Settings();
        try
        {
            if (File.Exists(_settingsLocation))
            {
                var json = File.ReadAllText(_settingsLocation);
                settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.Settings) ?? new Settings();
            }
        }
        catch
        {
            // Corrupt settings file — fall back to defaults rather than crash.
        }

        // Subscribe AFTER load so deserialization itself doesn't trigger a save.
        settings.PropertyChanged += (_, _) => settings.RequestSave();
        return settings;
    }

    // Debounce via System.Threading.Timer rather than Task.Run+Task.Delay. Timer fires
    // its callback on a thread-pool thread directly — fewer moving parts, no async state
    // machine, no chance of an unobserved task exception eating the save attempt.
    // Initialized lazily on first request (the singleton instance is reused across
    // deserialization, but Timer creation is cheap).
    private Timer? _saveTimer;
    private readonly object _saveLock = new();

    private void RequestSave()
    {
        lock (_saveLock)
        {
            _saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(300, Timeout.Infinite);
        }
    }

    /// <summary>
    /// Cancels any pending debounced save and writes immediately on the calling thread.
    /// Call from the app's Closing handler so a setting toggled seconds before exit
    /// doesn't get eaten by the 300ms debounce when the process tears down.
    /// </summary>
    public void FlushSave()
    {
        lock (_saveLock)
        {
            _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        SaveNow();
    }

    private void SaveNow()
    {
        try
        {
            File.WriteAllText(_settingsLocation, JsonSerializer.Serialize(this, SettingsJsonContext.Default.Settings));
        }
        catch
        {
            // Disk full / perms issue — non-fatal. Worst case: settings don't persist this session.
        }
    }

    // ----- Cutting -----

    private bool _removeAudio;
    public bool RemoveAudio { get => _removeAudio; set => Set(ref _removeAudio, value); }

    private bool _mergeSegments;
    public bool MergeSegments { get => _mergeSegments; set => Set(ref _mergeSegments, value); }

    private bool _includeAllStreams = true;
    public bool IncludeAllStreams { get => _includeAllStreams; set => Set(ref _includeAllStreams, value); }

    private bool _deleteSourceFileAfterDone;
    public bool DeleteSourceFileAfterDone { get => _deleteSourceFileAfterDone; set => Set(ref _deleteSourceFileAfterDone, value); }

    private bool _lowCuttingProcessPriority;
    public bool LowCuttingProcessPriority { get => _lowCuttingProcessPriority; set => Set(ref _lowCuttingProcessPriority, value); }

    // ----- Output -----

    private bool _saveToSourceFolder = true;
    public bool SaveToSourceFolder { get => _saveToSourceFolder; set => Set(ref _saveToSourceFolder, value); }

    private string _outputDirectory = string.Empty;
    public string OutputDirectory { get => _outputDirectory; set => Set(ref _outputDirectory, value); }

    private string _outputFilenameTemplate = OutputTemplate.Default;
    /// <summary>
    /// Template for the per-cut output filename. Tokens (case-insensitive):
    /// <c>{name}</c> · <c>{ext}</c> · <c>{start}</c> · <c>{end}</c> · <c>{duration}</c>
    /// · <c>{date}</c> · <c>{time}</c> · <c>{datetime}</c> · <c>{index}</c>.
    /// Empty / null falls back to <see cref="OutputTemplate.Default"/>. The result is
    /// sanitized for the host filesystem before joining with the output directory.
    /// </summary>
    public string OutputFilenameTemplate { get => _outputFilenameTemplate; set => Set(ref _outputFilenameTemplate, value); }

    // ----- Queue behavior -----

    private bool _autoStartQueue = true;
    public bool AutoStartQueue { get => _autoStartQueue; set => Set(ref _autoStartQueue, value); }

    private bool _removeFinishedSegments;
    public bool RemoveFinishedSegments { get => _removeFinishedSegments; set => Set(ref _removeFinishedSegments, value); }

    private bool _shutdownWhenDone;
    /// <summary>
    /// When true and the queue finishes successfully, schedule a Windows shutdown
    /// after a confirmation countdown. Replaces the old auto-close-app option —
    /// shutting down the PC is the more useful end-of-batch action.
    /// </summary>
    public bool ShutdownWhenDone { get => _shutdownWhenDone; set => Set(ref _shutdownWhenDone, value); }

    // ----- Window / UX -----

    private bool _openMaximized;
    public bool OpenMaximized { get => _openMaximized; set => Set(ref _openMaximized, value); }

    private bool _showConfirmationPrompts = true;
    public bool ShowConfirmationPrompts { get => _showConfirmationPrompts; set => Set(ref _showConfirmationPrompts, value); }

    private bool _generateTimelineFrames = true;
    /// <summary>
    /// When false, the timeline strip skips both the base-layer thumbnail extraction
    /// and the on-demand zoom-layer regeneration. The strip falls back to its plain
    /// dark background — useful on slow disks or huge files where ffmpeg-extracted
    /// thumbnails would otherwise stall the UI for several seconds.
    /// </summary>
    public bool GenerateTimelineFrames { get => _generateTimelineFrames; set => Set(ref _generateTimelineFrames, value); }

    private bool _generateWaveform;
    /// <summary>
    /// When true, audio peak amplitudes are extracted on file load and drawn as a
    /// translucent waveform overlay on the timeline strip. Default off — waveform
    /// extraction shells ffmpeg to read the entire audio stream which can take a few
    /// seconds on long files; opt-in keeps fast file-open feel for users who don't
    /// need it.
    /// </summary>
    public bool GenerateWaveform { get => _generateWaveform; set => Set(ref _generateWaveform, value); }

    private int _autoSeekDelayMs = 100;
    /// <summary>
    /// Pause (ms) between consecutive auto-repeat seeks (right-click on a jump
    /// button). Adds a deliberate visual-perception window between firings so the
    /// user can actually see what they're scrubbing past. 0 = fire as fast as mpv
    /// reports each frame ready (snappy but hard to read on long files). Range 0..1000.
    /// </summary>
    public int AutoSeekDelayMs { get => _autoSeekDelayMs; set => Set(ref _autoSeekDelayMs, value); }

    private bool _alwaysMuteAudio;
    /// <summary>
    /// When true, every loaded file starts with mpv's audio stream disabled
    /// (<c>aid=no</c>). Default off — auto-mute only kicks in when the probe
    /// detects truncated audio. Useful for users who routinely scrub long
    /// stream-recorded files and don't care about audio during preview.
    /// </summary>
    public bool AlwaysMuteAudio { get => _alwaysMuteAudio; set => Set(ref _alwaysMuteAudio, value); }

    // ----- Silence detection (auto-cut) -----

    private double _silenceThresholdDb = -30.0;
    /// <summary>
    /// Threshold (dBFS, negative) below which audio counts as silent for the auto-cut
    /// detector. -30 is a sensible default for stream/voice content; lower (e.g., -40)
    /// is stricter (only true silence cuts), higher (e.g., -20) is looser (catches
    /// quiet passages too).
    /// </summary>
    public double SilenceThresholdDb { get => _silenceThresholdDb; set => Set(ref _silenceThresholdDb, value); }

    private double _silenceMinDurationSeconds = 0.5;
    /// <summary>
    /// Absolute floor for "what counts as a cut-point silence". Combined with the
    /// percent knob below as <c>max(floor, percent × duration)</c> so a fixed
    /// 0.5s threshold doesn't flood a 3-hour file with cuts on every breath, and
    /// short clips still get sensible behavior. Default 0.5s.
    /// </summary>
    public double SilenceMinDurationSeconds { get => _silenceMinDurationSeconds; set => Set(ref _silenceMinDurationSeconds, value); }

    private double _silenceMinDurationPercentOfDuration = 0.1;
    /// <summary>
    /// Min-silence percentage of total file duration. <c>0.1</c> means "at least
    /// 0.1% of the file" — about 10.8s for a 3-hour recording, 0.3s for a 5-minute
    /// clip (floored by <see cref="SilenceMinDurationSeconds"/>). Set to 0 to use
    /// only the absolute floor.
    /// </summary>
    public double SilenceMinDurationPercentOfDuration { get => _silenceMinDurationPercentOfDuration; set => Set(ref _silenceMinDurationPercentOfDuration, value); }

    private double _silenceMinSpeechDurationSeconds = 0.5;
    /// <summary>
    /// Absolute floor for kept-segment duration (seconds). Combined with the percent
    /// knob below as <c>max(floor, percent × duration)</c>: the floor protects short
    /// files from over-filtering, the percent scales for long files where a fixed
    /// threshold would either flood the timeline (too low) or starve a 5-minute clip
    /// (too high). Default 0.5s.
    /// </summary>
    public double SilenceMinSpeechDurationSeconds { get => _silenceMinSpeechDurationSeconds; set => Set(ref _silenceMinSpeechDurationSeconds, value); }

    private double _silenceMinSpeechPercentOfDuration = 0.3;
    /// <summary>
    /// Min-speech percentage of total file duration. <c>0.3</c> means "at least 0.3%
    /// of the file" — about 32s for a 3-hour recording, 0.9s for a 5-minute clip,
    /// floored by <see cref="SilenceMinSpeechDurationSeconds"/> for very short files.
    /// Set to 0 to disable the relative scaling (use only the absolute floor).
    /// </summary>
    public double SilenceMinSpeechPercentOfDuration { get => _silenceMinSpeechPercentOfDuration; set => Set(ref _silenceMinSpeechPercentOfDuration, value); }

    // ----- Interface (toolbar button visibility) -----
    //
    // Each toggle hides one button (or button group) on the main window. The Settings
    // page surfaces these so users can de-clutter the action row without right-clicks
    // (which conflict with the timeline's right-click-to-seek gesture). Defaults: all
    // visible — power users opt out, new users see everything.

    private bool _showJumpButtons = true;
    public bool ShowJumpButtons { get => _showJumpButtons; set => Set(ref _showJumpButtons, value); }

    private bool _showFrameStepButtons = true;
    public bool ShowFrameStepButtons { get => _showFrameStepButtons; set => Set(ref _showFrameStepButtons, value); }

    private bool _showLoopButton = true;
    public bool ShowLoopButton { get => _showLoopButton; set => Set(ref _showLoopButton, value); }

    private bool _showSilenceButton = true;
    public bool ShowSilenceButton { get => _showSilenceButton; set => Set(ref _showSilenceButton, value); }

    private bool _showNextFileButton = true;
    public bool ShowNextFileButton { get => _showNextFileButton; set => Set(ref _showNextFileButton, value); }

    private bool _showDeleteFileButton = true;
    public bool ShowDeleteFileButton { get => _showDeleteFileButton; set => Set(ref _showDeleteFileButton, value); }

    // ----- File picker (Open from Folder) -----

    private string _filePickerFolderPath = string.Empty;
    public string FilePickerFolderPath { get => _filePickerFolderPath; set => Set(ref _filePickerFolderPath, value); }

    private bool _filePickerIncludeSubFolders = true;
    public bool FilePickerIncludeSubFolders { get => _filePickerIncludeSubFolders; set => Set(ref _filePickerIncludeSubFolders, value); }

    private bool _filePickerUseSize;
    public bool FilePickerUseSize { get => _filePickerUseSize; set => Set(ref _filePickerUseSize, value); }

    private double _filePickerSizeMin = 500d;
    public double FilePickerSizeMin { get => _filePickerSizeMin; set => Set(ref _filePickerSizeMin, value); }

    private double _filePickerSizeMax = 9_999_999d;
    public double FilePickerSizeMax { get => _filePickerSizeMax; set => Set(ref _filePickerSizeMax, value); }

    private FilePickerSortMode _filePickerSortMode = FilePickerSortMode.None;
    public FilePickerSortMode FilePickerSortMode { get => _filePickerSortMode; set => Set(ref _filePickerSortMode, value); }

    private bool _includeTSFiles = true;
    public bool IncludeTSFiles { get => _includeTSFiles; set => Set(ref _includeTSFiles, value); }

    // ----- Window state (restored on launch so the app feels less "fresh boot") -----

    private double _windowWidth = 1280;
    public double WindowWidth { get => _windowWidth; set => Set(ref _windowWidth, value); }

    private double _windowHeight = 780;
    public double WindowHeight { get => _windowHeight; set => Set(ref _windowHeight, value); }

    private double _windowLeft = double.NaN;
    public double WindowLeft { get => _windowLeft; set => Set(ref _windowLeft, value); }

    private double _windowTop = double.NaN;
    public double WindowTop { get => _windowTop; set => Set(ref _windowTop, value); }

    private bool _windowMaximized;
    public bool WindowMaximized { get => _windowMaximized; set => Set(ref _windowMaximized, value); }

    private double _leftPanelWidth = 320;
    public double LeftPanelWidth { get => _leftPanelWidth; set => Set(ref _leftPanelWidth, value); }

    // ----- Recent files (capped, MRU first) -----

    private System.Collections.Generic.List<string> _recentFiles = new();
    public System.Collections.Generic.List<string> RecentFiles
    {
        get => _recentFiles;
        set => Set(ref _recentFiles, value);
    }

    /// <summary>
    /// Bumps <paramref name="path"/> to the front of <see cref="RecentFiles"/>,
    /// dedupes case-insensitively, and trims to <c>maxCount</c>. Mutates the
    /// existing list in place — change-notification is fired manually so the
    /// debounced auto-save picks it up.
    /// </summary>
    public void AddRecentFile(string path, int maxCount = 8)
    {
        if (string.IsNullOrEmpty(path)) return;
        _recentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _recentFiles.Insert(0, path);
        if (_recentFiles.Count > maxCount)
            _recentFiles.RemoveRange(maxCount, _recentFiles.Count - maxCount);
        OnPropertyChanged(nameof(RecentFiles));
    }

    public void ClearRecentFiles()
    {
        if (_recentFiles.Count == 0) return;
        _recentFiles.Clear();
        OnPropertyChanged(nameof(RecentFiles));
    }
}
