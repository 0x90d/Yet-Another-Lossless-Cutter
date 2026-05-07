using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using YetAnotherLosslessCutter.NativeDeps;

namespace YetAnotherLosslessCutter;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Native-dep gate. If libmpv / FFmpeg are missing, MainWindow's MpvPlayer
            // would crash on its first P/Invoke. Show the first-run dialog before
            // creating MainWindow. Posted onto the dispatcher so framework init can
            // complete before ShowDialog spins up its message pump.
            Dispatcher.UIThread.Post(async () =>
            {
                var result = await NativeDepsDialog.ShowIfNeededAsync();
                if (result == NativeDepsDialogResult.RestartRequested)
                {
                    // Relaunch so the freshly-downloaded libmpv-2.dll is picked up by a
                    // clean P/Invoke binding — we can't reset the failed binding in-process.
                    // - Environment.ProcessPath is the AOT-friendly way to get our own exe
                    //   (Process.GetCurrentProcess().MainModule trips a trim warning and is
                    //   sometimes flaky under single-file/AOT publish).
                    // - UseShellExecute=true routes through ShellExecuteEx so the child is
                    //   fully detached — no inherited handles, no parent-shutdown side
                    //   effects on the new process.
                    var exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = exe,
                                UseShellExecute = true,
                                WorkingDirectory = AppContext.BaseDirectory,
                            });
                        }
                        catch
                        {
                            // If relaunch fails for any reason, fall through and just exit;
                            // the user can launch manually. Better than hanging.
                        }
                    }
                    desktop.Shutdown();
                    return;
                }
                desktop.MainWindow = new MainWindow();
                desktop.MainWindow.Show();
            });
        }

        base.OnFrameworkInitializationCompleted();
    }
}
