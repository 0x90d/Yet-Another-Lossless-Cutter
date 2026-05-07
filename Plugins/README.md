# Plugins

Drop a plugin folder in here and it gets picked up automatically by the main project's
`<ProjectReference Include="..\Plugins\**\*.csproj"/>` glob, then its
`[ModuleInitializer]` registers contributions with `PluginHost` at startup.

This folder is gitignored (except this README), so the public repo carries no plugin
code and your private plugins stay private.

## Why compile-time plugins, not runtime DLLs

YALC publishes as NativeAOT. NativeAOT does not support `Assembly.LoadFrom` of arbitrary
DLLs at runtime — a published binary can only execute code that was reachable at AOT
compile time. So plugins are sibling `.csproj`s, included via the glob above, compiled
into the same build graph as the main app. AOT-clean, type-safe, single binary.

## Minimal plugin

```
Plugins/MyPlugin/
  Yalc.Plugins.MyPlugin.csproj
  MyPlugin.cs
```

```xml
<!-- Yalc.Plugins.MyPlugin.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <RootNamespace>YetAnotherLosslessCutter.Plugins.MyPlugin</RootNamespace>
    <AssemblyName>Yalc.Plugins.MyPlugin</AssemblyName>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Yalc.Plugins.Abstractions\Yalc.Plugins.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

```csharp
// MyPlugin.cs
using System.Runtime.CompilerServices;
using YetAnotherLosslessCutter.Plugins;

namespace YetAnotherLosslessCutter.Plugins.MyPlugin;

internal static class PluginInit
{
    [ModuleInitializer]
    internal static void Register()
    {
        PluginHost.Register<IOutputPathPlugin>(new MyOutputPathPlugin());
        // …also IStatusBadgeProvider, ISettingsContribution, IFilePickerFilter as needed
    }
}

internal sealed class MyOutputPathPlugin : IOutputPathPlugin
{
    public string TransformOutputDirectory(string sourceFile, string baseOutputDir, OutputPathContext ctx)
    {
        // Inspect sourceFile, return a transformed directory or baseOutputDir unchanged.
        return baseOutputDir;
    }
}
```

The assembly name **must** start with `Yalc.Plugins.` — the loader filters by that
prefix when forcing module initializers to fire under JIT.

## Available extension points

All in the `YetAnotherLosslessCutter.Plugins` namespace
(`Yalc.Plugins.Abstractions` project):

- **`IOutputPathPlugin`** — transform the output directory per cut.
- **`ISettingsContribution`** — add a card to the Settings window.
- **`IStatusBadgeProvider`** — add pills to the main-window status bar.
  Call `PluginHost.NotifyBadgesChanged()` when your state flips.
- **`IFilePickerFilter`** — post-process the "Open from folder" file list.

Plugins manage their own settings persistence — write a JSON next to the exe, or
whatever fits.
