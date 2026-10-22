# Rain Explorer

Rain Explorer is a file manager for Windows and Linux, built with C# and .NET 10. It brings tabs, split panes, Quick Access, previews and file operations into one workspace, with the same glass surfaces and themes across both versions.

The Windows app uses WPF. The Linux port uses Avalonia and follows Linux desktop conventions for the trash, application launching, icons and removable drives.

![Rain Explorer on Windows](https://raw.githubusercontent.com/Aseoriy/Rain-Explorer/master/Preview.png)

*The screenshot above shows the Windows app.*

## Platform status

| Platform | Status | Downloads |
|---|---|---|
| Windows | Stable releases, with a separate 2.0 beta available | `RainExplorer-Setup-*.exe` |
| Linux | Early alpha: `0.1.0-linux-alpha` | x64 `.AppImage`, `.deb` and `.tar.gz` packages |

The Linux version does not have every Windows feature yet. See [Linux alpha limits](#linux-alpha-limits) before trying it.

## Features

- Home and Drives pages, breadcrumb navigation, search, sorting and multiple file layouts.
- Tabs, split panes, pinned tabs, groups, tab projects, previews and tear-off windows.
- Optional restoration of tabs and split panes when the app starts.
- Quick Access pins with custom names and icons, reorderable sidebar lists, and drag-to-pin support.
- Copy, cut, paste, rename, new files and folders, trash/recycle bin, permanent deletion and undo/redo.
- A preview pane, Properties, an Activity center and terminal shortcuts.
- Ten themes, configurable density and fonts, and keyboard shortcuts.

### Windows

- Native Windows context menus and shell integration.
- Git and GitHub tools for repository setup, cloning, reviewing changes, staging, commits, branches and pushing. These tools still need some work.
- Archive creation and extraction, plus photo, text, media and PDF previews where supported.
- Automatic update checks with an optional beta channel.

The downloadable Windows 2.0 beta adds Rain Archives integration and other changes described in its release notes. The Windows source currently on the default branch is `1.2.3`; building that checkout does not build the 2.0 beta.

### Linux

- The FreeDesktop trash, shared with file managers such as Dolphin and Nautilus.
- Clipboard copy/cut/paste that works with other desktop file managers.
- Desktop icon themes, including SVG icons, with Rain icons as a fallback.
- Removable-drive discovery, mounting, unmounting and safe removal through UDisks2 when available. Your desktop handles any authorization prompts.
- An Open with… application chooser and desktop terminal detection.
- Archive actions through an installed Ark, File Roller, Engrampa or Xarchiver application.
- Optional Show in folder integration and a setting to choose Rain as your default file manager. Installation alone does not change your defaults.
- Symbolic links stay links during copy, move and delete operations.

## Downloads and installation

Use the repository's [Releases page](https://github.com/Aseoriy/Rain-Explorer/releases). Check the platform and version in the release title before downloading.

### Windows

Download a `RainExplorer-Setup-*.exe` installer and run it. The installer is per-user and does not require administrator access by default.

### Linux alpha

Look for **Rain Explorer for Linux — 0.1.0 Alpha**. If the Linux packages are not listed yet, use the source-build instructions below.

The first Linux release targets **x64 / amd64**. ARM64 is a build option, but has not been validated for this release. Packages bundle the .NET runtime; normal Linux desktop libraries and fonts are still required. Compatibility across distributions is still being tested.

Choose one installation method:

| Package | Installation |
|---|---|
| `.AppImage` | Make executable and run; no root or extraction step needed |
| `.deb` | Install with the package manager on a compatible Debian/Ubuntu system |
| `.tar.gz` | Extract and install for your user without root, or run directly from the extracted folder |

#### AppImage

Download `rain-explorer-0.1.0-linux-alpha-linux-x64.AppImage`, keep it somewhere permanent, then run:

```bash
chmod +x rain-explorer-0.1.0-linux-alpha-linux-x64.AppImage
./rain-explorer-0.1.0-linux-alpha-linux-x64.AppImage
```

It bundles Rain and the .NET runtime into one file. Your settings still use the normal Linux configuration folder. It does not automatically install an app-menu entry or change your file manager defaults.

If the system cannot mount AppImages through FUSE, use the runtime's extraction fallback:

```bash
./rain-explorer-0.1.0-linux-alpha-linux-x64.AppImage --appimage-extract-and-run
```

If you enable Show in folder, keep the AppImage at the same path; launch the new location and use Retry after moving it. AppImage packaging avoids Debian package-manager requirements, but it still needs a compatible Linux system, its standard desktop libraries and fonts. It is not a guarantee that every distribution will work.

#### Debian / Ubuntu package

Download `rain-explorer_0.1.0~linux.alpha_amd64.deb`, open a terminal in the download folder, then run:

```bash
sudo apt install ./rain-explorer_0.1.0~linux.alpha_amd64.deb
```

Launch **Rain Explorer** from your app menu or run `rain-explorer`. The package installs the application under `/opt/rain-explorer`.

To remove it:

```bash
sudo apt remove rain-explorer
```

Your user settings are kept.

#### Per-user / portable package

Download `rain-explorer-0.1.0-linux-alpha-linux-x64.tar.gz`, open a terminal in the download folder, then run:

```bash
tar -xzf rain-explorer-0.1.0-linux-alpha-linux-x64.tar.gz
cd rain-explorer-0.1.0-linux-alpha-linux-x64
./install.sh
```

This installs the app in `~/.local/share/rain-explorer`, adds a command in `~/.local/bin`, and creates an app-menu entry. `$XDG_DATA_HOME` is honoured for the application data location. If `rain-explorer` is not found in your terminal, add `~/.local/bin` to your `PATH` or use the app menu.

To run without installing, use `./app/RainExplorer` from the extracted folder instead.

To uninstall a per-user installation, run this from the extracted package folder:

```bash
./install.sh --uninstall
```

Your user settings are kept. Keep the extracted package if you want to use its uninstaller later.

#### Checksums and updates

Download the matching `.sha256` file alongside your chosen Linux package. From that folder, verify whichever package files you downloaded:

```bash
sha256sum --ignore-missing -c rain-explorer-0.1.0-linux-alpha-linux-x64.sha256
```

Each downloaded package should report `OK`.

Linux automatic updates are not available yet. To update, close Rain, download a newer package and use the same method. For AppImages, replace the old file at its existing location and make the new file executable. Avoid mixing the `.deb` and per-user installations, since they share settings and create the same app-menu entry.

## Linux alpha limits

- In-app archive browsing and the managed Rain Archives worker are not ported. Linux archive actions use a separately installed desktop archive app.
- Git and GitHub integration are not ported.
- Previews support photos and the beginning of text/code files; audio/video playback and PDF page previews are not available.
- Non-photo thumbnails and folder-content previews are not available.
- Direct `smb://` browsing and network-share discovery are not available. Shares already mounted at a local path can be browsed.
- Automatic updates are not available.

External archive operations use the archive app's own options, overwrite prompts and progress. They are not part of Rain's undo/redo or managed Activity queue. Archive integration currently detects native executables, rather than Flatpak-only archive apps.

## Building from source

The commands below run from the repository root, alongside `windows/` and `linux/`. Building requires the **.NET 10 SDK**.

### Windows

Requires Windows 10 version 1809 or newer. Inno Setup 6 is needed only for the installer.

```powershell
dotnet build windows\Main\RainExplorer.csproj -c Release

dotnet publish windows\Main\RainExplorer.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o windows\dist\app-sc

dotnet test windows\Tests\RainExplorer.Git.Tests\RainExplorer.Git.Tests.csproj -c Release
```

Compile [`windows/Installer/RainExplorer.iss`](windows/Installer/RainExplorer.iss) with Inno Setup 6 after publishing. Its input, icon and output paths are relative to the installer script; setup output goes to `windows/dist/`.

### Linux

Use a Linux desktop with the normal GUI libraries, fonts, `xdg-utils` and shared MIME database installed. UDisks2 enables removable-drive actions; GLib's `gio` utility provides full desktop-entry launching for Open with…. A supported archive app is optional.

```bash
dotnet build linux/Main/RainExplorer.csproj -c Release
dotnet run --project linux/Main/RainExplorer.csproj
dotnet test linux/Tests/RainExplorer.Tests/RainExplorer.Tests.csproj -c Release
```

Create a self-contained x64 build, tarball, AppImage and checksum manifest:

```bash
./linux/packaging/build.sh
```

When `dpkg-deb` is installed, the same script also creates a Debian package. All output goes to `linux/dist/`, and the checksum manifest covers every package produced by that run.

The first AppImage build needs `curl` to download pinned, SHA-256-verified versions of the official AppImage tool and runtime. They are cached under `$XDG_CACHE_HOME/RainExplorer/appimage` (normally `~/.cache/RainExplorer/appimage`) for later builds. Set `BUILD_APPIMAGE=0` to build only the other formats, or provide local tool paths with `APPIMAGETOOL` and `APPIMAGE_RUNTIME`.

To build ARM64 artifacts for separate testing:

```bash
RID=linux-arm64 ./linux/packaging/build.sh
```

The Linux tests isolate settings and trash under temporary XDG directories. Headless UI tests can save screenshots to a folder selected with `RAIN_SNAPSHOT_DIR`.

## Configuration

| Platform | Settings file |
|---|---|
| Windows | `%APPDATA%\RainExplorer\settings.json` |
| Linux | `~/.config/RainExplorer/settings.json` (`$XDG_CONFIG_HOME` is honoured) |

Settings writes are atomic. An unparseable settings file is preserved as `settings.json.corrupt` rather than replaced with defaults. Application removal keeps your settings.

## Repository layout

```text
windows/
  Main/          WPF application source
  Installer/     Inno Setup script
  Tests/         Windows test source
linux/
  Main/          Avalonia application source
  Tests/         Linux service and headless UI test source
  packaging/     Build/install scripts, desktop entry and icons
  tools/         Icon conversion tool
README.md        Repository overview
Preview.png      Windows application preview
```

Each platform keeps its generated output in its own ignored `dist/` folder. Dependency directories, local notes and other generated files are also excluded from Git.

## License

No license has been added to this repository yet. All rights are reserved by the project author unless a separate license is provided.
