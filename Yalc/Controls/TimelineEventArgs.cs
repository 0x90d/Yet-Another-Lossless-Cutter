using Avalonia.Interactivity;

namespace YetAnotherLosslessCutter.Controls;

/// <summary>
/// Routed-event args carrying an absolute time (seconds) along the timeline. Used by
/// <see cref="TimelineControl.PositionDragged"/> and
/// <see cref="TimelineControl.AddSegmentRequested"/>.
/// </summary>
public sealed class TimelineTimeEventArgs : RoutedEventArgs
{
    public double Time { get; }

    public TimelineTimeEventArgs(RoutedEvent routedEvent, object source, double time)
        : base(routedEvent, source)
    {
        Time = time;
    }
}

/// <summary>
/// Routed-event args carrying a <see cref="VideoSegment"/> reference. Used by
/// <see cref="TimelineControl.PlaySegmentRequested"/>.
/// </summary>
public sealed class TimelineSegmentEventArgs : RoutedEventArgs
{
    public VideoSegment Segment { get; }

    public TimelineSegmentEventArgs(RoutedEvent routedEvent, object source, VideoSegment segment)
        : base(routedEvent, source)
    {
        Segment = segment;
    }
}

/// <summary>
/// Routed-event args raised once at the END of a segment drag (move / resize / edge),
/// carrying the segment's pre-drag bounds so the host can push a single undo entry
/// per drag rather than one per pointer-move tick.
/// </summary>
public sealed class TimelineSegmentEditedEventArgs : RoutedEventArgs
{
    public VideoSegment Segment { get; }
    public double OldFromSeconds { get; }
    public double OldToSeconds { get; }

    public TimelineSegmentEditedEventArgs(RoutedEvent routedEvent, object source,
        VideoSegment segment, double oldFromSeconds, double oldToSeconds)
        : base(routedEvent, source)
    {
        Segment = segment;
        OldFromSeconds = oldFromSeconds;
        OldToSeconds = oldToSeconds;
    }
}
