using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using YetAnotherLosslessCutter;

namespace YetAnotherLosslessCutter.Controls;

public class TimelineControl : Control
{
    private const double HandleHitRadius = 8;
    private const double HandleVisualWidth = 6;
    // Was 0.5s — too coarse for frame-level trim work. 0.1s is ~3 frames at 30fps,
    // ~6 at 60fps; tight enough that wheel-zoom-in feels like it stops near a frame
    // boundary without going to a degenerate single-frame view.
    private const double MinViewDuration = 0.1;

    public static readonly StyledProperty<double> RulerHeightProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(RulerHeight), defaultValue: 14);

    public static readonly StyledProperty<double> MinimapHeightProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(MinimapHeight), defaultValue: 8);

    public static readonly StyledProperty<double> CellAspectProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(CellAspect), defaultValue: 1.5);

    public static readonly StyledProperty<double> HoverPreviewWidthProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(HoverPreviewWidth), defaultValue: 240);

    /// <summary>Pixel height of the timecode ruler at the top of the control.</summary>
    public double RulerHeight { get => GetValue(RulerHeightProperty); set => SetValue(RulerHeightProperty, value); }

    /// <summary>Pixel height of the minimap overview band between the ruler and the strip.</summary>
    public double MinimapHeight { get => GetValue(MinimapHeightProperty); set => SetValue(MinimapHeightProperty, value); }

    /// <summary>
    /// Width-to-height ratio of each thumbnail cell on the strip. The source is 16:9
    /// (≈1.78), but a slight horizontal compression (1.5) packs ~80% more cells per
    /// row at low visual cost. Set to 1.78 for faithful aspect, or higher for even
    /// more density at the expense of visible stretching.
    /// </summary>
    public double CellAspect { get => GetValue(CellAspectProperty); set => SetValue(CellAspectProperty, value); }

    /// <summary>Pixel width of the floating hover preview (height auto-derives from 16:9).</summary>
    public double HoverPreviewWidth { get => GetValue(HoverPreviewWidthProperty); set => SetValue(HoverPreviewWidthProperty, value); }

    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(PlayheadBrush));

    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(MarkerBrush));

    public static readonly StyledProperty<IBrush?> StripBackgroundBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(StripBackgroundBrush));

    public static readonly StyledProperty<IBrush?> WaveformBrushProperty =
        AvaloniaProperty.Register<TimelineControl, IBrush?>(nameof(WaveformBrush));

    /// <summary>Brush used for the playhead line. Falls back to a default red if unset.</summary>
    public IBrush? PlayheadBrush { get => GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }

    /// <summary>Brush used for marker chevrons on the ruler. Falls back to amber if unset.</summary>
    public IBrush? MarkerBrush { get => GetValue(MarkerBrushProperty); set => SetValue(MarkerBrushProperty, value); }

    /// <summary>Fallback fill behind the thumbnail strip. Falls back to dark grey if unset.</summary>
    public IBrush? StripBackgroundBrush { get => GetValue(StripBackgroundBrushProperty); set => SetValue(StripBackgroundBrushProperty, value); }

    /// <summary>Brush used for the waveform overlay. Falls back to a soft translucent green if unset.</summary>
    public IBrush? WaveformBrush { get => GetValue(WaveformBrushProperty); set => SetValue(WaveformBrushProperty, value); }

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(Duration));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(Position),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> ViewStartProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(ViewStart));

    public static readonly StyledProperty<double> ViewEndProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(ViewEnd));

    public static readonly DirectProperty<TimelineControl, FrameSet?> BaseFramesProperty =
        AvaloniaProperty.RegisterDirect<TimelineControl, FrameSet?>(
            nameof(BaseFrames), o => o.BaseFrames, (o, v) => o.BaseFrames = v);

    public static readonly DirectProperty<TimelineControl, AudioPeaks?> AudioPeaksProperty =
        AvaloniaProperty.RegisterDirect<TimelineControl, AudioPeaks?>(
            nameof(AudioPeaks), o => o.AudioPeaks, (o, v) => o.AudioPeaks = v);

    public static readonly DirectProperty<TimelineControl, VideoSegment?> SelectedSegmentProperty =
        AvaloniaProperty.RegisterDirect<TimelineControl, VideoSegment?>(
            nameof(SelectedSegment), o => o.SelectedSegment, (o, v) => o.SelectedSegment = v);

    public static readonly StyledProperty<string?> StatusTextProperty =
        AvaloniaProperty.Register<TimelineControl, string?>(nameof(StatusText));

    public static readonly StyledProperty<bool> FollowPlayheadProperty =
        AvaloniaProperty.Register<TimelineControl, bool>(nameof(FollowPlayhead), defaultValue: true);

    public static readonly StyledProperty<bool> SnapToTargetsProperty =
        AvaloniaProperty.Register<TimelineControl, bool>(nameof(SnapToTargets), defaultValue: true);

    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double ViewStart { get => GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
    public double ViewEnd { get => GetValue(ViewEndProperty); set => SetValue(ViewEndProperty, value); }
    /// <summary>
    /// Transient overlay text rendered at the top of the strip. Used by the host to
    /// surface frame-extraction progress without needing a separate status label.
    /// </summary>
    public string? StatusText { get => GetValue(StatusTextProperty); set => SetValue(StatusTextProperty, value); }

    /// <summary>
    /// When true and the view is zoomed in, automatically pan the view as the playhead
    /// crosses the boundary so the playhead stays visible. Page-style: when the playhead
    /// leaves the view, snap to a new view that places it ~25% from the leading edge.
    /// Auto-pan is suppressed while the user is actively dragging or panning.
    /// </summary>
    public bool FollowPlayhead { get => GetValue(FollowPlayheadProperty); set => SetValue(FollowPlayheadProperty, value); }

    /// <summary>
    /// When true, segment-edge / segment-body drags snap to nearby targets (playhead,
    /// other segment edges, 0, Duration) within ~8 px. Hold Shift while dragging to
    /// temporarily disable.
    /// </summary>
    public bool SnapToTargets { get => GetValue(SnapToTargetsProperty); set => SetValue(SnapToTargetsProperty, value); }

    private FrameSet? _baseFrames;
    public FrameSet? BaseFrames
    {
        get => _baseFrames;
        set { if (SetAndRaise(BaseFramesProperty, ref _baseFrames, value)) InvalidateVisual(); }
    }

    private AudioPeaks? _audioPeaks;
    /// <summary>
    /// Pre-computed audio peak buckets covering the loaded file. When set, a
    /// translucent waveform overlay draws on the strip so the user can spot dialogue
    /// boundaries / silences at a glance. Host populates this from
    /// <see cref="Cutting.AudioPeakExtractor"/> after a file loads.
    /// </summary>
    public AudioPeaks? AudioPeaks
    {
        get => _audioPeaks;
        set { if (SetAndRaise(AudioPeaksProperty, ref _audioPeaks, value)) InvalidateVisual(); }
    }

    private VideoSegment? _selectedSegment;
    /// <summary>
    /// The segment currently selected in the segment list. Drawn with a contrasting
    /// inner stroke so the user can match list selection to the timeline band.
    /// </summary>
    public VideoSegment? SelectedSegment
    {
        get => _selectedSegment;
        set { if (SetAndRaise(SelectedSegmentProperty, ref _selectedSegment, value)) InvalidateVisual(); }
    }

    private readonly System.Collections.Generic.List<FrameSet> _zoomLayers = new();

    /// <summary>Append a new zoom layer (finer-grained frames over a sub-range). Older layers stay
    /// so revisiting a region uses the cached frames without regeneration.</summary>
    public void AddZoomLayer(FrameSet layer)
    {
        _zoomLayers.Add(layer);
        InvalidateVisual();
    }

    /// <summary>Remove all zoom layers (e.g. when loading a new file).</summary>
    public void ClearZoomLayers()
    {
        if (_zoomLayers.Count == 0) return;
        _zoomLayers.Clear();
        InvalidateVisual();
    }

    /// <summary>Drop the oldest layers when the count exceeds <paramref name="maxLayers"/>.</summary>
    public void TrimZoomLayers(int maxLayers)
    {
        if (_zoomLayers.Count <= maxLayers) return;
        _zoomLayers.RemoveRange(0, _zoomLayers.Count - maxLayers);
        InvalidateVisual();
    }

    /// <summary>Read-only view of all currently-cached zoom layers.</summary>
    public System.Collections.Generic.IReadOnlyList<FrameSet> ZoomLayers => _zoomLayers;

    public ObservableCollection<VideoSegment> Segments { get; } = new();

    /// <summary>
    /// Markers are absolute-time points (seconds) the host wants surfaced on the ruler.
    /// Hosts can populate this from any source — mpv chapter-list, custom bookmarks
    /// loaded from sidecar files, scene-detection output, etc. Click on or near a
    /// marker tick seeks the playhead to it.
    /// </summary>
    public ObservableCollection<double> Markers { get; } = new();

    public static readonly RoutedEvent<TimelineTimeEventArgs> PositionDraggedEvent =
        RoutedEvent.Register<TimelineControl, TimelineTimeEventArgs>(
            nameof(PositionDragged), RoutingStrategies.Bubble);

    /// <summary>
    /// Raised on every playhead-drag tick AND on Home/End and similar seeks. The host
    /// uses this to issue a player seek so video playback follows the timeline.
    /// </summary>
    public event EventHandler<TimelineTimeEventArgs> PositionDragged
    {
        add => AddHandler(PositionDraggedEvent, value);
        remove => RemoveHandler(PositionDraggedEvent, value);
    }

    public static readonly RoutedEvent<TimelineTimeEventArgs> AddSegmentRequestedEvent =
        RoutedEvent.Register<TimelineControl, TimelineTimeEventArgs>(
            nameof(AddSegmentRequested), RoutingStrategies.Bubble);

    /// <summary>
    /// Raised when the user requests a new segment via the right-click context menu.
    /// <see cref="TimelineTimeEventArgs.Time"/> is the absolute time at which they
    /// right-clicked. The host decides the segment's duration / clamping / queue policy.
    /// </summary>
    public event EventHandler<TimelineTimeEventArgs> AddSegmentRequested
    {
        add => AddHandler(AddSegmentRequestedEvent, value);
        remove => RemoveHandler(AddSegmentRequestedEvent, value);
    }

    public static readonly RoutedEvent<RoutedEventArgs> RemoveAllSegmentsRequestedEvent =
        RoutedEvent.Register<TimelineControl, RoutedEventArgs>(
            nameof(RemoveAllSegmentsRequested), RoutingStrategies.Bubble);

    /// <summary>
    /// Raised when the user picks "Remove all segments" from the context menu. The host
    /// owns the confirmation prompt (parity with the toolbar's Clear-All button).
    /// </summary>
    public event EventHandler<RoutedEventArgs> RemoveAllSegmentsRequested
    {
        add => AddHandler(RemoveAllSegmentsRequestedEvent, value);
        remove => RemoveHandler(RemoveAllSegmentsRequestedEvent, value);
    }

    public static readonly RoutedEvent<TimelineSegmentEventArgs> PlaySegmentRequestedEvent =
        RoutedEvent.Register<TimelineControl, TimelineSegmentEventArgs>(
            nameof(PlaySegmentRequested), RoutingStrategies.Bubble);

    /// <summary>
    /// Raised on double-click of a segment body. The host should seek to the segment's
    /// CutFrom and start playback (and may optionally arm an A-B loop to its CutTo).
    /// </summary>
    public event EventHandler<TimelineSegmentEventArgs> PlaySegmentRequested
    {
        add => AddHandler(PlaySegmentRequestedEvent, value);
        remove => RemoveHandler(PlaySegmentRequestedEvent, value);
    }

    private void RaiseTime(RoutedEvent<TimelineTimeEventArgs> ev, double time) =>
        RaiseEvent(new TimelineTimeEventArgs(ev, this, time));

    private enum DragMode { None, Playhead, SegmentStart, SegmentEnd, SegmentBody }
    private DragMode _drag = DragMode.None;
    private VideoSegment? _dragSegment;
    private double _dragGrabOffset;

    private double _hoverTime = -1;
    private bool _isHovering;
    private int _lastHoverPx = int.MinValue;

    // ---- Default brushes (used when the corresponding StyledProperty is unset) ----
    private static readonly IBrush DefaultBgBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18));
    private static readonly IBrush DefaultStripBackgroundBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
    private static readonly IBrush RulerTickBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly IBrush RulerTextBrush = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa));
    private static readonly Pen RulerTickPen = new(RulerTickBrush, 1);
    private static readonly IBrush DefaultPlayheadBrush = new SolidColorBrush(Color.FromRgb(0xff, 0x50, 0x50));
    private static readonly IBrush HoverFrameBrush = new SolidColorBrush(Color.FromRgb(0xff, 0xff, 0xff));
    private static readonly Pen HoverFramePen = new(HoverFrameBrush, 1);
    private static readonly IBrush HoverShadowBrush = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0));
    // Status text gets a near-opaque dark pad so it stays legible over bright thumbnails.
    private static readonly IBrush StatusBgBrush = new SolidColorBrush(Color.FromArgb(0xe8, 0x10, 0x10, 0x10));
    private static readonly IBrush StatusTextBrush = new SolidColorBrush(Color.FromRgb(0xf0, 0xf0, 0xf0));
    private static readonly Pen SelectedSegmentPen = new(HoverFrameBrush, 2);
    private static readonly IBrush MinimapBgBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
    private static readonly IBrush MinimapTrackBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
    private static readonly Pen FocusAccentPen = new(new SolidColorBrush(Color.FromArgb(0xb0, 0xff, 0xff, 0xff)), 1);
    // Amber for markers — visually distinct from the red playhead and the colored
    // segment bands. Drawn as a small filled chevron at the top of the ruler.
    private static readonly IBrush DefaultMarkerBrush = new SolidColorBrush(Color.FromRgb(0xff, 0xc8, 0x40));
    // Soft green at low alpha for the waveform overlay — readable over thumbnails
    // without competing with segment bands or the playhead.
    private static readonly IBrush DefaultWaveformBrush = new SolidColorBrush(Color.FromArgb(0xa0, 0x60, 0xc8, 0x80));
    private const double MarkerHitRadius = 5.0;
    private const double MarkerHalfWidth = 3.0;
    private const double MarkerHeight = 6.0;
    private static readonly IBrush MinimapWindowBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xff, 0xff, 0xff));
    private static readonly Pen MinimapWindowPen = new(new SolidColorBrush(Color.FromRgb(0xc0, 0xc0, 0xc0)), 1);
    private static readonly Typeface MonoFace = new("Consolas, Menlo, Courier New");

    // Cached cursors — Avalonia's Cursor ctor allocates an OS handle, so reuse.
    private static readonly Cursor SeekCursor = new(StandardCursorType.Ibeam);
    private static readonly Cursor HandleCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor BodyCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor PanCursor = new(StandardCursorType.Hand);

    // Per-instance cache of segment border pens, keyed by the palette IBrush. Segments
    // share IBrush instances by ColorIndex (see SegmentPalette), so this caps at ~8 entries.
    private readonly System.Collections.Generic.Dictionary<IBrush, Pen> _borderPenCache = new();

    // Per-instance cached Pen for the playhead — rebuilt only when PlayheadBrush changes.
    private IBrush? _playheadPenCacheBrush;
    private Pen? _playheadPenCache;
    private Pen GetPlayheadPen()
    {
        var brush = PlayheadBrush ?? DefaultPlayheadBrush;
        if (!ReferenceEquals(_playheadPenCacheBrush, brush))
        {
            _playheadPenCacheBrush = brush;
            _playheadPenCache = new Pen(brush, 2);
        }
        return _playheadPenCache!;
    }

    // VideoSegment property names whose changes affect timeline rendering. Other
    // properties (Status, Progress, FailureReason, Thumbnail, SourceFile, …) raise
    // PropertyChanged frequently during a queue run and shouldn't trigger redraws.
    private static readonly System.Collections.Generic.HashSet<string> RenderAffectingSegmentProps = new()
    {
        nameof(VideoSegment.CutFrom), nameof(VideoSegment.CutTo),
        nameof(VideoSegment.CutFromSeconds), nameof(VideoSegment.CutToSeconds),
        nameof(VideoSegment.ColorIndex), nameof(VideoSegment.Color),
        nameof(VideoSegment.Brush), nameof(VideoSegment.FillBrush),
    };

    static TimelineControl()
    {
        AffectsRender<TimelineControl>(
            DurationProperty, PositionProperty,
            ViewStartProperty, ViewEndProperty,
            StatusTextProperty,
            RulerHeightProperty, MinimapHeightProperty,
            CellAspectProperty, HoverPreviewWidthProperty,
            PlayheadBrushProperty, MarkerBrushProperty, StripBackgroundBrushProperty,
            WaveformBrushProperty);
        FocusableProperty.OverrideDefaultValue<TimelineControl>(true);
        DurationProperty.Changed.AddClassHandler<TimelineControl>((c, e) => c.OnDurationChanged((double)e.NewValue!));
        PositionProperty.Changed.AddClassHandler<TimelineControl>((c, e) => c.OnPositionMaybeFollow((double)e.NewValue!));
    }

    private void OnPositionMaybeFollow(double newPos)
    {
        if (!FollowPlayhead) return;
        // User is in control — never yank the view out from under them.
        if (_drag != DragMode.None || _isPanning) return;
        if (Duration <= 0) return;
        var viewDur = ViewDuration;
        // Not zoomed (full duration visible) → nothing to follow.
        if (viewDur >= Duration - 0.001) return;
        // Already comfortably in view → nothing to do.
        if (newPos >= ViewStart && newPos <= ViewEnd) return;

        // Page so the playhead lands ~25% from the left edge — gives ~75% of the view
        // as look-ahead, which is the convention for page-style scrolling in NLEs.
        var newStart = Math.Clamp(newPos - viewDur * 0.25, 0, Duration - viewDur);
        ViewStart = newStart;
        ViewEnd = newStart + viewDur;
    }

    public TimelineControl()
    {
        Segments.CollectionChanged += OnSegmentsChanged;
        Markers.CollectionChanged += (_, _) => InvalidateVisual();
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => InvalidateVisual();
    }

    private void OnDurationChanged(double newDuration)
    {
        // Empty → fit-to-view on first non-zero value; otherwise clamp the existing
        // view window into the new range. Preserves user zoom across mpv duration
        // re-reports (mpv refines for some formats mid-playback). Hosts that want a
        // fresh full-view on new-file load should call <see cref="ResetView"/>.
        var (s, e) = TimelineMath.ClampViewToDuration(ViewStart, ViewEnd, newDuration, MinViewDuration);
        ViewStart = s;
        ViewEnd = e;
    }

    /// <summary>
    /// Reset the visible window to the full file. Call this from the host on new-file
    /// load so the user starts each file zoomed-out, while still letting mpv duration
    /// refinements during playback preserve the current zoom (see OnDurationChanged).
    /// </summary>
    public void ResetView()
    {
        if (Duration <= 0) { ViewStart = ViewEnd = 0; return; }
        ViewStart = 0;
        ViewEnd = Duration;
    }

    private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (VideoSegment s in e.OldItems)
                s.PropertyChanged -= OnSegmentChanged;
        if (e.NewItems != null)
            foreach (VideoSegment s in e.NewItems)
                s.PropertyChanged += OnSegmentChanged;
        InvalidateVisual();
    }

    private void OnSegmentChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Null/empty PropertyName means "all properties changed" — always redraw.
        // Otherwise only redraw if it's a render-affecting property.
        if (string.IsNullOrEmpty(e.PropertyName) || RenderAffectingSegmentProps.Contains(e.PropertyName))
            InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        ctx.FillRectangle(DefaultBgBrush, b);

        if (Duration <= 0)
        {
            DrawCentered(ctx, b, "No media loaded", RulerTextBrush, 13);
            return;
        }

        var stripTop = RulerHeight + MinimapHeight;
        var stripRect = new Rect(0, stripTop, b.Width, b.Height - stripTop);
        ctx.FillRectangle(StripBackgroundBrush ?? DefaultStripBackgroundBrush, stripRect);

        DrawThumbnailStrip(ctx, stripRect);
        DrawWaveform(ctx, stripRect);
        DrawRuler(ctx, new Rect(0, 0, b.Width, RulerHeight));
        DrawMinimap(ctx, new Rect(0, RulerHeight, b.Width, MinimapHeight));
        DrawMarkers(ctx);
        DrawSegments(ctx, stripRect);
        DrawPlayhead(ctx, b);

        // Suppress the floating preview while any drag is active — during playhead
        // drag the cursor IS the playhead so the preview is redundant; during segment
        // drags it's distracting. Hover-without-drag still gets it.
        var isDragging = _drag != DragMode.None || _isPanning || _isMinimapDragging;
        if (_isHovering && !isDragging && _hoverTime >= ViewStart && _hoverTime <= ViewEnd)
            DrawHoverPreview(ctx, _hoverTime, b);

        DrawZoomIndicator(ctx, b);
        DrawStatusText(ctx, b);

        // Focus indicator: thin accent line at the very bottom of the control. Sits
        // below the segment handles (which span only the strip area, not its bottom
        // 1px) so it doesn't visually compete. Surfaces "you can use Home/End/+/-/0
        // here" to the user without a heavy focus ring.
        if (IsFocused)
        {
            var y = b.Height - 0.5;
            ctx.DrawLine(FocusAccentPen, new Point(0, y), new Point(b.Width, y));
        }
    }

    private void DrawMarkers(DrawingContext ctx)
    {
        if (Markers.Count == 0) return;
        // Small filled chevron at the top of the ruler at each in-view marker.
        // Drawn as a filled rect — cheaper than building a StreamGeometry per render
        // and visually distinct from the 1px ruler ticks.
        foreach (var time in Markers)
        {
            if (time < ViewStart || time > ViewEnd) continue;
            var x = TimeToX(time);
            ctx.FillRectangle(MarkerBrush ?? DefaultMarkerBrush,
                new Rect(x - MarkerHalfWidth, 0, MarkerHalfWidth * 2, MarkerHeight));
        }
    }

    /// <summary>
    /// Returns the time of the marker the user clicked at <paramref name="x"/>, or
    /// <c>null</c> if no marker is within <see cref="MarkerHitRadius"/> px. Only
    /// marker hits in the ruler area count — clicks on the strip stay scrub clicks.
    /// </summary>
    private double? HitTestMarker(double x)
    {
        if (Markers.Count == 0) return null;
        double? best = null;
        var bestDist = double.MaxValue;
        foreach (var time in Markers)
        {
            if (time < ViewStart || time > ViewEnd) continue;
            var d = Math.Abs(TimeToX(time) - x);
            if (d <= MarkerHitRadius && d < bestDist) { bestDist = d; best = time; }
        }
        return best;
    }

    private void DrawMinimap(DrawingContext ctx, Rect rect)
    {
        // Background: subtle separation from the ruler above and the strip below.
        ctx.FillRectangle(MinimapBgBrush, rect);
        var trackRect = new Rect(rect.X, rect.Y + 1, rect.Width, rect.Height - 2);
        ctx.FillRectangle(MinimapTrackBrush, trackRect);

        var pxPerSec = rect.Width / Duration;

        // Segment marks at absolute scale — gives at-a-glance "where are my cuts."
        foreach (var seg in Segments)
        {
            var x1 = seg.CutFromSeconds * pxPerSec;
            var x2 = seg.CutToSeconds * pxPerSec;
            var w = Math.Max(1, x2 - x1);
            ctx.FillRectangle(seg.FillBrush, new Rect(x1, trackRect.Y, w, trackRect.Height));
        }

        // View-window rectangle: shows what slice the main strip is currently showing.
        var winX1 = ViewStart * pxPerSec;
        var winX2 = ViewEnd * pxPerSec;
        var winW = Math.Max(2, winX2 - winX1);
        var winRect = new Rect(winX1, rect.Y, winW, rect.Height);
        ctx.FillRectangle(MinimapWindowBrush, winRect);
        ctx.DrawRectangle(null, MinimapWindowPen, winRect);

        // Playhead marker at absolute scale.
        var phX = Position * pxPerSec;
        if (phX >= rect.X && phX <= rect.Right)
            ctx.DrawLine(GetPlayheadPen(), new Point(phX, rect.Y), new Point(phX, rect.Bottom));
    }

    private void DrawStatusText(DrawingContext ctx, Rect bounds)
    {
        var text = StatusText;
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            MonoFace, 11, StatusTextBrush);
        // Pinned to the top-left of the strip area. A near-opaque dark pad keeps it
        // readable even when bright thumbnails sit underneath.
        var pad = 4d;
        var x = pad;
        var y = RulerHeight + MinimapHeight + pad;
        var bgRect = new Rect(x - 3, y - 1, ft.Width + 6, ft.Height + 2);
        ctx.FillRectangle(StatusBgBrush, bgRect);
        ctx.DrawText(ft, new Point(x, y));
    }

    /// <summary>
    /// Draw the waveform overlay across the strip — one vertical bar per pixel column,
    /// height proportional to the peak amplitude in that column's time window.
    /// Bars are centered vertically on the strip with a small top/bottom pad so
    /// segment border colors stay readable along the strip edges.
    /// </summary>
    private void DrawWaveform(DrawingContext ctx, Rect stripRect)
    {
        var peaks = _audioPeaks;
        if (peaks == null || peaks.Peaks.Length == 0 || stripRect.Width <= 0) return;
        if (Duration <= 0) return;

        var brush = WaveformBrush ?? DefaultWaveformBrush;
        var pad = 2.0;
        var maxBarHalf = Math.Max(1, (stripRect.Height - pad * 2) * 0.5);
        var midY = stripRect.Y + stripRect.Height * 0.5;

        // One bar per integer pixel column. Each column maps to a tiny time slice;
        // grab the loudest peak in that slice so transients are visible at any zoom.
        var width = (int)Math.Ceiling(stripRect.Width);
        var pxToTime = ViewDuration / width;
        for (var x = 0; x < width; x++)
        {
            var t1 = ViewStart + x * pxToTime;
            var t2 = t1 + pxToTime;
            var amp = peaks.MaxInRange(t1, t2);
            if (amp <= 0) continue;
            var half = amp * maxBarHalf;
            // 1px-wide rect from midY-half to midY+half. FillRectangle with sub-pixel
            // values fine — Avalonia anti-aliases to a soft column.
            ctx.FillRectangle(brush, new Rect(x, midY - half, 1, half * 2));
        }
    }

    private void DrawThumbnailStrip(DrawingContext ctx, Rect stripRect)
    {
        if (_baseFrames == null && _zoomLayers.Count == 0) return;

        // Cell width slightly compressed from 16:9 source aspect — packs more thumbs
        // visibly per row at modest visual distortion.
        var idealCellW = stripRect.Height * CellAspect;
        var displayCount = Math.Max(1, (int)Math.Floor(stripRect.Width / idealCellW));
        var cellW = stripRect.Width / displayCount;

        var viewDur = ViewDuration;
        var cellTimeSpan = viewDur / displayCount;

        for (var i = 0; i < displayCount; i++)
        {
            var cellTime = ViewStart + (i + 0.5) * cellTimeSpan;
            var src = PickFinestLayer(cellTime);
            if (src == null || src.Bitmaps.Count == 0) continue;

            // Cap distance to two cells' worth — if the chosen layer has no thumb
            // anywhere near this cell (e.g. extraction failed across a chunk of the
            // file), draw nothing rather than smearing a duplicate across the strip.
            var bmp = src.PickNearest(cellTime, cellTimeSpan * 2.0);
            if (bmp == null) continue;

            var dstRect = new Rect(i * cellW, stripRect.Y, cellW, stripRect.Height);
            ctx.DrawImage(bmp, dstRect);
        }
    }

    /// <summary>
    /// Of all available frame layers (zoom layers + base), return the one that covers
    /// <paramref name="time"/> with the smallest seconds-per-frame. This is the "finest"
    /// available frame for that point in time. Returns null if nothing covers it.
    /// </summary>
    private FrameSet? PickFinestLayer(double time)
    {
        FrameSet? best = null;
        for (var i = 0; i < _zoomLayers.Count; i++)
        {
            var layer = _zoomLayers[i];
            if (!layer.Covers(time)) continue;
            if (best == null || layer.SecondsPerFrame < best.SecondsPerFrame)
                best = layer;
        }
        if (_baseFrames != null && _baseFrames.Covers(time))
        {
            if (best == null || _baseFrames.SecondsPerFrame < best.SecondsPerFrame)
                best = _baseFrames;
        }
        return best;
    }

    private void DrawRuler(DrawingContext ctx, Rect rect)
    {
        var pxPerSec = Bounds.Width / ViewDuration;
        if (pxPerSec <= 0) return;
        var targetInterval = 80 / pxPerSec;
        var interval = NiceInterval(targetInterval);
        if (interval <= 0) return;

        // Snap first tick to a multiple of interval at or before ViewStart.
        var firstTick = Math.Floor(ViewStart / interval) * interval;
        // Track the right edge of the last drawn label so we can skip labels that
        // would visually collide. Tick LINES still draw at every interval — only
        // the LABELS get suppressed. Important when the right-edge clamp pulls a
        // label leftward into the previous one.
        var prevLabelRight = double.NegativeInfinity;
        const double labelGap = 4;
        for (var t = firstTick; t <= ViewEnd + 0.001; t += interval)
        {
            if (t < ViewStart - 0.001) continue;
            var x = TimeToX(t);
            if (x < -2 || x > Bounds.Width + 2) continue;
            ctx.DrawLine(RulerTickPen, new Point(x, rect.Bottom - 5), new Point(x, rect.Bottom));
            var label = FormatTime(t);
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoFace, 10, RulerTextBrush);
            var lx = Math.Min(x + 3, rect.Right - ft.Width - 2);
            if (lx < prevLabelRight + labelGap) continue;
            ctx.DrawText(ft, new Point(lx, 1));
            prevLabelRight = lx + ft.Width;
        }
    }

    /// <summary>
    /// Height of the segment "header" band — the opaque grab strip drawn at the top
    /// of each segment. Plain click-drag inside the header moves the segment; clicks
    /// in the rest of the segment area scrub the playhead like empty strip clicks.
    /// Clamped so the band stays grabbable on short strips and doesn't dominate on
    /// tall ones.
    /// </summary>
    private static double GetSegmentHeaderHeight(double stripHeight) =>
        Math.Clamp(stripHeight * 0.20, 14.0, 28.0);

    private void DrawSegments(DrawingContext ctx, Rect stripRect)
    {
        var headerH = GetSegmentHeaderHeight(stripRect.Height);
        foreach (var seg in Segments)
        {
            // Cull segments fully outside view
            if (seg.CutToSeconds < ViewStart || seg.CutFromSeconds > ViewEnd) continue;
            var x1 = TimeToX(seg.CutFromSeconds);
            var x2 = TimeToX(seg.CutToSeconds);
            if (x2 <= x1) continue;
            var fillBrush = seg.FillBrush;
            var edgeBrush = seg.Brush;

            // Header band — opaque "tab" on top of the strip. This is the grab zone
            // for moving the segment; clicks below this band fall through to scrub.
            var headerRect = new Rect(x1, stripRect.Y, x2 - x1, headerH);
            ctx.FillRectangle(edgeBrush, headerRect);

            // Body tint — keeps the segment's extent visible across the thumbnail
            // strip without blocking interaction below the header.
            var bodyRect = new Rect(x1, stripRect.Y + headerH, x2 - x1, stripRect.Height - headerH);
            if (bodyRect.Height > 0)
                ctx.FillRectangle(fillBrush, bodyRect);

            // Edge handles span the full strip height — easy to grab and act as
            // visible "this is where the cut starts/ends" guide lines.
            if (x1 >= -HandleVisualWidth && x1 <= Bounds.Width + HandleVisualWidth)
                ctx.FillRectangle(edgeBrush,
                    new Rect(x1 - HandleVisualWidth / 2, stripRect.Y, HandleVisualWidth, stripRect.Height));
            if (x2 >= -HandleVisualWidth && x2 <= Bounds.Width + HandleVisualWidth)
                ctx.FillRectangle(edgeBrush,
                    new Rect(x2 - HandleVisualWidth / 2, stripRect.Y, HandleVisualWidth, stripRect.Height));

            // Selected-segment highlight: 2px white inner stroke inside the header
            // band so it doesn't fight the handle bars on either side.
            if (seg == _selectedSegment && headerRect.Width > 6 && headerRect.Height > 6)
            {
                var inset = new Rect(headerRect.X + 3, headerRect.Y + 2,
                    headerRect.Width - 6, headerRect.Height - 4);
                ctx.DrawRectangle(null, SelectedSegmentPen, inset);
            }
        }
    }

    private Pen GetBorderPen(IBrush edgeBrush)
    {
        if (!_borderPenCache.TryGetValue(edgeBrush, out var pen))
        {
            pen = new Pen(edgeBrush, 1.5);
            _borderPenCache[edgeBrush] = pen;
        }
        return pen;
    }

    private void DrawPlayhead(DrawingContext ctx, Rect bounds)
    {
        if (Position < ViewStart || Position > ViewEnd) return;
        var px = TimeToX(Position);
        // Two segments: through the ruler and through the strip. Skipping the minimap
        // band — the minimap uses absolute-duration coordinates, so a continuous line
        // would visually misregister there. The minimap draws its own playhead marker.
        var pen = GetPlayheadPen();
        ctx.DrawLine(pen, new Point(px, 0), new Point(px, RulerHeight));
        ctx.DrawLine(pen,
            new Point(px, RulerHeight + MinimapHeight),
            new Point(px, bounds.Height));
    }

    private void DrawZoomIndicator(DrawingContext ctx, Rect bounds)
    {
        if (Duration <= 0) return;
        var zoom = Duration / ViewDuration;
        if (zoom <= 1.001) return;

        var label = $"{zoom:0.#}× zoom";
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            MonoFace, 10, RulerTextBrush);
        var x = bounds.Width - ft.Width - 8;
        ctx.DrawText(ft, new Point(x, 2));
    }

    private void DrawHoverPreview(DrawingContext ctx, double t, Rect bounds)
    {
        var src = PickFinestLayer(t);
        if (src == null || src.Bitmaps.Count == 0) return;
        // Hover gets a generous distance budget — better to show *some* nearby
        // thumbnail than nothing while the user is scrubbing.
        var bmp = src.PickNearest(t, src.SecondsPerFrame * 4.0);
        if (bmp == null) return;

        var dstW = HoverPreviewWidth;
        var dstH = dstW * 9.0 / 16.0; // 16:9 — preview itself stays correct aspect
        const double gap = 8;
        const double margin = 4;

        var hoverX = TimeToX(t);

        // Centered horizontally on cursor, clamped to control bounds.
        var dstX = Math.Clamp(hoverX - dstW / 2, margin, bounds.Width - dstW - margin);

        // Prefer to float ABOVE the timeline (negative Y) — relies on ClipToBounds=False
        // on the control so the preview renders into the video area above the strip.
        // Fall back to BELOW the strip when there isn't enough room above (e.g.,
        // a short window or a layout with little space above the timeline) — better
        // to overlap the transport row briefly than render off-screen.
        var availableAbove = double.MaxValue;
        if (this.VisualRoot is Avalonia.Visual root)
        {
            var topInRoot = this.TranslatePoint(new Point(0, 0), root);
            if (topInRoot.HasValue) availableAbove = Math.Max(0, topInRoot.Value.Y);
        }
        var dstY = availableAbove >= dstH + gap + margin
            ? -dstH - gap
            : bounds.Height + gap;
        ctx.FillRectangle(HoverShadowBrush, new Rect(dstX + 2, dstY + 2, dstW, dstH));
        var dst = new Rect(dstX, dstY, dstW, dstH);
        ctx.DrawImage(bmp, dst);
        ctx.DrawRectangle(null, HoverFramePen, dst);

        var ft = new FormattedText(FormatTime(t), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            MonoFace, 11, HoverFrameBrush);
        ctx.DrawText(ft, new Point(dstX + 6, dstY + dstH - ft.Height - 4));
    }

    private static void DrawCentered(DrawingContext ctx, Rect bounds, string text, IBrush brush, double size)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            MonoFace, size, brush);
        ctx.DrawText(ft, new Point(bounds.Center.X - ft.Width / 2, bounds.Center.Y - ft.Height / 2));
    }

    // --------- interaction ---------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Duration <= 0) return;

        var pt = e.GetCurrentPoint(this);

        if (pt.Properties.IsRightButtonPressed)
        {
            Focus();
            var rightClickTime = XToTime(pt.Position.X);
            ShowContextMenu(rightClickTime);
            e.Handled = true;
            return;
        }

        if (!pt.Properties.IsLeftButtonPressed && !pt.Properties.IsMiddleButtonPressed) return;

        Focus();
        var x = pt.Position.X;
        var t = XToTime(x);
        var py = pt.Position.Y;

        if (pt.Properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _panAnchorTime = t;
            Cursor = PanCursor;
            e.Pointer.Capture(this);
            return;
        }

        // Double-click on the ruler or minimap → fit-to-view (reset zoom). Common
        // gesture in NLEs and a fast escape from a deep zoom level without reaching
        // for the keyboard.
        if (e.ClickCount >= 2 && py < RulerHeight + MinimapHeight)
        {
            ResetView();
            e.Handled = true;
            return;
        }

        // Double-click on a segment body → emit PlaySegmentRequested. Host seeks to
        // the segment start and plays. Dbl-click on a handle or empty strip falls
        // through to normal click-to-seek behavior.
        if (e.ClickCount >= 2 && py >= RulerHeight + MinimapHeight)
        {
            var dblSeg = FindSegmentAt(x);
            if (dblSeg != null)
            {
                RaiseEvent(new TimelineSegmentEventArgs(PlaySegmentRequestedEvent, this, dblSeg));
                e.Handled = true;
                return;
            }
        }

        // Single click on a marker in the ruler region → seek to that marker time.
        // Hit only applies in the ruler area (above the minimap) so strip clicks
        // still scrub normally.
        if (py < RulerHeight)
        {
            var marker = HitTestMarker(x);
            if (marker.HasValue)
            {
                Position = marker.Value;
                RaiseTime(PositionDraggedEvent,marker.Value);
                e.Handled = true;
                return;
            }
        }

        // Click in the minimap region → pan the main view to center on this absolute
        // time. Drag continues to update — the user can scrub the overview.
        if (py >= RulerHeight && py < RulerHeight + MinimapHeight && Duration > 0)
        {
            _isMinimapDragging = true;
            CenterViewOnAbsX(x);
            Cursor = PanCursor;
            e.Pointer.Capture(this);
            return;
        }

        // Always seek to the click position FIRST, regardless of where you clicked.
        // Click on a segment = put playhead there too (then optionally drag the segment).
        // This matches WPF original behavior: a click always moves the playhead.
        Position = t;
        RaiseTime(PositionDraggedEvent,t);

        // Then determine drag mode based on what's under the cursor. Segment-body
        // moves register only when the click is inside the segment's header band
        // (see HitTestSegments) — clicks below the header fall through to scrub so
        // dragging to seek inside a cut works without a modifier.
        var hit = HitTestSegments(x, py);
        if (hit.mode != DragMode.None)
        {
            _drag = hit.mode;
            _dragSegment = hit.seg;
            _dragGrabOffset = hit.mode == DragMode.SegmentBody ? t - hit.seg!.CutFromSeconds : 0;
            e.Pointer.Capture(this);
            return;
        }

        // Empty area — drag to scrub the playhead.
        _drag = DragMode.Playhead;
        e.Pointer.Capture(this);
    }

    /// <summary>
    /// Find the nearest snap target to <paramref name="t"/> within ~8 px. Snap targets
    /// are the playhead, 0, Duration, and the edges of all other segments. Returns
    /// <paramref name="t"/> unchanged if no target is within range or snapping is off.
    /// </summary>
    private double SnapTime(double t, VideoSegment? exclude, bool snapDisabled)
    {
        if (snapDisabled || !SnapToTargets) return t;
        if (Duration <= 0 || Bounds.Width <= 0) return t;

        // Build the candidate target list. 3 fixed entries + 2 per non-excluded
        // segment — small lists stay on the small-object heap.
        var targets = new System.Collections.Generic.List<double>(3 + Segments.Count * 2)
        {
            0, Duration, Position
        };
        foreach (var seg in Segments)
        {
            if (seg == exclude) continue;
            targets.Add(seg.CutFromSeconds);
            targets.Add(seg.CutToSeconds);
        }
        return TimelineMath.SnapTime(t, ViewDuration, Bounds.Width, targets, HandleHitRadius);
    }

    private (DragMode mode, VideoSegment? seg) HitTestSegments(double x, double y)
    {
        // SegmentBody only registers inside the header band; the rest of the segment
        // area falls through to playhead scrub. Edge handles stay full-strip-height.
        var stripTop = RulerHeight + MinimapHeight;
        var stripHeight = Math.Max(0, Bounds.Height - stripTop);
        var headerH = GetSegmentHeaderHeight(stripHeight);
        var inHeader = y >= stripTop && y < stripTop + headerH;
        foreach (var seg in Segments)
        {
            var x1 = TimeToX(seg.CutFromSeconds);
            var x2 = TimeToX(seg.CutToSeconds);
            if (Math.Abs(x - x1) <= HandleHitRadius) return (DragMode.SegmentStart, seg);
            if (Math.Abs(x - x2) <= HandleHitRadius) return (DragMode.SegmentEnd, seg);
            if (inHeader && x > x1 && x < x2) return (DragMode.SegmentBody, seg);
        }
        return (DragMode.None, null);
    }

    /// <summary>
    /// Find the segment whose visible x-range covers <paramref name="x"/>, ignoring
    /// y. Used by the double-click-to-play path so the user can dbl-click anywhere
    /// on the colored segment (header or body tint) and have it play.
    /// </summary>
    private VideoSegment? FindSegmentAt(double x)
    {
        foreach (var seg in Segments)
        {
            var x1 = TimeToX(seg.CutFromSeconds);
            var x2 = TimeToX(seg.CutToSeconds);
            if (x > x1 && x < x2) return seg;
        }
        return null;
    }

    private bool _isPanning;
    private double _panAnchorTime;
    private bool _isMinimapDragging;

    /// <summary>
    /// Move <see cref="ViewStart"/>/<see cref="ViewEnd"/> so the time corresponding to
    /// pixel <paramref name="absX"/> on the minimap (which uses absolute-duration
    /// coordinates) sits at the center of the view. Used for click + drag on the
    /// minimap overview bar.
    /// </summary>
    private void CenterViewOnAbsX(double absX)
    {
        if (Duration <= 0 || Bounds.Width <= 0) return;
        var absT = Math.Clamp(absX / Bounds.Width * Duration, 0, Duration);
        var dur = ViewDuration;
        var newStart = Math.Clamp(absT - dur * 0.5, 0, Math.Max(0, Duration - dur));
        ViewStart = newStart;
        ViewEnd = newStart + dur;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Duration <= 0) return;

        var x = e.GetPosition(this).X;
        var t = XToTime(x);

        if (_isPanning)
        {
            // Pan so that the time we initially grabbed stays under the cursor.
            var delta = _panAnchorTime - t;
            PanBy(delta);
            return;
        }

        if (_isMinimapDragging)
        {
            CenterViewOnAbsX(x);
            return;
        }

        if (_drag == DragMode.None)
        {
            // Cursor feedback: tell the user what each part of the strip will do.
            // Body cursor only shows over the segment header band — the rest of the
            // strip (including segment body tint) scrubs.
            var hit = HitTestSegments(x, e.GetPosition(this).Y);
            Cursor = hit.mode switch
            {
                DragMode.SegmentStart or DragMode.SegmentEnd => HandleCursor,
                DragMode.SegmentBody => BodyCursor,
                _ => SeekCursor,
            };

            // Throttle hover redraws to 1px buckets — avoids re-rendering the entire
            // control (including thumbnail strip) on sub-pixel mouse jitter.
            var px = (int)Math.Round(x);
            _isHovering = true;
            _hoverTime = t;
            if (px != _lastHoverPx)
            {
                _lastHoverPx = px;
                InvalidateVisual();
            }
            return;
        }

        if (_isHovering) { _isHovering = false; _lastHoverPx = int.MinValue; InvalidateVisual(); }

        var snapDisabled = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        switch (_drag)
        {
            case DragMode.Playhead:
                Position = t;
                RaiseTime(PositionDraggedEvent,t);
                break;
            case DragMode.SegmentStart when _dragSegment != null:
            {
                var snapped = SnapTime(t, _dragSegment, snapDisabled);
                _dragSegment.CutFromSeconds = Math.Min(snapped, _dragSegment.CutToSeconds - 0.04);
                break;
            }
            case DragMode.SegmentEnd when _dragSegment != null:
            {
                var snapped = SnapTime(t, _dragSegment, snapDisabled);
                _dragSegment.CutToSeconds = Math.Max(snapped, _dragSegment.CutFromSeconds + 0.04);
                break;
            }
            case DragMode.SegmentBody when _dragSegment != null:
            {
                // Snap the leading (start) edge — matches NLE convention.
                var proposedStart = t - _dragGrabOffset;
                var snappedStart = SnapTime(proposedStart, _dragSegment, snapDisabled);
                _dragSegment.MoveTo(TimeSpan.FromSeconds(snappedStart));
                break;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag != DragMode.None || _isPanning || _isMinimapDragging)
        {
            _drag = DragMode.None;
            _dragSegment = null;
            _isPanning = false;
            _isMinimapDragging = false;
            Cursor = null;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isHovering = false;
        _hoverTime = -1;
        _lastHoverPx = int.MinValue;
        Cursor = null;
        InvalidateVisual();
    }


    /// <summary>Seconds the playhead moves per wheel notch in plain-wheel mode.
    /// Default 60s matches the WPF original.</summary>
    public static double WheelSeekStepSeconds { get; set; } = 60.0;

    /// <summary>
    /// When true, plain-wheel-up seeks BACKWARD instead of forward. Some users prefer
    /// the inverted convention (matches scrolling a document — wheel-up = earlier
    /// in the file). Default false matches the WPF original.
    /// </summary>
    public static bool WheelSeekInverted { get; set; } = false;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Duration <= 0) return;

        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        if (ctrl)
        {
            // Ctrl+wheel = zoom with cursor as pivot. Time at cursor stays put.
            var x = e.GetPosition(this).X;
            var t = XToTime(x);
            var factor = e.Delta.Y > 0 ? 0.7 : 1.0 / 0.7;
            ZoomAround(t, factor);
        }
        else if (shift)
        {
            // Shift+wheel = pan horizontally
            var amount = -e.Delta.Y * ViewDuration * 0.15;
            PanBy(amount);
        }
        else
        {
            // Plain wheel = seek (matches WPF original behavior).
            // Wheel up = forward in time, unless WheelSeekInverted is set.
            var direction = WheelSeekInverted ? -1.0 : 1.0;
            var newPos = Math.Clamp(Position + direction * e.Delta.Y * WheelSeekStepSeconds, 0, Duration);
            Position = newPos;
            RaiseTime(PositionDraggedEvent,newPos);
        }
        e.Handled = true;
    }

    private void PanBy(double seconds)
    {
        var (s, e) = TimelineMath.PanBy(seconds, ViewStart, ViewEnd, Duration);
        ViewStart = s;
        ViewEnd = e;
    }

    /// <summary>
    /// Zoom the view by <paramref name="factor"/> around <paramref name="pivotTime"/>.
    /// factor &lt; 1 zooms in (tighter view), &gt; 1 zooms out. Pivot time stays at the
    /// same screen X. Clamps to [0, Duration] and respects MinViewDuration.
    /// </summary>
    private void ZoomAround(double pivotTime, double factor)
    {
        var (s, e) = TimelineMath.ZoomAround(pivotTime, factor, ViewStart, ViewEnd, Duration, MinViewDuration);
        ViewStart = s;
        ViewEnd = e;
    }

    private void ShowContextMenu(double atTime)
    {
        if (Duration <= 0) return;

        var menu = new ContextMenu();

        var addItem = new MenuItem { Header = "Add segment here" };
        addItem.Click += (_, _) => RaiseTime(AddSegmentRequestedEvent, atTime);
        menu.Items.Add(addItem);

        if (Segments.Count > 0)
        {
            var removeAll = new MenuItem { Header = "Remove all segments" };
            removeAll.Click += (_, _) => RaiseEvent(new RoutedEventArgs(RemoveAllSegmentsRequestedEvent, this));
            menu.Items.Add(removeAll);
        }

        menu.Items.Add(new Separator());

        var fitZoom = new MenuItem { Header = "Fit zoom" };
        fitZoom.Click += (_, _) => ResetView();
        menu.Items.Add(fitZoom);

        var formatted = FormatTime(atTime);
        var copyTs = new MenuItem { Header = $"Copy timestamp ({formatted})" };
        copyTs.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(formatted));
            await clipboard.SetDataAsync(transfer);
        };
        menu.Items.Add(copyTs);

        menu.PlacementTarget = this;
        menu.Open(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Duration <= 0) return;

        // Pivot zooms around the playhead when it's in view, otherwise the view center —
        // matches the "stay where you're looking" expectation.
        var pivot = (Position >= ViewStart && Position <= ViewEnd)
            ? Position
            : (ViewStart + ViewEnd) * 0.5;

        switch (e.Key)
        {
            case Key.Home:
                Position = 0;
                RaiseTime(PositionDraggedEvent,0);
                e.Handled = true;
                break;
            case Key.End:
                // Seeking exactly to Duration lands past the last frame and mpv hits
                // EOF (the user sees the post-end state, not the last frame). Back off
                // ~50ms — invisible on the ruler at any zoom, and reliably decodes a
                // real frame regardless of source fps.
                var endTarget = Math.Max(0, Duration - 0.05);
                Position = endTarget;
                RaiseTime(PositionDraggedEvent,endTarget);
                e.Handled = true;
                break;
            case Key.OemPlus:
            case Key.Add:
                ZoomAround(pivot, 0.7);
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                ZoomAround(pivot, 1.0 / 0.7);
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                ViewStart = 0;
                ViewEnd = Duration;
                e.Handled = true;
                break;
        }
    }

    private double ViewDuration => Math.Max(MinViewDuration, ViewEnd - ViewStart);
    private double TimeToX(double t) => TimelineMath.TimeToX(t, ViewStart, ViewEnd, Bounds.Width);
    private double XToTime(double x) => TimelineMath.XToTime(x, ViewStart, ViewEnd, Bounds.Width, Duration);

    private static double NiceInterval(double target)
    {
        ReadOnlySpan<double> nice = [0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];
        foreach (var n in nice)
            if (n >= target) return n;
        return 3600;
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : ts.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
}
