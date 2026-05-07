using System;

namespace YetAnotherLosslessCutter;

/// <summary>
/// Base class for items that go through the cutting pipeline. Holds progress + status.
/// </summary>
public class Segment : ViewModelBase
{
    private double _progress;
    public double Progress
    {
        get => _progress;
        set
        {
            // Original silently rejected out-of-range values. Clamp instead — caller
            // bugs shouldn't be invisible.
            value = Math.Clamp(value, 0d, 1d);
            if (Set(ref _progress, value))
                OnPropertyChanged(nameof(ProgressText));
        }
    }

    public string ProgressText => $"{Math.Round(_progress * 100d)}%";

    private ProgressStatus _status;
    public ProgressStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
                OnPropertyChanged(nameof(IsEnabled));
        }
    }

    /// <summary>True if this segment can be edited / removed from the queue.</summary>
    public bool IsEnabled => _status is
        ProgressStatus.Idle or
        ProgressStatus.Finished or
        ProgressStatus.Failed or
        ProgressStatus.Cancelled;
}
