using Avalonia;
using System;
using YetAnotherLosslessCutter.Mpv;
using YetAnotherLosslessCutter.Plugins;

namespace YetAnotherLosslessCutter;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Map the "libmpv-2" DllImport name to the right shared library per OS
        // (libmpv-2.dll / libmpv.so.2 / libmpv.2.dylib). Must run before any
        // P/Invoke into LibMpv.
        LibMpvResolver.Register();

        // Force every referenced Yalc.Plugins.* assembly to load now so each
        // plugin's [ModuleInitializer] runs and registers with PluginHost before any
        // window starts querying for contributions. See PluginLoader for the why.
        PluginLoader.LoadAll();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
