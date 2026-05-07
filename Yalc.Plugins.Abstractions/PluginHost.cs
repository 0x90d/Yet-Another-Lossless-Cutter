using System;
using System.Collections.Generic;

namespace YetAnotherLosslessCutter.Plugins;

/// <summary>
/// Static registry for plugin contributions. Plugins call <see cref="Register{T}"/> from
/// a [ModuleInitializer]; core code calls <see cref="Get{T}"/> at the relevant hook points.
/// AOT-clean: no reflection, no dynamic loading. The full set of plugins is determined at
/// compile time via ProjectReferences.
/// </summary>
public static class PluginHost
{
    private static readonly Dictionary<Type, List<object>> _registry = new();
    private static readonly object _lock = new();

    /// <summary>
    /// Register a plugin contribution. Typically called from a plugin's
    /// [ModuleInitializer] so the registration is wired up before any core code
    /// queries the host.
    /// </summary>
    public static void Register<T>(T contribution) where T : class
    {
        lock (_lock)
        {
            if (!_registry.TryGetValue(typeof(T), out var list))
            {
                list = new List<object>();
                _registry[typeof(T)] = list;
            }
            list.Add(contribution);
        }
    }

    /// <summary>
    /// Returns all registered contributions of the given type, in registration order.
    /// Returns an empty list if none are registered.
    /// </summary>
    public static IReadOnlyList<T> Get<T>() where T : class
    {
        lock (_lock)
        {
            if (!_registry.TryGetValue(typeof(T), out var list) || list.Count == 0)
                return Array.Empty<T>();
            // Snapshot copy so callers can iterate without holding the lock.
            var snapshot = new T[list.Count];
            for (var i = 0; i < list.Count; i++) snapshot[i] = (T)list[i];
            return snapshot;
        }
    }

    /// <summary>
    /// Raised when a plugin's status badge state changes. The main window subscribes
    /// and re-renders pills. Plugins call <see cref="NotifyBadgesChanged"/> when their
    /// own settings change. Stays decoupled from any specific UI framework.
    /// </summary>
    public static event Action? BadgesChanged;

    public static void NotifyBadgesChanged() => BadgesChanged?.Invoke();
}
