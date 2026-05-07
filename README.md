# Yalc — Yet Another Lossless Cutter

A keyboard-friendly, frame-accurate lossless video cutter for Windows, Linux and macOS.
Trim segments out of long recordings without re-encoding — cuts are **lossless** (stream
copy via ffmpeg) so a 5-second clip of a 4K HDR file finishes in seconds and the output
is bit-identical to the source.

Built on Avalonia 12 / .NET 10 with libmpv embedded directly for smooth, frame-accurate
scrubbing.

## Features

- **Frame-accurate scrubbing** — drag the timeline, jump 1 frame at a time with `,` / `.`
- **Multi-segment cuts** — mark several in/out points; queue runs them as separate output files (or merged into one)
- **Custom timeline** with thumbnail strip, hover preview, zoom + pan, audio waveform overlay, and per-segment color bands
- **Queue with persistence** — cuts survive app restarts; failed jobs keep their error reason
- **Open from folder** — bulk-load a directory of videos as a playlist with size / sort filters
- **Plugin system** — extend output-path routing, settings, status badges and file-picker filtering without touching core. See [Plugins/README.md](Plugins/README.md).
- **Configurable** — auto-mute, auto-delete source after cut, low-priority cutting (so it doesn't fight foreground apps), shutdown PC when queue is done

## Install

### Prebuilt releases

Download the matching archive for your OS from the [latest release](../../releases/latest):

| OS | File |
|---|---|
| Windows x64 | `Yalc-win-x64.zip` |
| Linux x64 | `Yalc-linux-x64.tar.gz` |
| Linux arm64 | `Yalc-linux-arm64.tar.gz` |
| macOS Intel | `Yalc-osx-x64.tar.gz` |
| macOS Apple Silicon | `Yalc-osx-arm64.tar.gz` |

Releases are self-contained — no .NET runtime required.

### Windows

1. Unzip `Yalc-win-x64.zip` anywhere
2. Double-click `Yalc.exe`
3. On first run Yalc detects that `libmpv-2.dll` and the FFmpeg shared libraries are
   missing and offers to download them (~80 MB, one-time). They're placed next to
   `Yalc.exe` — your system PATH isn't touched. Yalc restarts itself once the
   download is done.

If you'd rather install manually, drop `libmpv-2.dll` (from the
[shinchiro mpv-dev](https://github.com/shinchiro/mpv-winbuild-cmake/releases) archive)
and the FFmpeg shared DLLs (from [BtbN](https://github.com/BtbN/FFmpeg-Builds/releases))
next to `Yalc.exe` and skip the first-run prompt.

### Linux

1. Extract: `tar xzf Yalc-linux-x64.tar.gz`
2. Install runtime dependencies via your package manager:
   - **Debian / Ubuntu:** `sudo apt install libmpv2 ffmpeg`
   - **Fedora:** `sudo dnf install mpv-libs ffmpeg`
   - **Arch:** `sudo pacman -S mpv ffmpeg`
3. Run: `./Yalc`

Optional desktop integration: copy `yalc.desktop` to `~/.local/share/applications/` and
the bundled icon to `~/.local/share/icons/`.

### macOS

1. Extract: `tar xzf Yalc-osx-arm64.tar.gz` (or the x64 variant on Intel Macs)
2. Install dependencies via Homebrew: `brew install mpv ffmpeg`
3. First run: right-click `Yalc.app` → Open (Gatekeeper prompts the first time only).
   Subsequent launches work normally.

## Keyboard

| Key | Action |
|---|---|
| Space | Play / pause |
| ← / → | Seek 1 second |
| `,` / `.` | Step one frame back / forward |
| `S` / `E` | Set in / out point on most-recent segment |
| `A` | Add a 5-second segment at the playhead |
| `Ctrl + scroll` | Zoom timeline (cursor pivots) |
| `Shift + scroll` | Pan timeline |
| Middle-drag | Pan timeline |
| Scroll | Seek (60s by default) |
| Drag-and-drop files | Load as playlist |

## Building from source

Prerequisites: .NET 10 SDK.

```sh
git clone https://github.com/<you>/Yet-Another-Lossless-Cutter.git
cd Yet-Another-Lossless-Cutter
dotnet build Yalc.sln
```

For development you'll also need libmpv and ffmpeg locally — see [Yalc/native/](Yalc/native/)
for placement (the csproj copies them into the build output if present).

## Plugins

YALC ships with no opinion about output-path routing or workflow-specific filters —
those live in plugins under [Plugins/](Plugins/). Drop a sibling `.csproj` and it gets
auto-included via a glob in the main project. Compile-time linked, NativeAOT-clean —
see [Plugins/README.md](Plugins/README.md) for the contract and a minimal example.

## License

[GPL-3.0](LICENSE). Yalc dynamically links to libmpv and FFmpeg at runtime
(via the first-run auto-download). The pre-built libmpv binaries from
[shinchiro](https://github.com/shinchiro/mpv-winbuild-cmake) and the FFmpeg
`gpl-shared` builds from [BtbN](https://github.com/BtbN/FFmpeg-Builds) include
GPL-licensed components, so the combined work is GPL.

If you need to embed Yalc in a non-GPL pipeline, you'd need to fork it, swap
the downloader to LGPL builds (BtbN ships `lgpl-shared` variants; an LGPL
libmpv would have to be self-built), and re-license your fork accordingly.
