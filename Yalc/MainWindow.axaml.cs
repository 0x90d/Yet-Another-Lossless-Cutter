using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using YetAnotherLosslessCutter.Controls;
using YetAnotherLosslessCutter.Cutting;
using YetAnotherLosslessCutter.Mpv;
using YetAnotherLosslessCutter.Plugins;

namespace YetAnotherLosslessCutter;

public partial class MainWindow : Window
{
    private readonly MpvPlayer _player = new();
    private readonly NativeFfmpegThumbnailExtractor _extractor = new();
    private readonly ObservableCollection<VideoSegment> _queueSegments = new();
    private readonly System.Collections.Generic.List<string> _filePlaylist = new();
    private int _playlistIndex = -1;
    private double _duration;
    private bool _isPaused = true;
    private string? _currentFile;
    private string? _extractorLoadedPath;
    private CancellationTokenSource? _baseThumbCts;
    private CancellationTokenSource? _zoomThumbCts;
    private CancellationTokenSource? _waveformCts;
    private CancellationTokenSource? _probeCts;
    private DispatcherTimer? _zoomDebounceTimer;
    private DispatcherTimer? _queueSaveDebounce;

    // Base layer is just enough to fill the visible strip (~20 cells) plus some hover variety.
    // For finer detail we rely on the on-demand zoom layer, which kicks in early.
    private const double ZoomLayerThreshold = 2.0;
    private const int BaseLayerCount = 40;
    private const int ZoomLayerCount = 100;
    private const int MaxZoomLayers = 8;             // LRU cap — keep N most recent zoom layers

    public MainWindow()
    {
        InitializeComponent();

        // Restore window geometry from last session BEFORE the window shows so
        // the user doesn't see it pop up at default-size and then jump.
        ApplyPersistedWindowState();

        VideoView.Player = _player;
        SegmentList.ItemsSource = Timeline.Segments;
        QueueList.ItemsSource = _queueSegments;
        UpdateEmptyStateVisibility();
        UpdateQueueRemaining();
        UpdateActiveSettingsBadges();
        RefreshRecentFilesFlyout();

        // Auto-save the queue on every mutation (add/remove/status change) instead
        // of only on graceful shutdown. A force-kill (debugger Stop, OS task-kill,
        // crash) would otherwise lose the queue. Debounced 500ms so a batch op
        // (e.g. enqueueing 30 segments at once) only writes once.
        _queueSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _queueSaveDebounce.Tick += (_, _) =>
        {
            _queueSaveDebounce!.Stop();
            try { QueuePersistence.Save(_queueSegments); } catch { }
        };

        _queueSegments.CollectionChanged += (_, args) =>
        {
            if (args.NewItems != null)
                foreach (VideoSegment s in args.NewItems)
                    s.PropertyChanged += OnQueueItemPropertyChanged;
            if (args.OldItems != null)
                foreach (VideoSegment s in args.OldItems)
                    s.PropertyChanged -= OnQueueItemPropertyChanged;
            KickQueueSave();
            UpdateEmptyStateVisibility();
            UpdateQueueRemaining();
        };
        Timeline.Segments.CollectionChanged += (_, _) => UpdateEmptyStateVisibility();

        // Restore the persisted queue from a previous session (if any). Items come back
        // as Waiting (or Failed); we never auto-start the runner — the user kicks it off
        // explicitly via the Start button or by cutting a new segment.
        foreach (var seg in QueuePersistence.Load())
            _queueSegments.Add(seg);

        // Cut-button label tracks AutoStartQueue so the user can see at a glance
        // whether pressing it will start cutting immediately or just queue.
        UpdateCutButtonLabel();
        Settings.Instance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Settings.AutoStartQueue))
                Dispatcher.UIThread.Post(UpdateCutButtonLabel);
            // Any of these going on/off changes which pills we render.
            if (args.PropertyName == nameof(Settings.SaveToSourceFolder)
                || args.PropertyName == nameof(Settings.AutoStartQueue)
                || args.PropertyName == nameof(Settings.ShutdownWhenDone)
                || args.PropertyName == nameof(Settings.DeleteSourceFileAfterDone))
                Dispatcher.UIThread.Post(UpdateActiveSettingsBadges);
        };

        // Plugins call PluginHost.NotifyBadgesChanged() when their own settings flip;
        // bounce onto the UI thread because the notification might originate anywhere.
        PluginHost.BadgesChanged += () => Dispatcher.UIThread.Post(UpdateActiveSettingsBadges);

        // Drag-drop: drop a file (or several) anywhere on the window to load them
        // as a playlist. Mirrors the WPF Window's PreviewDrop, but supports multi-file.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Last-file crash recovery: if lastfile.txt exists at launch, the previous
        // session didn't shut down cleanly. Offer to reload that file. Runs after
        // the window is shown so the dialog has a parent to center on.
        Opened += async (_, _) => await TryRecoverLastFileAsync();

        _player.DurationChanged += d => Dispatcher.UIThread.Post(() =>
        {
            _duration = d;
            Timeline.Duration = d;
            DurationLabel.Text = FormatTime(d);
        });
        _player.TimePosChanged += t => Dispatcher.UIThread.Post(() =>
        {
            // Update playhead — but don't fight a drag.
            Timeline.Position = Math.Clamp(t, 0, _duration > 0 ? _duration : 0);
            PositionLabel.Text = FormatTime(t);
        });
        _player.PauseChanged += p => Dispatcher.UIThread.Post(() =>
        {
            _isPaused = p;
            PlayPauseButton.Content = p ? "▶ Play" : "❚❚ Pause";
        });
        _player.FileLoaded += () => Dispatcher.UIThread.Post(async () =>
        {
            // New file = fresh full-view (the timeline preserves zoom across mpv
            // duration re-reports for the same file, so it won't reset itself).
            Timeline.ResetView();
            // Surface any chapter markers the file ships with. Most stream-recorder
            // .ts files have none; mainstream MKV/MP4s usually do.
            Timeline.Markers.Clear();
            foreach (var t in _player.GetChapterTimes()) Timeline.Markers.Add(t);
            if (Settings.Instance.GenerateTimelineFrames)
                SetStatus("loaded — generating thumbnails…");
            if (_currentFile != null)
            {
                // Waveform runs in parallel — independent ffmpeg pipe, no shared state
                // with the thumbnail extractor. Fire-and-forget; failures stay quiet.
                _ = GenerateWaveformAsync(_currentFile);
                await GenerateThumbnailsAsync(_currentFile);
            }
        });
        _player.EndFile += () => Dispatcher.UIThread.Post(() =>
        {
            SetStatus(null);
            // Belt-and-suspenders for auto-repeat: if mpv reaches EOF while auto-repeat
            // is on, stop firing so we don't keep nudging the seek past the boundary.
            // The PlaybackRestart-side direction check usually catches this first, but
            // some containers don't emit PlaybackRestart on a clamped-past-EOF seek.
            StopAutoRepeat();
        });

        // Auto-repeat fires the next jump after the new frame is decoded + a brief pause.
        // This mirrors WPF's render-tick pattern (variable-rate, lets the user actually
        // see what they're scrubbing past) instead of a fixed-interval timer.
        _player.PlaybackRestart += () => Dispatcher.UIThread.Post(OnPlayerPlaybackRestart);

        Timeline.PositionDragged += (_, e) =>
        {
            if (!_player.IsInitialized) return;
            var t = e.Time;
            // Seeking exactly to Duration lands past the last frame and mpv goes EOF
            // (user sees no frame). Back off ~50ms whenever any path lands at/past the
            // end — covers End key, drag-to-edge, programmatic seeks alike.
            if (_duration > 0 && t >= _duration - 0.001) t = Math.Max(0, _duration - 0.05);
            _player.SeekAbsolute(t, exact: true);
        };

        // Mirror segment-list selection onto the timeline so the selected band gets a
        // contrasting highlight — useful when a segment is too short to find by eye.
        SegmentList.SelectionChanged += (_, _) =>
            Timeline.SelectedSegment = SegmentList.SelectedItem as VideoSegment;

        // Context-menu actions: timeline raises events so it doesn't need to know
        // about the queue / confirmation policy / segment-creation defaults.
        Timeline.AddSegmentRequested += (_, e) =>
        {
            if (_duration <= 0) return;
            var endSec = Math.Min(e.Time + 5, _duration);
            AddSegmentInternal(e.Time, endSec);
        };
        Timeline.RemoveAllSegmentsRequested += (_, _) => ClearSegmentsButton_Click(this, new RoutedEventArgs());

        // Double-click on a segment body → seek to its start and play.
        Timeline.PlaySegmentRequested += (_, e) =>
        {
            if (!_player.IsInitialized) return;
            _player.SeekAbsolute(e.Segment.CutFromSeconds, exact: true);
            _player.Play();
        };

        // Debounce zoom-layer regeneration: kick the timer whenever ViewStart/ViewEnd change.
        _zoomDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _zoomDebounceTimer.Tick += (_, _) =>
        {
            _zoomDebounceTimer.Stop();
            _ = RegenerateZoomLayerAsync();
        };
        TimelineControl.ViewStartProperty.Changed.AddClassHandler<TimelineControl>((_, _) => KickZoomDebounce());
        TimelineControl.ViewEndProperty.Changed.AddClassHandler<TimelineControl>((_, _) => KickZoomDebounce());

        KeyDown += OnKeyDown;

        // Any wheel anywhere in the window stops auto-repeat (matches WPF behavior:
        // "right-click +10s to auto-advance, scroll to stop").
        AddHandler(PointerWheelChangedEvent, (_, _) => StopAutoRepeat(),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        Closing += (_, _) =>
        {
            try { _baseThumbCts?.Cancel(); } catch { }
            try { _zoomThumbCts?.Cancel(); } catch { }
            try { _probeCts?.Cancel(); } catch { }
            try { _zoomDebounceTimer?.Stop(); } catch { }
            try { _queueSaveDebounce?.Stop(); } catch { }
            try { StopAutoRepeat(); } catch { }
            // Cancel any running ffmpeg cut so the cutter process tree dies cleanly,
            // then wait briefly for the runner Task to settle before we dispose mpv.
            try { _runnerCts?.Cancel(); } catch { }
            try { _runnerTask?.Wait(2000); } catch { }
            // Persist queue BEFORE the player goes away — we want to capture statuses
            // as they currently are. A Running item gets flushed back to Waiting by
            // QueueItemDto.From so the user can retry next session.
            try { QueuePersistence.Save(_queueSegments); } catch { }
            // Window geometry — size, position, maximized state, panel split.
            try { PersistWindowState(); } catch { }
            // Flush any pending debounced settings save before the process exits —
            // otherwise a setting toggled seconds before close (or the WindowState
            // writes from PersistWindowState above) gets eaten by the 300ms debounce.
            try { Settings.Instance.FlushSave(); } catch { }
            // Clean shutdown — clear the crash marker so we don't prompt next launch.
            ClearLastSession();
            // Dispose the extractor first — it's the one with background extraction tasks
            // that could be holding the mpv handle. Its command lock blocks Dispose until
            // the in-flight task settles cleanly.
            try { _extractor.Dispose(); } catch { }
            try { _player.Dispose(); } catch { }
        };
    }

    private async Task EnsureExtractorLoadedAsync(string path, CancellationToken ct)
    {
        if (_extractorLoadedPath == path) return;
        await _extractor.LoadFileAsync(path, ct);
        _extractorLoadedPath = path;
    }

    private void KickZoomDebounce()
    {
        if (_zoomDebounceTimer == null) return;
        _zoomDebounceTimer.Stop();
        _zoomDebounceTimer.Start();
    }

    // --- Window state persistence ---

    private void ApplyPersistedWindowState()
    {
        var s = Settings.Instance;
        if (s.WindowWidth >= 480) Width = s.WindowWidth;
        if (s.WindowHeight >= 320) Height = s.WindowHeight;
        // NaN guard — first launch has no recorded position.
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)s.WindowLeft, (int)s.WindowTop);
        }
        // OpenMaximized = user wants every launch maximized regardless of last close
        // state. WindowMaximized = auto-restore of the last session's state. Either
        // wins.
        if (s.OpenMaximized || s.WindowMaximized) WindowState = WindowState.Maximized;

        // Restore left-panel width — addressed via the Grid's ColumnDefinitions
        // because Avalonia doesn't generate code-behind fields for ColumnDefinitions
        // even when x:Name'd.
        if (BodyGrid != null && s.LeftPanelWidth >= 200 &&
            BodyGrid.ColumnDefinitions.Count > 0)
        {
            BodyGrid.ColumnDefinitions[0].Width = new GridLength(s.LeftPanelWidth);
        }
    }

    private void PersistWindowState()
    {
        var s = Settings.Instance;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        // Only record size/position when NOT maximized — otherwise we'd save the
        // virtual-desktop dimensions and the next launch would open at full-screen
        // size in windowed mode.
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Width;
            s.WindowHeight = Height;
            s.WindowLeft = Position.X;
            s.WindowTop = Position.Y;
        }
        // GridSplitter mutates the ColumnDefinition's ActualWidth — read it back
        // and persist so the next launch starts at the user's preferred split.
        if (BodyGrid != null && BodyGrid.ColumnDefinitions.Count > 0)
        {
            var w = BodyGrid.ColumnDefinitions[0].ActualWidth;
            if (w >= 200) s.LeftPanelWidth = w;
        }
    }

    // --- Empty-state visibility ---

    /// <summary>
    /// Toggle the "drop a video here" / "no items" / "no segments" placeholder
    /// overlays based on whether the corresponding state actually has content.
    /// Cheap to call on every change.
    /// </summary>
    private void UpdateEmptyStateVisibility()
    {
        if (VideoEmptyState != null)
            VideoEmptyState.IsVisible = string.IsNullOrEmpty(_currentFile);
        if (QueueEmptyState != null)
            QueueEmptyState.IsVisible = _queueSegments.Count == 0;
        if (SegmentsEmptyState != null)
            SegmentsEmptyState.IsVisible = Timeline.Segments.Count == 0;
    }

    // --- Active settings badges ---

    /// <summary>
    /// Render the small status pills below the file label so users can see at a
    /// glance which output / queue modes are active without opening the cog.
    /// Each pill has a colored dot (active = accent, inactive = grey).
    /// </summary>
    private void UpdateActiveSettingsBadges()
    {
        if (ActiveSettingsBadges == null) return;
        var s = Settings.Instance;
        ActiveSettingsBadges.Children.Clear();
        AddBadge("Source folder", s.SaveToSourceFolder);
        AddBadge("Auto-start", s.AutoStartQueue);
        AddBadge("Shutdown", s.ShutdownWhenDone);
        AddBadge("Auto-delete", s.DeleteSourceFileAfterDone);

        // Plugin-contributed badges follow the built-in ones. Plugins decide their own
        // visibility (e.g. by reading their own settings) and just hand back the list.
        foreach (var provider in PluginHost.Get<IStatusBadgeProvider>())
        {
            foreach (var badge in provider.GetBadges())
                AddBadge(badge.Label, badge.Active);
        }
    }

    private void AddBadge(string label, bool active)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 6, Height = 6,
            Margin = new Thickness(0, 0, 5, 0),
            Fill = active
                ? new SolidColorBrush(Color.Parse("#4caf50"))
                : new SolidColorBrush(Color.Parse("#555555")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(active
                ? Color.Parse("#cccccc")
                : Color.Parse("#666666")),
        };
        var pill = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Margin = new Thickness(0, 0, 12, 0),
        };
        pill.Children.Add(dot);
        pill.Children.Add(text);
        ActiveSettingsBadges.Children.Add(pill);
    }

    // --- Recent files ---

    private void RememberRecentFile(string path)
    {
        Settings.Instance.AddRecentFile(path);
        RefreshRecentFilesFlyout();
    }

    private void RefreshRecentFilesFlyout()
    {
        // Avalonia doesn't generate fields for x:Name'd MenuFlyout, so reach it
        // through the parent button's Flyout property.
        if (RecentFilesButton?.Flyout is not MenuFlyout menu) return;
        menu.Items.Clear();
        var recents = Settings.Instance.RecentFiles;
        if (recents.Count == 0)
        {
            var empty = new MenuItem { Header = "(no recent files)", IsEnabled = false };
            menu.Items.Add(empty);
            return;
        }
        foreach (var path in recents)
        {
            var item = new MenuItem
            {
                Header = Path.GetFileName(path),
                Tag = path,
                // Tooltip shows full path so users can disambiguate same-named files.
            };
            ToolTip.SetTip(item, path);
            item.Click += RecentFile_Click;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear recent files" };
        clear.Click += (_, _) =>
        {
            Settings.Instance.ClearRecentFiles();
            RefreshRecentFilesFlyout();
        };
        menu.Items.Add(clear);
    }

    private void RecentFile_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not string path) return;
        if (!File.Exists(path))
        {
            SetStatus($"file no longer exists: {path}");
            // Drop it from the list so the menu doesn't keep showing it.
            Settings.Instance.RecentFiles.RemoveAll(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            RefreshRecentFilesFlyout();
            return;
        }
        _filePlaylist.Clear();
        _filePlaylist.Add(path);
        _playlistIndex = 0;
        LoadCurrentPlaylistItem();
    }

    private void KickQueueSave()
    {
        if (_queueSaveDebounce == null) return;
        _queueSaveDebounce.Stop();
        _queueSaveDebounce.Start();
    }

    /// <summary>
    /// Queue items raise PropertyChanged on Status / Progress / FailureReason; we
    /// only care about the ones that affect what's actually persisted (Status and
    /// FailureReason). Progress is per-cut state that's not worth touching disk for.
    /// </summary>
    private void OnQueueItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Segment.Status) || e.PropertyName == nameof(VideoSegment.FailureReason))
            KickQueueSave();
        if (e.PropertyName == nameof(Segment.Status))
            UpdateQueueRemaining();
    }

    /// <summary>
    /// Renders the "total left: X" footer under the queue list. Counts items still
    /// to process (Waiting + Running). Hidden when nothing is pending so the queue
    /// area is silent in the steady state.
    /// </summary>
    private void UpdateQueueRemaining()
    {
        if (QueueRemainingLabel == null) return;
        var remaining = 0;
        foreach (var s in _queueSegments)
        {
            if (s.MarkedForDeletion) continue;
            if (s.Status == ProgressStatus.Waiting || s.Status == ProgressStatus.Running)
                remaining++;
        }
        if (remaining > 0)
        {
            QueueRemainingLabel.Text = $"total left: {remaining}";
            QueueRemainingLabel.IsVisible = true;
        }
        else
        {
            QueueRemainingLabel.IsVisible = false;
        }
    }

    /// <summary>
    /// Updates the timeline overlay text. Use null/empty to clear. Replaces the old
    /// top-left StatusLabel — frame-generation progress and other transient hints
    /// land here directly above the strip.
    /// </summary>
    private void SetStatus(string? text)
    {
        Timeline.StatusText = string.IsNullOrEmpty(text) ? null : text;
    }

    private static string FormatFileSize(long bytes)
    {
        // Binary (KiB/MiB/GiB) magnitudes labeled with the casual KB/MB/GB suffixes
        // — matches what File Explorer shows on Windows, which is what users compare against.
        const double kb = 1024d;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        if (bytes >= gb) return (bytes / gb).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        if (bytes >= mb) return (bytes / mb).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        if (bytes >= kb) return (bytes / kb).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        return bytes + " B";
    }

    private static string TryFormatFileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? FormatFileSize(info.Length) : string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Decode the file's audio into peak-amplitude buckets and feed them to the
    /// timeline's waveform overlay. Independent of the thumbnail pipeline — runs in
    /// parallel and fails quietly if there's no audio / ffmpeg's missing.
    /// </summary>
    private async Task GenerateWaveformAsync(string path)
    {
        if (!Settings.Instance.GenerateWaveform) return;

        // Same duration-availability wait as the thumbnail extractor — mpv's
        // DurationChanged event may not have arrived yet right after FileLoaded.
        for (var i = 0; i < 20 && _duration <= 0; i++)
            await Task.Delay(50);
        if (_duration <= 0) return;

        _waveformCts?.Cancel();
        _waveformCts = new CancellationTokenSource();
        var ct = _waveformCts.Token;
        var duration = _duration;

        try
        {
            var peaks = await AudioPeakExtractor.ExtractAsync(path, duration, ct: ct);
            if (ct.IsCancellationRequested) return;
            if (_currentFile != path) return;
            if (peaks != null) Timeline.AudioPeaks = peaks;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus("waveform error: " + ex.Message);
        }
    }

    private async Task GenerateThumbnailsAsync(string path)
    {
        // Wait briefly for mpv to report SOMETHING, then probe with ffprobe in
        // parallel for an authoritative duration. mpv mis-reports for streaming
        // containers (.ts) where audio drops out partway through — it'll latch onto
        // the audio's duration first and only later notice the video runs longer.
        // ffprobe reads format.duration which reflects the file's actual extent.
        for (var i = 0; i < 20 && _duration <= 0; i++)
            await Task.Delay(50);

        double duration = _duration;
        try
        {
            var info = await MediaInfoProbe.ProbeAsync(path);
            if (info != null && info.Duration.TotalSeconds > 0)
            {
                var probed = info.Duration.TotalSeconds;
                // Adopt ffprobe's value when it's longer (mpv's short estimate) or
                // when mpv hasn't reported anything yet. A small ±1% gap is normal
                // and not worth swapping for.
                if (duration <= 0 || probed > duration * 1.01)
                {
                    duration = probed;
                    _duration = probed;
                    Timeline.Duration = probed;
                    DurationLabel.Text = FormatTime(probed);
                }
            }
        }
        catch { /* probe is best-effort — fall back to whatever mpv said */ }

        if (duration <= 0)
        {
            SetStatus(null);
            return;
        }

        if (!Settings.Instance.GenerateTimelineFrames)
        {
            SetStatus(null);
            return;
        }

        _baseThumbCts?.Cancel();
        _baseThumbCts = new CancellationTokenSource();
        var ct = _baseThumbCts.Token;

        try
        {
            await EnsureExtractorLoadedAsync(path, ct);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<double>(p => SetStatus($"base thumbnails… {p:P0}"));
            var frames = await _extractor.ExtractRangeAsync(0, duration,
                count: BaseLayerCount, progress: progress, ct: ct);
            sw.Stop();
            if (ct.IsCancellationRequested) return;
            // Identity guard: the file may have changed between the last cancellation
            // check and now. Don't paint frames from `path` onto a different file's timeline.
            if (_currentFile != path) return;

            Timeline.BaseFrames = frames;
            SetStatus(null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus("thumbnail error: " + ex.Message);
        }
    }

    private async Task RegenerateZoomLayerAsync()
    {
        if (_currentFile == null || _duration <= 0) return;
        if (!Settings.Instance.GenerateTimelineFrames) return;

        var viewStart = Timeline.ViewStart;
        var viewEnd = Timeline.ViewEnd;
        var viewDur = viewEnd - viewStart;
        if (viewDur <= 0) return;

        var zoom = _duration / viewDur;
        if (zoom < ZoomLayerThreshold)
        {
            SetStatus(null);
            return;
        }

        // Aspect-correct cell width to estimate how many cells the strip will show.
        var stripWidth = Timeline.Bounds.Width;
        var stripHeight = Math.Max(40, Timeline.Bounds.Height - 18);
        var idealCellW = stripHeight * 16.0 / 9.0;
        var displayCells = Math.Max(1, (int)Math.Floor(stripWidth / idealCellW));
        // We want a frame at least as fine as cell-width / 2 so adjacent cells get distinct frames.
        var requiredSecondsPerFrame = (viewDur / displayCells) * 0.5;

        // Already covered well enough by an existing layer (base or any cached zoom)?
        if (IsViewCoveredByCachedLayer(viewStart, viewEnd, requiredSecondsPerFrame))
        {
            SetStatus(null);
            return;
        }

        // Cap thumb count by source frame rate so we never extract more thumbs than there are
        // unique source frames in the requested span.
        var fps = _extractor.SourceFps > 0 ? _extractor.SourceFps : 30.0;
        var maxUseful = Math.Max(16, (int)Math.Ceiling(viewDur * fps));
        var count = Math.Min(ZoomLayerCount, maxUseful);

        _zoomThumbCts?.Cancel();
        _zoomThumbCts = new CancellationTokenSource();
        var ct = _zoomThumbCts.Token;
        var path = _currentFile;

        try
        {
            await EnsureExtractorLoadedAsync(path, ct);

            SetStatus($"zoom thumbs ({zoom:0.#}×, {count} frames)…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Progress<double>(p =>
                SetStatus($"zoom thumbs ({zoom:0.#}×, {count} frames)… {p:P0}"));
            var frames = await _extractor.ExtractRangeAsync(viewStart, viewEnd,
                count: count, progress: progress, ct: ct);
            sw.Stop();
            if (ct.IsCancellationRequested) return;
            // Identity guard: file may have changed mid-extraction (see GenerateThumbnailsAsync).
            if (_currentFile != path) return;

            Timeline.AddZoomLayer(frames);
            Timeline.TrimZoomLayers(MaxZoomLayers);
            SetStatus(null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus("zoom thumbnail error: " + ex.Message);
        }
    }

    /// <summary>
    /// Returns true if some already-cached layer (base or any zoom layer) covers
    /// [viewStart, viewEnd] with frame density at least as fine as <paramref name="requiredSecondsPerFrame"/>.
    /// </summary>
    private bool IsViewCoveredByCachedLayer(double viewStart, double viewEnd, double requiredSecondsPerFrame)
    {
        foreach (var layer in Timeline.ZoomLayers)
        {
            if (layer.StartTime <= viewStart && layer.EndTime >= viewEnd &&
                layer.SecondsPerFrame <= requiredSecondsPerFrame)
                return true;
        }
        var baseLayer = Timeline.BaseFrames;
        if (baseLayer != null &&
            baseLayer.StartTime <= viewStart && baseLayer.EndTime >= viewEnd &&
            baseLayer.SecondsPerFrame <= requiredSecondsPerFrame)
            return true;
        return false;
    }

    private async void OpenButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open video",
            // Multi-select builds the playlist. After each Cut we advance to the next file.
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video")
                {
                    Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.ts", "*.m4v", "*.flv" }
                },
                FilePickerFileTypes.All
            }
        });
        if (files.Count == 0) return;

        _filePlaylist.Clear();
        foreach (var f in files)
        {
            var p = f.TryGetLocalPath();
            if (!string.IsNullOrEmpty(p)) _filePlaylist.Add(p);
        }
        if (_filePlaylist.Count == 0) return;

        // Recent-files = explicit user-pick events. Bulk loads (Open folder, drag-drop
        // multiple) only record the first to avoid swamping the menu with co-loaded files.
        foreach (var p in _filePlaylist) RememberRecentFile(p);
        _playlistIndex = 0;
        LoadCurrentPlaylistItem();
    }

    private async void OpenFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var win = new OpenFolderWindow();
        var result = await win.ShowDialog<OpenFromFolderViewModel?>(this);
        if (result == null) return;

        if (string.IsNullOrWhiteSpace(result.FolderPath))
        {
            SetStatus("open from folder: no folder selected");
            return;
        }
        if (!Directory.Exists(result.FolderPath))
        {
            SetStatus($"open from folder: not found — {result.FolderPath}");
            return;
        }

        SetStatus("scanning folder…");
        List<string> files;
        try
        {
            // Enumeration + sort can be slow on large recursive folders;
            // run it on a worker so the UI stays responsive.
            files = await Task.Run(() => EnumerateFolderFiles(result));
        }
        catch (Exception ex)
        {
            SetStatus("open from folder failed: " + ex.Message);
            return;
        }

        if (files.Count == 0)
        {
            SetStatus("open from folder: no matching files");
            return;
        }

        _filePlaylist.Clear();
        _filePlaylist.AddRange(files);
        _playlistIndex = 0;
        LoadCurrentPlaylistItem();
        SetStatus($"loaded {files.Count} file(s) from folder");
    }

    private static List<string> EnumerateFolderFiles(OpenFromFolderViewModel vm)
    {
        var now = DateTime.UtcNow;
        var enumOpts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = vm.IncludeSubFolders,
        };

        bool ShouldKeep(FileInfo f)
        {
            var ext = f.Extension.ToLowerInvariant();
            if (ext != ".mp4" && ext != ".mkv" && !(vm.IncludeTSFiles && ext == ".ts"))
                return false;
            if (vm.UseFileSize)
            {
                var mb = f.Length / (1024.0 * 1024.0);
                if (mb < vm.FileSizeMin || mb > vm.FileSizeMax) return false;
            }
            // Skip files created in the last 6 hours so we don't pick up captures
            // that are still being written. Matches the WPF original's guard.
            if (f.CreationTimeUtc.AddHours(6) > now) return false;
            return true;
        }

        var files = new DirectoryInfo(vm.FolderPath)
            .EnumerateFiles("*", enumOpts)
            .Where(ShouldKeep)
            .ToList();

        IEnumerable<FileInfo> sorted = vm.SortMode switch
        {
            FilePickerSortMode.Newest => files.OrderByDescending(f => f.CreationTimeUtc),
            FilePickerSortMode.Oldest => files.OrderBy(f => f.CreationTimeUtc),
            FilePickerSortMode.Smallest => files.OrderBy(f => f.Length),
            FilePickerSortMode.Random => files.OrderBy(_ => Guid.NewGuid()),
            FilePickerSortMode.MostFiles => SortByFolderPopulation(files, descending: true),
            FilePickerSortMode.LeastFiles => SortByFolderPopulation(files, descending: false),
            // None / unspecified: WPF fell back to "largest first" — preserve that.
            _ => files.OrderByDescending(f => f.Length),
        };
        var result = sorted.Select(f => f.FullName).ToList();

        // Hand the post-filter list to any registered IFilePickerFilter so plugins
        // can apply workflow-specific filtering or reordering on top.
        var pickerCtx = new FilePickerContext
        {
            FolderPath = vm.FolderPath,
            IncludeSubFolders = vm.IncludeSubFolders,
        };
        IReadOnlyList<string> view = result;
        foreach (var filter in PluginHost.Get<IFilePickerFilter>())
            view = filter.Apply(view, pickerCtx);

        return view as List<string> ?? view.ToList();
    }

    private async Task TryRecoverLastFileAsync()
    {
        var session = LastSessionStore.Load();
        if (session == null || session.Files.Count == 0) return;

        // Don't prompt if a file is already loaded (e.g. CLI arg in a future round) —
        // that's a deliberate user action and shouldn't be overridden by recovery.
        if (!string.IsNullOrEmpty(_currentFile)) return;

        // Drop entries whose backing file no longer exists, but keep the rest of the
        // playlist together. Map the old index onto the kept slice so the user lands
        // on the file they were actually working on (or the closest survivor).
        var alive = new List<string>(session.Files.Count);
        var oldIndexInAlive = -1;
        for (var i = 0; i < session.Files.Count; i++)
        {
            if (!File.Exists(session.Files[i])) continue;
            if (i == session.CurrentIndex) oldIndexInAlive = alive.Count;
            alive.Add(session.Files[i]);
        }
        if (alive.Count == 0)
        {
            ClearLastSession();
            SetStatus("crash recovery skipped — no recorded files still exist");
            return;
        }
        if (oldIndexInAlive < 0)
            oldIndexInAlive = Math.Min(session.CurrentIndex, alive.Count - 1);

        var summary = alive.Count == 1
            ? alive[0]
            : $"({oldIndexInAlive + 1}/{alive.Count}) starting at: {alive[oldIndexInAlive]}";
        var ok = await ConfirmDialog.ShowAsync(this, "Recover previous session",
            $"YALC didn't shut down cleanly last time.\n\nResume the playlist?\n\n{summary}");
        if (!ok)
        {
            ClearLastSession();
            return;
        }

        _filePlaylist.Clear();
        _filePlaylist.AddRange(alive);
        _playlistIndex = oldIndexInAlive;
        LoadCurrentPlaylistItem();
    }

    // --- Drag-drop ---

    private static readonly HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".ts", ".m4v", ".flv"
    };

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Only accept file drops; reject text/clipboard/etc. so the cursor doesn't lie.
        e.DragEffects = e.DataTransfer?.Contains(DataFormat.File) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var dropped = e.DataTransfer?.TryGetFiles();
        if (dropped == null) return;

        var paths = new List<string>();
        foreach (var f in dropped)
        {
            var p = f.TryGetLocalPath();
            if (string.IsNullOrEmpty(p)) continue;
            if (Directory.Exists(p)) continue; // ignore folders — use Open folder for that
            if (!_videoExtensions.Contains(Path.GetExtension(p))) continue;
            paths.Add(p);
        }
        if (paths.Count == 0)
        {
            SetStatus("drop ignored — no recognized video files");
            return;
        }

        _filePlaylist.Clear();
        _filePlaylist.AddRange(paths);
        foreach (var p in paths) RememberRecentFile(p);
        _playlistIndex = 0;
        LoadCurrentPlaylistItem();
        e.Handled = true;
    }

    // --- Clear-segments ---

    private async void ClearSegmentsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (Timeline.Segments.Count == 0) return;
        if (Settings.Instance.ShowConfirmationPrompts)
        {
            var ok = await ConfirmDialog.ShowAsync(this, "Clear segments",
                $"Remove all {Timeline.Segments.Count} segment(s) from the timeline?");
            if (!ok) return;
        }
        // Mark each as deleted so an in-flight queue runner won't pick them up if
        // they're shared references with the queue list. Then clear the timeline.
        foreach (var seg in Timeline.Segments)
            seg.MarkedForDeletion = true;
        Timeline.Segments.Clear();
    }

    // --- Cut button label ---

    private void UpdateCutButtonLabel()
    {
        // Tracks WPF behavior: when AutoStartQueue is on, "Cut" runs immediately;
        // when off, the button only enqueues — label that explicitly so users aren't
        // surprised by the queue silently growing.
        CutButton.Content = Settings.Instance.AutoStartQueue
            ? "✂ Cut"
            : "+ Add to queue";
    }

    private static IEnumerable<FileInfo> SortByFolderPopulation(List<FileInfo> files, bool descending)
    {
        var counts = files
            .GroupBy(f => f.DirectoryName ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.Count());
        return descending
            ? files.OrderByDescending(f => counts[f.DirectoryName ?? string.Empty])
            : files.OrderBy(f => counts[f.DirectoryName ?? string.Empty]);
    }

    private void LoadCurrentPlaylistItem()
    {
        if (_playlistIndex < 0 || _playlistIndex >= _filePlaylist.Count) return;
        var path = _filePlaylist[_playlistIndex];

        _currentFile = path;
        _extractorLoadedPath = null;
        UpdatePlaylistRemaining();
        FileLabel.Text = path;
        FileSizeLabel.Text = TryFormatFileSize(path);
        SetStatus("loading…");
        Timeline.Segments.Clear();
        Timeline.Markers.Clear();
        Timeline.BaseFrames = null;
        Timeline.AudioPeaks = null;
        Timeline.ClearZoomLayers();
        _waveformCts?.Cancel();
        // Cancel any in-flight thumbnail extraction from the previous file. Otherwise
        // the old extraction can complete after we've cleared BaseFrames and assign
        // OLD-file frames to the NEW file's timeline (the cancel inside
        // GenerateThumbnailsAsync/RegenerateZoomLayerAsync only runs after the
        // FileLoaded event + duration-probe wait, which is far too late).
        _baseThumbCts?.Cancel();
        _zoomThumbCts?.Cancel();
        // Reset the timeline's notion of duration synchronously BEFORE the new file
        // starts loading. Otherwise the prior file's ViewStart/ViewEnd survive until
        // the (async) FileLoaded event runs ResetView — and if mpv's DurationChanged
        // for the new file fires first, OnDurationChanged enters its "preserve zoom"
        // branch (correct for same-file re-reports, wrong here) and the user lands
        // on a brand-new file showing the previous zoom region.
        _duration = 0;
        Timeline.Duration = 0;
        _player.LoadFile(path);
        UpdateNextFileButtonState();
        UpdateDeleteFileButtonState();
        UpdateEmptyStateVisibility();
        WriteLastSession();
        ResetMediaInfo();
        _ = ProbeMediaInfoAsync(path);
    }

    /// <summary>
    /// Refresh the "N left" pill above the file label. Hidden in single-file mode.
    /// "Left" counts files after the current one — at the last file, the counter
    /// reads 0 (the visible "0 left" is intentional, signaling end-of-playlist).
    /// </summary>
    private void UpdatePlaylistRemaining()
    {
        if (PlaylistRemainingPill == null || PlaylistRemainingLabel == null) return;
        if (_filePlaylist.Count <= 1 || _playlistIndex < 0)
        {
            PlaylistRemainingPill.IsVisible = false;
            return;
        }
        var remaining = _filePlaylist.Count - _playlistIndex - 1;
        PlaylistRemainingLabel.Text = $"{remaining} left";
        PlaylistRemainingPill.IsVisible = true;
    }

    /// <summary>
    /// Persist the playlist + current index so a crashed session can be recovered
    /// mid-playlist on next launch (not just the one file that happened to be loaded).
    /// </summary>
    private void WriteLastSession()
    {
        if (_filePlaylist.Count == 0 || _playlistIndex < 0) return;
        LastSessionStore.Save(_filePlaylist, _playlistIndex);
    }

    private static void ClearLastSession() => LastSessionStore.Clear();

    // --- Media info ---

    /// <summary>User's "mute audio stream" toggle, persists across file loads.
    /// True means we've passed <c>aid=no</c> to mpv; the audio stream is skipped.</summary>
    private bool _audioMuted;

    private void ResetMediaInfo()
    {
        // Per-file mute resets to the global default each file load:
        //   AlwaysMuteAudio = false → unmute (audio plays unless probe re-mutes)
        //   AlwaysMuteAudio = true  → stay muted (manual unmute is per-file only)
        // The truncated-audio path in ProbeMediaInfoAsync re-mutes when needed.
        var desiredMuted = Settings.Instance.AlwaysMuteAudio;
        if (_audioMuted != desiredMuted)
        {
            _audioMuted = desiredMuted;
            try { _player.SetAudioEnabled(!desiredMuted); } catch { }
        }
        UpdateAudioToggleVisual();
        NoAudioIcon.IsVisible = false;
        AudioToggleButton.IsVisible = false;
        MediaInfoButton.IsEnabled = false;
        ToolTip.SetTip(MediaInfoButton, "(probing…)");
    }

    private async Task ProbeMediaInfoAsync(string path)
    {
        _probeCts?.Cancel();
        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;
        try
        {
            var info = await MediaInfoProbe.ProbeAsync(path, ct);
            if (ct.IsCancellationRequested) return;
            if (info == null)
            {
                ToolTip.SetTip(MediaInfoButton, "(no media info — ffprobe unavailable)");
                return;
            }
            NoAudioIcon.IsVisible = !info.HasAudio;
            AudioToggleButton.IsVisible = info.HasAudio;
            UpdateAudioToggleVisual();
            MediaInfoButton.IsEnabled = true;
            ToolTip.SetTip(MediaInfoButton, info.ToString());

            // Truncated-audio detection: when the audio stream ends well before
            // the video, mpv re-scans the file looking for audio on every seek
            // past the audio EOF — manifests as laggy scrubbing past that point.
            // Auto-mute on detection so seeks stay fast; the user is told what
            // happened and can flip the toggle back if they want audio for the
            // first portion.
            if (info.HasAudio
                && info.AudioDuration > TimeSpan.Zero
                && info.Duration > TimeSpan.Zero
                && info.AudioDuration < info.Duration - TimeSpan.FromSeconds(30)
                && info.AudioDuration < info.Duration * 0.75)
            {
                _audioMuted = true;
                try { _player.SetAudioEnabled(false); } catch { }
                UpdateAudioToggleVisual();
                ToolTip.SetTip(AudioToggleButton,
                    $"Audio auto-muted (truncated at {info.AudioDuration:mm\\:ss}). " +
                    $"Click to unmute if you want the audio for the first portion.");

                var msg =
                    $"This file's audio stream ends at {info.AudioDuration:mm\\:ss}, " +
                    $"but the video runs to {info.Duration:hh\\:mm\\:ss}.\n\n" +
                    $"Seeking past {info.AudioDuration:mm\\:ss} would otherwise be " +
                    $"slow because mpv has to scan the file looking for audio each " +
                    $"time, so the audio stream has been muted automatically — " +
                    $"seeks will be fast everywhere.\n\n" +
                    $"Click the 🔇 button in the top bar to re-enable audio if you " +
                    $"want to hear the first {info.AudioDuration:mm\\:ss} of the file. " +
                    $"The cut output is unaffected either way — ffmpeg handles the " +
                    $"truncation cleanly and includes whatever audio is present.";
                _ = InfoDialog.ShowAsync(this, "Audio auto-muted (truncated)", msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ToolTip.SetTip(MediaInfoButton, "media info error: " + ex.Message);
        }
    }

    private void AudioToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        _audioMuted = !_audioMuted;
        try { _player.SetAudioEnabled(!_audioMuted); }
        catch { /* mpv may not be initialized yet — toggle state still tracked */ }
        UpdateAudioToggleVisual();
    }

    private void UpdateAudioToggleVisual()
    {
        AudioToggleButton.Content = _audioMuted ? "🔇" : "🔊";
        if (!_audioMuted)
            ToolTip.SetTip(AudioToggleButton, "Mute audio stream (fast seeking on truncated-audio files)");
        else
            ToolTip.SetTip(AudioToggleButton, "Audio muted — click to unmute");
    }

    private bool TryAdvanceToNextFile()
    {
        if (_playlistIndex < 0) return false;
        var nextIndex = _playlistIndex + 1;
        if (nextIndex >= _filePlaylist.Count) return false;
        _playlistIndex = nextIndex;
        LoadCurrentPlaylistItem();
        return true;
    }

    /// <summary>
    /// Stop the player and clear the "current file" UI state. Called whenever the
    /// player should release its OS handle on the source — e.g. before deleting the
    /// file, or after the user has queued a single-file playlist (the queue runner
    /// needs ffmpeg to read the file and the player no longer needs a handle).
    /// Caller is responsible for not invoking this if the user might still want to
    /// preview the file.
    /// </summary>
    private void ReleaseCurrentFile()
    {
        try { _player.Stop(); } catch { }
        _currentFile = null;
        FileLabel.Text = "(no file)";
        FileSizeLabel.Text = string.Empty;
        if (PlaylistRemainingPill != null) PlaylistRemainingPill.IsVisible = false;
        Timeline.Segments.Clear();
        Timeline.Markers.Clear();
        Timeline.BaseFrames = null;
        Timeline.AudioPeaks = null;
        Timeline.ClearZoomLayers();
        _waveformCts?.Cancel();
        // Cancel any in-flight thumbnail extraction from the previous file. Otherwise
        // the old extraction can complete after we've cleared BaseFrames and assign
        // OLD-file frames to the NEW file's timeline (the cancel inside
        // GenerateThumbnailsAsync/RegenerateZoomLayerAsync only runs after the
        // FileLoaded event + duration-probe wait, which is far too late).
        _baseThumbCts?.Cancel();
        _zoomThumbCts?.Cancel();
        SetStatus(null);
        ResetMediaInfo();
        UpdateNextFileButtonState();
        UpdateDeleteFileButtonState();
        ClearLastSession();
    }

    private bool HasNextFile =>
        _playlistIndex >= 0 && _playlistIndex + 1 < _filePlaylist.Count;

    private void UpdateNextFileButtonState() => NextFileButton.IsEnabled = HasNextFile;

    private void NextFileButton_Click(object? sender, RoutedEventArgs e)
    {
        StopAutoRepeat();
        if (!TryAdvanceToNextFile())
            SetStatus("playlist done");
    }

    private void PlayPauseButton_Click(object? sender, RoutedEventArgs e) { StopAutoRepeat(); _player.TogglePause(); }
    private void FrameBackButton_Click(object? sender, RoutedEventArgs e) { StopAutoRepeat(); _player.FrameBackStep(); }
    private void FrameForwardButton_Click(object? sender, RoutedEventArgs e) { StopAutoRepeat(); _player.FrameStep(); }
    private void Back1Button_Click(object? sender, RoutedEventArgs e) { StopAutoRepeat(); Seek(-1); }
    private void Forward1Button_Click(object? sender, RoutedEventArgs e) { StopAutoRepeat(); Seek(+1); }
    private void Back10Button_Click(object? sender, RoutedEventArgs e) { StopAutoRepeat(); Seek(-10); }
    private void Forward10Button_Click(object? sender, RoutedEventArgs e) { StopAutoRepeat(); Seek(+10); }
    private void Back60Button_Click(object? sender, RoutedEventArgs e) { StopAutoRepeat(); Seek(-60); }
    private void Forward60Button_Click(object? sender, RoutedEventArgs e) { StopAutoRepeat(); Seek(+60); }

    // Right-click on a jump button starts auto-repeat: keeps invoking the seek until
    // the user clicks anything else, scrolls the wheel, or right-clicks again.
    private void Back10Button_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        TryStartAutoRepeat(e, "back10", -10);
    private void Back1Button_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        TryStartAutoRepeat(e, "back1", -1);
    private void Forward1Button_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        TryStartAutoRepeat(e, "fwd1", +1);
    private void Forward10Button_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        TryStartAutoRepeat(e, "fwd10", +10);
    private void Back60Button_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        TryStartAutoRepeat(e, "back60", -60);
    private void Forward60Button_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        TryStartAutoRepeat(e, "fwd60", +60);

    /// <summary>
    /// Wheel over a jump button: use the button's Tag (magnitude in seconds) as the
    /// step, with the wheel direction setting the sign. So scroll up over `+10s` = +10s
    /// forward, scroll down over `+10s` = -10s backward. Matches the "wheel near a
    /// specific button uses that button's amount" muscle-memory from the WPF version.
    /// </summary>
    private void JumpButton_Wheel(object? sender, PointerWheelEventArgs e)
    {
        StopAutoRepeat();
        if (sender is not Button btn) return;
        if (btn.Tag is not string tagStr || !double.TryParse(tagStr,
                System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out var magnitude))
            return;
        var delta = magnitude * Math.Sign(e.Delta.Y);
        if (delta == 0) return;
        Seek(delta);
        e.Handled = true;
    }

    /// <summary>
    /// Wheel over the video area = default seek (60s by default, matches WPF).
    /// </summary>
    private void VideoArea_Wheel(object? sender, PointerWheelEventArgs e)
    {
        StopAutoRepeat();
        var delta = TimelineControl.WheelSeekStepSeconds * Math.Sign(e.Delta.Y);
        if (delta == 0) return;
        Seek(delta);
        e.Handled = true;
    }

    // ---- Auto-repeat ----
    // Driven by mpv's PlaybackRestart event: each jump fires when the previous seek's
    // frame is actually decoded and ready to display, so cadence adapts to the file's
    // decode latency. No fixed timer needed (the WPF original used one, with a
    // user-tunable delay; that became redundant once we switched to event-driven).

    private string? _autoRepeatTag;
    private Action? _autoRepeatAction;
    private double _autoRepeatStep;          // signed seconds-per-step
    private double _prevAutoRepeatPos = double.NaN;

    private void TryStartAutoRepeat(PointerPressedEventArgs e, string tag, double delta)
    {
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsRightButtonPressed) return;

        // Right-clicking the same button toggles off.
        if (_autoRepeatTag == tag)
        {
            StopAutoRepeat();
            e.Handled = true;
            return;
        }

        _autoRepeatTag = tag;
        _autoRepeatStep = delta;
        _autoRepeatAction = () => Seek(delta);
        _prevAutoRepeatPos = _player.IsInitialized ? _player.TimePos : double.NaN;
        _autoRepeatAction();   // fire once immediately — playback-restart drives the next firing
        e.Handled = true;
    }

    private void StopAutoRepeat()
    {
        _autoRepeatTag = null;
        _autoRepeatAction = null;
        _autoRepeatStep = 0;
        _prevAutoRepeatPos = double.NaN;
    }

    private async void OnPlayerPlaybackRestart()
    {
        var actionRef = _autoRepeatAction;
        if (actionRef == null) return;

        // Stuck-or-runaway detection. mpv's seek behavior at file boundaries varies by
        // container (esp .ts): seeking past the last decodable frame can either land on
        // the last keyframe AND emit nothing further (stuck), or fall back to a much
        // earlier keyframe (runaway loop, walking through the same range forever). Catch
        // both by comparing the new position to the previous firing's position:
        //   • no movement → stuck at boundary, stop.
        //   • movement opposite to our step direction → mpv backed up, stop.
        // Position-tracking is approximate (mpv updates time-pos asynchronously) so use
        // a small epsilon and trust the sign rather than the magnitude.
        if (_player.IsInitialized && _autoRepeatStep != 0 && !double.IsNaN(_prevAutoRepeatPos))
        {
            var pos = _player.TimePos;
            var moved = pos - _prevAutoRepeatPos;
            if (Math.Abs(moved) < 0.001)
            {
                StopAutoRepeat();
                return;
            }
            if (Math.Sign(moved) != Math.Sign(_autoRepeatStep) && Math.Abs(moved) > 0.5)
            {
                StopAutoRepeat();
                return;
            }
            _prevAutoRepeatPos = pos;
        }

        // Visual-perception delay between firings — gives the user time to read the
        // newly-decoded frame before we move on. Re-verify the action is still the
        // same one after the wait (user might have stopped auto-repeat during it).
        var delayMs = Math.Clamp(Settings.Instance.AutoSeekDelayMs, 0, 1000);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs);
            if (!ReferenceEquals(_autoRepeatAction, actionRef)) return;
        }

        actionRef();
    }

    // Each click here stops auto-repeat first — otherwise the right-click-on-+1m
    // auto-seek would keep firing while the user is setting in/out points or
    // queuing a cut, and (worse) leak across file changes when Cut auto-advances
    // the playlist. Matches the pattern used by the playback/jump click handlers.
    private void SetInButton_Click(object? sender, RoutedEventArgs e) { StopAutoRepeat(); SetInPoint(); }
    private void SetOutButton_Click(object? sender, RoutedEventArgs e) { StopAutoRepeat(); SetOutPoint(); }
    private void AddSegmentButton_Click(object? sender, RoutedEventArgs e) { StopAutoRepeat(); AddSegment(); }
    private void CutButton_Click(object? sender, RoutedEventArgs e) { StopAutoRepeat(); EnqueueAndStart(); }

    // Right-click on these buttons starts auto-seek +1m (matches DeleteFileButton).
    private void SetInButton_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        TryStartAutoRepeat(e, "setin-fwd60", +60);
    private void SetOutButton_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        TryStartAutoRepeat(e, "setout-fwd60", +60);
    private void AddSegmentButton_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        TryStartAutoRepeat(e, "addseg-fwd60", +60);
    private void CutButton_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        TryStartAutoRepeat(e, "cut-fwd60", +60);
    private void NextFileButton_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        TryStartAutoRepeat(e, "next-fwd60", +60);

    /// <summary>
    /// Move all idle timeline segments into the processing queue and (per
    /// <see cref="Settings.AutoStartQueue"/>) start the runner. Segments that are
    /// already queued or finished are skipped — clicking Cut again on the same set
    /// is a no-op rather than re-cutting.
    /// </summary>
    private void EnqueueAndStart()
    {
        if (string.IsNullOrEmpty(_currentFile))
        {
            SetStatus("open a file first");
            return;
        }
        if (Timeline.Segments.Count == 0)
        {
            // No segments == "queue the whole file as-is". Synthesize a 0..duration
            // segment which the cutter's whole-file fast path turns into a byte-copy
            // (no ffmpeg, no re-encode). Bail if there's no usable duration yet —
            // the file probably hasn't finished loading.
            if (_duration <= 0)
            {
                SetStatus("file not loaded yet — wait for duration to be reported");
                return;
            }
            AddSegmentInternal(0, _duration);
        }

        var enqueued = 0;
        foreach (var seg in Timeline.Segments)
        {
            if (seg.Status != ProgressStatus.Idle) continue;
            seg.Status = ProgressStatus.Waiting;
            seg.Progress = 0;
            if (!_queueSegments.Contains(seg))
                _queueSegments.Add(seg);
            enqueued++;
        }

        if (enqueued == 0)
        {
            SetStatus("nothing new to enqueue");
            return;
        }

        // Clear timeline once segments are queued — they're tracked in the Queue panel
        // now, and the user is moving on (typically to the next file).
        Timeline.Segments.Clear();

        if (Settings.Instance.AutoStartQueue)
            StartQueueIfIdle();

        // Advance to next file in the playlist if one exists. If not, release the
        // player — the queue runner needs ffmpeg to read the source (and possibly
        // delete it via DeleteSourceFileAfterDone), and the player has no further
        // use for the file once its segments are queued.
        var advanced = TryAdvanceToNextFile();
        if (!advanced)
        {
            ReleaseCurrentFile();
            SetStatus($"queued {enqueued} segment(s) — playlist done");
        }
    }

    private void StartQueueButton_Click(object? sender, RoutedEventArgs e)
    {
        StopAutoRepeat();
        StartQueueIfIdle();
    }

    private void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        // Modal so the user can't trigger cuts mid-edit. Settings auto-save on change,
        // so closing the window without an explicit Save is fine.
        var win = new SettingsWindow();
        _ = win.ShowDialog(this);
    }

    private async void DeleteSegment_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VideoSegment seg) return;
        await DeleteSegmentInternal(seg);
    }

    /// <summary>
    /// Removes a segment from both the timeline and the queue. Refuses while it's mid-cut
    /// (would orphan a partially-written file) and asks for confirmation when
    /// <see cref="Settings.ShowConfirmationPrompts"/> is on.
    /// </summary>
    private async Task DeleteSegmentInternal(VideoSegment seg)
    {
        if (seg.Status == ProgressStatus.Running)
        {
            SetStatus("can't delete a segment that's currently cutting");
            return;
        }
        if (Settings.Instance.ShowConfirmationPrompts)
        {
            var ok = await ConfirmDialog.ShowAsync(this, "Remove segment",
                $"Remove this segment from the list?\n{seg.CutFrom:hh\\:mm\\:ss\\.fff} → {seg.CutTo:hh\\:mm\\:ss\\.fff}");
            if (!ok) return;
        }
        seg.MarkedForDeletion = true;
        Timeline.Segments.Remove(seg);
        _queueSegments.Remove(seg);
    }

    // --- Context menu handlers (queue + segment list) ---

    private void ContextOpenSourceInExplorer_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not VideoSegment seg) return;
        if (string.IsNullOrEmpty(seg.SourceFile)) return;
        OpenInExplorer(seg.SourceFile);
    }

    private void ContextOpenOutputFolder_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not VideoSegment seg) return;
        string? folder;
        try { folder = Path.GetDirectoryName(seg.ComputeOutputFile(Settings.Instance)); }
        catch (Exception ex)
        {
            SetStatus("open output folder: " + ex.Message);
            return;
        }
        if (string.IsNullOrEmpty(folder)) return;
        // The output folder may not exist yet (cut hasn't run, or a plugin routed
        // it into a subfolder that hasn't been created). Create on demand so
        // Explorer can navigate there.
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex)
        {
            SetStatus("create output folder failed: " + ex.Message);
            return;
        }
        OpenInExplorer(folder);
    }

    private async void ContextCopyError_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not VideoSegment seg) return;
        if (string.IsNullOrEmpty(seg.FailureReason))
        {
            SetStatus("no error to copy");
            return;
        }
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(seg.FailureReason));
            await clipboard.SetDataAsync(transfer);
            SetStatus("error copied to clipboard");
        }
    }

    private async void ContextRemoveFromQueue_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not VideoSegment seg) return;
        if (seg.Status == ProgressStatus.Running)
        {
            SetStatus("can't remove a segment that's currently cutting");
            return;
        }
        if (Settings.Instance.ShowConfirmationPrompts)
        {
            var ok = await ConfirmDialog.ShowAsync(this, "Remove from queue",
                $"Remove from queue?\n{seg.SourceFileName}");
            if (!ok) return;
        }
        seg.MarkedForDeletion = true;
        _queueSegments.Remove(seg);
    }

    private async void ContextRemoveSegment_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not VideoSegment seg) return;
        await DeleteSegmentInternal(seg);
    }

    private void FileLabelOpenInExplorer_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFile))
        {
            SetStatus("no file open");
            return;
        }
        OpenInExplorer(_currentFile);
    }

    /// <summary>
    /// Opens Explorer at <paramref name="path"/>. If <paramref name="path"/> is a file,
    /// selects it within its parent folder; if a directory, opens the directory.
    /// Best-effort — failures are swallowed because there's nothing useful to do.
    /// </summary>
    private static void OpenInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* nothing useful to surface — user can retry */ }
    }

    // --- Delete current file ---

    private void DeleteFileButton_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        TryStartAutoRepeat(e, "delete-fwd60", +60);

    private async void DeleteFileButton_Click(object? sender, RoutedEventArgs e)
    {
        StopAutoRepeat();
        if (string.IsNullOrEmpty(_currentFile)) return;
        var path = _currentFile;

        if (Settings.Instance.ShowConfirmationPrompts)
        {
            var ok = await ConfirmDialog.ShowAsync(this, "Delete file",
                $"Permanently delete this file?\n\n{path}\n\nThis cannot be undone.");
            if (!ok) return;
        }

        // Advance playlist BEFORE deleting so the player loads the next file (which
        // releases the current one's handle). If there's no next file, stop the player
        // explicitly so the OS lock on the current file is released.
        var advanced = TryAdvanceToNextFile();
        if (!advanced)
            ReleaseCurrentFile();

        // mpv's loadfile/stop are queued on its worker thread — the OS handle on the
        // previous file isn't released synchronously. Retry-with-backoff handles the
        // race instead of silently leaving the file on disk.
        await DeleteWithRetryAsync(path, "delete");
    }

    /// <summary>
    /// Delete a file that mpv may still hold an OS handle on. Retries with backoff to
    /// ride out the gap between mpv's queued stop/loadfile command and the actual
    /// handle release. Logs to the error log on final failure (status bar alone is
    /// too easy to miss).
    /// </summary>
    private async Task DeleteWithRetryAsync(string path, string tag)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                File.Delete(path);
                SetStatus($"deleted {Path.GetFileName(path)}");
                return;
            }
            catch (IOException ex)
            {
                lastError = ex;
                await Task.Delay(150 * (attempt + 1));
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        AppendErrorLog($"[{tag}] {path} -> {lastError}");
        SetStatus($"delete failed: {lastError?.Message}");
    }

    private void UpdateDeleteFileButtonState() =>
        DeleteFileButton.IsEnabled = !string.IsNullOrEmpty(_currentFile);

    // --- Failure logging helpers ---

    /// <summary>
    /// Returns a one-line user-friendly summary of an ffmpeg failure: the last non-empty
    /// stderr line, trimmed. The full stderr + command go into the on-disk error log.
    /// </summary>
    private static string SummarizeFailure(FfmpegException ex)
    {
        if (string.IsNullOrWhiteSpace(ex.Stderr))
            return $"ffmpeg exit {ex.ExitCode}";
        var lines = ex.Stderr.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim('\r', ' ', '\t');
            if (line.Length > 0)
                return line.Length > 200 ? line[..200] + "…" : line;
        }
        return $"ffmpeg exit {ex.ExitCode}";
    }

    /// <summary>
    /// Append a timestamped block to <c>last-error.log</c> next to the executable so
    /// the user can grep ffmpeg failures after the fact (the queue tooltip only shows
    /// a one-line summary).
    /// </summary>
    private static void AppendErrorLog(string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), "last-error.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n\n");
        }
        catch
        {
            // Disk-full / permission issue — non-fatal. Failure already shown in UI.
        }
    }

    // ---- Queue runner ----
    // Sequential ffmpeg cutter. One Task lives at a time; new enqueues will wake an
    // idle runner but won't spawn parallel cutters (ffmpeg + the queue's progress
    // accounting are easier to reason about one segment at a time).

    private CancellationTokenSource? _runnerCts;
    private Task? _runnerTask;

    private void StartQueueIfIdle()
    {
        if (_runnerTask is { IsCompleted: false }) return;
        _runnerCts?.Dispose();
        _runnerCts = new CancellationTokenSource();
        var ct = _runnerCts.Token;
        _runnerTask = Task.Run(() => RunQueueAsync(ct));
    }

    private async Task RunQueueAsync(CancellationToken ct)
    {
        var cutter = new FfmpegCutter(Settings.Instance);
        var doneCount = 0;
        var failedCount = 0;

        // Track per-source success/failure for the optional MergeSegments pass below.
        // We can't rely on _queueSegments after the fact because RemoveFinishedSegments
        // may have already cleared finished items out of it.
        var mergeGroups = new Dictionary<string, List<VideoSegment>>(StringComparer.OrdinalIgnoreCase);
        var failedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            // Pick next Waiting segment on the UI thread (safe collection access).
            VideoSegment? next = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var seg in _queueSegments)
                {
                    if (seg.MarkedForDeletion) continue;
                    if (seg.Status == ProgressStatus.Waiting) { next = seg; return; }
                }
            });
            if (next == null) break;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                next.Status = ProgressStatus.Running;
                next.Progress = 0;
                SetStatus($"cutting {next.SourceFileName}…");
            });

            // Progress callback marshals to UI thread (Progress<T> default behavior captures
            // the SyncContext from where it was constructed; we construct inside this UI dispatch).
            IProgress<double>? progress = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                progress = new Progress<double>(p => next.Progress = p);
            });

            try
            {
                var priority = Settings.Instance.LowCuttingProcessPriority
                    ? ProcessPriorityClass.BelowNormal
                    : ProcessPriorityClass.Normal;
                await cutter.CutAsync(next, progress, priority, ct);
                doneCount++;
                if (!mergeGroups.TryGetValue(next.SourceFile, out var group))
                    mergeGroups[next.SourceFile] = group = new List<VideoSegment>();
                group.Add(next);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    next.Status = ProgressStatus.Finished;
                    next.Progress = 1;
                    // Auto-remove finished items from the queue list when the user has
                    // opted into RemoveFinishedSegments (matches WPF behavior).
                    if (Settings.Instance.RemoveFinishedSegments)
                        _queueSegments.Remove(next);
                });

                // After the segment is marked Finished, optionally delete the source.
                // Has to run *after* the UI marshalling above so the "is anything else
                // pending for this source?" check sees the just-completed item as Finished.
                // Skip when MergeSegments is on — the source is still needed by the
                // post-queue concat pass to resolve the cut output paths via
                // VideoSegment.ComputeOutputFile (which dereferences the source path).
                // Auto-delete still runs after the merge step if the user wants both.
                if (Settings.Instance.DeleteSourceFileAfterDone && !Settings.Instance.MergeSegments)
                    await TryAutoDeleteSourceAsync(next.SourceFile);
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() => next.Status = ProgressStatus.Cancelled);
                break;
            }
            catch (FfmpegException ex)
            {
                failedCount++;
                failedSources.Add(next.SourceFile);
                var summary = SummarizeFailure(ex);
                AppendErrorLog($"[{next.SourceFileName}] ffmpeg exit {ex.ExitCode}\n  cmd: {ex.Command}\n  stderr:\n{ex.Stderr}");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    next.FailureReason = summary;
                    next.Status = ProgressStatus.Failed;
                    SetStatus($"cut failed — {summary} (right-click queue item for details)");
                });
            }
            catch (Exception ex)
            {
                failedCount++;
                failedSources.Add(next.SourceFile);
                AppendErrorLog($"[{next.SourceFileName}] unexpected: {ex}");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    next.FailureReason = ex.Message;
                    next.Status = ProgressStatus.Failed;
                    SetStatus($"cut failed — {ex.Message} (right-click queue item for details)");
                });
            }
        }

        // MergeSegments: after the cut loop, concat each source's outputs into a single
        // merged file. Skips sources where any segment failed (concat would either drop
        // the missing segment silently or fail the whole pass — neither is what the user
        // expects). Only runs when there are at least 2 segments to combine.
        if (!ct.IsCancellationRequested && Settings.Instance.MergeSegments)
        {
            foreach (var (source, segs) in mergeGroups)
            {
                if (failedSources.Contains(source)) continue;
                if (segs.Count < 2) continue;
                try
                {
                    var anyCut = segs[0].ComputeOutputFile(Settings.Instance);
                    var outDir = Path.GetDirectoryName(anyCut)!;
                    var stem = Path.GetFileNameWithoutExtension(source);
                    var ext = Path.GetExtension(source);
                    var mergedPath = Path.Combine(outDir, $"{stem}-merged{ext}");

                    await Dispatcher.UIThread.InvokeAsync(() =>
                        SetStatus($"merging {Path.GetFileName(source)}…"));

                    await FfmpegCutter.MergeAsync(mergedPath, segs, Settings.Instance, ct);

                    // Merge succeeded — delete the intermediate cuts. They served their
                    // purpose; the user asked for a single merged output and keeping
                    // both pollutes the output folder. Done last so a failed merge
                    // doesn't lose the cuts.
                    foreach (var seg in segs)
                    {
                        try
                        {
                            var cutPath = seg.ComputeOutputFile(Settings.Instance);
                            if (File.Exists(cutPath)) File.Delete(cutPath);
                        }
                        catch { /* leave the cut behind if delete fails — non-fatal */ }
                    }

                    // Now that the merge is done, the original DeleteSourceFileAfterDone
                    // pass we deferred above can run.
                    if (Settings.Instance.DeleteSourceFileAfterDone)
                        await TryAutoDeleteSourceAsync(source);
                }
                catch (OperationCanceledException) { break; }
                catch (FfmpegException ex)
                {
                    failedCount++;
                    AppendErrorLog($"[merge {Path.GetFileName(source)}] ffmpeg exit {ex.ExitCode}\n  cmd: {ex.Command}\n  stderr:\n{ex.Stderr}");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        SetStatus($"merge failed — {Path.GetFileName(source)} (see error log)"));
                }
                catch (Exception ex)
                {
                    failedCount++;
                    AppendErrorLog($"[merge {Path.GetFileName(source)}] unexpected: {ex}");
                }
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SetStatus(failedCount == 0
                ? $"queue done — {doneCount} finished"
                : $"queue done — {doneCount} finished, {failedCount} failed");
        });

        // Shutdown when done: only when the user opted in AND nothing failed. A
        // failed cut probably needs investigation; shutting down away from a Failed
        // status would hide the problem.
        if (Settings.Instance.ShutdownWhenDone && failedCount == 0 && doneCount > 0
            && !ct.IsCancellationRequested)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var cancelled = await CountdownDialog.ShowAsync(this, "Shutdown PC",
                    "Queue finished. Shutting down PC in {0}s.\n\nClick Cancel to stay running.", 15);
                if (!cancelled) ShutdownComputer();
            });
        }
    }

    /// <summary>
    /// Schedule an OS shutdown, then close this window so the queue/state save runs
    /// cleanly. On Windows uses <c>shutdown /s /t 5</c> as a small safety buffer
    /// (the user already had a 15s in-app countdown). On Linux uses
    /// <c>systemctl poweroff</c>; on macOS uses an AppleScript shutdown event.
    /// </summary>
    private void ShutdownComputer()
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("shutdown", "/s /t 5");
            }
            else if (OperatingSystem.IsMacOS())
            {
                psi = new ProcessStartInfo("osascript",
                    "-e \"tell application \\\"System Events\\\" to shut down\"");
            }
            else if (OperatingSystem.IsLinux())
            {
                psi = new ProcessStartInfo("systemctl", "poweroff");
            }
            else
            {
                Close();
                return;
            }
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            SetStatus("shutdown failed: " + ex.Message);
            return;
        }
        Close();
    }

    /// <summary>
    /// Honors <see cref="Settings.DeleteSourceFileAfterDone"/>: after a segment finishes,
    /// delete the source file — but only when no OTHER queue item still depends on it,
    /// and after releasing the player's handle if the player happens to still have it
    /// loaded (single-file playlist case — Cut doesn't auto-advance there, so mpv is
    /// still holding the OS lock when the cut completes).
    /// </summary>
    private async Task TryAutoDeleteSourceAsync(string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath)) return;
        if (!File.Exists(sourcePath)) return;

        bool anyPending = false;
        bool isCurrent = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var s in _queueSegments)
            {
                if (s.MarkedForDeletion) continue;
                if (!string.Equals(s.SourceFile, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (s.Status == ProgressStatus.Waiting || s.Status == ProgressStatus.Running)
                {
                    anyPending = true;
                    return;
                }
            }
            isCurrent = string.Equals(_currentFile, sourcePath, StringComparison.OrdinalIgnoreCase);
        });
        if (anyPending) return;

        if (isCurrent)
            await Dispatcher.UIThread.InvokeAsync(ReleaseCurrentFile);

        // mpv's "stop" command is queued — the OS-level handle isn't released
        // synchronously. Retry-with-backoff handles that timing without blocking
        // the UI thread on a guess-the-right-delay sleep.
        Exception? lastError = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                File.Delete(sourcePath);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _filePlaylist.RemoveAll(p =>
                        string.Equals(p, sourcePath, StringComparison.OrdinalIgnoreCase));
                    SetStatus($"deleted source {Path.GetFileName(sourcePath)}");
                });
                return;
            }
            catch (IOException ex)
            {
                lastError = ex;
                await Task.Delay(150 * (attempt + 1));
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        AppendErrorLog($"[auto-delete] {sourcePath} -> {lastError}");
        await Dispatcher.UIThread.InvokeAsync(() =>
            SetStatus($"auto-delete failed: {lastError?.Message}"));
    }

    /// <summary>
    /// Returns the segment Set In/Out should operate on: prefer the currently-selected
    /// list item, fall back to the last segment in timeline order, return null only if
    /// there are no segments at all. Lets the user retarget via the segment list — useful
    /// for segments too short to drag-edit on the timeline.
    /// </summary>
    private VideoSegment? GetEditTarget()
    {
        if (SegmentList.SelectedItem is VideoSegment selected) return selected;
        return Timeline.Segments.Count > 0 ? Timeline.Segments[^1] : null;
    }

    private void SetInPoint()
    {
        if (_duration <= 0) return;
        var pos = _player.TimePos;
        var target = GetEditTarget();
        if (target == null)
        {
            AddSegmentInternal(pos, _duration);
            return;
        }
        target.CutFromSeconds = Math.Min(pos, target.CutToSeconds - 0.04);
        _ = LoadThumbnailAsync(target);
    }

    private void SetOutPoint()
    {
        if (_duration <= 0) return;
        var pos = _player.TimePos;
        var target = GetEditTarget();
        if (target == null)
        {
            AddSegmentInternal(0, pos);
            return;
        }
        target.CutToSeconds = Math.Max(pos, target.CutFromSeconds + 0.04);
    }

    private void AddSegment()
    {
        if (_duration <= 0) return;
        var pos = _player.TimePos;
        var endSec = Timeline.Segments.Count == 0 ? _duration : Math.Min(pos + 5, _duration);
        AddSegmentInternal(pos, endSec);
    }

    private void AddSegmentInternal(double startSec, double endSec)
    {
        if (string.IsNullOrEmpty(_currentFile)) return;
        var seg = new VideoSegment
        {
            SourceFile = _currentFile,
            // Order matters — MaxDuration first (so cuts can be set inside the file's range).
            MaxDuration = TimeSpan.FromSeconds(_duration),
            CutFrom = TimeSpan.FromSeconds(startSec),
            CutTo = TimeSpan.FromSeconds(endSec),
            // Cycle through the palette so multiple segments are visually distinct
            // both on the timeline and in the segment list.
            ColorIndex = Timeline.Segments.Count,
        };
        Timeline.Segments.Add(seg);
        _ = LoadThumbnailAsync(seg);
    }

    /// <summary>
    /// Fetch the frame at <c>seg.CutFrom</c> and assign it to <c>seg.Thumbnail</c>.
    /// Reuses the same headless mpv extractor we use for timeline thumbnails — no
    /// additional ffmpeg processes spawned.
    /// </summary>
    private async Task LoadThumbnailAsync(VideoSegment seg)
    {
        if (string.IsNullOrEmpty(seg.SourceFile) || _currentFile == null) return;
        try
        {
            await EnsureExtractorLoadedAsync(seg.SourceFile, CancellationToken.None);
            var bmp = await _extractor.ExtractFrameAsync(seg.CutFromSeconds);
            // bmp == null is the "couldn't decode at this timestamp" path —
            // leave the existing thumbnail alone rather than nuking it.
            if (bmp != null) seg.Thumbnail = bmp;
        }
        catch (Exception)
        {
            // Thumbnail is optional — silently leave alone on unexpected failure.
        }
    }

    private void Seek(double delta)
    {
        if (!_player.IsInitialized) return;
        var current = _player.TimePos;
        var target = Math.Clamp(current + delta, 0, _duration);
        // No-op seek (already at boundary) — also stop any auto-repeat so we don't
        // spin firing playback-restart events forever at the edge of the file.
        if (Math.Abs(target - current) < 0.001)
        {
            if (_autoRepeatAction != null) StopAutoRepeat();
            return;
        }
        _player.SeekAbsolute(target, exact: true);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_player.IsInitialized) return;
        // Any keypress stops auto-repeat — Esc is the natural cancel,
        // but the user is also already moving on if they're using shortcuts.
        StopAutoRepeat();
        switch (e.Key)
        {
            case Key.Space: _player.TogglePause(); e.Handled = true; break;
            case Key.Left: Seek(-1); e.Handled = true; break;
            case Key.Right: Seek(+1); e.Handled = true; break;
            case Key.OemComma: _player.FrameBackStep(); e.Handled = true; break;
            case Key.OemPeriod: _player.FrameStep(); e.Handled = true; break;
            case Key.S: SetInPoint(); e.Handled = true; break;
            case Key.E: SetOutPoint(); e.Handled = true; break;
            case Key.A: AddSegment(); e.Handled = true; break;
            case Key.C: EnqueueAndStart(); e.Handled = true; break;
        }
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }
}
