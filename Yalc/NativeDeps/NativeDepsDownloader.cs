using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;

namespace YetAnotherLosslessCutter.NativeDeps;

/// <summary>
/// Downloads + extracts libmpv (.7z from shinchiro/mpv-winbuild-cmake) and FFmpeg
/// (.zip from BtbN/FFmpeg-Builds) into the app directory. Windows-only — Linux and
/// macOS users are expected to install via their package manager.
///
/// Reports progress via <see cref="StatusChanged"/> (one short line of text) and
/// <see cref="ProgressChanged"/> (bytes downloaded / total or null when unknown).
/// All operations honour the supplied <see cref="CancellationToken"/>.
/// </summary>
internal sealed class NativeDepsDownloader
{
    private const long MaxDownloadBytes = 500L * 1024 * 1024;
    private static readonly HttpClient _http = CreateHttpClient();

    public event Action<string>? StatusChanged;
    public event Action<long, long?>? ProgressChanged;

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        // GitHub's API rejects User-Agent: null — supply a project-identifying string.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Yalc-NativeDepsDownloader/1.0");
        return c;
    }

    public async Task DownloadLibmpvAsync(string targetDir, CancellationToken ct = default)
    {
        Status("Querying latest libmpv release…");
        var assetUrl = await FindShinchiroLibmpvAssetAsync(ct);
        var assetName = Path.GetFileName(new Uri(assetUrl).AbsolutePath);

        var tempRoot = Path.Combine(Path.GetTempPath(), "Yalc.NativeDeps");
        Directory.CreateDirectory(tempRoot);
        var archivePath = Path.Combine(tempRoot, "mpv-dev.7z");

        Status("Downloading libmpv…");
        await DownloadAsync(assetUrl, archivePath, ct);

        Status("Verifying libmpv checksum…");
        await VerifyChecksumIfAvailableAsync(assetUrl, archivePath, assetName, ct);

        Status("Extracting libmpv…");
        ExtractLibmpvFromSevenZip(archivePath, targetDir);

        TryDelete(archivePath);
    }

    public async Task DownloadFfmpegAsync(string targetDir, CancellationToken ct = default)
    {
        // BtbN tag pattern: ffmpeg-n{minor}-latest-{platform}-gpl-shared-{minor}.zip.
        // FFmpeg.AutoGen 8.0.0 binds against the FFmpeg 8.x ABI (avcodec major 62) — n8.1
        // ships the same major and is what BtbN currently publishes. Bump the constant
        // when FFmpeg.AutoGen moves to a higher major.
        const string version = "8.1";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win64",
            Architecture.Arm64 => "winarm64",
            _ => "win64",
        };
        var assetName = $"ffmpeg-n{version}-latest-{arch}-gpl-shared-{version}.zip";
        var assetUrl = $"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/{assetName}";

        var tempRoot = Path.Combine(Path.GetTempPath(), "Yalc.NativeDeps");
        Directory.CreateDirectory(tempRoot);
        var archivePath = Path.Combine(tempRoot, assetName);

        Status("Downloading FFmpeg…");
        await DownloadAsync(assetUrl, archivePath, ct);

        Status("Verifying FFmpeg checksum…");
        await VerifyChecksumIfAvailableAsync(assetUrl, archivePath, assetName, ct);

        Status("Extracting FFmpeg…");
        ExtractFfmpegFromZip(archivePath, targetDir);

        TryDelete(archivePath);
    }

    /// <summary>
    /// Fetches the upstream <c>checksums.sha256</c> sibling file (BtbN ships this for
    /// every release; shinchiro doesn't always), parses out the line for the asset we
    /// just downloaded, and compares the SHA256 of the file on disk. Throws if the
    /// hashes mismatch (download is corrupted or tampered with). Logs and skips
    /// verification if the checksum file is unavailable — matches VDF's behaviour.
    /// </summary>
    private static async Task VerifyChecksumIfAvailableAsync(
        string downloadUrl, string filePath, string assetName, CancellationToken ct)
    {
        var checksumUrl = new Uri(new Uri(downloadUrl), "checksums.sha256");
        string checksumText;
        try
        {
            using var resp = await _http.GetAsync(checksumUrl, ct);
            if (!resp.IsSuccessStatusCode) return; // upstream doesn't publish hashes — skip
            checksumText = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException) { return; } // network blip on the sibling URL — skip

        // checksums.sha256 format: "<hex-hash>  <filename>" lines (two spaces).
        string? expected = null;
        foreach (var line in checksumText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split("  ", 2, StringSplitOptions.None);
            if (parts.Length == 2
                && parts[1].Trim().Equals(assetName, StringComparison.OrdinalIgnoreCase))
            {
                expected = parts[0].Trim().ToLowerInvariant();
                break;
            }
        }
        if (expected == null) return; // entry not present (some bundles omit individual files)

        await using var fs = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(fs, ct);
        var actual = Convert.ToHexStringLower(hashBytes);

        if (actual != expected)
            throw new InvalidOperationException(
                $"Checksum mismatch for '{assetName}': expected {expected}, got {actual}. " +
                "Download corrupted or tampered with.");
    }

    private async Task<string> FindShinchiroLibmpvAssetAsync(CancellationToken ct)
    {
        // Asset filenames carry a date stamp (mpv-dev-x86_64-v3-YYYYMMDD-git-XXXXXXX.7z)
        // so we can't predict them — query the GitHub API for the latest release and
        // pick the matching asset.
        const string apiUrl = "https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest";
        var release = await _http.GetFromJsonAsync(apiUrl, GitHubJsonContext.Default.GitHubRelease, ct)
                      ?? throw new InvalidOperationException("Empty response from GitHub API");

        // x86_64-v3 is the most broadly compatible build (AVX2-only would gate out older CPUs).
        // arm64 builds also exist but Yalc's libmpv P/Invoke is win-x64 only for now.
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "aarch64"
            : "x86_64-v3";

        var asset = release.Assets?.FirstOrDefault(a =>
            a.Name != null
            && a.Name.StartsWith($"mpv-dev-{arch}-", StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));

        if (asset?.BrowserDownloadUrl == null)
            throw new InvalidOperationException(
                $"No mpv-dev-{arch}-*.7z asset found in latest shinchiro/mpv-winbuild-cmake release");

        return asset.BrowserDownloadUrl;
    }

    private async Task DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase} for {url}");

        var totalBytes = resp.Content.Headers.ContentLength;
        if (totalBytes > MaxDownloadBytes)
            throw new HttpRequestException($"Download too large ({totalBytes} bytes, max {MaxDownloadBytes})");

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buf = new byte[81920];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            total += read;
            if (total > MaxDownloadBytes)
                throw new HttpRequestException($"Download exceeded size limit ({MaxDownloadBytes} bytes)");
            ProgressChanged?.Invoke(total, totalBytes);
        }
        ProgressChanged?.Invoke(total, totalBytes);
    }

    private static void ExtractLibmpvFromSevenZip(string archivePath, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        using var archive = SevenZipArchive.OpenArchive(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory || entry.Key == null) continue;
            // We only need libmpv-2.dll. The shinchiro archive also ships includes,
            // headers and a few other DLLs we don't use — skip them to keep the
            // extracted footprint small.
            var name = Path.GetFileName(entry.Key);
            if (!string.Equals(name, "libmpv-2.dll", StringComparison.OrdinalIgnoreCase))
                continue;
            var destPath = SafeJoin(targetDir, name);
            using var stream = entry.OpenEntryStream();
            using var dest = File.Create(destPath);
            stream.CopyTo(dest);
        }
    }

    private static void ExtractFfmpegFromZip(string archivePath, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        using var zip = ZipFile.OpenRead(archivePath);
        // BtbN's archive contains a single top-level folder; the files we need are
        // under .../bin/ — we want all *.dll, ffmpeg.exe and ffprobe.exe.
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!entry.FullName.Contains("/bin/", StringComparison.Ordinal)) continue;

            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (ext != ".dll" && ext != ".exe") continue;
            // Skip duplicates if the archive somehow has a name collision under bin/.
            if (!wanted.Add(entry.Name)) continue;

            var destPath = SafeJoin(targetDir, entry.Name);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    private static string SafeJoin(string root, string fileName)
    {
        // Defence-in-depth against zip-slip — even though we only ever pass the leaf
        // file name, harden the join in case a pathological extractor change slips in.
        if (Path.IsPathRooted(fileName) || fileName.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing suspicious entry name: {fileName}");
        var full = Path.GetFullPath(Path.Combine(root, fileName));
        var rootFull = Path.GetFullPath(root);
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Entry would extract outside target directory");
        return full;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* leftover in temp is fine */ }
    }

    private void Status(string s) => StatusChanged?.Invoke(s);

    // --- GitHub API JSON shapes (source-gen'd for AOT safety) ---

    internal sealed class GitHubRelease
    {
        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    internal sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}

[JsonSerializable(typeof(NativeDepsDownloader.GitHubRelease))]
[JsonSerializable(typeof(NativeDepsDownloader.GitHubAsset))]
internal partial class GitHubJsonContext : JsonSerializerContext { }
