using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace YetAnotherLosslessCutter.Plugins;

/// <summary>
/// Loads plugins at startup and populates <see cref="PluginRegistry"/> with their
/// names so the Settings UI can show what's installed.
///
/// Two paths, one for each .NET runtime mode:
/// <list type="bullet">
///   <item><b>JIT</b> — scan <c>{app}/Yalc.Plugins.*.dll</c>, <c>Assembly.LoadFrom</c>
///   each, then <see cref="RuntimeHelpers.RunModuleConstructor"/> to fire each
///   plugin's <c>[ModuleInitializer]</c>. <c>LoadFrom</c> alone doesn't run
///   module initializers — the runtime fires them only when code in the module
///   is first invoked, and core code never touches plugin types directly.</item>
///   <item><b>NativeAOT</b> — plugin code is statically linked into the binary;
///   module initializers run unconditionally at startup before <c>Main</c>. We
///   skip the LoadFrom path entirely (the .dll files don't exist on disk anyway)
///   and just enumerate already-loaded assemblies for the registry.</item>
/// </list>
/// </summary>
internal static class PluginLoader
{
    private const string Prefix = "Yalc.Plugins.";
    private const string AbstractionsName = "Yalc.Plugins.Abstractions";

    [UnconditionalSuppressMessage(
        "Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "LoadFrom is JIT-only — guarded by RuntimeFeature.IsDynamicCodeSupported.")]
    [UnconditionalSuppressMessage(
        "SingleFile", "IL3000:RequiresAssemblyFiles",
        Justification = "LoadFrom is JIT-only — guarded by RuntimeFeature.IsDynamicCodeSupported.")]
    internal static void LoadAll()
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
            LoadFromDirectory();

        // Populate the registry from currently-loaded assemblies. Works in both modes:
        // under JIT, the LoadFrom step above made the plugins discoverable here; under
        // AOT, they were linked into the binary and are already loaded as managed
        // assembly identities.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (string.IsNullOrEmpty(name)) continue;
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(name, AbstractionsName, StringComparison.OrdinalIgnoreCase)) continue;

            var displayName = name[Prefix.Length..];
            PluginRegistry.Add(new PluginRegistry.PluginInfo(name, displayName));
        }
    }

    // Suppressions repeated here because they don't propagate from LoadAll's call
    // site into the helper. Same justification: LoadFrom is JIT-only, gated by
    // RuntimeFeature.IsDynamicCodeSupported in LoadAll.
    [UnconditionalSuppressMessage(
        "Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "LoadFrom is JIT-only — guarded by RuntimeFeature.IsDynamicCodeSupported in LoadAll.")]
    [UnconditionalSuppressMessage(
        "SingleFile", "IL3000:RequiresAssemblyFiles",
        Justification = "LoadFrom is JIT-only — guarded by RuntimeFeature.IsDynamicCodeSupported in LoadAll.")]
    private static void LoadFromDirectory()
    {
        var dir = AppContext.BaseDirectory;
        if (!Directory.Exists(dir)) return;

        foreach (var dll in Directory.EnumerateFiles(dir, $"{Prefix}*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(dll);
            if (string.Equals(name, AbstractionsName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var asm = Assembly.LoadFrom(dll);
                // Force the [ModuleInitializer] to run — see the class summary for why.
                foreach (var module in asm.GetModules())
                    RuntimeHelpers.RunModuleConstructor(module.ModuleHandle);
            }
            catch { /* missing / blocked / load failure — non-fatal, skip */ }
        }
    }
}
