using System.Diagnostics;
using System.IO;
using RainExplorer.Models;

namespace RainExplorer.Services;

/// <summary>
/// All filesystem access lives here. Enumeration runs off the UI thread so the
/// window never freezes on a slow/large directory.
/// </summary>
public sealed class FileSystemService
{
    /// <summary>Read a directory's immediate children. Inaccessible entries are skipped.</summary>
    public async Task<IReadOnlyList<FileItem>> ReadDirectoryAsync(string path)
    {
        bool showHidden = SettingsStore.Instance.Settings.ShowHiddenFiles;
        return await Task.Run(() =>
        {
            var result = new List<FileItem>();
            var dir = new DirectoryInfo(path);

            try
            {
                foreach (var info in dir.EnumerateFileSystemInfos())
                {
                    try
                    {
                        // Skip hidden/system entries unless the user opted to show them.
                        if (!showHidden &&
                            (info.Attributes.HasFlag(FileAttributes.Hidden) ||
                             info.Attributes.HasFlag(FileAttributes.System)))
                            continue;

                        bool isDir = info.Attributes.HasFlag(FileAttributes.Directory);
                        result.Add(new FileItem
                        {
                            Name = info.Name,
                            FullPath = info.FullName,
                            IsDirectory = isDir,
                            Size = isDir ? 0 : ((FileInfo)info).Length,
                            Modified = info.LastWriteTime,
                            Created = info.CreationTime,
                        });
                    }
                    catch
                    {
                        // Entry vanished or is unreadable mid-enumeration — ignore.
                    }
                }
            }
            catch
            {
                // The directory itself may be inaccessible, or its lazy enumerator may
                // fail after yielding a few entries. Keep the entries already read.
            }

            return (IReadOnlyList<FileItem>)result;
        });
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

                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(dir); }
                catch { continue; }   // unreadable folder — skip it

                try
                {
                    foreach (string path in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        bool isDir;
                        FileSystemInfo info;
                        FileAttributes attr;
                        try
                        {
                            attr = File.GetAttributes(path);
                            if (!showHidden &&
                                (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System)))
                                continue;
                            isDir = attr.HasFlag(FileAttributes.Directory);
                            info = isDir ? new DirectoryInfo(path) : new FileInfo(path);
                        }
                        catch { continue; }

                        // Junctions and symlinked directories can point back to an
                        // ancestor. Include a matching link, but never recurse through
                        // it or a search can loop forever.
                        if (isDir && !attr.HasFlag(FileAttributes.ReparsePoint))
                            stack.Push(path);

                        string name = Path.GetFileName(path);
                        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(new FileItem
                            {
                                Name = name,
                                FullPath = path,
                                IsDirectory = isDir,
                                Size = isDir ? 0 : ((FileInfo)info).Length,
                                Modified = info.LastWriteTime,
                                Created = info.CreationTime,
                            });
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
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaults = new List<(string Name, string Path, string Icon)>
        {
            ("Desktop",   Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "monitor"),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),      "file-text"),
            ("Downloads", Path.Combine(home, "Downloads"),                                       "download"),
            ("Pictures",  Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),       "image"),
            ("Music",     Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),          "music"),
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
            if (key.Equals("quick", StringComparison.OrdinalIgnoreCase))
            {
                if (!settings.ShowQuickAccessInSidebar) continue;
                bool collapsed = settings.QuickAccessCollapsed;
                nodes.Add(SidebarNode.HeaderNode(settings.QuickAccessName, pinnedHeader: true,
                    groupKey: "quick", collapsed: collapsed));
                if (!collapsed)
                {
                    nodes.Add(SidebarNode.SpecialNode("Home", "Home", "home", "quick"));
                    AddPins(nodes, settings.Pinned, "quick");
                }
                continue;
            }

            if (key.Equals("drives", StringComparison.OrdinalIgnoreCase))
            {
                if (!settings.ShowDrivesInSidebar) continue;
                bool collapsed = settings.DrivesCollapsed;
                nodes.Add(SidebarNode.HeaderNode(settings.DrivesName,
                    groupKey: "drives", collapsed: collapsed));
                if (!collapsed)
                {
                    nodes.Add(SidebarNode.SpecialNode("All drives", "Drives", "hard-drive", "drives"));
                    foreach (var drive in DriveInfo.GetDrives())
                    {
                        if (!drive.IsReady) continue;
                        string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                            ? drive.Name
                            : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                        nodes.Add(SidebarNode.Folder(label, drive.RootDirectory.FullName,
                            "hard-drive", NodeKind.Drive, "drives"));
                    }
                }
                continue;
            }

            if (!key.StartsWith("custom:", StringComparison.OrdinalIgnoreCase)) continue;
            string id = key[7..];
            var group = settings.CustomGroups.FirstOrDefault(g =>
                string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
            if (group is null) continue;
            nodes.Add(SidebarNode.HeaderNode(group.Name, pinnedHeader: true,
                groupKey: key, collapsed: group.Collapsed, customHeader: true));
            if (!group.Collapsed) AddPins(nodes, group.Items, key);
        }

        return nodes;
    }

    private static void AddPins(List<SidebarNode> nodes, List<PinnedItem> pins, string groupKey)
    {
        foreach (var p in pins)
        {
            if (string.IsNullOrWhiteSpace(p.Path)) continue;
            string name = string.IsNullOrWhiteSpace(p.Name)
                ? Path.GetFileName(p.Path.TrimEnd('\\')) : p.Name;
            string icon = string.IsNullOrWhiteSpace(p.IconKey) ? "folder" : p.IconKey;
            nodes.Add(SidebarNode.Folder(name, p.Path, icon, NodeKind.Pinned, groupKey));
        }
    }

    public string? GetParent(string path)
    {
        var parent = Directory.GetParent(path);
        return parent?.FullName;
    }

    /// <summary>Open a file with its default app, or a folder in this explorer's caller.</summary>
    public void OpenWithShell(string path)
    {
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
}
