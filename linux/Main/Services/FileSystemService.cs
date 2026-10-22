using RainExplorer.Models;

namespace RainExplorer.Services;

/// <summary>
/// All filesystem access lives here. Enumeration runs off the UI thread so the
/// window never freezes on a slow/large directory.
/// </summary>
public sealed class FileSystemService
{
    /// <summary>Read a directory's immediate children. Inaccessible entries are skipped.</summary>
    public async Task<IReadOnlyList<FileItem>> ReadDirectoryAsync(string path, CancellationToken ct = default,
        IReadOnlySet<string>? revealedPaths = null)
    {
        bool showHidden = SettingsStore.Instance.Settings.ShowHiddenFiles;
        bool slow = IsSlowMount(path);
        var read = Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Not a folder: {path}");
            var dir = new DirectoryInfo(path);
            // Surface a permission error for the folder itself instead of an empty list.
            using (var probe = Directory.EnumerateFileSystemEntries(path).GetEnumerator())
                probe.MoveNext();

            var result = new List<FileItem>();
            try
            {
                foreach (var info in dir.EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    if (CreateItem(info, showHidden || revealedPaths?.Contains(info.FullName) == true) is { } item) result.Add(item);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // The lazy enumerator may fail after yielding a few entries (a folder
                // removed mid-read). Keep the entries already read.
            }

            return (IReadOnlyList<FileItem>)result;
        }, ct);
        // Network mounts can hang in the kernel; stop waiting instead of freezing the tab.
        return slow
            ? await read.WaitAsync(TimeSpan.FromSeconds(20), ct)
            : await read.WaitAsync(ct);
    }

    /// <summary>Build a row for one entry, or null for a hidden entry that should be skipped.</summary>
    internal static FileItem? CreateItem(FileSystemInfo info, bool showHidden)
    {
        try
        {
            bool hidden = info.Name.StartsWith('.');
            if (hidden && !showHidden) return null;

            bool isLink = info.LinkTarget is not null;
            // A link to a folder is shown and opened as a folder, like other file managers.
            bool isDir = info is DirectoryInfo || (isLink && Directory.Exists(info.FullName));
            long size = 0;
            if (!isDir)
            {
                try { size = isLink ? new FileInfo(info.FullName).Length : ((FileInfo)info).Length; }
                catch { size = 0; }   // dangling link
            }

            return new FileItem
            {
                Name = info.Name,
                FullPath = info.FullName,
                IsDirectory = isDir,
                IsSymlink = isLink,
                IsHidden = hidden,
                Size = size,
                Modified = info.LastWriteTime,
                Created = info.CreationTime,
            };
        }
        catch
        {
            return null;   // Entry vanished or is unreadable mid-enumeration — ignore.
        }
    }

    /// <summary>
    /// Recursively search <paramref name="root"/> for entries whose name contains
    /// <paramref name="query"/>. Tolerates per-folder access errors, honours
    /// cancellation, and caps results so a huge tree can't flood the UI.
    /// </summary>
    public async Task<IReadOnlyList<FileItem>> SearchAsync(
        string root, string query, CancellationToken ct)
    {
        const int cap = 10_000;
        bool showHidden = SettingsStore.Instance.Settings.ShowHiddenFiles;
        return await Task.Run(() =>
        {
            var results = new List<FileItem>();
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string dir = stack.Pop();

                IEnumerable<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
                catch { continue; }   // unreadable folder — skip it

                try
                {
                    foreach (FileSystemInfo info in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!showHidden && info.Name.StartsWith('.')) continue;

                        // Symlinked directories can point back to an ancestor (or "/").
                        // Include a matching link, but never recurse through it or a
                        // search can loop forever.
                        if (info is DirectoryInfo && info.LinkTarget is null)
                            stack.Push(info.FullName);

                        if (info.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                            && CreateItem(info, showHidden) is { } item)
                        {
                            results.Add(item);
                            if (results.Count >= cap) return (IReadOnlyList<FileItem>)results;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Directory enumeration is lazy and can fail after yielding
                    // entries (for example when a folder is removed mid-search).
                    // Continue with the remaining directories.
                }
            }
            return results;
        }, ct);
    }

    /// <summary>
    /// The default Quick Access places, seeded into the user's pin list on first run.
    /// After seeding the user can freely rename, re-icon, reorder or unpin any of them.
    /// </summary>
    public static IReadOnlyList<PinnedItem> DefaultQuickAccess()
    {
        var defaults = new List<(string Name, string Path, string Icon)>
        {
            ("Desktop",   XdgDirs.Desktop,   "monitor"),
            ("Documents", XdgDirs.Documents, "file-text"),
            ("Downloads", XdgDirs.Downloads, "download"),
            ("Pictures",  XdgDirs.Pictures,  "image"),
            ("Music",     XdgDirs.Music,     "music"),
            ("Videos",    XdgDirs.Videos,    "film"),
        };
        return defaults
            .Where(d => !string.IsNullOrEmpty(d.Path) && Directory.Exists(d.Path))
            .Select(d => new PinnedItem { Name = d.Name, Path = d.Path, IconKey = d.Icon })
            .ToList();
    }

    /// <summary>Build the sidebar as a tree of expandable nodes (Quick access, custom
    /// lists, Drives). Sections are collapsible and the Drives section is optional.</summary>
    public IReadOnlyList<SidebarNode> GetSidebarNodes()
    {
        var settings = SettingsStore.Instance.Settings;
        var nodes = new List<SidebarNode>();

        foreach (string key in settings.SidebarOrder)
        {
            if (key.Equals("quick", StringComparison.Ordinal))
            {
                if (!settings.ShowQuickAccessInSidebar) continue;
                bool collapsed = settings.QuickAccessCollapsed;
                nodes.Add(SidebarNode.HeaderNode(settings.QuickAccessName, pinnedHeader: true,
                    groupKey: "quick", collapsed: collapsed));
                if (!collapsed)
                {
                    nodes.Add(SidebarNode.SpecialNode("Home", "Home", "home", "quick"));
                    AddPins(nodes, settings.Pinned, "quick");
                    nodes.Add(SidebarNode.SpecialNode("Trash", TrashToken, "trash", "quick"));
                }
                continue;
            }

            if (key.Equals("drives", StringComparison.Ordinal))
            {
                if (!settings.ShowDrivesInSidebar) continue;
                bool collapsed = settings.DrivesCollapsed;
                nodes.Add(SidebarNode.HeaderNode(settings.DrivesName,
                    groupKey: "drives", collapsed: collapsed));
                if (!collapsed)
                {
                    nodes.Add(SidebarNode.SpecialNode("All drives", "Drives", "hard-drive", "drives"));
                    foreach (MountEntry drive in MountService.GetAvailableDrives())
                    {
                        nodes.Add(SidebarNode.DriveNode(drive));
                    }
                }
                continue;
            }

            if (!key.StartsWith("custom:", StringComparison.Ordinal)) continue;
            string id = key[7..];
            var group = settings.CustomGroups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal));
            if (group is null) continue;
            nodes.Add(SidebarNode.HeaderNode(group.Name, pinnedHeader: true,
                groupKey: key, collapsed: group.Collapsed, customHeader: true));
            if (!group.Collapsed) AddPins(nodes, group.Items, key);
        }

        return nodes;
    }

    /// <summary>The trash's "files" folder, browsed like any other folder.</summary>
    public static string TrashToken => Path.Combine(TrashService.HomeTrash, "files");

    private static void AddPins(List<SidebarNode> nodes, List<PinnedItem> pins, string groupKey)
    {
        foreach (var p in pins)
        {
            if (string.IsNullOrWhiteSpace(p.Path)) continue;
            string name = string.IsNullOrWhiteSpace(p.Name) ? PathUtil.DisplayName(p.Path) : p.Name;
            string icon = string.IsNullOrWhiteSpace(p.IconKey) ? "folder" : p.IconKey;
            nodes.Add(SidebarNode.Folder(name, p.Path, icon, NodeKind.Pinned, groupKey));
        }
    }

    public string? GetParent(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return null;
        try { return Directory.GetParent(path.TrimEnd('/'))?.FullName; }
        catch (ArgumentException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>Open a file with its default app.</summary>
    public void OpenWithShell(string path) => ProcessLauncher.OpenDefault(path);

    /// <summary>Network filesystems can block for a long time when the server is gone.</summary>
    public static bool IsSlowMount(string path) =>
        MountService.GetDrives().Any(d => d.IsNetwork && PathUtil.IsSameOrInside(d.MountPoint, path));
}
