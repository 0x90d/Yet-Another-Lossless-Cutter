namespace YetAnotherLosslessCutter.Plugins;

/// <summary>
/// Overrides the default magnitude (in seconds) for the right-click auto-repeat
/// "jump forward" action that core hardcodes at 1 minute. Each registered rule
/// is consulted in order; the first non-null return wins. Returning <c>null</c>
/// defers to the next rule (or, if none match, the core default of 60s).
/// </summary>
public interface IAutoSeekRule
{
    /// <summary>
    /// Decide the auto-seek magnitude for <paramref name="sourceFile"/>. Should be
    /// pure — no I/O, no side effects. Called every time the user right-clicks an
    /// auto-repeat button (cheap operation, but don't do anything expensive here).
    /// </summary>
    /// <returns>Seconds to use for the next auto-repeat, or null to defer.</returns>
    double? GetAutoSeekSeconds(string sourceFile);
}
