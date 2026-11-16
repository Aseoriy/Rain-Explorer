using Avalonia.Controls;
using Avalonia.Platform.Storage;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.Views;

namespace RainExplorer.Controls;

/// <summary>
/// "Git &amp; GitHub" submenu of the file context menu, ported from the Windows build. The submenu is filled
/// when it opens: install hint, "not a repository" (clone / initialize) or the repository's branch, staging
/// shortcuts and the changes window. Nothing is staged, committed or pushed without an explicit choice.
/// </summary>
public partial class PaneView
{
    private MenuItem _menuGit = null!;
    private Separator _gitSeparator = null!;
    private CancellationTokenSource? _gitMenuCts;
    private GitRepositoryContext? _gitMenuRepository;
    private GitRepositoryStatus? _gitMenuStatus;

    private void BuildGitMenu()
    {
        _gitSeparator = new Separator();
        _menuGit = new MenuItem { Header = "Git & GitHub", Name = "GitMenu" };
        ResetGitMenu();
        _menuGit.SubmenuOpened += GitMenu_SubmenuOpened;
    }

    private void UpdateGitMenu(bool physical, bool trash)
    {
        bool show = SettingsStore.Instance.Settings.GitIntegrationEnabled && !trash;
        _menuGit.IsVisible = show;
        _gitSeparator.IsVisible = show;
        _menuGit.IsEnabled = physical;
        ResetGitMenu();
    }

    private void ResetGitMenu()
    {
        _gitMenuCts?.Cancel();
        _gitMenuCts?.Dispose();
        _gitMenuCts = null;
        _gitMenuRepository = null;
        _gitMenuStatus = null;
        _menuGit.Items.Clear();
        _menuGit.Items.Add(Note("Checking repository…"));
    }

    private static MenuItem Note(string text) => new() { Header = text, IsEnabled = false };

    private async void GitMenu_SubmenuOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, _menuGit) || !SettingsStore.Instance.Settings.GitIntegrationEnabled) return;

        _gitMenuCts?.Cancel();
        _gitMenuCts?.Dispose();
        var cts = new CancellationTokenSource();
        _gitMenuCts = cts;
        CancellationToken token = cts.Token;
        _menuGit.Items.Clear();
        _menuGit.Items.Add(Note("Checking repository…"));

        try
        {
            var runtime = GitIntegrationRuntime.Instance;
            GitInstallationInfo? installation = await runtime.ExecutableLocator.FindAsync(token);
            if (token.IsCancellationRequested) return;
            if (installation is not { IsSupported: true })
            {
                BuildGitUnavailableMenu(installation);
                return;
            }

            string? candidate = GitMenuCandidate();
            if (candidate is null)
            {
                _menuGit.Items.Clear();
                _menuGit.Items.Add(Note("No folder is available"));
                return;
            }

            GitRepositoryContext? repository = await runtime.RepositoryLocator.FindAsync(candidate, token);
            if (token.IsCancellationRequested) return;
            if (repository is null)
            {
                BuildGitInitializeMenu(candidate);
                return;
            }

            GitRepositoryStatus status = await runtime.StatusReader.ReadAsync(repository, token);
            if (token.IsCancellationRequested) return;
            _gitMenuRepository = repository;
            _gitMenuStatus = status;
            BuildGitRepositoryMenu(status);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            _menuGit.Items.Clear();
            _menuGit.Items.Add(Note(GitSecurity.Redact(ex.Message)));
        }
    }

    private string? GitMenuCandidate()
    {
        if (SelectedItems() is [{ IsDirectory: true } directory] && Directory.Exists(directory.FullPath))
            return directory.FullPath;
        if (_tab is { IsFolderView: true } && Directory.Exists(_tab.CurrentPath))
            return _tab.CurrentPath;
        return null;
    }

    private void BuildGitUnavailableMenu(GitInstallationInfo? installation)
    {
        _menuGit.Items.Clear();
        _menuGit.Items.Add(Note(installation is null
            ? "Git is not installed"
            : $"Git {installation.DisplayVersion} is too old (2.40+ required)"));
        _menuGit.Items.Add(MenuFactory.Item("Choose Git executable…", null, () => _ = ChooseGitExecutable()));
        _menuGit.Items.Add(MenuFactory.Item("How to install Git", null,
            () => OpenWebAddress("https://git-scm.com/downloads/linux")));
    }

    private void BuildGitInitializeMenu(string folder)
    {
        _gitMenuRepository = null;
        _gitMenuStatus = null;
        _menuGit.Items.Clear();
        _menuGit.Items.Add(Note("Not a Git repository"));
        _menuGit.Items.Add(MenuFactory.Item("Open Git & GitHub…", null, () => _ = OpenGitSetup(folder)));
        _menuGit.Items.Add(MenuFactory.Item("Initialize Git repository…", null, () => _ = InitializeGit(folder)));
    }

    private void BuildGitRepositoryMenu(GitRepositoryStatus status)
    {
        _menuGit.Items.Clear();
        _menuGit.Items.Add(Note($"{status.Branch.DisplayName} · {status.Files.Count} changed · "
                                + $"{status.Files.Count(file => file.IsStaged)} staged"));
        if (status.Repository.OperationState != GitRepositoryOperationState.Normal)
            _menuGit.Items.Add(Note($"{status.Repository.OperationState} in progress"));

        _menuGit.Items.Add(new Separator());
        _menuGit.Items.Add(MenuFactory.Item("Open repository changes…", null, OpenGitChangesFromMenu));

        HashSet<string> selected = SelectedRepositoryPaths(status.Repository);
        GitFileStatus[] selectedStatuses = status.Files.Where(file =>
            selected.Count == 0 || selected.Any(path => IsSameOrChild(file.Path, path))).ToArray();
        if (selected.Count > 0 && selectedStatuses.Any(file => file.IsUnstaged || file.IsConflict))
            _menuGit.Items.Add(MenuFactory.Item("Stage selected files", null, () => _ = StageFromGitMenu(selectedStatuses)));
        if (selected.Count > 0 && selectedStatuses.Any(file => file.IsStaged))
            _menuGit.Items.Add(MenuFactory.Item("Unstage selected files", null, () => _ = UnstageFromGitMenu(selectedStatuses)));

        bool normal = status.Repository.OperationState == GitRepositoryOperationState.Normal;
        _menuGit.Items.Add(MenuFactory.Item("Commit staged changes…", null, OpenGitChangesFromMenu,
            enabled: status.HasStagedChanges && !status.HasConflicts && normal && !status.Branch.IsDetached));

        GitRemoteInfo? pushRemote = status.Remotes.FirstOrDefault(remote => remote.Name == status.PreferredPushRemote)
                                    ?? (status.Remotes.Count == 1 ? status.Remotes[0] : null);
        bool hasPushableCommits = string.IsNullOrWhiteSpace(status.Branch.Upstream) || status.Branch.Ahead > 0;
        _menuGit.Items.Add(MenuFactory.Item(
            pushRemote is null
                ? "Push (no push remote configured)"
                : $"Push {status.Branch.DisplayName} to {pushRemote.Name}…",
            null, OpenGitChangesFromMenu,
            enabled: pushRemote is not null && !status.Branch.IsDetached && !status.Branch.IsUnborn
                     && !status.HasConflicts && normal && hasPushableCommits));

        if (pushRemote is { IsGitHub: true, WebUrl.Length: > 0 })
        {
            string web = pushRemote.WebUrl;
            _menuGit.Items.Add(MenuFactory.Item("Open repository on GitHub", null, () => OpenWebAddress(web)));
        }

        _menuGit.Items.Add(new Separator());
        string root = status.Repository.WorkTreeRoot;
        _menuGit.Items.Add(MenuFactory.Item("Copy repository root", null,
            () => _ = ClipboardService.SetTextAsync(TopLevel.GetTopLevel(this), root)));
    }

    private HashSet<string> SelectedRepositoryPaths(GitRepositoryContext repository)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string fullPath in SelectedPaths())
        {
            try
            {
                string relative = Path.GetRelativePath(repository.WorkTreeRoot, fullPath);
                if (relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal))
                    paths.Add(relative == "." ? string.Empty : relative);
            }
            catch { }
        }
        return paths;
    }

    private static bool IsSameOrChild(string filePath, string selectedPath) =>
        string.Equals(filePath, selectedPath, StringComparison.Ordinal)
        || filePath.StartsWith(selectedPath.TrimEnd('/') + "/", StringComparison.Ordinal);

    private async Task StageFromGitMenu(GitFileStatus[] files)
    {
        if (_gitMenuRepository is not { } repository) return;
        string[] paths = files.Where(file => file.IsUnstaged || file.IsConflict)
            .Select(file => file.Path).Distinct(StringComparer.Ordinal).ToArray();
        GitOperationResult result = await GitIntegrationRuntime.Instance.Mutations.StageAsync(
            repository, paths, CancellationToken.None);
        SetStatus(result.Message ?? (result.Succeeded ? "Selected files staged." : "⚠️ Staging failed."));
        await (_tab?.ReloadAfterOperationAsync() ?? Task.CompletedTask);
    }

    private async Task UnstageFromGitMenu(GitFileStatus[] files)
    {
        if (_gitMenuRepository is not { } repository || _gitMenuStatus is not { } status) return;
        string[] paths = files.Where(file => file.IsStaged)
            .Select(file => file.Path).Distinct(StringComparer.Ordinal).ToArray();
        GitOperationResult result = await GitIntegrationRuntime.Instance.Mutations.UnstageAsync(
            repository, paths, !status.Branch.IsUnborn, CancellationToken.None);
        SetStatus(result.Message ?? (result.Succeeded ? "Selected files unstaged." : "⚠️ Unstaging failed."));
        await (_tab?.ReloadAfterOperationAsync() ?? Task.CompletedTask);
    }

    private async Task InitializeGit(string folder)
    {
        if (!await ConfirmDialog.Ask(OwnerWindow, "Initialize Git repository",
                $"Create Git repository metadata in:\n{folder}\n\n"
                + "This will not stage, commit, connect a remote, or upload files.",
                "Initialize", "Cancel"))
            return;
        GitOperationResult result = await GitIntegrationRuntime.Instance.Mutations.InitializeAsync(
            folder, CancellationToken.None);
        SetStatus(result.Succeeded
            ? "Git repository initialized. No files were staged."
            : $"⚠️ {result.Message ?? "Repository initialization failed."}");
        if (!result.Succeeded) return;
        GitIntegrationRuntime.Instance.RepositoryLocator.Invalidate(folder);
        GitRepositoryContext? repository = await GitIntegrationRuntime.Instance.RepositoryLocator.FindAsync(
            folder, CancellationToken.None, useCache: false);
        _ = _tab?.ReloadAfterOperationAsync();
        if (repository is not null) await OpenGitChanges(repository);
    }

    private async Task OpenGitSetup(string folder)
    {
        string? repositoryPath = await GitSetupWindow.ShowFor(OwnerWindow, folder);
        if (repositoryPath is null) return;
        _ = _tab?.ReloadAfterOperationAsync();
        if (!PathUtil.Same(repositoryPath, folder)) _vm?.NewTab(repositoryPath, activate: true);
        GitRepositoryContext? repository = await GitIntegrationRuntime.Instance.RepositoryLocator
            .FindAsync(repositoryPath, CancellationToken.None, useCache: false);
        if (repository is not null) await OpenGitChanges(repository);
    }

    private void OpenGitChangesFromMenu()
    {
        if (_gitMenuRepository is { } repository) _ = OpenGitChanges(repository);
    }

    private async Task OpenGitChanges(GitRepositoryContext repository)
    {
        var window = new GitChangesWindow(repository, SelectedPaths());
        if (OwnerWindow is { } owner) await window.ShowDialog(owner);
        else window.Show();
        _ = _tab?.ReloadAfterOperationAsync();
    }

    private async Task ChooseGitExecutable()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Git executable",
            AllowMultiple = false,
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        SettingsStore.Instance.Settings.GitExecutablePath = path;
        SettingsStore.Instance.Flush();
        SetStatus("Git executable updated.");
    }

    private void OpenWebAddress(string url)
    {
        try { ProcessLauncher.OpenDefault(url); }
        catch (Exception ex) { SetStatus($"⚠️ {ex.Message}"); }
    }
}
