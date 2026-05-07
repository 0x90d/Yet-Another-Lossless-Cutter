using System.Collections.Generic;

namespace YetAnotherLosslessCutter.Plugins;

/// <summary>
/// Contributes status pills shown below the file label in the main window.
/// Called whenever the status bar refreshes (settings changes, etc.) — keep
/// implementations cheap and side-effect-free.
/// </summary>
public interface IStatusBadgeProvider
{
    IReadOnlyList<StatusBadge> GetBadges();
}

/// <summary>One pill: a label and an active/inactive flag (controls the dot + text colour).</summary>
public sealed class StatusBadge
{
    public string Label { get; init; } = "";
    public bool Active { get; init; }
}
