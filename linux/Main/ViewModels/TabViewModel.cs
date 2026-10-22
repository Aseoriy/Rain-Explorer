using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Threading;
using RainExplorer.Helpers;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.ViewModels;

/// <summary>Which page a tab is showing.</summary>
public enum PageKind { Folder, Home, Drives }

/// <summary>
/// One browser tab: its own current folder, navigation history, sort, and filter.
/// All per-tab state lives here so tabs are fully independent.
/// </summary>
public sealed class TabViewModel : ObservableObject, IDisposable
{
    /// <summary>Sentinel paths for the special dashboard pages.</summary>
    public const string HomeToken = "Home";
    public const string DrivesToken = "Drives";

    private readonly FileSystemService _fs;
    private readonly List<FileItem> _all = new();      // unfiltered contents of cwd
    private IReadOnlySet<string>? _revealedPaths;
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

    private string _sortKey = "Name";
    private int _sortDir = 1;                            // 1 asc, -1 desc

    /// <summary>Active sort column key ("Name", "Modified", "Created", "Type", "Size").</summary>
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
                OnPropertyChanged(nameof(IsHomeView));
                OnPropertyChanged(nameof(IsDrivesView));
                OnPropertyChanged(nameof(CanUsePhysicalFileCommands));
                OnPropertyChanged(nameof(IsTrashView));
            }
        }
    }
    public bool IsFolderView => _page == PageKind.Folder;
    public bool IsHomeView => _page == PageKind.Home;
    public bool IsDrivesView => _page == PageKind.Drives;
    public bool CanUsePhysicalFileCommands => IsFolderView;
    /// <summary>True while browsing the trash's files folder (offers Restore / Empty trash).</summary>
    public bool IsTrashView => IsFolderView && PathUtil.Same(CurrentPath, FileSystemService.TrashToken);

    public bool HasNoRecents => Recents.Count == 0;

    /// <summary>Raised after a successful navigation, so the view can animate and reset scroll.</summary>
    public event Action? ContentsChanged;

    public TabViewModel(FileSystemService fs)
    {
        _fs = fs;
        SettingsStore.Instance.Settings.PropertyChanged += OnSettingsChanged;
        BackCommand = new RelayCommand(_ => GoBack(), _ => CanGoBack);
        ForwardCommand = new RelayCommand(_ => GoForward(), _ => CanGoForward);
        UpCommand = new RelayCommand(_ => GoUp(), _ => CanGoUp);
        RefreshCommand = new RelayCommand(_ => Refresh());
        OpenCommand = new RelayCommand(p => Open(p as FileItem));
        SortCommand = new RelayCommand(p => Sort(p as string ?? "Name"));
        OpenEntryCommand = new RelayCommand(OpenEntry);
    }

    /// <summary>Open a tile from a dashboard page (a recent/pinned item or a drive).</summary>
    private void OpenEntry(object? p)
    {
        switch (p)
        {
            case FileItem fi when fi.IsDirectory: _ = NavigateAsync(fi.FullPath, true); break;
            case FileItem fi: Open(fi); break;
            case DriveVM d: _ = OpenDriveAsync(d); break;
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
            OnPropertyChanged(nameof(CanGoUp));
            OnPropertyChanged(nameof(IsTrashView));
        }
    }

    /// <summary>The location session persistence should restore, including a folder that
    /// is still loading and has not replaced <see cref="CurrentPath"/> yet.</summary>
    public string RestoreTarget => _pendingRestoreTarget ?? CurrentPath;

    public bool CanDuplicate => true;

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

    /// <summary>Icon key for the tab header (home, drives or folder).</summary>
    public string TabIconKey => _page switch
    {
        PageKind.Home => "home",
        PageKind.Drives => "hard-drive",
        _ => IsTrashView ? "trash" : "folder",
    };

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

    private Avalonia.Media.Imaging.Bitmap? _previewImage;
    /// <summary>Last rendered snapshot shown when the user hovers this tab.</summary>
    public Avalonia.Media.Imaging.Bitmap? PreviewImage
    {
        get => _previewImage;
        set
        {
            if (Set(ref _previewImage, value)) OnPropertyChanged(nameof(HasPreviewImage));
        }
    }

    public bool HasPreviewImage => _previewImage is not null;

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

    /// <summary>True while the list shows recursive search results (adds a folder column).</summary>
    public bool IsSearchView
    {
        get => _isSearchView;
        private set => Set(ref _isSearchView, value);
    }

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
            OnPropertyChanged(nameof(IsWarning));
            _statusClearTimer?.Stop();
            if (!IsWarning) return;
            _statusClearTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            _statusClearTimer.Tick -= OnStatusClearTick;
            _statusClearTimer.Tick += OnStatusClearTick;
            _statusClearTimer.Start();
        }
    }

    public bool IsWarning => _status.StartsWith("⚠️", StringComparison.Ordinal);

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
    public ICommand OpenEntryCommand { get; }

    public bool CanGoBack => _histIndex > 0;
    public bool CanGoForward => _histIndex >= 0 && _histIndex < _history.Count - 1;
    public bool CanGoUp => IsFolderView && !string.IsNullOrEmpty(CurrentPath) && _fs.GetParent(CurrentPath) is not null;

    // ---- Navigation ---------------------------------------------------------
    public async Task NavigateAsync(string? path, bool pushHistory, IReadOnlyList<string>? revealPaths = null)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path)) return;
        int navigationVersion = unchecked(++_navigationVersion);
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        _navigationCts = new CancellationTokenSource();
        CancellationToken ct = _navigationCts.Token;
        _searchCts?.Cancel();

        // Special dashboard pages.
        if (path is HomeToken or DrivesToken)
        {
            SetPendingRestoreTarget(null);
            Busy = false;
            ShowPage(path == HomeToken ? PageKind.Home : PageKind.Drives, path, pushHistory);
            return;
        }

        try
        {
            path = revealPaths is null ? PathUtil.Normalize(PathUtil.ExpandTilde(path))
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            Status = $"⚠️ {ex.Message}";
            return;
        }

        // The trash folder exists only once something was trashed; the spec allows creating it.
        if (PathUtil.Same(path, FileSystemService.TrashToken))
        {
            try { Directory.CreateDirectory(path); } catch { }
        }

        SetPendingRestoreTarget(path);
        Busy = true;
        try
        {
            var revealed = revealPaths?.ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<FileItem> entries = await _fs.ReadDirectoryAsync(path, ct, revealed);
            if (_disposed || navigationVersion != _navigationVersion) return;

            _all.Clear();
            _all.AddRange(entries);
            _revealedPaths = revealed;
            Page = PageKind.Folder;
            CurrentPath = path;
            SetPendingRestoreTarget(null);
            Title = FolderDisplayName(path);
            OnPropertyChanged(nameof(TabIconKey));
            RecentsStore.Instance.Add(path, isDirectory: true);
            WatchFolder(path);

            // Restore this folder's remembered sort.
            var pref = SortStore.Instance.Get(path);
            SortKey = pref.Key;
            SortDir = pref.Dir;

            // Navigating clears any active search/filter.
            _searchCts?.Cancel();
            IsSearchView = false;
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
                SetPendingRestoreTarget(null);
                Status = ex switch
                {
                    TimeoutException => "⚠️ The network location took too long to respond. Check the connection, then try again.",
                    UnauthorizedAccessException => $"⚠️ You don't have permission to open {FolderDisplayName(path)}.",
                    _ => $"⚠️ {ex.Message}",
                };
                // A tab that never managed to load anything falls back to Home.
                if (string.IsNullOrEmpty(CurrentPath) && path != HomeToken)
                    ShowPage(PageKind.Home, HomeToken, pushHistory: true);
            }
        }
        finally
        {
            if (!_disposed && navigationVersion == _navigationVersion) Busy = false;
        }
    }

    private static string FolderDisplayName(string path)
    {
        if (PathUtil.Same(path, FileSystemService.TrashToken)) return "Trash";
        if (path == "/") return "File System";
        if (PathUtil.Same(path, XdgDirs.Home)) return "Home folder";
        return PathUtil.DisplayName(path);
    }

    private void PushHistory(string path)
    {
        if (_histIndex < _history.Count - 1)
            _history.RemoveRange(_histIndex + 1, _history.Count - _histIndex - 1);
        _history.Add(path);
        _histIndex = _history.Count - 1;
        NotifyNavigationStateChanged();
    }

    private void NotifyNavigationStateChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        RelayCommand.InvalidateRequerySuggested();
    }

    // ---- Dashboard pages ----------------------------------------------------
    private void ShowPage(PageKind kind, string token, bool pushHistory)
    {
        StopWatchingFolder();
        _searchCts?.Cancel();
        IsSearchView = false;
        _filter = string.Empty;
        OnPropertyChanged(nameof(Filter));

        Page = kind;
        CurrentPath = token;
        Title = kind == PageKind.Home ? "Home" : "Drives";
        OnPropertyChanged(nameof(TabIconKey));

        if (kind == PageKind.Home) LoadHome();
        else LoadDrivesCollection();

        if (pushHistory) PushHistory(token);
        else NotifyNavigationStateChanged();
        Status = kind == PageKind.Home ? "Home" : $"{Drives.Count} drive{(Drives.Count == 1 ? "" : "s")}";
        ContentsChanged?.Invoke();
    }

    private void LoadHome()
    {
        Recents.Clear();
        foreach (var e in RecentsStore.Instance.Items)
        {
            bool exists = e.IsDirectory ? Directory.Exists(e.Path) : File.Exists(e.Path);
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
            if (!Directory.Exists(p.Path)) continue;
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

    private static string NiceName(string path) => FolderDisplayName(path);

    public async Task OpenDriveAsync(DriveVM drive)
    {
        if (drive.Drive is { IsMounted: false } entry)
        {
            var result = await DriveActions.RunAsync(entry, DriveAction.Mount);
            if (result.Error is { } error) { Status = $"⚠️ {error}"; return; }
            if (result.MountPoint is { Length: > 0 } mountPoint)
                await NavigateAsync(mountPoint, true);
            return;
        }
        await NavigateAsync(drive.Path, true);
    }

    public void RefreshDrives()
    {
        if (_disposed || Page == PageKind.Folder) return;
        string previousCount = $"{Drives.Count} drive{(Drives.Count == 1 ? "" : "s")}";
        LoadDrivesCollection();
        if (Page == PageKind.Drives && Status == previousCount)
            Status = $"{Drives.Count} drive{(Drives.Count == 1 ? "" : "s")}";
        ContentsChanged?.Invoke();
    }

    private void LoadDrivesCollection()
    {
        _driveCountCts?.Cancel();
        _driveCountCts?.Dispose();
        _driveCountCts = new CancellationTokenSource();
        Drives.Clear();
        foreach (MountEntry mount in MountService.GetAvailableDrives())
        {
            var vm = new DriveVM
            {
                Label = mount.Label, Path = mount.MountPoint,
                Drive = mount.UDisksPath is null ? null : mount,
                IconKey = "hard-drive",
                UsageText = mount.IsMounted ? "Reading usage…" : "Not mounted",
                FreeText = mount.IsMounted ? "" : "Click to mount",
                SummaryText = mount.IsMounted ? "Reading usage…" : "Not mounted · Click to mount",
                TypeText = mount.IsNetwork ? "Network" : mount.IsRemovable ? "Removable" : "Fixed",
            };
            Drives.Add(vm);
            if (!mount.IsMounted) continue;
            _ = LoadDriveUsageAsync(vm, _driveCountCts.Token);
            if (SettingsStore.Instance.Settings.ShowDriveItemCounts && !mount.IsNetwork)
                _ = CountDriveAsync(vm, _driveCountCts.Token);
        }
    }

    private static async Task LoadDriveUsageAsync(DriveVM vm, CancellationToken token)
    {
        try
        {
            var (total, free, used) = await Task.Run(() =>
            {
                var drive = new DriveInfo(vm.Path);
                return (drive.TotalSize, drive.AvailableFreeSpace, drive.TotalSize - drive.TotalFreeSpace);
            }, token);
            if (token.IsCancellationRequested) return;
            vm.UsedPercent = total > 0 ? used * 100.0 / total : 0;
            vm.UsageText = $"{Gb(used)} of {Gb(total)} used";
            vm.FreeText = $"{Gb(free)} free";
            vm.SummaryText = $"{Gb(free)} free of {Gb(total)}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (token.IsCancellationRequested) return;
            vm.UsageText = vm.SummaryText = "Usage unavailable";
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
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                    RecurseSubdirectories = false,
                };
                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    string dir = stack.Pop();
                    try
                    {
                        foreach (var _ in Directory.EnumerateFiles(dir, "*", options))
                        {
                            if ((++n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                            if (n >= 5_000_000) return n;
                        }
                        foreach (var d in new DirectoryInfo(dir).EnumerateDirectories("*", options))
                        {
                            // Stay on this filesystem and never follow links (no /proc, no loops).
                            if (d.LinkTarget is null && UnixStat.SameDevice(vm.Path, d.FullName))
                                stack.Push(d.FullName);
                        }
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
        if (!CanGoBack) return;
        _histIndex--;
        NotifyNavigationStateChanged();
        _ = NavigateAsync(_history[_histIndex], false);
    }

    private void GoForward()
    {
        if (!CanGoForward) return;
        _histIndex++;
        NotifyNavigationStateChanged();
        _ = NavigateAsync(_history[_histIndex], false);
    }

    private void GoUp()
    {
        var parent = _fs.GetParent(CurrentPath);
        if (parent is not null) _ = NavigateAsync(parent, true);
    }

    private void Refresh() => _ = ReloadAsync();

    /// <summary>Re-read the current folder (used after a file operation).</summary>
    public async Task ReloadAsync(bool animate = true, bool automatic = false)
    {
        if (_disposed) return;
        if (string.IsNullOrEmpty(CurrentPath) && _pendingRestoreTarget is { } pending)
        {
            if (!automatic && !Busy) await NavigateAsync(pending, pushHistory: true);
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
            var entries = await _fs.ReadDirectoryAsync(path, _navigationCts?.Token ?? default, _revealedPaths);
            if (_disposed || navigationVersion != _navigationVersion || Page != PageKind.Folder ||
                !string.Equals(CurrentPath, path, StringComparison.Ordinal))
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
            {
                // The folder itself was deleted or unmounted: walk up to the nearest survivor.
                if (ex is DirectoryNotFoundException && _fs.GetParent(path) is { } parent)
                {
                    string survivor = parent;
                    while (!Directory.Exists(survivor) && _fs.GetParent(survivor) is { } up) survivor = up;
                    Busy = false;
                    await NavigateAsync(survivor, pushHistory: true);
                    return;
                }
                Status = ex is TimeoutException ? "⚠️ The network location took too long to respond. Try refreshing again." : $"⚠️ {ex.Message}";
            }
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

    private void WatchFolder(string path)
    {
        if (_disposed) return;
        if (!SettingsStore.Instance.Settings.AutoRefreshFolders)
        {
            StopWatchingFolder();
            return;
        }
        if (_folderWatcher is not null &&
            string.Equals(_folderWatcher.Path, path, StringComparison.Ordinal))
            return;

        StopWatchingFolder();
        try
        {
            _folderWatcher = CreateFolderWatcher(path);
        }
        catch
        {
            // The location can still be browsed and refreshed if inotify is unavailable
            // (for example when the per-user watch limit has been reached).
        }
    }

    private FileSystemWatcher CreateFolderWatcher(string path)
    {
        var watcher = new FileSystemWatcher(path);
        try
        {
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size;
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
            .ToArray();
        if (deleted.Length == 0) return;

        bool IsDeleted(string candidate) => deleted.Any(root => PathUtil.IsSameOrInside(root, candidate));

        _all.RemoveAll(item => IsDeleted(item.FullPath));
        foreach (var item in Items.Where(item => IsDeleted(item.FullPath)).ToList())
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
        Dispatcher.UIThread.Post(() =>
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
        Dispatcher.UIThread.Post(() =>
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
            _ = ReloadAsync(animate: false, automatic: true);
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
        Items.FirstOrDefault(i => string.Equals(i.FullPath, fullPath, StringComparison.Ordinal));

    public void Open(FileItem? item)
    {
        if (item is null) return;
        if (item.IsDirectory) _ = NavigateAsync(item.FullPath, true);
        else
        {
            try
            {
                _fs.OpenWithShell(item.FullPath);
                RecentsStore.Instance.Add(item.FullPath, isDirectory: false);
            }
            catch (Exception ex) { Status = $"⚠️ {ex.Message}"; }
        }
    }

    // ---- Sort + filter ------------------------------------------------------
    private void Sort(string key)
    {
        if (SortKey == key) SortDir = -SortDir;
        else { SortKey = key; SortDir = 1; }
        SortStore.Instance.Set(CurrentPath, new SortPref(SortKey, SortDir));

        // Re-sort whatever is currently shown (folder contents or search results).
        if (_isSearchView) PopulateSorted(_searchResults);
        else ApplyView();
    }

    /// <summary>Set an explicit sort (from the sort menu) instead of toggling.</summary>
    public void SetSort(string key, int dir)
    {
        SortKey = key;
        SortDir = dir < 0 ? -1 : 1;
        SortStore.Instance.Set(CurrentPath, new SortPref(SortKey, SortDir));
        if (_isSearchView) PopulateSorted(_searchResults);
        else ApplyView();
    }

    /// <summary>Decide between local filtering and a recursive subfolder search.</summary>
    private void OnQueryChanged()
    {
        _searchCts?.Cancel();
        if (Recursive && !string.IsNullOrWhiteSpace(Filter) && IsFolderView)
            _ = RunSearchAsync(Filter);
        else
        {
            IsSearchView = false;
            ApplyView();
        }
    }

    private async Task RunSearchAsync(string query)
    {
        if (_disposed) return;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        CancellationToken token = cts.Token;
        IsSearchView = true;
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
            _ => NaturalCompare(a.Name, b.Name),
        };
        if (r == 0 && _sortKey != "Name") r = NaturalCompare(a.Name, b.Name);
        return r * _sortDir;
    }

    /// <summary>Case-insensitive "natural" order so "file2" sorts before "file10", like Dolphin.</summary>
    private static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                int c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = string.Compare(a, i, b, j, 1, StringComparison.CurrentCultureIgnoreCase);
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        int tail = (a.Length - i).CompareTo(b.Length - j);
        return tail != 0 ? tail : string.CompareOrdinal(a, b);
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
            view = view.Where(i => i.Name.Contains(Filter, StringComparison.CurrentCultureIgnoreCase));

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
        if (!SettingsStore.Instance.Settings.CalculateFolderSizes || Page != PageKind.Folder) return;

        var dirs = Items.Where(i => i.IsDirectory && !i.IsSymlink && !i.FolderSizeKnown).ToList();
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
                Dispatcher.UIThread.Post(() =>
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
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) return total;
            string dir = stack.Pop();
            try
            {
                foreach (FileSystemInfo entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    if (entry.LinkTarget is not null) continue;   // never follow links
                    if (entry is FileInfo f) total += f.Length;
                    else stack.Push(entry.FullName);
                }
            }
            catch { /* unreadable folder — skip */ }
        }
        return total;
    }
}
