using Avalonia.Controls;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Controls;

public partial class PaneView
{
    private MenuItem _menuArchiveOpen = null!, _menuExtract = null!, _menuExtractHere = null!,
        _menuExtractTo = null!, _menuCompress = null!;
    private Separator _archiveSeparator = null!;

    private void BuildArchiveMenu()
    {
        _archiveSeparator = new Separator();
        _menuArchiveOpen = MenuFactory.Item("Open in archive app", "archive", () => _ = RunArchiveAction(ArchiveAction.Open));
        _menuArchiveOpen.Name = "ArchiveOpen";
        _menuExtractHere = MenuFactory.Item("Extract here", "archive", () => _ = RunArchiveAction(ArchiveAction.ExtractHere));
        _menuExtractHere.Name = "ArchiveExtractHere";
        _menuExtractTo = MenuFactory.Item("Extract to…", "folder-open", () => _ = RunArchiveAction(ArchiveAction.ExtractTo));
        _menuExtractTo.Name = "ArchiveExtractTo";
        _menuExtract = MenuFactory.Submenu("Extract", "archive", [_menuExtractHere, _menuExtractTo]);
        _menuExtract.Name = "ArchiveExtract";
        _menuCompress = MenuFactory.Item("Compress…", "archive", () => _ = RunArchiveAction(ArchiveAction.Compress));
        _menuCompress.Name = "ArchiveCompress";
    }

    private void UpdateArchiveMenu(List<FileItem> items, bool physical, bool trash)
    {
        bool eligible = physical && !trash && items.Count > 0 && items.All(item => !item.IsSymlink);
        bool archives = eligible && items.All(ArchiveLauncher.LooksLikeArchive);
        var app = ArchiveLauncher.Resolve(out _);
        _menuArchiveOpen.Header = app is null ? "Open in archive app" : $"Open in {app.Label}";
        _menuArchiveOpen.IsVisible = archives && items.Count == 1;
        _menuExtract.IsVisible = archives;
        _menuExtractHere.IsVisible = app?.CanExtractHere ?? true;
        _menuExtractHere.IsEnabled = archives && !_fileOperationInProgress
            && items.All(item => PathUtil.Same(Path.GetDirectoryName(item.FullPath) ?? XdgDirs.Home,
                Path.GetDirectoryName(items[0].FullPath) ?? XdgDirs.Home));
        _menuCompress.IsVisible = eligible;
        _menuArchiveOpen.IsEnabled = _menuExtract.IsEnabled = _menuExtractTo.IsEnabled =
            _menuCompress.IsEnabled = eligible && !_fileOperationInProgress;
        _archiveSeparator.IsVisible = eligible;
    }

    private async Task RunArchiveAction(ArchiveAction action)
    {
        if (_tab is not { CanUsePhysicalFileCommands: true, IsTrashView: false } || _fileOperationInProgress) return;
        var items = SelectedItems();
        if (items.Count == 0 || items.Any(item => !item.HasPhysicalPath || item.IsSymlink)) return;
        if (action != ArchiveAction.Compress && items.Any(item => !ArchiveLauncher.LooksLikeArchive(item))) return;
        var tab = _tab;
        string[] paths = items.Select(item => item.FullPath).ToArray();
        _fileOperationInProgress = true;
        tab.Status = action == ArchiveAction.Compress ? "Checking items for compression…" : "Opening archive app…";
        (bool Started, string? App, string? Error) result;
        try
        {
            result = await Task.Run(() =>
            {
                bool started = ArchiveLauncher.TryLaunch(action, paths, out string? app, out string? error);
                return (started, app, error);
            });
        }
        finally { _fileOperationInProgress = false; }
        if (!result.Started)
        {
            tab.Status = $"⚠️ {result.Error}";
            return;
        }
        tab.Status = action == ArchiveAction.Compress ? $"Compression opened in {result.App}."
            : action == ArchiveAction.Open ? $"Opened {result.App}." : $"Extraction opened in {result.App}.";
        if (action == ArchiveAction.Open) RecentsStore.Instance.Add(items[0].FullPath, isDirectory: false);
    }
}
