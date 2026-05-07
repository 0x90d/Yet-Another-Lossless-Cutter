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
