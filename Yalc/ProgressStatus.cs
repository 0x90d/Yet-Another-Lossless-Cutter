namespace YetAnotherLosslessCutter;

public enum ProgressStatus
{
    Idle,
    Waiting,
    Running,
    Merging,
    Failed,
    Finished,
    /// <summary>User cancelled mid-cut. Distinct from Failed (which means an unexpected error).</summary>
    Cancelled,
}
