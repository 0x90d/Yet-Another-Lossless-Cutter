using System.Collections.Generic;

namespace YetAnotherLosslessCutter.Plugins;

/// <summary>
/// Tracks plugins discovered at startup so the Settings window can list them.
/// Populated by <see cref="PluginLoader"/> once at startup; consumers read
/// <see cref="All"/> after that.
/// </summary>
internal static class PluginRegistry
{
    public sealed record PluginInfo(string AssemblyName, string DisplayName);

    private static readonly List<PluginInfo> _all = new();
    public static IReadOnlyList<PluginInfo> All => _all;

    internal static void Add(PluginInfo info) => _all.Add(info);
}
