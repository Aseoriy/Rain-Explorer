using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.ViewModels;

public sealed class GitChangesViewModel : ObservableObject, IDisposable
{
    private readonly GitIntegrationRuntime _runtime;
    private readonly HashSet<string> _initialPaths;
    private CancellationTokenSource? _operationCts;
    private GitRepositoryStatus? _status;
    private GitPushPreview _pushPreview = new(0, [], []);

    public GitRepositoryContext Repository { get; }
    public ObservableCollection<GitFileStatus> Conflicts { get; } = new();
    public ObservableCollection<GitFileStatus> Staged { get; } = new();
    public ObservableCollection<GitFileStatus> Unstaged { get; } = new();
    public ObservableCollection<GitFileStatus> Untracked { get; } = new();
    public ObservableCollection<GitFileStatus> Ignored { get; } = new();
    public ObservableCollection<GitRemoteInfo> Remotes { get; } = new();
    public ObservableCollection<GitBranchInfo> Branches { get; } = new();

    public GitChangesViewModel(
        GitRepositoryContext repository,
        IEnumerable<string>? initialFullPaths = null,
        GitIntegrationRuntime? runtime = null)
    {
        Repository = repository;
        _runtime = runtime ?? GitIntegrationRuntime.Instance;
        _initialPaths = (initialFullPaths ?? [])
            .Select(ToRepositoryPath)
            .Where(path => path is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    public string RepositoryName
    {
        get
        {
            string name = Path.GetFileName(Repository.WorkTreeRoot.TrimEnd(Path.DirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(name) ? Repository.WorkTreeRoot : name;
        }
    }

    public string RepositoryPath => Repository.WorkTreeRoot;

    private string _branchText = "Loading…";
    public string BranchText { get => _branchText; private set => Set(ref _branchText, value); }

    private string _trackingText = string.Empty;
    public string TrackingText { get => _trackingText; private set => Set(ref _trackingText, value); }

    private string _message = "Loading repository status…";
    public string Message { get => _message; private set => Set(ref _message, value); }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            OnPropertyChanged(nameof(CanCommit));
            OnPropertyChanged(nameof(CanPush));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanCreateBranch));
            OnPropertyChanged(nameof(CanManageBranches));
            OnPropertyChanged(nameof(CanSwitchBranch));
            OnPropertyChanged(nameof(CanDeleteBranch));
            OnPropertyChanged(nameof(BranchManagementHint));
        }
    }

    public bool CanCancel => Busy;
    public bool CanCommit => !Busy
        && _status?.HasStagedChanges == true
        && _status.HasConflicts == false
        && _status.Repository.OperationState == GitRepositoryOperationState.Normal
        && !_status.Branch.IsDetached;
    public bool CanPush => !Busy
        && _status is { HasConflicts: false }
        && _status.Repository.OperationState == GitRepositoryOperationState.Normal
        && !_status.Branch.IsDetached
        && !_status.Branch.IsUnborn
        && !string.IsNullOrWhiteSpace(_status.Branch.Name)
        && Remotes.Count > 0
        && _pushPreview.CommitCount > 0;
    public bool NeedsUpstream => string.IsNullOrWhiteSpace(_status?.Branch.Upstream);
    public string CurrentBranch => _status?.Branch.Name ?? string.Empty;
    public string StagedSummary => $"{Staged.Count} staged file{(Staged.Count == 1 ? "" : "s")}";
    public string PushButtonText => SelectedRemote is null
        ? "Push…"
        : _pushPreview.CommitCount == 0
            ? "Nothing to push"
            : $"Push to {SelectedRemote.Name}…";
    public bool HasGitHubRemote => SelectedRemote?.IsGitHub == true;
    public GitPushPreview PushPreview => _pushPreview;
    public bool HasConflicts => Conflicts.Count > 0;
    public bool HasStagedFiles => Staged.Count > 0;
    public bool HasUnstagedFiles => Unstaged.Count > 0;
    public bool HasUntrackedFiles => Untracked.Count > 0;
    public bool HasIgnoredFiles => Ignored.Count > 0;
    public bool HasFileEntries => HasConflicts || HasStagedFiles || HasUnstagedFiles
        || HasUntrackedFiles || HasIgnoredFiles;
    public bool HasNoFileEntries => !HasFileEntries;
    public bool CanCreateBranch => !Busy
        && _status is { HasConflicts: false }
        && _status.Repository.OperationState == GitRepositoryOperationState.Normal
        && !_status.Branch.IsUnborn;
    public bool CanManageBranches => !Busy
        && _status is not null
        && !_status.Files.Any(file => !file.IsIgnored)
        && _status.Repository.OperationState == GitRepositoryOperationState.Normal
        && !_status.Branch.IsDetached
        && !_status.Branch.IsUnborn;
    public bool CanSwitchBranch => CanManageBranches && SelectedBranch is { IsCurrent: false };
    public bool CanDeleteBranch => !Busy
        && _status?.Repository.OperationState == GitRepositoryOperationState.Normal
        && SelectedBranch is { IsCurrent: false };
    public string BranchManagementHint => CanManageBranches
        ? "Branches are local until you push them."
        : CanCreateBranch
            ? "New branches keep your current changes. Commit or resolve changes before switching to an existing branch."
            : "Resolve conflicts or finish the current Git operation before changing branches.";

    private GitRemoteInfo? _selectedRemote;
    public GitRemoteInfo? SelectedRemote
    {
        get => _selectedRemote;
        set
        {
            if (!Set(ref _selectedRemote, value)) return;
            OnPropertyChanged(nameof(PushButtonText));
            OnPropertyChanged(nameof(HasGitHubRemote));
        }
    }

    private GitBranchInfo? _selectedBranch;
    public GitBranchInfo? SelectedBranch
    {
        get => _selectedBranch;
        set
        {
            if (!Set(ref _selectedBranch, value)) return;
            OnPropertyChanged(nameof(CanSwitchBranch));
            OnPropertyChanged(nameof(CanDeleteBranch));
        }
    }

    public async Task RefreshAsync()
    {
        await RunAsync("Refreshing repository…", async token =>
        {
            GitRepositoryStatus status = await _runtime.StatusReader.ReadAsync(Repository, token);
            _status = status;
            _pushPreview = await _runtime.PushPreflight.ReadAsync(Repository, status.Branch, token);
            IReadOnlyList<GitBranchInfo> branches =
                await _runtime.Branches.ReadAsync(Repository, token);

            Replace(Conflicts, status.Files.Where(file => file.IsConflict));
            Replace(Staged, status.Files.Where(file => file.IsStaged && !file.IsConflict));
            Replace(Unstaged, status.Files.Where(file =>
                file.IsUnstaged && !file.IsUntracked && !file.IsConflict));
            Replace(Untracked, status.Files.Where(file => file.IsUntracked));
            Replace(Ignored, status.Files.Where(file => file.IsIgnored));
            Replace(Remotes, status.Remotes);
            Replace(Branches, branches);
            SelectedBranch = Branches.FirstOrDefault(branch => branch.IsCurrent)
                             ?? Branches.FirstOrDefault();

            SelectedRemote = Remotes.FirstOrDefault(remote =>
                                 remote.Name == status.PreferredPushRemote)
                             ?? (Remotes.Count == 1 ? Remotes[0] : null);

            BranchText = status.Branch.DisplayName;
            TrackingText = BuildTrackingText(status);
            Message = StatusMessage(status);
            NotifyState();
        });
    }

    public async Task StageAsync(IEnumerable<GitFileStatus> files)
    {
        string[] paths = files.Select(file => file.Path).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) { Message = "Select one or more unstaged files first."; return; }
        await RunMutationAsync("Staging selected files…",
            token => _runtime.Mutations.StageAsync(Repository, paths, token),
            "Stage", $"{paths.Length} file{(paths.Length == 1 ? "" : "s")}");
    }

    public async Task UnstageAsync(IEnumerable<GitFileStatus> files)
    {
        string[] paths = files.Select(file => file.Path).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) { Message = "Select one or more staged files first."; return; }
        bool hasHead = _status?.Branch.IsUnborn == false;
        await RunMutationAsync("Unstaging selected files…",
            token => _runtime.Mutations.UnstageAsync(Repository, paths, hasHead, token),
            "Unstage", $"{paths.Length} file{(paths.Length == 1 ? "" : "s")}");
    }

    public async Task CommitAsync(string summary, string description)
    {
        string? reviewedTree = _status?.IndexTreeId;
        await RunMutationAsync("Creating local commit…",
            token => _runtime.Mutations.CommitAsync(
                Repository, summary, description, reviewedTree, token),
            "Local commit", RepositoryName);
    }

    public async Task PushAsync()
    {
        if (SelectedRemote is null) { Message = "Choose a push remote first."; return; }
        if (_pushPreview.CommitCount <= 0) { Message = "Nothing to push."; return; }
        string branch = CurrentBranch;
        string remote = SelectedRemote.Name;
        await RunMutationAsync($"Pushing {branch} to {remote}…",
            token => _runtime.Mutations.PushAsync(
                Repository, branch, remote, NeedsUpstream, token),
            "Push", $"{RepositoryName} → {remote}");
    }

    public Task CreateBranchAsync(string name) =>
        RunMutationAsync(
            "Creating branch…",
            token => _runtime.Branches.CreateAndSwitchAsync(Repository, name, token),
            "Create branch",
            name.Trim());

    public Task SwitchBranchAsync()
    {
        if (SelectedBranch is null)
        {
            Message = "Choose a branch first.";
            return Task.CompletedTask;
        }
        string name = SelectedBranch.Name;
        return RunMutationAsync(
            $"Switching to {name}…",
            token => _runtime.Branches.SwitchAsync(Repository, name, token),
            "Switch branch",
            name);
    }

    public Task DeleteBranchAsync()
    {
        if (SelectedBranch is null)
        {
            Message = "Choose a branch first.";
            return Task.CompletedTask;
        }
        string name = SelectedBranch.Name;
        return RunMutationAsync(
            $"Deleting {name}…",
            token => _runtime.Branches.DeleteAsync(Repository, name, token),
            "Delete branch",
            name);
    }

    public bool IsInitiallySelected(GitFileStatus file)
    {
        if (_initialPaths.Count == 0) return false;
        return _initialPaths.Any(selected =>
            string.Equals(file.Path, selected, StringComparison.Ordinal)
            || file.Path.StartsWith(selected.TrimEnd('/') + "/", StringComparison.Ordinal));
    }

    public void OpenSelectedRemoteOnGitHub()
    {
        if (SelectedRemote?.WebUrl is not { Length: > 0 } url) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public void CancelCurrentOperation() => _operationCts?.Cancel();

    private async Task RunMutationAsync(
        string progress,
        Func<CancellationToken, Task<GitOperationResult>> operation,
        string activityTitle,
        string activityDetail)
    {
        GitOperationResult? result = null;
        await RunAsync(progress, async token => result = await operation(token));
        if (result is null) return;

        Message = result.Message ?? (result.Succeeded ? "Done." : "The Git operation failed.");
        var activity = ActivityService.Instance.Begin(activityTitle, activityDetail, "cloud");
        if (result.Outcome == GitOperationOutcome.Canceled)
            ActivityService.Instance.Cancel(activity);
        else
            ActivityService.Instance.Complete(activity, result.Succeeded, result.Message);

        await RefreshAsync();
        if (!string.IsNullOrWhiteSpace(result.Message)) Message = result.Message!;
    }

    private async Task RunAsync(string progress, Func<CancellationToken, Task> action)
    {
        if (Busy) return;
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        Busy = true;
        Message = progress;
        try
        {
            await action(_operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            Message = "Operation canceled.";
        }
        catch (Exception ex)
        {
            Message = GitSecurity.Redact(ex.Message);
        }
        finally
        {
            Busy = false;
            NotifyState();
        }
    }

    private static string BuildTrackingText(GitRepositoryStatus status)
    {
        if (status.Repository.OperationState != GitRepositoryOperationState.Normal)
            return $"{status.Repository.OperationState} in progress";
        if (status.Branch.IsDetached) return "Commit and push are disabled until a branch is checked out.";
        if (status.Branch.Upstream is null)
            return status.Remotes.Count == 0 ? "Local repository · no remote"
                : "No upstream branch";
        return $"{status.Branch.Upstream} · {status.Branch.Ahead} ahead, {status.Branch.Behind} behind";
    }

    private static string StatusMessage(GitRepositoryStatus status)
    {
        if (status.IsTruncated)
            return $"Showing the first {GitStatusReader.MaxRows:N0} entries. Narrow the repository before staging.";
        if (status.HasConflicts)
            return "Resolve all conflicts before committing or pushing.";
        if (status.Files.Count == 0) return "Working tree clean.";
        return $"{status.Files.Count} changed entr{(status.Files.Count == 1 ? "y" : "ies")} · "
               + $"{status.Files.Count(file => file.IsStaged)} staged";
    }

    private string? ToRepositoryPath(string fullPath)
    {
        try
        {
            string relative = Path.GetRelativePath(Repository.WorkTreeRoot, fullPath).Replace('\\', '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) return null;
            return relative == "." ? string.Empty : relative;
        }
        catch { return null; }
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(CanPush));
        OnPropertyChanged(nameof(NeedsUpstream));
        OnPropertyChanged(nameof(CurrentBranch));
        OnPropertyChanged(nameof(StagedSummary));
        OnPropertyChanged(nameof(PushButtonText));
        OnPropertyChanged(nameof(HasGitHubRemote));
        OnPropertyChanged(nameof(PushPreview));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(HasStagedFiles));
        OnPropertyChanged(nameof(HasUnstagedFiles));
        OnPropertyChanged(nameof(HasUntrackedFiles));
        OnPropertyChanged(nameof(HasIgnoredFiles));
        OnPropertyChanged(nameof(HasFileEntries));
        OnPropertyChanged(nameof(HasNoFileEntries));
        OnPropertyChanged(nameof(CanCreateBranch));
        OnPropertyChanged(nameof(CanManageBranches));
        OnPropertyChanged(nameof(CanSwitchBranch));
        OnPropertyChanged(nameof(CanDeleteBranch));
        OnPropertyChanged(nameof(BranchManagementHint));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (T item in source) target.Add(item);
    }

    public void Dispose()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
    }
}
