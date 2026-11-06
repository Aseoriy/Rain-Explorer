# Rain Explorer v0.1.0-linux-alpha

**just download the AppImage if you want to try it without installing anything. there's also a .deb and .tar.gz, and the .sha256 file is there if you want to check your download.**

Hey pookies, this is the first Linux alpha for Rain Explorer. it brings over the glass look, tabs, split panes, Quick Access and file operations from the Windows app, with Linux desktop integration underneath. there's also desktop icon support, removable-drive controls, and an AppImage so you can run it without going through a package manager.

**This is still an early alpha and doesn't have every Windows feature yet.** In-app archive browsing, Git integration and some previews aren't ported, so check the notes below before trying it as your main file manager.

This covers what's included in the first Linux release, rather than changes from a previous Linux version.

## 1. Linux Port

- **Folder browsing and layouts** — Home and Drives pages, breadcrumb navigation, an editable address bar, search, sorting, and Details, List, Tiles and Grid layouts. Photo thumbnails are supported too.
- **Tabs and split panes** — Open folders in tabs, pin them, drag to reorder or group them, and use split view to browse two locations at once. Tabs can move between panes or tear off into separate windows.
- **Tab projects and session restoration** — Save sets of tabs as projects, reopen closed tabs, preview visited tabs, and optionally restore your tabs and split panes when Rain starts again.
- **Customizable Quick Access** — Pin folders with custom names and icons, create extra sidebar lists, reorder pins and sections, and drag folders onto a section to add shortcuts.
- **File operations and undo/redo** — Copy, cut, paste, rename, create files and folders, move items to trash or delete them permanently. Transfers include conflict decisions, progress, pause and cancellation in the Activity center.
- **Symbolic links stay links** — Copying, moving or deleting a symbolic link operates on the link instead of following it into another folder.
- **Previews and Properties** — Preview photos and the beginning of text/code files, or open Properties for general information, permissions, details and file hashes.
- **Themes and appearance settings** — Includes the ten Rain themes, density and font settings, tab animations, and a choice between Rain's title bar and your desktop's own. Performance mode reduces rendering to 60 Hz and removes the ambient glow and settings blur.
- **Keyboard shortcuts** — Familiar shortcuts for tabs, split view, navigation, file operations, hidden files, previews and opening a terminal.

## 2. Linux Desktop Integration

- **Shared desktop trash** — Deleted items use the FreeDesktop trash, so they also appear in Dolphin and Nautilus. Rain's Trash view can restore items, delete them permanently or empty the trash.
- **Desktop clipboard support** — Copy and cut files between Rain and other desktop file managers through the clipboard.
- **Desktop icon themes** — Files and folders use your KDE/GNOME icon theme, including SVG icons, with Rain icons as a fallback.
- **Removable-drive controls** — Discover removable volumes, click to mount and open them, unmount them for later use, or safely remove supported devices. Busy volumes are reported instead of silently forcing removal. These actions use UDisks2 and your desktop's authorization prompts.
- **Open with and terminal support** — Choose from compatible installed applications and open a terminal in the current folder. Rain detects common desktop terminals rather than requiring one specific terminal app.
- **Archive app integration** — Open, extract and compress through an installed Ark, File Roller, Engrampa or Xarchiver application. Supported formats and options depend on that app and its installed backends.
- **Optional Show in folder support** — Apps using the FreeDesktop FileManager1 interface can ask Rain to reveal files, open folders or show Properties. It is off by default. Installation does not change your default file manager.
- **Persistent AppImage activation** — Show in folder reopens the AppImage itself rather than saving a path inside its temporary mount. Keep the AppImage in a permanent location; if you move it, launch the new copy and use Retry to update activation.

## 3. Downloads, Installation & Internal Changes

- **AppImage** — Download `rain-explorer-0.1.0-linux-alpha-linux-x64.AppImage`, make it executable and run it. No root access or separate .NET installation is needed. It does not automatically add an app-menu entry.

```bash
chmod +x rain-explorer-0.1.0-linux-alpha-linux-x64.AppImage
./rain-explorer-0.1.0-linux-alpha-linux-x64.AppImage
```

If your system cannot mount AppImages through FUSE, use the extraction fallback:

```bash
./rain-explorer-0.1.0-linux-alpha-linux-x64.AppImage --appimage-extract-and-run
```

- **Debian/Ubuntu package** — Download `rain-explorer_0.1.0~linux.alpha_amd64.deb` and install it with your package manager. This installs the app, command and menu entry.

```bash
sudo apt install ./rain-explorer_0.1.0~linux.alpha_amd64.deb
```

- **Per-user / portable package** — Download `rain-explorer-0.1.0-linux-alpha-linux-x64.tar.gz`. Extract it and run `install.sh` for a per-user installation without root, or run `app/RainExplorer` directly from the extracted folder.

```bash
tar -xzf rain-explorer-0.1.0-linux-alpha-linux-x64.tar.gz
cd rain-explorer-0.1.0-linux-alpha-linux-x64
./install.sh
```

- **Runtime included** — All three packages include the .NET runtime. Compatible Linux system libraries and fonts are still required.
- **Download verification** — `rain-explorer-0.1.0-linux-alpha-linux-x64.sha256` contains checksums for the AppImage, Debian package and tarball. Download it alongside your chosen package, then check the files you downloaded:

```bash
sha256sum --ignore-missing -c rain-explorer-0.1.0-linux-alpha-linux-x64.sha256
```

- **Settings kept between launches and updates** — Linux settings use `~/.config/RainExplorer`, with `$XDG_CONFIG_HOME` support. Settings writes are atomic, and an unparseable settings file is preserved instead of overwritten with defaults. Uninstalling keeps your user settings.
- **Build and packaging checks** — The Release build and all **203 automated tests** passed. The AppImage was also checked through actual desktop launches, normal closing and closed-app Show in folder activation, using both normal mounting and the extraction fallback. Package checksums were verified too.

## 4. Alpha Notes & Known Limits

- **This is a Linux alpha, not the complete Windows 2.0 beta feature set.** The platforms have separate builds and release versions.
- **In-app archive browsing is not ported.** Linux uses separately installed desktop archive apps rather than the managed Rain Archives worker. Those external operations use the archive app's own prompts and progress, and are not part of Rain's undo/redo or managed Activity queue. Flatpak-only archive app installations are not currently detected.
- **Git and GitHub integration are not ported yet.** The Windows repository tools are not included in this Linux release.
- **Previews are limited.** Photos and the beginning of text/code files are supported. Audio/video playback, PDF page previews, non-photo thumbnails and folder-content previews are not available yet.
- **Network browsing is limited to mounted paths.** Already-mounted shares can be browsed as local folders. Direct `smb://` browsing and network-share discovery are not available.
- **Downloads are x64 / amd64 only for this release.** ARM64 is a source-build option, but has not been validated for this release.
- **Other distributions still need testing.** Desktop testing was done on **Ubuntu 26.04 / KDE**. The AppImage avoids Debian package-manager requirements, but it still needs compatible system and desktop libraries; it is not a guarantee that every distribution will work.
- **Updates are manual for now.** Download a newer version and replace the AppImage, or install the newer Debian/per-user package using the same method. Avoid mixing the Debian and per-user installations, since they share settings and create the same app-menu entry.
- **This is still an alpha.** Automated checks don't cover every desktop, drive, file operation or distribution, so expect rough edges and please lmk what breaks.

First Linux release: **v0.1.0-linux-alpha**.
