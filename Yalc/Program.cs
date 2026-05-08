using Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;
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
        // Catch unhandled exceptions and dump them to last-error.log next to the
        // executable. macOS crash reports only carry native frames, so without this
        // a managed exception escaping to the runtime is invisible to the user.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogFatal("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            LogFatal("UnobservedTaskException", e.Exception);

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

    private static void LogFatal(string source, Exception? ex)
    {
        try
        {
            var path = Path.Combine(
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                "last-error.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n");
        }
        catch { /* nothing useful to do if logging itself fails */ }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // macOS: Avalonia 12 defaults to [Metal, OpenGl, Software]. The
            // video surface (MpvOpenGlVideoView : OpenGlControlBase) only gets
            // driven when the OpenGL backend is active — under Metal,
            // OnOpenGlInit / OnOpenGlRender never fire, the mpv render context
            // never comes up, and loadfile stays queued forever (player area
            // is black, timeline shows "No media loaded"). Force OpenGl first.
            .With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = new[]
                {
                    AvaloniaNativeRenderingMode.OpenGl,
                    AvaloniaNativeRenderingMode.Software,
                },
            })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
