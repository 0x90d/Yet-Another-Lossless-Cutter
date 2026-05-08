using System;

namespace YetAnotherLosslessCutter.Plugins;

/// <summary>
/// Contributes additional tokens to the output-filename template renderer. Each
/// registered plugin gets a chance to resolve any token the core renderer doesn't
/// know about; the first non-null/non-empty return wins. Plugins compose left-to-right
/// in registration order.
///
/// Example: a CamshowRouter plugin could provide <c>{model}</c> by extracting the
/// streamer name from the source filename, letting users build templates like
/// <c>{model}/{name}-{start}-{end}{ext}</c>.
/// </summary>
public interface IOutputTokenPlugin
{
    /// <summary>
    /// Try to resolve <paramref name="token"/>. Return <c>null</c> (or empty) to defer
    /// to other plugins / the core renderer. Should be pure — no side effects, no I/O.
    /// </summary>
    string? ResolveToken(string token, OutputTokenContext context);
}

/// <summary>
/// Read-only context for token resolution. Carries the inputs a plugin might need
/// to compute its token value, without coupling to the full Settings or VideoSegment
/// types.
/// </summary>
public sealed class OutputTokenContext
{
    public string SourceFile { get; init; } = string.Empty;
    public TimeSpan SegmentStart { get; init; }
    public TimeSpan SegmentEnd { get; init; }
    public int SegmentIndex { get; init; }
}
