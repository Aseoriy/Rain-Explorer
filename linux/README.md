# Rain Explorer for Linux

Rain Explorer for Linux is a port of the Rain Explorer Windows file manager to Linux, built with [Avalonia UI](https://avaloniaui.net/) and .NET 10. It keeps the same purple-glass look, the same tabs, split panes and Quick Access, and the same safety rules for file operations, while using Linux conventions underneath: the FreeDesktop trash, `xdg-open`, your desktop's terminal, and removable drives through UDisks2 alongside mounted filesystems from `/proc/mounts`.

> **Status: alpha (0.1.0-linux-alpha).** The Windows control templates and layouts are ported to Avalonia, including tab dragging, groups, projects and desktop icon themes. Archive actions use installed desktop archive apps; in-app archive browsing, Git integration, media/PDF previews and updates are still deferred (see [Not yet ported](#not-yet-ported)).

## Features

- Home dashboard (pinned folders, drives, recent items), All drives page, breadcrumb navigation with an editable address bar (Ctrl+L, `~` expansion, `file://` URIs), search with an optional subfolder search, natural sorting, and Details / List / Tiles / Grid layouts with photo thumbnails.
- Tabs (middle-click to close, Ctrl+Shift+T to reopen), pinned tabs, drag reordering, groups, tab previews, saved tab projects, and tear-off windows. Drag tabs between split panes (F3), and optionally restore tabs and panes on the next launch.
- Windows-style motion: tabs fade and scale in, neighbours slide into place, closed tabs shrink away, and the new-tab button follows the row. Drag previews use the Windows smoothing response; group rows, sidebar folders, menus, tooltips, theme changes and tear-off windows animate too.
- Rendering follows the highest active display refresh rate, up to 360 Hz (120 Hz when detection is unavailable). **Settings → Appearance → Performance mode** switches live to 60 Hz and removes the ambient glow and settings blur, keeping tab motion and drag behavior. The preference is saved across restarts.
- Quick Access pins with custom names and icons, extra user-created sidebar lists, collapsible sections, and drives (root, `/home`, removable media, `/mnt` and network mounts) with click-to-mount for unmounted removable volumes. Removable drives have an inline Mount / Safely remove button; their context menus also offer Unmount (to keep the device available for remounting) and Eject for supported media. The sidebar and drive cards update when devices are attached, mounted or removed. Safe removal unmounts sibling volumes before powering off or ejecting, and stops if a volume is busy. Tabs browsing an unmounted volume return to Home. Drag pins above or below each other to reorder them, drop a pin on another list's header to move it, and drag section headers to rearrange entire lists. Drop folders from a file view on a list header to pin shortcuts without moving the folders. Ordering and custom names/icons are saved.
- File operations: copy, cut and paste (interoperable with Dolphin and Nautilus through the clipboard), drag and drop onto folders, tabs, breadcrumbs and the sidebar, rename (dialog or inline), new folder / new file, move to trash or delete permanently, and undo/redo.
- One conflict decision per transfer (replace all / skip all / cancel), with progress, pause and cancel in the Activity center.
- FreeDesktop.org trash: deleted items appear in Dolphin's and Nautilus's trash too. A Trash entry in the sidebar lets you restore items, delete them permanently or empty the trash.
- Symbolic links are copied, moved and deleted as links and are never followed, so a link to a large or recursive folder can't balloon an operation.
- Preview pane (Space) for photos and the start of text/code files; a Properties dialog (Alt+Enter) with General, Permissions, Details and Hashes pages. Folder links are measured as links; permissions and checksums never operate on their targets.
- Files and folders use your KDE/GNOME desktop icon theme, including SVG icons, with Rain glyphs as a fallback. Details columns resize by dragging their edges, and dragging on empty list space selects a rectangle of files.
- An Open with… application chooser lists compatible apps first, respects hidden user overrides and uses GIO to launch installed desktop entries (including terminal apps).
- Archive context-menu actions: Open in the selected archive app, Extract here, Extract to… and Compress…. Supports Ark, File Roller, Engrampa and Xarchiver with automatic desktop detection or a saved choice in Settings → Files.
- Open in terminal (Shift+F4) with automatic detection of Konsole, GNOME Console/Terminal, kitty, Alacritty and others.
- The same ten colour themes as the Windows build, row density, interface font, ambient background, and a choice between Rain's custom title bar and your desktop's own.
- Single instance: launching Rain Explorer again with a folder opens it in a new tab of the running window.
- Optional desktop Show in folder integration: other apps can launch Rain to reveal and select local files, open folders or show properties through FreeDesktop's FileManager1 interface.

## Working with tabs

Drag a tab past another tab’s midpoint to change its position. Hold the facing edges of two unpinned tabs partially overlapping for about half a second, then release to group them, as in the Windows build. Group members appear in a second row; the tab context menu can rename, ungroup or close the group. Drag into another pane or Rain window to move a live tab. Hold a dragged tab over the left or right side (the outer 30% of the browsing area) for a moment to preview a split, then release to move it to that side. An existing split uses its left or right pane; a lone tab cannot split by itself. Release elsewhere away from the tab strip to open it in a separate window, including over the source window’s contents. Dragging a group’s top-row header moves its members together; dragging a lower-row member moves that tab alone.

Use **Tab projects** to save or switch sets of tabs; a project keeps its paths, order, pinned state, groups and selected tab. Hover a visited tab to see its preview.

While dragging, the source pane shows another tab’s contents, or stays blank if all its tabs are being dragged. Returning to the source tab row restores the grabbed tab’s contents. Neighbouring tabs make room without changing their stored order until release, and hover previews stay closed. Press Esc to cancel. Hover previews fit the entire pane into the card, preserving its aspect ratio and filenames rather than cropping the screenshot. Clicking blank list space or window chrome clears file selection; Ctrl/Shift marquee selection and scrollbar clicks preserve it.

## Archive apps

Ark is optional. Rain detects supported archive apps on `PATH`: Ark on KDE, File Roller on GNOME/Cinnamon, Engrampa on MATE, and Xarchiver on Xfce/LXDE/LXQt, falling back to another installed supported app. Choose a specific app in **Settings → Files → Archive app**. If that saved choice is later removed, Rain asks you to choose another rather than silently switching it.

Select archive files and use **Extract → Extract here** or **Extract → Extract to…**. Extract here uses the app's containing-folder behavior; archives with several top-level items normally get their own subfolder. Xarchiver offers Extract to… only, because its direct-extraction switches force overwrites. Select files or folders and use **Compress…** to choose an archive name and format in the app's creation dialog. Xarchiver compression requires items from the same folder. Selected symbolic links are excluded from archive actions. Before compression, Rain checks folders in the background and refuses trees containing symbolic links, because archive apps differ in whether they follow them.

The archive app handles format validation, supported formats, passwords, extraction options, overwrite prompts and progress. These external operations do not enter Rain's undo/redo or managed Activity queue. Folder auto-refresh shows resulting files; press F5 if auto-refresh is disabled. Double-clicking an archive still uses the desktop's normal default association.

If no supported app is installed, install any of Ark, File Roller, Engrampa or Xarchiver using your distribution's software manager and reopen Settings. Rain does not install packages or change file associations. This integration currently detects native executable installations, not Flatpak-only archive apps.

## Show in folder

Use **Settings → Advanced → Make Rain my default file manager** after installing Rain. One click sets the desktop's folder default, enables Show in folder, and refreshes desktop activation so browsers can launch Rain even when it is closed. It needs no administrator access. The button verifies the folder default and reports any incomplete step. On KDE it asks only Dolphin's background-only helper to quit normally; open file-manager windows are left running. If another running file manager still holds the Show in folder service, close it normally to finish the switch.

Enable **Settings → Advanced → Handle Show in folder** to let apps using the [FreeDesktop FileManager1 interface](https://wiki.freedesktop.org/www/Specifications/file-manager-interface/) reveal files in Rain. It is off by default and does not change your default folder-opening app or MIME associations. Install Rain first, or keep the enabled build in a stable location: desktop activation points to that executable. If you move it, launch the new copy and use Retry to update the activation entry.

The status shows **Active** when Rain owns the desktop service. If Dolphin, Nautilus or another file manager already owns it, Rain waits for that app to release it. Close its windows/process normally, then check the status; Rain does not terminate or replace a running owner. Other apps that directly launch their own file manager rather than using FileManager1 are outside this integration.

ShowItems selects files or folders in their parent folder, grouping items from the same folder in one tab and reusing matching tabs in the active pane. A requested hidden item appears temporarily in that tab without enabling hidden files globally. ShowFolders opens folder contents, and ShowItemProperties opens Rain's Properties dialog. Local `file://` URIs, including `localhost`, are supported; remote URIs such as `smb://` require a mounted local path. Rain restores minimized windows and asks the desktop to activate them; your compositor's focus rules still apply.

Enabling writes an owned activation entry to `$XDG_DATA_HOME/dbus-1/services/org.freedesktop.FileManager1.service` (normally `~/.local/share/dbus-1/services/`) and refreshes D-Bus's activation cache. This lets D-Bus start Rain when it is closed. Disabling or using the per-user uninstaller removes Rain's entry; existing entries owned by another app are preserved and reported in Settings.

You can also reveal items from a terminal without enabling desktop integration:

```bash
rain-explorer --select /path/to/file /path/to/another-file
```

Passing a folder without `--select` continues to open its contents. A file passed directly opens its parent with the file selected.

## Keyboard shortcuts

| Action | Shortcut |
|---|---|
| New tab / close tab / reopen closed tab | Ctrl+T / Ctrl+W / Ctrl+Shift+T |
| Next / previous tab | Ctrl+Tab / Ctrl+Shift+Tab |
| Back / forward / up | Alt+Left / Alt+Right / Alt+Up or Backspace |
| Edit address / search | Ctrl+L / Ctrl+F |
| Refresh | F5 |
| Split view | F3 |
| New folder | Ctrl+Shift+N |
| Rename | F2 |
| Move to trash / delete permanently | Delete / Shift+Delete |
| Copy / cut / paste / copy path | Ctrl+C / Ctrl+X / Ctrl+V / Ctrl+Shift+C |
| Undo / redo | Ctrl+Z / Ctrl+Shift+Z |
| Show hidden files | Ctrl+H |
| Preview pane | Space |
| Properties | Alt+Enter |
| Open terminal here | Shift+F4 |
| Toggle sidebar / settings | Ctrl+B / Ctrl+, |

## Installation

### Ubuntu / Debian package

Build a release (below), then install the `.deb` with your package manager:

```bash
sudo apt install ./dist/rain-explorer_0.1.0~linux.alpha_amd64.deb
```

Launch **Rain Explorer** from the app menu or run `rain-explorer`. The package bundles
the .NET runtime, installs the app in `/opt/rain-explorer`, and adds its launcher and
icons. Remove it with `sudo apt remove rain-explorer`; your settings are kept.

### Per-user installation / portable package

Extract the `.tar.gz`, then run the installer from its folder (or the release folder
created by the build):

```bash
cd dist/rain-explorer-*-linux-x64
./install.sh
```

This installs for the current user only (no root): the app goes to `~/.local/share/rain-explorer`, a `rain-explorer` command to `~/.local/bin`, and a menu entry plus icons to `~/.local/share`. It does not make Rain your default file manager. To remove it, run `./install.sh --uninstall` (your settings are kept).

Choose one installation method. Both use the same settings and add the same app-menu
entry. To use Rain as your main explorer, choose **Rain Explorer** in your desktop's
default-app settings (KDE: **System Settings → Default Applications → File Manager**),
or use **Settings → Advanced → Make Rain my default file manager** inside Rain to also enable Show in folder.
Installation does not change file manager defaults or enable FileManager1 integration.

## Building from source

### Requirements

- Linux x64 (arm64 works with `RID=linux-arm64`)
- .NET 10 SDK
- `xdg-open` (xdg-utils); `udisks2` for removable-drive discovery, mount, unmount and safe removal (optional; desktop PolicyKit handles authorization)
- `gio` (GLib utilities) for full Open with… desktop-entry support, including terminal applications. Simple non-terminal apps can fall back to direct launching without it.
- Ark, File Roller, Engrampa or Xarchiver for archive actions (optional; supported formats depend on that app's installed archive backends).
- An installed desktop icon theme and shared MIME database. The build includes Svg.Skia 3.7.0 to render SVG theme icons.
- Installed Linux UI fonts (Noto Sans / DejaVu Sans by default); Segoe UI is not bundled.

Build and run:

```bash
dotnet build Main/RainExplorer.csproj -c Release
dotnet run --project Main/RainExplorer.csproj
```

Create a self-contained release (bundles the .NET runtime), a `.tar.gz`, and a `.deb`
when `dpkg-deb` is installed:

```bash
packaging/build.sh
```

Packages and a SHA-256 checksum file are written to `dist/`. `RID=linux-arm64` builds
ARM64 artifacts; the default is x64 (`amd64` in Debian package names).

Run the tests (service tests plus headless UI tests that drive the real window):

```bash
dotnet test Tests/RainExplorer.Tests/RainExplorer.Tests.csproj
```

The UI tests save screenshots to a temp folder; set `RAIN_SNAPSHOT_DIR` to choose where. Tests isolate settings and trash under temporary XDG directories, and use a small SVG icon-theme fixture so icon rendering is reproducible. They exercise breadcrumbs, keyboard file operations and undo, drag reordering/grouping, transfers between panes, adoption into a tear-off window, project restoration, symbolic-link properties, settings across windows and all ten themes. FileManager1 tests use `dbus-daemon` to create private session buses and never claim the real desktop service.

## Configuration

User settings are stored at:

```text
~/.config/RainExplorer/settings.json
```

`$XDG_CONFIG_HOME` is honoured. Settings are written atomically, an unreadable file is preserved as `settings.json.corrupt` instead of being overwritten, and the file uses the same property names as the Windows build. Recents, per-folder sort choices, the activity log and a crash log live in the same folder.

## Not yet ported

- In-app archive browsing and the managed Rain Archives worker (Windows-only today). Linux creation and extraction use installed desktop archive apps.
- Git and GitHub integration.
- Audio/video playback and PDF page previews; folders show a preview hint rather than a contents preview.
- Thumbnails for non-photo files.
- Network share discovery and direct `smb://` browsing (already-mounted shares work as local paths).
- Automatic updates.

## Project layout

```text
Main/         Avalonia application source
Tests/        xunit tests (services + headless UI)
packaging/    build.sh, install.sh, desktop entry and icons
tools/        convert_icons.py (regenerates Themes/Icons.axaml from the Windows icon set)
dist/         Local build output (ignored by Git)
```

## License

No license has been added to this repository yet. All rights are reserved by the project author unless a separate license is provided.
