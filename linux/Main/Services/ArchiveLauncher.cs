using System.Diagnostics;
using RainExplorer.Models;

namespace RainExplorer.Services;

public enum ArchiveAction { Open, ExtractHere, ExtractTo, Compress }

public sealed record ArchiveApp(string Id, string Label, bool CanExtractHere = true);

/// <summary>Archive actions delegated to the installed desktop archive application.</summary>
public static class ArchiveLauncher
{
    private static readonly ArchiveApp[] Known =
    [
        new("ark", "Ark"),
        new("file-roller", "File Roller"),
        new("engrampa", "Engrampa"),
        new("xarchiver", "Xarchiver", CanExtractHere: false),
    ];

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "zipx", "rar", "7z", "tar", "gz", "tgz", "bz2", "tbz", "tbz2", "xz", "txz",
        "zst", "tzst", "lz", "lzma", "lzo", "lz4", "Z", "cab", "arj", "lha", "lzh", "cpio",
        "iso", "deb", "rpm", "jar", "war", "ear", "cbz", "cbr", "cb7", "cbt",
    };

    public static IReadOnlyList<ArchiveApp> Installed() =>
        Known.Where(app => ProcessLauncher.FindExecutable(app.Id) is not null).ToList();

    public static string LabelFor(string id) => Known.FirstOrDefault(app => app.Id == id)?.Label ?? id;

    // The filename is only a menu hint. The archive app validates the actual format.
    public static bool LooksLikeArchive(FileItem item) =>
        !item.IsDirectory && !item.IsSymlink && item.HasPhysicalPath && ArchiveExtensions.Contains(item.Extension);

    public static ArchiveApp? Resolve(out string? error)
    {
        string configured = SettingsStore.Instance.Settings.ArchiveManager;
        if (!string.IsNullOrEmpty(configured))
        {
            var app = Known.FirstOrDefault(app => app.Id == configured);
            error = app is null ? "Choose a supported archive app in Settings → Files."
                : ProcessLauncher.FindExecutable(app.Id) is null
                    ? $"{app.Label} is no longer installed. Choose another archive app in Settings → Files."
                    : null;
            return error is null ? app : null;
        }

        string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
        string preferred = desktop.Contains("MATE", StringComparison.OrdinalIgnoreCase) ? "engrampa"
            : desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase)
              || desktop.Contains("Cinnamon", StringComparison.OrdinalIgnoreCase)
              || desktop.Contains("Unity", StringComparison.OrdinalIgnoreCase)
              || desktop.Contains("Budgie", StringComparison.OrdinalIgnoreCase) ? "file-roller"
            : desktop.Contains("XFCE", StringComparison.OrdinalIgnoreCase)
              || desktop.Contains("LXDE", StringComparison.OrdinalIgnoreCase)
              || desktop.Contains("LXQt", StringComparison.OrdinalIgnoreCase) ? "xarchiver" : "ark";
        var selected = Installed().OrderBy(app => app.Id == preferred ? 0 : 1).FirstOrDefault();
        error = selected is null
            ? "No supported archive app was found. Install Ark, File Roller, Engrampa or Xarchiver using your software manager, then choose it in Settings → Files."
            : null;
        return selected;
    }

    public static bool TryLaunch(ArchiveAction action, IReadOnlyList<string> paths, out string? appName, out string? error)
    {
        var app = Resolve(out error);
        appName = app?.Label;
        if (app is null) return false;
        try
        {
            var start = BuildStartInfo(app, action, paths);
            ProcessLauncher.StartDetached(start);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                      or InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static ProcessStartInfo BuildStartInfo(ArchiveApp app, ArchiveAction action, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || action == ArchiveAction.Open && paths.Count != 1)
            throw new ArgumentException("Select an archive to open, or one or more items for an archive action.");
        if (!Enum.IsDefined(action)) throw new ArgumentException("Unknown archive action.");
        if (Known.All(known => known.Id != app.Id)) throw new ArgumentException("Unsupported archive app.");
        string[] inputs = paths.Select(Path.GetFullPath).ToArray();
        foreach (string path in inputs)
        {
            if (new FileInfo(path).LinkTarget is not null)
                throw new IOException("Select the original items instead of symbolic links for archive actions.");
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new IOException($"“{PathUtil.DisplayName(path)}” no longer exists.");
            if (action != ArchiveAction.Compress && Directory.Exists(path))
                throw new ArgumentException("Select archive files to extract or open.");
        }

        string parent = Path.GetDirectoryName(inputs[0]) ?? XdgDirs.Home;
        bool sameParent = inputs.All(path => PathUtil.Same(Path.GetDirectoryName(path) ?? XdgDirs.Home, parent));
        if (action == ArchiveAction.ExtractHere && !sameParent)
            throw new ArgumentException("Select archives from one folder, or use Extract to… to choose a destination.");
        if (app.Id == "xarchiver" && action == ArchiveAction.Compress && !sameParent)
            throw new ArgumentException("Select items from one folder to compress with Xarchiver.");
        if (action == ArchiveAction.Compress)
            foreach (string path in inputs.Where(Directory.Exists)) EnsureNoNestedLinks(path);

        var start = new ProcessStartInfo(ProcessLauncher.FindExecutable(app.Id)
            ?? throw new IOException($"{app.Label} is no longer installed."))
        {
            UseShellExecute = false,
            WorkingDirectory = parent,
        };
        var args = start.ArgumentList;
        switch (app.Id)
        {
            case "ark":
                if (action == ArchiveAction.Compress)
                {
                    args.Add("--add"); args.Add("--dialog"); args.Add("--changetofirstpath");
                }
                else if (action is ArchiveAction.ExtractHere or ArchiveAction.ExtractTo)
                {
                    args.Add("--batch"); args.Add("--autodestination"); args.Add("--autosubfolder");
                    if (action == ArchiveAction.ExtractTo) args.Add("--dialog");
                }
                break;
            case "file-roller":
            case "engrampa":
                if (action == ArchiveAction.ExtractHere) args.Add("--extract-here");
                else if (action is ArchiveAction.ExtractTo or ArchiveAction.Compress)
                {
                    args.Add(action == ArchiveAction.Compress ? "--add" : "--extract");
                    args.Add("--default-dir"); args.Add(parent);
                }
                break;
            case "xarchiver":
                // Xarchiver's --extract-to/--ensure-directory force overwrites. Use its
                // interactive destination dialog, which retains the user's conflict choice.
                if (action == ArchiveAction.ExtractHere)
                    throw new NotSupportedException("Use Extract to… with Xarchiver to review extraction options.");
                if (action == ArchiveAction.ExtractTo)
                    args.Add(inputs.Length == 1 ? "--extract" : "--multi-extract");
                else if (action == ArchiveAction.Compress)
                {
                    // --compress takes the first file as its value, then further files
                    // as separate positional arguments (not one joined command string).
                    args.Add("--compress"); args.Add(inputs[0]);
                    inputs = inputs[1..];
                }
                break;
        }
        args.Add("--");
        foreach (string path in inputs) args.Add(path);
        return start;
    }

    private static void EnsureNoNestedLinks(string folder)
    {
        // Archive formats and apps differ in link handling. Do not hand off a tree
        // that could include a link target; never descend through a link ourselves.
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(folder));
        while (pending.TryPop(out var directory))
        {
            if (directory.LinkTarget is not null)
                throw new IOException("This folder contains symbolic links. Select items without links to compress.");
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null)
                    throw new IOException("This folder contains symbolic links. Select items without links to compress.");
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        }
    }
}
