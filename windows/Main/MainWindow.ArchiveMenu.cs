using System.IO;
using System.Windows;
using RainExplorer.Controls;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using RainExplorer.Views;

namespace RainExplorer;

// Runs a "Rain Archives" right-click entry (see ArchiveContextMenuService). Each action goes
// through the same Rain Archives paths as the in-app commands; the result is shown in the
// new tab's status bar and in Activity, exactly as if it had been started inside Rain.
public partial class MainWindow
{
    internal async Task RunArchiveMenuCommandAsync(ArchiveMenuVerb verb, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        try
        {
            switch (verb)
            {
                case ArchiveMenuVerb.Open:
                    foreach (string path in paths.Where(File.Exists)) OpenPathInNewTab(path, null);
                    break;
                case ArchiveMenuVerb.Test:
                    foreach (string path in paths.Where(File.Exists)) await TestArchiveFromMenuAsync(path);
                    break;
                case ArchiveMenuVerb.ExtractToNewFolder:
                case ArchiveMenuVerb.ExtractTo:
                    await ExtractArchivesFromMenuAsync(paths, chooseParent: verb == ArchiveMenuVerb.ExtractTo);
                    break;
                case ArchiveMenuVerb.Add:
                    await AddToArchiveFromMenuAsync(paths);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            SetActiveStatus("Archive operation cancelled; no retry was started.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
                                              or InvalidDataException or NotSupportedException
                                              or ObjectDisposedException or UnauthorizedAccessException)
        {
            SetActiveStatus("⚠️ Rain Archives could not complete the operation safely.");
        }
    }

    /// <summary>Open <paramref name="path"/> in a new tab and wait until it has loaded.</summary>
    private async Task<TabViewModel> OpenTabAndWaitAsync(string path, string? select = null)
    {
        App.SelectPath = select;
        TabViewModel tab = _vm.ActivePane.NewTab(path, activate: true);
        await tab.InitialNavigation;
        return tab;
    }

    private void SetActiveStatus(string status)
    {
        if (_vm.ActivePane.SelectedTab is { } tab) tab.Status = status;
    }

    private async Task TestArchiveFromMenuAsync(string archivePath)
    {
        TabViewModel tab = await OpenTabAndWaitAsync(archivePath);
        if (!tab.CanTestArchiveIntegrity)
        {
            tab.Status = "Integrity testing is unavailable for this archive.";
            return;
        }

        char[]? password = null;
        if (tab.RequiresArchiveOperationPassword)
        {
            password = AskArchivePassword(tab.Title);
            if (password is null) return;
        }
        try
        {
            ArchiveOperationResult result = await tab.TestArchiveIntegrityAsync(password);
            tab.Status = PaneView.ArchiveOperationStatus(result, extraction: false);
        }
        finally
        {
            if (password is not null) Array.Clear(password);
        }
    }

    private async Task ExtractArchivesFromMenuAsync(IReadOnlyList<string> paths, bool chooseParent)
    {
        string[] archives = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (archives.Length == 0) return;
        if (archives.Length > PhysicalArchiveExtractionService.MaximumArchiveCount)
        {
            SetActiveStatus($"⚠️ Select 1–{PhysicalArchiveExtractionService.MaximumArchiveCount} archives to extract at once.");
            return;
        }

        string? folder = Path.GetDirectoryName(archives[0]);
        if (folder is null) return;
        TabViewModel tab = await OpenTabAndWaitAsync(folder, archives[0]);
        if (!tab.CanUsePhysicalFileCommands) return;

        string? destinationParent = null;
        if (chooseParent)
        {
            var picker = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose where Rain Explorer should create the extracted folder",
                InitialDirectory = folder,
            };
            if (picker.ShowDialog(this) != true) return;
            destinationParent = picker.FolderName;
        }

        tab.Status = "Checking selected file contents with the isolated Worker…";
        PhysicalArchiveExtractionBatchResult result = destinationParent is null
            ? await tab.ExtractPhysicalArchivesAsync(archives, AskArchivePassword)
            : await tab.ExtractPhysicalArchivesToAsync(archives, destinationParent, AskArchivePassword);
        tab.Status = PaneView.PhysicalArchiveExtractionStatus(result);
        if (result.CompletedCount > 0) _ = tab.ReloadAfterOperationAsync();
    }

    private async Task AddToArchiveFromMenuAsync(IReadOnlyList<string> paths)
    {
        var items = paths.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (Path: path, IsDirectory: Directory.Exists(path)))
            .Where(item => item.IsDirectory || File.Exists(item.Path))
            .ToList();
        if (items.Count == 0) return;

        string? folder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(items[0].Path));
        if (folder is null) return;
        TabViewModel tab = await OpenTabAndWaitAsync(folder, items[0].Path);
        if (!tab.CanUsePhysicalFileCommands) return;

        if (!ArchiveWriteValidation.TryBuildSources(items, out IReadOnlyList<ArchiveCreationSource> sources)
            || !ArchiveWriteValidation.TryValidateSourceRoots(sources))
        {
            tab.Status = "⚠️ Select one or more ordinary physical files or folders with distinct names.";
            return;
        }

        tab.Status = "Checking managed archive formats with the connected Worker…";
        ArchiveCreationCapabilitySnapshot capabilities = await tab.LoadArchiveCreationCapabilitiesAsync();
        IReadOnlyList<ArchiveCreationFormatOption> formats =
            CreateArchiveDialog.CompatibleFormats(capabilities, sources);
        if (formats.Count == 0)
        {
            tab.Status = PaneView.ArchiveWriteCapabilityStatus(capabilities.State, rebuild: false);
            return;
        }

        string suggested = ArchiveFileTypes.FreeArchivePath(
            folder,
            ArchiveFileTypes.SuggestedArchiveStem(sources, folder, formats[0].Format),
            formats[0].Extension);

        var dialog = new CreateArchiveDialog(capabilities, sources, suggested) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Selection is not { } request) return;
        if (!request.Sources.SequenceEqual(sources))
        {
            tab.Status = "⚠️ The archive request changed. Start it again.";
            return;
        }

        tab.BeginKnownFileOperation();
        tab.Status = "Creating archive through the isolated Worker…";
        ArchiveOperationResult result = await tab.CreateArchiveAsync(request);
        tab.Status = PaneView.ArchiveWriteOperationStatus(result, rebuild: false);
        if (result.Succeeded) _ = tab.ReloadAfterOperationAsync();
    }

    private char[]? AskArchivePassword(string archiveName)
    {
        var dialog = new ArchivePasswordDialog(archiveName) { Owner = this };
        try
        {
            return dialog.ShowDialog() == true ? dialog.TakePassword() : null;
        }
        finally
        {
            dialog.ClearCapturedPassword();
        }
    }
}
