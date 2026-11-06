using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RainExplorer.Helpers;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.ViewModels;

/// <summary>
/// One browser tab: its own current folder, navigation history, sort, and filter.
/// All per-tab state lives here so tabs are fully independent.
/// </summary>
public enum PageKind { Folder, Home, Drives, Archive }

public sealed class TabViewModel : ObservableObject, IDisposable
{
    /// <summary>Sentinel paths for the special dashboard pages.</summary>
    public const string HomeToken = "Home";
    public const string DrivesToken = "Drives";

    private readonly FileSystemService _fs;
    private readonly ArchiveLocationAdapter _archive;
    private readonly ArchiveCreationService _archiveCreation;
    private readonly PhysicalArchiveExtractionService _physicalArchiveExtraction;
    private readonly List<FileItem> _all = new();      // unfiltered contents of cwd
    private readonly List<string> _history = new();
    private int _histIndex = -1;
    private CancellationTokenSource? _driveCountCts;
    private FileSystemWatcher? _folderWatcher;
    private int _watchSetupVersion;
    private DispatcherTimer? _watchRefreshTimer;
    private DateTime _suppressWatchedRefreshUntilUtc;
    private int _inlineEditDepth;
    private bool _watchedRefreshPending;
    private bool _reloadAfterBusy;
    private bool _disposed;
    private int _navigationVersion;
    private CancellationTokenSource? _navigationCts;
    private string? _pendingRestoreTarget;
    private Task? _archiveDisposeTask;
    private Task? _archiveCreationDisposeTask;
    private Task? _physicalArchiveExtractionDisposeTask;

    private string _sortKey = "Name";
    private int _sortDir = 1;                            // 1 asc, -1 desc

    /// <summary>Active sort column key ("Name", "Modified", "Type", "Size").</summary>
    public string SortKey { get => _sortKey; private set => Set(ref _sortKey, value); }

    /// <summary>Sort direction: 1 ascending, -1 descending.</summary>
    public int SortDir { get => _sortDir; private set => Set(ref _sortDir, value); }

    public ObservableCollection<FileItem> Items { get; } = new();

    // ---- Dashboard-page data (Home / Drives) -------------------------------
    public ObservableCollection<FileItem> Recents { get; } = new();
    public ObservableCollection<FileItem> PinnedTiles { get; } = new();
    public ObservableCollection<DriveVM> Drives { get; } = new();

    private PageKind _page = PageKind.Folder;
    public PageKind Page
    {
        get => _page;
        private set
        {
            if (Set(ref _page, value))
            {
                OnPropertyChanged(nameof(IsFolderView));
                OnPropertyChanged(nameof(IsContentsView));
                OnPropertyChanged(nameof(IsHomeView));
                OnPropertyChanged(nameof(IsDrivesView));
                OnPropertyChanged(nameof(IsArchiveView));
                OnPropertyChanged(nameof(IsNetworkServer));
                OnPropertyChanged(nameof(CanUsePhysicalFileCommands));
                OnPropertyChanged(nameof(RequiresArchiveOperationPassword));
                OnPropertyChanged(nameof(CanTestArchiveIntegrity));
                OnPropertyChanged(nameof(CanExtractArchiveSelection));
                OnPropertyChanged(nameof(CanRebuildArchiveCopy));
            }
        }
    }
    public bool IsFolderView => _page == PageKind.Folder;
    public bool IsContentsView => IsFolderView || IsArchiveView;
    public bool IsHomeView => _page == PageKind.Home;
    public bool IsDrivesView => _page == PageKind.Drives;
    public bool IsArchiveView => _page == PageKind.Archive;
    public bool IsNetworkServer => IsFolderView && NetworkPath.TryGetServerRoot(CurrentPath, out _);
    public bool CanUsePhysicalFileCommands => IsFolderView && !IsNetworkServer;
    public bool RequiresArchiveOperationPassword => IsArchiveView
        && _archive.RequiresOperationPassword;
    public bool CanTestArchiveIntegrity => IsArchiveView && _archive.CanTestIntegrity;
    public bool CanExtractArchiveSelection => IsArchiveView && _archive.CanExtractSelection;
    public bool CanRebuildArchiveCopy => IsArchiveView && _archive.CanRebuildCopy;
    public IReadOnlyList<ArchiveBreadcrumb> ArchiveBreadcrumbs => _archive.Breadcrumbs;
    public bool CanGoToPreviousArchivePage => IsArchiveView && _archive.CanGoToPreviousPage;
    public bool CanGoToNextArchivePage => IsArchiveView && _archive.CanGoToNextPage;

    public bool HasNoRecents => Recents.Count == 0;

    /// <summary>Raised after a successful navigation, so the view can animate.</summary>
    public event Action? ContentsChanged;

    public TabViewModel(FileSystemService fs, ArchiveClientRuntime? archiveRuntime = null)
        : this(fs, archiveRuntime ?? ArchiveClientRuntime.Shared, previewLeaseFactory: null)
    {
    }

    internal TabViewModel(
        FileSystemService fs,
        ArchiveClientRuntime archiveRuntime,
        ArchiveEntryPreviewLeaseFactory? previewLeaseFactory)
    {
        _fs = fs;
        _archive = previewLeaseFactory is null
            ? new ArchiveLocationAdapter(archiveRuntime)
            : new ArchiveLocationAdapter(archiveRuntime, previewLeaseFactory);
        _archiveCreation = new ArchiveCreationService(archiveRuntime, ArchiveActivitySink.Instance);
        _physicalArchiveExtraction = new PhysicalArchiveExtractionService(archiveRuntime);
        _archive.Changed += OnArchiveChanged;
        SettingsStore.Instance.Settings.PropertyChanged += OnSettingsChanged;
        BackCommand = new RelayCommand(_ => GoBack(), _ => CanGoBack);
        ForwardCommand = new RelayCommand(_ => GoForward(), _ => CanGoForward);
        UpCommand = new RelayCommand(_ => GoUp(), _ => CanGoUp);
        RefreshCommand = new RelayCommand(_ => Refresh());
        OpenCommand = new RelayCommand(p => Open(p as FileItem));
        SortCommand = new RelayCommand(p => Sort(p as string ?? "Name"));
        GoToAddressCommand = new RelayCommand(_ => _ = NavigateAsync(CurrentPath, true));
        OpenEntryCommand = new RelayCommand(OpenEntry);
    }

    /// <summary>Open a tile from a dashboard page (a recent/pinned item or a drive).</summary>
    private void OpenEntry(object? p)
    {
        switch (p)
        {
            case FileItem fi when fi.IsDirectory: _ = NavigateAsync(fi.FullPath, true); break;
            case FileItem fi: Open(fi); break;
            case DriveVM d: _ = NavigateAsync(d.Path, true); break;
            case string s: _ = NavigateAsync(s, true); break;
        }
    }

    // ---- Bindable properties ------------------------------------------------
    private string _currentPath = string.Empty;
    public string CurrentPath
    {
        get => _currentPath;
        set
        {
            if (!Set(ref _currentPath, value)) return;
            OnPropertyChanged(nameof(IsNetworkServer));
            OnPropertyChanged(nameof(CanUsePhysicalFileCommands));
            OnPropertyChanged(nameof(CanGoUp));
        }
    }

    /// <summary>The location session persistence should restore, including a folder that
    /// is still loading and has not replaced <see cref="CurrentPath"/> yet.</summary>
    public string RestoreTarget
    {
        get
        {
            if (_pendingRestoreTarget is not null) return _pendingRestoreTarget;
            if (IsArchiveView) return _archive.TryGetPersistentTarget(out string target) ? target : string.Empty;
            return CurrentPath;
        }
    }

    public bool CanDuplicate => !IsArchiveView || _archive.CanPersistLocation;

    private void SetPendingRestoreTarget(string? target)
    {
        if (string.Equals(_pendingRestoreTarget, target, StringComparison.Ordinal)) return;
        _pendingRestoreTarget = target;
        OnPropertyChanged(nameof(RestoreTarget));
    }

    private string _title = "New Tab";
    public string Title
    {
        get => _title;
        private set
        {
            if (Set(ref _title, value)) OnPropertyChanged(nameof(TopLevelTitle));
        }
    }

    private bool _isPinned;
    /// <summary>Whether this tab is kept while using bulk-close commands.</summary>
    public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }

    private bool _isActive;
    /// <summary>Whether this is the tab whose contents are currently displayed.</summary>
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    private string? _groupId;
    public string? GroupId
    {
        get => _groupId;
        set
        {
            if (!Set(ref _groupId, value)) return;
            OnPropertyChanged(nameof(IsGrouped));
        }
    }

    private string _groupName = "Tab group";
    public string GroupName
    {
        get => _groupName;
        set
        {
            if (Set(ref _groupName, value)) OnPropertyChanged(nameof(TopLevelTitle));
        }
    }
    public bool IsGrouped => !string.IsNullOrWhiteSpace(GroupId);

    private bool _isGroupLeader;
    public bool IsGroupLeader
    {
        get => _isGroupLeader;
        set
        {
            if (Set(ref _isGroupLeader, value)) OnPropertyChanged(nameof(TopLevelTitle));
        }
    }

    private int _groupCount;
    public int GroupCount { get => _groupCount; set => Set(ref _groupCount, value); }
    public string TopLevelTitle => IsGroupLeader ? GroupName : Title;

    private bool _isGroupDropTarget;
    public bool IsGroupDropTarget { get => _isGroupDropTarget; set => Set(ref _isGroupDropTarget, value); }

    private bool _isDragging;
    public bool IsDragging { get => _isDragging; set => Set(ref _isDragging, value); }

    private ImageSource? _previewImage;
    /// <summary>Last rendered snapshot shown when the user hovers this tab.</summary>
    public ImageSource? PreviewImage { get => _previewImage; set => Set(ref _previewImage, value); }

    private string _filter = string.Empty;
    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) OnQueryChanged(); }
    }

    private bool _recursive;
    /// <summary>When true and a query is present, search subfolders instead of just this one.</summary>
    public bool Recursive
    {
        get => _recursive;
        set { if (Set(ref _recursive, value)) OnQueryChanged(); }
    }

    // Recursive-search state.
    private CancellationTokenSource? _searchCts;
    private readonly List<FileItem> _searchResults = new();
    private bool _isSearchView;

    private string _status = string.Empty;
    private DispatcherTimer? _statusClearTimer;

    // Warnings/errors ("⚠️ …") are transient toasts — clear them after a while so the
    // footer doesn't get stuck showing a stale error. Normal status (item counts, page
    // labels) is left alone since it reflects ongoing state, not a one-off notification.
    public string Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            _statusClearTimer?.Stop();
            if (!value.StartsWith("⚠️", StringComparison.Ordinal)) return;
            _statusClearTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            _statusClearTimer.Tick -= OnStatusClearTick;
            _statusClearTimer.Tick += OnStatusClearTick;
            _statusClearTimer.Start();
        }
    }

    private void OnStatusClearTick(object? sender, EventArgs e)
    {
        _statusClearTimer!.Stop();
        if (Page == PageKind.Folder) ApplyView();
        else Status = string.Empty;
    }

    private bool _busy;
    public bool Busy { get => _busy; set => Set(ref _busy, value); }

    private bool _isDropTarget;
    /// <summary>True while a file drag hovers this tab — its header paints an accent highlight.</summary>
    public bool IsDropTarget { get => _isDropTarget; set => Set(ref _isDropTarget, value); }

    // ---- Commands -----------------------------------------------------------
    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand SortCommand { get; }
    public ICommand GoToAddressCommand { get; }
    public ICommand OpenEntryCommand { get; }

    public bool CanGoBack => IsArchiveView
        ? _archive.CanGoBack || _histIndex > 0
        : _histIndex > 0;
    public bool CanGoForward => IsArchiveView
        ? _archive.CanGoForward || HasOuterForwardHistory
        : HasOuterForwardHistory;
    public bool CanGoUp => IsArchiveView ? _archive.CanGoUp
        : !string.IsNullOrEmpty(CurrentPath) && _fs.GetParent(CurrentPath) is not null;

    // ---- Navigation ---------------------------------------------------------
    public async Task NavigateAsync(string? path, bool pushHistory)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path)) return;
        path = NetworkPath.Normalize(path);
        int navigationVersion = unchecked(++_navigationVersion);
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = new CancellationTokenSource();
        CancellationToken ct = _navigationCts.Token;
        _searchCts?.Cancel();

        if (ArchiveLocationSerializer.IsArchiveTarget(path))
        {
            await OpenArchiveLocationAsync(path, pushHistory, navigationVersion);
            return;
        }

        // Special dashboard pages.
        if (path is HomeToken or DrivesToken)
        {
            await CloseArchiveLocationAsync();
            if (_disposed || navigationVersion != _navigationVersion) return;
            SetPendingRestoreTarget(null);
            Busy = false;
            ShowPage(path == HomeToken ? PageKind.Home : PageKind.Drives, path, pushHistory);
            return;
        }

        SetPendingRestoreTarget(path);
        Busy = true;
        try
        {
            if (!NetworkPath.TryGetServerRoot(path, out _) && ArchiveFileTypes.IsSupportedPath(path)
                && await Task.Run(() => File.Exists(path), ct).WaitAsync(TimeSpan.FromSeconds(20), ct))
            {
                if (_disposed || navigationVersion != _navigationVersion) return;
                await OpenArchiveLocationAsync(path, pushHistory, navigationVersion);
                return;
            }

            IReadOnlyList<FileItem> entries;
            try
            {
                entries = await _fs.ReadDirectoryAsync(path, ct);
            }
            catch (Exception ex) when (pushHistory && NetworkPath.IsNetworkPath(path)
                                       && NetworkConnectionService.RequiresSignIn(ex))
            {
                ct.ThrowIfCancellationRequested();
                var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);
                if (owner is null) throw;
                if (!await NetworkConnectionService.SignInAsync(path, new WindowInteropHelper(owner).Handle, ct))
                {
                    SetPendingRestoreTarget(string.IsNullOrEmpty(CurrentPath) ? path : null);
                    Status = "Network sign-in cancelled.";
                    return;
                }
                entries = await _fs.ReadDirectoryAsync(path, ct);
            }
            if (_disposed || navigationVersion != _navigationVersion) return;
            await CloseArchiveLocationAsync();
            if (_disposed || navigationVersion != _navigationVersion) return;

            _all.Clear();
            _all.AddRange(entries);
            Page = PageKind.Folder;
            CurrentPath = path;
            SetPendingRestoreTarget(null);
            Title = FolderDisplayName(path);
            if (!IsNetworkServer) RecentsStore.Instance.Add(path, isDirectory: true);
            WatchFolder(path);

            // Restore this folder's remembered sort.
            var pref = SortStore.Instance.Get(path);
            SortKey = pref.Key;
            SortDir = pref.Dir;

            // Navigating clears any active search/filter.
            _searchCts?.Cancel();
            _isSearchView = false;
            _filter = string.Empty;
            OnPropertyChanged(nameof(Filter));

            if (pushHistory) PushHistory(path);

            ApplyView();
            ContentsChanged?.Invoke();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && navigationVersion == _navigationVersion)
            {
                // An offline network tab restored into an empty tab must retain its
                // saved address so Refresh and the next session can try it again.
                SetPendingRestoreTarget(string.IsNullOrEmpty(CurrentPath) && NetworkPath.IsNetworkPath(path) ? path : null);
                Status = ex is TimeoutException
                    ? "⚠️ The network location took too long to respond. Check the address and connection, then try again."
                    : $"⚠️ {ex.Message}";
            }
        }
        finally
        {
            if (!_disposed && navigationVersion == _navigationVersion) Busy = false;
        }
    }

    private void OnArchiveChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            if (_disposed) return;
            StopWatchingFolder();
            _sizeCts?.Cancel();
            _all.Clear();
            _all.AddRange(_archive.Items);
            Page = PageKind.Archive;
            CurrentPath = string.Empty;
            Title = _archive.Title;
            Busy = _archive.State == ArchiveLocationState.Loading;
            Status = _archive.Status;
            PopulateSorted(_all);
            OnPropertyChanged(nameof(ArchiveBreadcrumbs));
            OnPropertyChanged(nameof(RestoreTarget));
            OnPropertyChanged(nameof(CanDuplicate));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
            OnPropertyChanged(nameof(CanGoUp));
            OnPropertyChanged(nameof(CanGoToPreviousArchivePage));
            OnPropertyChanged(nameof(CanGoToNextArchivePage));
            OnPropertyChanged(nameof(RequiresArchiveOperationPassword));
            OnPropertyChanged(nameof(CanTestArchiveIntegrity));
            OnPropertyChanged(nameof(CanExtractArchiveSelection));
            OnPropertyChanged(nameof(CanRebuildArchiveCopy));
            CommandManager.InvalidateRequerySuggested();
            ContentsChanged?.Invoke();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply();
        else _ = dispatcher.BeginInvoke(Apply);
    }

    private async Task CloseArchiveLocationAsync()
    {
        if (!_archive.IsOpen && !IsArchiveView) return;
        await _archive.CloseAsync().ConfigureAwait(true);
    }

    private static string FolderDisplayName(string path)
    {
        string name = NetworkPath.GetDisplayName(path);
        return string.IsNullOrEmpty(name) ? path : name;   // root like "C:\" has no file name
    }

    private void PushHistory(string path)
    {
        if (_histIndex < _history.Count - 1)
            _history.RemoveRange(_histIndex + 1, _history.Count - _histIndex - 1);
        _history.Add(path);
        _histIndex = _history.Count - 1;
        NotifyNavigationStateChanged();
    }

    private bool HasOuterForwardHistory =>
        _histIndex >= 0 && _histIndex < _history.Count - 1;

    private async Task OpenArchiveLocationAsync(
        string target,
        bool pushHistory,
        int navigationVersion)
    {
        bool hadPriorArchive = _archive.IsOpen;
        string priorSource = _archive.SourcePath;
        string? requestedSource = TryGetArchiveSourcePath(target);
        string historyTarget = ArchiveLocationSerializer.IsArchiveTarget(target)
            ? target
            : requestedSource ?? target;

        SetPendingRestoreTarget(target);
        try
        {
            await _archive.OpenAsync(target);
            if (_disposed || navigationVersion != _navigationVersion || !pushHistory) return;

            bool adoptedRequestedArchive = requestedSource is not null
                && _archive.IsOpen
                && string.Equals(
                    _archive.SourcePath,
                    requestedSource,
                    StringComparison.OrdinalIgnoreCase);
            bool initialAttemptOwnsPresentation = !hadPriorArchive && IsArchiveView;
            bool preservedDifferentArchive = hadPriorArchive
                && _archive.IsOpen
                && string.Equals(
                    _archive.SourcePath,
                    priorSource,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(priorSource, requestedSource, StringComparison.OrdinalIgnoreCase);
            if ((!adoptedRequestedArchive && !initialAttemptOwnsPresentation)
                || preservedDifferentArchive)
                return;

            if (_history.Count == 0
                && requestedSource is not null
                && Path.GetDirectoryName(requestedSource) is { Length: > 0 } parent)
            {
                PushHistory(parent);
            }
            PushHistory(historyTarget);
        }
        finally
        {
            SetPendingRestoreTarget(null);
        }
    }

    private static string? TryGetArchiveSourcePath(string target)
    {
        if (ArchiveLocationSerializer.IsArchiveTarget(target))
            return ArchiveLocationSerializer.TryDeserialize(target, out ArchiveLocation location)
                ? location.SourcePath
                : null;
        try { return Path.GetFullPath(target); }
        catch (Exception exception) when (exception is ArgumentException
                                               or NotSupportedException
                                               or PathTooLongException)
        {
            return null;
        }
    }

    private void NotifyNavigationStateChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- Dashboard pages ----------------------------------------------------
    private void ShowPage(PageKind kind, string token, bool pushHistory)
    {
        StopWatchingFolder();
        _searchCts?.Cancel();
        _isSearchView = false;
        _filter = string.Empty;
        OnPropertyChanged(nameof(Filter));

        Page = kind;
        CurrentPath = token;
        Title = token;

        if (kind == PageKind.Home) LoadHome();
        else LoadDrivesCollection();

        if (pushHistory) PushHistory(token);
        Status = kind == PageKind.Home ? "Home" : $"{Drives.Count} drive{(Drives.Count == 1 ? "" : "s")}";
        ContentsChanged?.Invoke();
    }

    private void LoadHome()
    {
        Recents.Clear();
        foreach (var e in RecentsStore.Instance.Items)
        {
            bool exists = NetworkPath.IsNetworkPath(e.Path)
                || (e.IsDirectory ? Directory.Exists(e.Path) : File.Exists(e.Path));
            if (!exists) continue;
            Recents.Add(new FileItem
            {
                Name = NiceName(e.Path),
                FullPath = e.Path,
                IsDirectory = e.IsDirectory,
                Modified = e.AccessedUtc.ToLocalTime(),
            });
            if (Recents.Count >= 16) break;
        }
        OnPropertyChanged(nameof(HasNoRecents));

        PinnedTiles.Clear();
        foreach (var p in SettingsStore.Instance.Settings.Pinned)
        {
            if (!NetworkPath.IsNetworkPath(p.Path) && !Directory.Exists(p.Path)) continue;
            PinnedTiles.Add(new FileItem
            {
                Name = string.IsNullOrWhiteSpace(p.Name) ? NiceName(p.Path) : p.Name,
                FullPath = p.Path,
                IsDirectory = true,
            });
        }

        LoadDrivesCollection();
    }

    /// <summary>Refresh the visible Home dashboard after shared pins change.</summary>
    public void RefreshHome()
    {
        if (Page != PageKind.Home) return;
        LoadHome();
        Status = "Home";
        ContentsChanged?.Invoke();
    }

    private static string NiceName(string path)
    {
        string name = NetworkPath.GetDisplayName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private void LoadDrivesCollection()
    {
        _driveCountCts?.Cancel();
        _driveCountCts = new CancellationTokenSource();

        Drives.Clear();
        foreach (var di in DriveInfo.GetDrives())
        {
            if (!di.IsReady) continue;
            try
            {
                long total = di.TotalSize, free = di.TotalFreeSpace, used = total - free;
                double pct = total > 0 ? used * 100.0 / total : 0;
                string label = string.IsNullOrWhiteSpace(di.VolumeLabel)
                    ? di.Name
                    : $"{di.VolumeLabel} ({di.Name.TrimEnd('\\')})";

                var vm = new DriveVM
                {
                    Label = label,
                    Path = di.RootDirectory.FullName,
                    UsedPercent = pct,
                    UsageText = $"{Gb(used)} of {Gb(total)} used",
                    FreeText = $"{Gb(free)} free",
                    TypeText = di.DriveType.ToString(),
                };
                Drives.Add(vm);

                if (SettingsStore.Instance.Settings.ShowDriveItemCounts)
                    _ = CountDriveAsync(vm, _driveCountCts.Token);
            }
            catch { /* drive went away */ }
        }
    }

    private static async Task CountDriveAsync(DriveVM vm, CancellationToken ct)
    {
        vm.CountText = "Counting…";
        try
        {
            long count = await Task.Run(() =>
            {
                long n = 0;
                var stack = new Stack<string>();
                stack.Push(vm.Path);
                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    string dir = stack.Pop();
                    try
                    {
                        foreach (var _ in Directory.EnumerateFiles(dir))
                        {
                            if ((++n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                            if (n >= 5_000_000) return n;
                        }
                        foreach (var d in Directory.EnumerateDirectories(dir)) stack.Push(d);
                    }
                    catch { /* skip unreadable */ }
                }
                return n;
            }, ct);

            if (!ct.IsCancellationRequested) vm.CountText = $"{count:N0} files";
        }
        catch (OperationCanceledException) { }
        catch { vm.CountText = ""; }
    }

    private static string Gb(long bytes)
    {
        double g = bytes / 1_000_000_000.0;
        if (g >= 1000) return $"{g / 1000:0.0} TB";
        if (g >= 1) return $"{g:0.0} GB";
        return $"{bytes / 1_000_000.0:0} MB";
    }

    private void GoBack()
    {
        if (IsArchiveView)
        {
            if (_archive.GoBack()) ClearArchiveQuery();
            else if (_histIndex > 0)
            {
                _histIndex--;
                NotifyNavigationStateChanged();
                _ = NavigateAsync(_history[_histIndex], false);
            }
            return;
        }
        if (!CanGoBack) return;
        _histIndex--;
        NotifyNavigationStateChanged();
        _ = NavigateAsync(_history[_histIndex], false);
    }

    private void GoForward()
    {
        if (IsArchiveView)
        {
            if (_archive.GoForward()) ClearArchiveQuery();
            else if (HasOuterForwardHistory)
            {
                _histIndex++;
                NotifyNavigationStateChanged();
                _ = NavigateAsync(_history[_histIndex], false);
            }
            return;
        }
        if (!CanGoForward) return;
        _histIndex++;
        NotifyNavigationStateChanged();
        _ = NavigateAsync(_history[_histIndex], false);
    }

    private void GoUp()
    {
        if (IsArchiveView)
        {
            if (_archive.GoUp()) ClearArchiveQuery();
            return;
        }
        var parent = _fs.GetParent(CurrentPath);
        if (parent is not null) _ = NavigateAsync(parent, true);
    }

    private void Refresh() => _ = IsArchiveView ? _archive.RefreshAsync() : ReloadAsync();

    /// <summary>Re-read the current folder (used after a file operation).</summary>
    public async Task ReloadAsync(bool animate = true, bool automatic = false)
    {
        if (_disposed) return;
        if (string.IsNullOrEmpty(CurrentPath) && _pendingRestoreTarget is { } pending)
        {
            if (!automatic && !Busy) await NavigateAsync(pending, pushHistory: true);
            return;
        }
        if (IsArchiveView)
        {
            if (!automatic) await _archive.RefreshAsync();
            return;
        }
        if (automatic && !SettingsStore.Instance.Settings.AutoRefreshFolders) return;

        if (Page != PageKind.Folder)
        {
            await NavigateAsync(CurrentPath, false);
            return;
        }

        if (Busy)
        {
            if (automatic) QueueWatchedRefresh();
            else _reloadAfterBusy = true;
            return;
        }

        string path = CurrentPath;
        int navigationVersion = _navigationVersion;
        Busy = true;
        try
        {
            var entries = await _fs.ReadDirectoryAsync(path, _navigationCts?.Token ?? default);
            if (_disposed || navigationVersion != _navigationVersion || Page != PageKind.Folder ||
                !string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            _all.Clear();
            _all.AddRange(entries);
            if (Recursive && !string.IsNullOrWhiteSpace(Filter))
                OnQueryChanged();
            else
                ApplyView();
            if (animate) ContentsChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed && navigationVersion == _navigationVersion)
                Status = ex is TimeoutException ? "⚠️ The network location took too long to respond. Try refreshing again." : $"⚠️ {ex.Message}";
        }
        finally
        {
            if (!_disposed && navigationVersion == _navigationVersion) Busy = false;
            if (_reloadAfterBusy && !_disposed && navigationVersion == _navigationVersion)
            {
                _reloadAfterBusy = false;
                _ = ReloadAsync(animate: false);
            }
        }
    }

    private async void WatchFolder(string path)
    {
        if (_disposed) return;
        if (NetworkPath.TryGetServerRoot(path, out _) || !SettingsStore.Instance.Settings.AutoRefreshFolders)
        {
            StopWatchingFolder();
            return;
        }
        if (_folderWatcher is not null &&
            string.Equals(_folderWatcher.Path, path, StringComparison.OrdinalIgnoreCase))
            return;

        StopWatchingFolder();
        int setupVersion = _watchSetupVersion;
        try
        {
            var watcher = NetworkPath.IsNetworkPath(path)
                ? await Task.Run(() => CreateFolderWatcher(path))
                : CreateFolderWatcher(path);
            if (_disposed || setupVersion != _watchSetupVersion
                || !string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                watcher.Dispose();
                return;
            }
            _folderWatcher = watcher;
        }
        catch
        {
            // The location can still be browsed and refreshed if notifications are unavailable.
        }
    }

    private FileSystemWatcher CreateFolderWatcher(string path)
    {
        var watcher = new FileSystemWatcher(path);
        try
        {
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;
            watcher.IncludeSubdirectories = false;
            watcher.Changed += OnWatchedFolderChanged;
            watcher.Created += OnWatchedFolderChanged;
            watcher.Deleted += OnWatchedFolderChanged;
            watcher.Renamed += OnWatchedFolderChanged;
            watcher.Error += OnWatchedFolderError;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Re-read after an operation Rain performed itself. The folder watcher will also see
    /// that operation, so briefly suppress its duplicate animated refresh.
    /// </summary>
    public Task ReloadAfterOperationAsync()
    {
        BeginKnownFileOperation();
        return ReloadAsync(animate: false);
    }

    /// <summary>Suppress watcher echoes before Rain changes the folder itself.</summary>
    public void BeginKnownFileOperation()
    {
        _suppressWatchedRefreshUntilUtc = DateTime.UtcNow.AddSeconds(2);
        _watchedRefreshPending = false;
        _watchRefreshTimer?.Stop();
    }

    /// <summary>Keep watcher refreshes from replacing the row that owns an active rename box.</summary>
    public void BeginInlineEdit()
    {
        _inlineEditDepth++;
        _watchRefreshTimer?.Stop();
    }

    public void EndInlineEdit()
    {
        if (_inlineEditDepth > 0) _inlineEditDepth--;
        if (_inlineEditDepth == 0 && _watchedRefreshPending &&
            DateTime.UtcNow >= _suppressWatchedRefreshUntilUtc)
        {
            _watchedRefreshPending = false;
            QueueWatchedRefresh();
        }
    }

    /// <summary>Update and re-sort one successfully renamed row without enumerating the folder.</summary>
    public void ApplyLocalRename(FileItem item, string newPath)
    {
        BeginKnownFileOperation();
        item.ApplyRename(newPath);
        if (!string.IsNullOrWhiteSpace(Filter))
        {
            ApplyView();
            return;
        }

        // A rename only changes one row. Reposition that row instead of clearing and
        // rebuilding the entire directory, which can stall the UI in large folders.
        if (!Items.Remove(item)) return;
        int insertAt = 0;
        while (insertAt < Items.Count && Compare(Items[insertAt], item) <= 0) insertAt++;
        Items.Insert(insertAt, item);
    }

    /// <summary>Remove successfully deleted rows without re-enumerating the whole folder.</summary>
    public void ApplyLocalDelete(IReadOnlyList<string> completedPaths)
    {
        if (completedPaths.Count == 0) return;
        BeginKnownFileOperation();

        var deleted = completedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToArray();
        if (deleted.Length == 0) return;

        static bool IsDeleted(string candidate, IReadOnlyList<string> roots)
        {
            string path = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (string root in roots)
            {
                if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return true;
                if (path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && (path[root.Length] == Path.DirectorySeparatorChar
                        || path[root.Length] == Path.AltDirectorySeparatorChar))
                    return true;
            }
            return false;
        }

        _all.RemoveAll(item => IsDeleted(item.FullPath, deleted));
        foreach (var item in Items.Where(item => IsDeleted(item.FullPath, deleted)).ToList())
            Items.Remove(item);

        int total = _all.Count;
        Status = !string.IsNullOrWhiteSpace(Filter) && Items.Count != total
            ? $"{Items.Count} of {total} items"
            : $"{total} item{(total == 1 ? "" : "s")}";
    }

    private void OnWatchedFolderChanged(object sender, FileSystemEventArgs e)
    {
        if (!ReferenceEquals(sender, _folderWatcher)) return;
        if (!SettingsStore.Instance.Settings.AutoRefreshFolders) return;
        if (DateTime.UtcNow < _suppressWatchedRefreshUntilUtc) return;
        if (_inlineEditDepth > 0)
        {
            _watchedRefreshPending = true;
            return;
        }
        QueueWatchedRefresh();
    }

    private void OnWatchedFolderError(object sender, ErrorEventArgs e)
    {
        if (_disposed || !SettingsStore.Instance.Settings.AutoRefreshFolders) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !ReferenceEquals(sender, _folderWatcher) || Page != PageKind.Folder
                || !SettingsStore.Instance.Settings.AutoRefreshFolders) return;
            StopWatchingFolder();
            WatchFolder(CurrentPath);
        });
    }

    private void QueueWatchedRefresh()
    {
        if (_disposed || !SettingsStore.Instance.Settings.AutoRefreshFolders) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !SettingsStore.Instance.Settings.AutoRefreshFolders) return;
            if (_inlineEditDepth > 0)
            {
                _watchedRefreshPending = true;
                return;
            }
            _watchRefreshTimer ??= CreateWatchRefreshTimer();
            _watchRefreshTimer.Stop();
            _watchRefreshTimer.Start();
        });
    }

    private DispatcherTimer CreateWatchRefreshTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!SettingsStore.Instance.Settings.AutoRefreshFolders
                || DateTime.UtcNow < _suppressWatchedRefreshUntilUtc) return;
            if (_inlineEditDepth > 0)
            {
                _watchedRefreshPending = true;
                return;
            }
            _ = ReloadAsync(automatic: true);
        };
        return timer;
    }

    private void StopWatchingFolder()
    {
        unchecked { _watchSetupVersion++; }
        if (_folderWatcher is null) return;
        _folderWatcher.EnableRaisingEvents = false;
        _folderWatcher.Changed -= OnWatchedFolderChanged;
        _folderWatcher.Created -= OnWatchedFolderChanged;
        _folderWatcher.Deleted -= OnWatchedFolderChanged;
        _folderWatcher.Renamed -= OnWatchedFolderChanged;
        _folderWatcher.Error -= OnWatchedFolderError;
        _folderWatcher.Dispose();
        _folderWatcher = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        unchecked { _navigationVersion++; }
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = null;
        SettingsStore.Instance.Settings.PropertyChanged -= OnSettingsChanged;
        StopWatchingFolder();
        _watchRefreshTimer?.Stop();
        _statusClearTimer?.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _driveCountCts?.Cancel();
        _driveCountCts?.Dispose();
        _sizeCts?.Cancel();
        _sizeCts?.Dispose();
        _sizeCts = null;
        _archive.Changed -= OnArchiveChanged;
        _archiveDisposeTask ??= _archive.DisposeAsync().AsTask();
        _archiveCreationDisposeTask ??= _archiveCreation.DisposeAsync().AsTask();
        _physicalArchiveExtractionDisposeTask ??=
            _physicalArchiveExtraction.DisposeAsync().AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_archiveDisposeTask is not null) await _archiveDisposeTask.ConfigureAwait(false);
        if (_archiveCreationDisposeTask is not null)
            await _archiveCreationDisposeTask.ConfigureAwait(false);
        if (_physicalArchiveExtractionDisposeTask is not null)
            await _physicalArchiveExtractionDisposeTask.ConfigureAwait(false);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.AutoRefreshFolders)) return;

        _watchedRefreshPending = false;
        _watchRefreshTimer?.Stop();
        if (!SettingsStore.Instance.Settings.AutoRefreshFolders)
        {
            StopWatchingFolder();
            return;
        }

        if (Page == PageKind.Folder)
            WatchFolder(CurrentPath);
    }

    /// <summary>Find a currently-shown item by full path (e.g. a just-created folder).</summary>
    public FileItem? Find(string fullPath) =>
        Items.FirstOrDefault(i => string.Equals(i.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

    public void Open(FileItem? item)
    {
        if (item is null) return;
        if (item.IsArchiveEntry)
        {
            if (item.IsDirectory)
            {
                if (_archive.Open(item)) ClearArchiveQuery();
            }
            else _ = OpenNestedArchiveAsync(item);
            return;
        }
        if (item.IsDirectory) _ = NavigateAsync(item.FullPath, true);
        else if (ArchiveFileTypes.IsSupportedPath(item.Name))
            _ = NavigateAsync(item.FullPath, pushHistory: true);
        else
        {
            try { _fs.OpenWithShell(item.FullPath); RecentsStore.Instance.Add(item.FullPath, isDirectory: false); }
            catch (Exception ex) { Status = $"⚠️ {ex.Message}"; }
        }
    }

    public bool TryGetArchiveTarget(FileItem item, out string target) =>
        _archive.TryGetPersistentTarget(item, out target);

    public bool NavigateArchive(string? nodeId)
    {
        bool navigated = IsArchiveView && _archive.Navigate(nodeId);
        if (navigated) ClearArchiveQuery();
        return navigated;
    }

    public bool NavigateArchive(int archiveDepth, string? nodeId)
    {
        bool navigated = IsArchiveView && _archive.Navigate(archiveDepth, nodeId);
        if (navigated) ClearArchiveQuery();
        return navigated;
    }

    public bool CanOpenNestedArchive(FileItem? item) =>
        IsArchiveView && _archive.CanOpenNestedArchive(item);

    public async Task<ArchiveNestedOpenOutcome> OpenNestedArchiveAsync(
        FileItem item,
        char[]? password = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsArchiveView)
        {
            if (password is not null) Array.Clear(password);
            return ArchiveNestedOpenOutcome.Refused;
        }
        ArchiveNestedOpenOutcome outcome = await _archive.OpenNestedArchiveAsync(
            item, password, cancellationToken);
        if (outcome == ArchiveNestedOpenOutcome.Opened) ClearArchiveQuery();
        return outcome;
    }

    private void ClearArchiveQuery()
    {
        if (_filter.Length != 0)
        {
            _filter = string.Empty;
            OnPropertyChanged(nameof(Filter));
        }
        if (_recursive)
        {
            _recursive = false;
            OnPropertyChanged(nameof(Recursive));
        }
    }

    public bool MoveArchivePage(int direction) => IsArchiveView && _archive.MovePage(direction);

    public Task<ArchiveEntryPreviewResult> LoadArchivePreviewAsync(
        FileItem item,
        CancellationToken cancellationToken = default) =>
        IsArchiveView
            ? _archive.LoadPreviewAsync(item, cancellationToken)
            : Task.FromResult(ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Stale));

    public void UpdateArchiveSelection(IReadOnlyList<FileItem> selectedItems)
    {
        if (!IsArchiveView) return;
        _archive.UpdateSelection(selectedItems);
        OnPropertyChanged(nameof(CanExtractArchiveSelection));
    }

    public Task<ArchiveOperationResult> TestArchiveIntegrityAsync(
        char[]? password = null,
        CancellationToken cancellationToken = default) =>
        IsArchiveView
            ? _archive.TestIntegrityAsync(password, cancellationToken)
            : Task.FromResult(new ArchiveOperationResult(ArchiveOperationOutcome.Stale));

    public Task<ArchiveOperationResult> ExtractArchiveSelectionAsync(
        string destinationPath,
        char[]? password = null,
        CancellationToken cancellationToken = default) =>
        IsArchiveView
            ? _archive.ExtractSelectionAsync(destinationPath, password, cancellationToken)
            : Task.FromResult(new ArchiveOperationResult(ArchiveOperationOutcome.Stale));

    public Task<ArchiveRebuildCapabilitySnapshot> LoadArchiveRebuildCapabilitiesAsync(
        CancellationToken cancellationToken = default) =>
        IsArchiveView
            ? _archive.LoadRebuildCapabilitiesAsync(cancellationToken)
            : Task.FromResult(ArchiveRebuildCapabilitySnapshot.Fixed(
                ArchiveWriteCapabilityState.Stale));

    public Task<ArchiveOperationResult> RebuildArchiveCopyAsync(
        ArchiveRebuildRequest request,
        CancellationToken cancellationToken = default) =>
        IsArchiveView
            ? _archive.RebuildCopyAsync(request, cancellationToken)
            : Task.FromResult(new ArchiveOperationResult(ArchiveOperationOutcome.Stale));

    public Task<ArchiveCreationCapabilitySnapshot> LoadArchiveCreationCapabilitiesAsync(
        CancellationToken cancellationToken = default) =>
        IsFolderView
            ? _archiveCreation.LoadCapabilitiesAsync(cancellationToken)
            : Task.FromResult(ArchiveCreationCapabilitySnapshot.Fixed(
                ArchiveWriteCapabilityState.Stale));

    public Task<ArchiveOperationResult> CreateArchiveAsync(
        ArchiveCreationRequest request,
        CancellationToken cancellationToken = default) =>
        IsFolderView
            ? _archiveCreation.CreateAsync(request, cancellationToken)
            : Task.FromResult(new ArchiveOperationResult(ArchiveOperationOutcome.Stale));

    internal Task<PhysicalArchiveExtractionBatchResult> ExtractPhysicalArchivesAsync(
        IReadOnlyList<string> sourcePaths,
        Func<string, char[]?> passwordPrompt,
        CancellationToken cancellationToken = default) =>
        IsFolderView
            ? _physicalArchiveExtraction.ExtractAsync(
                sourcePaths,
                passwordPrompt,
                cancellationToken)
            : Task.FromResult(PhysicalArchiveExtractionBatchResult.Refused(
                sourcePaths.Count,
                ArchiveOperationOutcome.Stale));

    internal Task<PhysicalArchiveExtractionBatchResult> ExtractPhysicalArchivesToAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationParent,
        Func<string, char[]?> passwordPrompt,
        CancellationToken cancellationToken = default) =>
        IsFolderView
            ? _physicalArchiveExtraction.ExtractToAsync(
                sourcePaths,
                destinationParent,
                passwordPrompt,
                cancellationToken)
            : Task.FromResult(PhysicalArchiveExtractionBatchResult.Refused(
                sourcePaths.Count,
                ArchiveOperationOutcome.Stale));

    // ---- Sort + filter ------------------------------------------------------
    private void Sort(string key)
    {
        if (SortKey == key) SortDir = -SortDir;
        else { SortKey = key; SortDir = 1; }
        if (!IsArchiveView) SortStore.Instance.Set(CurrentPath, new SortPref(SortKey, SortDir));

        // Re-sort whatever is currently shown (folder contents or search results).
        if (_isSearchView) PopulateSorted(_searchResults);
        else ApplyView();
    }

    /// <summary>Decide between local filtering and a recursive subfolder search.</summary>
    private void OnQueryChanged()
    {
        _searchCts?.Cancel();
        if (IsArchiveView)
        {
            _archive.SetSearch(Filter, Recursive);
            return;
        }
        if (!IsNetworkServer && Recursive && !string.IsNullOrWhiteSpace(Filter))
            _ = RunSearchAsync(Filter);
        else
        {
            _isSearchView = false;
            ApplyView();
        }
    }

    private async Task RunSearchAsync(string query)
    {
        if (_disposed) return;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        CancellationToken token = cts.Token;
        _isSearchView = true;
        Status = "Searching…";
        try
        {
            var results = await _fs.SearchAsync(CurrentPath, query, token);
            if (_disposed || token.IsCancellationRequested) return;
            _searchResults.Clear();
            _searchResults.AddRange(results);
            PopulateSorted(_searchResults);
            Status = $"{results.Count} result{(results.Count == 1 ? "" : "s")} for \"{query}\""
                     + (results.Count >= 10_000 ? " (showing first 10,000)" : "");
        }
        catch (OperationCanceledException) { /* superseded by a newer query */ }
        catch (Exception ex) { if (!_disposed) Status = $"⚠️ {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) _searchCts = null;
            cts.Dispose();
        }
    }

    // ---- Sort + populate (shared by folder view and search view) -----------
    private int Compare(FileItem a, FileItem b)
    {
        if (SettingsStore.Instance.Settings.FoldersFirst && a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;
        int r = _sortKey switch
        {
            "Modified" => a.Modified.CompareTo(b.Modified),
            "Created" => a.Created.CompareTo(b.Created),
            "Type" => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
            "Size" => a.Size.CompareTo(b.Size),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        return r * _sortDir;
    }

    private void PopulateSorted(IReadOnlyList<FileItem> items)
    {
        var sorted = items.ToList();
        sorted.Sort(Compare);
        Items.Clear();
        foreach (var i in sorted) Items.Add(i);
    }

    private void ApplyView()
    {
        IEnumerable<FileItem> view = _all;
        if (!string.IsNullOrWhiteSpace(Filter))
            view = view.Where(i => i.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase));

        var list = view.ToList();
        PopulateSorted(list);

        int total = _all.Count;
        Status = !string.IsNullOrWhiteSpace(Filter) && list.Count != total
            ? $"{list.Count} of {total} items"
            : $"{total} item{(total == 1 ? "" : "s")}";

        CalcFolderSizesIfEnabled();
    }

    // ---- Optional background folder-size measurement -----------------------
    private CancellationTokenSource? _sizeCts;

    /// <summary>Recompute when the setting is toggled on (called from the host VM).</summary>
    public void RefreshFolderSizes() => CalcFolderSizesIfEnabled();

    private void CalcFolderSizesIfEnabled()
    {
        _sizeCts?.Cancel();
        _sizeCts?.Dispose();
        _sizeCts = null;
        if (!SettingsStore.Instance.Settings.CalculateFolderSizes || Page != PageKind.Folder || IsNetworkServer) return;

        var dirs = Items.Where(i => i.IsDirectory && !i.FolderSizeKnown).ToList();
        if (dirs.Count == 0) return;

        var cts = new CancellationTokenSource();
        _sizeCts = cts;
        CancellationToken token = cts.Token;

        _ = Task.Run(() =>
        {
            foreach (var d in dirs)
            {
                if (token.IsCancellationRequested) return;
                long size = DirSize(d.FullPath, token);
                if (token.IsCancellationRequested) return;
                var item = d;
                App.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (_disposed || token.IsCancellationRequested
                        || !ReferenceEquals(_sizeCts, cts)) return;
                    item.SetFolderSize(size);
                });
            }
        }, token);
    }

    private static long DirSize(string root, CancellationToken ct)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) return total;
            string dir = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir)) stack.Push(sub);
            }
            catch { /* unreadable folder — skip */ }
        }
        return total;
    }
}
