using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace YetAnotherLosslessCutter.NativeDeps;

/// <summary>
/// First-run dialog shown when libmpv and/or FFmpeg are missing. Two flavours:
/// <list type="bullet">
///   <item>Windows — offers to auto-download into the app directory.</item>
///   <item>Linux / macOS — shows the per-distro install command.</item>
/// </list>
/// Returns one of <see cref="NativeDepsDialogResult"/> via <c>ShowDialog</c>.
/// </summary>
public partial class NativeDepsDialog : Window
{
    private readonly NativeDepsCheck.MissingDeps _missing;
    private readonly bool _isWindows;
    private CancellationTokenSource? _downloadCts;
    private bool _downloadCompleted;

    public NativeDepsDialog() : this(NativeDepsCheck.Detect()) { }

    // Internal — MissingDeps is internal, and the only legitimate caller is our own
    // ShowIfNeededAsync. The parameterless ctor is what XAML / public callers use.
    internal NativeDepsDialog(NativeDepsCheck.MissingDeps missing)
    {
        _missing = missing;
        _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        InitializeComponent();
        RenderInitialState();
    }

    /// <summary>
    /// Convenience: detect, show only if anything is missing, return when done.
    /// Use from <c>App.OnFrameworkInitializationCompleted</c> before constructing the
    /// main window so the runtime check happens before any P/Invoke into libmpv.
    /// </summary>
    public static Task<NativeDepsDialogResult> ShowIfNeededAsync()
    {
        var missing = NativeDepsCheck.Detect();
        if (!missing.AnyMissing) return Task.FromResult(NativeDepsDialogResult.NotNeeded);
        return ShowAsync(missing, owner: null);
    }

    /// <summary>
    /// Forces the dialog open even if all components are present — pretends both
    /// libmpv and FFmpeg are missing. Used by the "Reinstall native components"
    /// button in Settings to refetch / repair an installation.
    /// </summary>
    public static Task<NativeDepsDialogResult> ShowForceAsync(Window owner) =>
        ShowAsync(new NativeDepsCheck.MissingDeps(Libmpv: true, Ffmpeg: true), owner);

    private static Task<NativeDepsDialogResult> ShowAsync(NativeDepsCheck.MissingDeps missing, Window? owner)
    {
        var dlg = new NativeDepsDialog(missing);
        var tcs = new TaskCompletionSource<NativeDepsDialogResult>();
        dlg.Closed += (_, _) => tcs.TrySetResult(dlg._result);
        // ShowIfNeededAsync runs before any window exists; the reinstall button has a
        // real owner. Use ShowDialog when owned so the modal blocks the Settings window.
        if (owner != null) _ = dlg.ShowDialog(owner);
        else dlg.Show();
        return tcs.Task;
    }

    private NativeDepsDialogResult _result = NativeDepsDialogResult.Skipped;

    private void RenderInitialState()
    {
        var missing = MissingList();
        HeadlineText.Text = "Native components needed";

        if (_isWindows)
        {
            MessageText.Text =
                $"Yalc needs the following to play and cut video: {missing}.\n\n" +
                "Click Download to fetch them automatically (~80 MB, one-time). " +
                "They'll be placed next to Yalc.exe — your system PATH won't be touched.";
            PrimaryButton.Content = "Download";
            SecondaryButton.Content = "Skip";
        }
        else
        {
            // Linux / macOS — no auto-download; show package-manager instructions.
            MessageText.Text =
                $"Yalc needs the following to play and cut video: {missing}.\n\n" +
                "Install them via your package manager, then restart Yalc:";
            DetailText.Text = BuildUnixInstallCommands();
            DetailText.IsVisible = true;
            PrimaryButton.Content = "OK";
            SecondaryButton.Content = "Continue anyway";
        }
    }

    private string MissingList()
    {
        if (_missing.Libmpv && _missing.Ffmpeg) return "libmpv and FFmpeg";
        if (_missing.Libmpv) return "libmpv";
        return "FFmpeg";
    }

    private string BuildUnixInstallCommands()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "brew install mpv ffmpeg";

        // Linux — show the three most common distro families. Users on niche distros
        // can adapt; we don't try to detect the distro.
        return
            "Debian / Ubuntu:  sudo apt install libmpv2 ffmpeg\n" +
            "Fedora:           sudo dnf install mpv-libs ffmpeg\n" +
            "Arch:             sudo pacman -S mpv ffmpeg";
    }

    private async void Primary_Click(object? sender, RoutedEventArgs e)
    {
        if (!_isWindows)
        {
            // OK on the Unix branch — the user is supposed to install via package
            // manager and restart, so we close cleanly.
            _result = NativeDepsDialogResult.Skipped;
            Close();
            return;
        }

        if (_downloadCompleted)
        {
            // Second click after download success — primary button reads "Restart".
            _result = NativeDepsDialogResult.RestartRequested;
            Close();
            return;
        }

        await RunDownloadAsync();
    }

    private void Secondary_Click(object? sender, RoutedEventArgs e)
    {
        if (_downloadCts != null && !_downloadCompleted)
        {
            // Cancel an in-flight download — "Skip" turns into "Cancel" while running.
            _downloadCts.Cancel();
            return;
        }
        _result = NativeDepsDialogResult.Skipped;
        Close();
    }

    private async Task RunDownloadAsync()
    {
        PrimaryButton.IsEnabled = false;
        SecondaryButton.Content = "Cancel";
        ProgressCard.IsVisible = true;
        DetailText.IsVisible = false;

        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        var downloader = new NativeDepsDownloader();
        downloader.StatusChanged += s => Dispatcher.UIThread.Post(() => ProgressStatusText.Text = s);
        downloader.ProgressChanged += (read, total) => Dispatcher.UIThread.Post(() =>
        {
            ProgressBar.Value = total is { } t && t > 0 ? read / (double)t * 100 : 0;
            ProgressBytesText.Text = string.Format(CultureInfo.InvariantCulture,
                "{0} / {1}", FormatBytes(read), total.HasValue ? FormatBytes(total.Value) : "?");
        });

        try
        {
            var targetDir = AppContext.BaseDirectory;
            // Order matters: libmpv is the bigger download; do it first so a cancel
            // mid-flight only affects the bigger one and ffmpeg might still succeed
            // on a retry. (Ordering's mostly cosmetic — both are near-mandatory.)
            if (_missing.Libmpv)
                await downloader.DownloadLibmpvAsync(targetDir, ct);
            if (_missing.Ffmpeg)
                await downloader.DownloadFfmpegAsync(targetDir, ct);

            _downloadCompleted = true;
            HeadlineText.Text = "Done";
            MessageText.Text =
                "Components downloaded. Yalc needs to restart to load them — " +
                "click Restart to launch with the new binaries.";
            ProgressCard.IsVisible = false;
            PrimaryButton.Content = "Restart";
            PrimaryButton.IsEnabled = true;
            SecondaryButton.Content = "Quit";
        }
        catch (OperationCanceledException)
        {
            // User cancelled — leave the dialog open so they can hit Skip or retry.
            HeadlineText.Text = "Download cancelled";
            ProgressCard.IsVisible = false;
            PrimaryButton.Content = "Retry";
            PrimaryButton.IsEnabled = true;
            SecondaryButton.Content = "Skip";
        }
        catch (Exception ex)
        {
            HeadlineText.Text = "Download failed";
            MessageText.Text =
                "Something went wrong. You can retry, skip (Yalc will run but " +
                "video playback won't work), or download libmpv + FFmpeg manually " +
                "and drop the files next to Yalc.exe.";
            DetailText.Text = ex.Message;
            DetailText.IsVisible = true;
            ProgressCard.IsVisible = false;
            PrimaryButton.Content = "Retry";
            PrimaryButton.IsEnabled = true;
            SecondaryButton.Content = "Skip";
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private static string FormatBytes(long bytes)
    {
        double size = bytes;
        string[] units = { "B", "KB", "MB", "GB" };
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", size, units[unit]);
    }
}

public enum NativeDepsDialogResult
{
    /// <summary>Nothing was missing — dialog wasn't shown.</summary>
    NotNeeded,
    /// <summary>User dismissed without downloading; app should proceed but may misbehave.</summary>
    Skipped,
    /// <summary>Download completed; the app must be restarted before P/Invoking the new binaries.</summary>
    RestartRequested,
}
