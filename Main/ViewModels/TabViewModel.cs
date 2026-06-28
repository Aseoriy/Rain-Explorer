using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows.Input;
using RainExplorer.Helpers;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.ViewModels;

/// <summary>
/// One browser tab: its own current folder, navigation history, sort, and filter.
/// All per-tab state lives here so tabs are fully independent.
/// </summary>
public enum PageKind { Folder, Home, Drives }

public sealed class TabViewModel : ObservableObject
{
    /// <summary>Sentinel paths for the special dashboard pages.</summary>
    public const string HomeToken = "Home";
    public const string DrivesToken = "Drives";

    private readonly FileSystemService _fs;
    private readonly List<FileItem> _all = new();      // unfiltered contents of cwd
    private readonly List<string> _history = new();
    private int _histIndex = -1;
    private CancellationTokenSource? _driveCountCts;

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
                OnPropertyChanged(nameof(IsHomeView));
                OnPropertyChanged(nameof(IsDrivesView));
            }
        }
    }
    public bool IsFolderView => _page == PageKind.Folder;
    public bool IsHomeView => _page == PageKind.Home;
    public bool IsDrivesView => _page == PageKind.Drives;

    public bool HasNoRecents => Recents.Count == 0;

    /// <summary>Raised after a successful navigation, so the view can animate.</summary>
    public event Action? ContentsChanged;

    public TabViewModel(FileSystemService fs)
    {
        _fs = fs;
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
    public string CurrentPath { get => _currentPath; set => Set(ref _currentPath, value); }

    private string _title = "New Tab";
    public string Title { get => _title; private set => Set(ref _title, value); }

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
    public string Status { get => _status; set => Set(ref _status, value); }

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

    public bool CanGoBack => _histIndex > 0;
    public bool CanGoForward => _histIndex >= 0 && _histIndex < _history.Count - 1;
    public bool CanGoUp => !string.IsNullOrEmpty(CurrentPath) && _fs.GetParent(CurrentPath) is not null;

    // ---- Navigation ---------------------------------------------------------
    public async Task NavigateAsync(string? path, bool pushHistory)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = path.Trim();

        // Special dashboard pages.
        if (path is HomeToken or DrivesToken)
        {
            ShowPage(path == HomeToken ? PageKind.Home : PageKind.Drives, path, pushHistory);
            return;
        }

        if (!Directory.Exists(path))
        {
            Status = $"⚠️ Not a folder: {path}";
            return;
        }

        Busy = true;
        try
        {
            var entries = await _fs.ReadDirectoryAsync(path);
            _all.Clear();
            _all.AddRange(entries);
            Page = PageKind.Folder;
            CurrentPath = path;
            Title = FolderDisplayName(path);
            RecentsStore.Instance.Add(path, isDirectory: true);

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
        catch (Exception ex)
        {
            Status = $"⚠️ {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private static string FolderDisplayName(string path)
    {
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;   // root like "C:\" has no file name
    }

    private void PushHistory(string path)
    {
        if (_histIndex < _history.Count - 1)
            _history.RemoveRange(_histIndex + 1, _history.Count - _histIndex - 1);
        _history.Add(path);
        _histIndex = _history.Count - 1;
    }

    // ---- Dashboard pages ----------------------------------------------------
    private void ShowPage(PageKind kind, string token, bool pushHistory)
    {
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

    private static string NiceName(string path)
    {
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
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
        if (!CanGoBack) return;
        _histIndex--;
        _ = NavigateAsync(_history[_histIndex], false);
    }

    private void GoForward()
    {
        if (!CanGoForward) return;
        _histIndex++;
        _ = NavigateAsync(_history[_histIndex], false);
    }

    private void GoUp()
    {
        var parent = _fs.GetParent(CurrentPath);
        if (parent is not null) _ = NavigateAsync(parent, true);
    }

    private void Refresh() => _ = NavigateAsync(CurrentPath, false);

    /// <summary>Re-read the current folder (used after a file operation).</summary>
    public Task ReloadAsync() => NavigateAsync(CurrentPath, false);

    /// <summary>Find a currently-shown item by full path (e.g. a just-created folder).</summary>
    public FileItem? Find(string fullPath) =>
        Items.FirstOrDefault(i => string.Equals(i.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

    public void Open(FileItem? item)
    {
        if (item is null) return;
        if (item.IsDirectory) _ = NavigateAsync(item.FullPath, true);
        else
        {
            try { _fs.OpenWithShell(item.FullPath); RecentsStore.Instance.Add(item.FullPath, isDirectory: false); }
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

    /// <summary>Decide between local filtering and a recursive subfolder search.</summary>
    private void OnQueryChanged()
    {
        _searchCts?.Cancel();
        if (Recursive && !string.IsNullOrWhiteSpace(Filter))
            _ = RunSearchAsync(Filter);
        else
        {
            _isSearchView = false;
            ApplyView();
        }
    }

    private async Task RunSearchAsync(string query)
    {
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _isSearchView = true;
        Status = "Searching…";
        try
        {
            var results = await _fs.SearchAsync(CurrentPath, query, cts.Token);
            if (cts.Token.IsCancellationRequested) return;
            _searchResults.Clear();
            _searchResults.AddRange(results);
            PopulateSorted(_searchResults);
            Status = $"{results.Count} result{(results.Count == 1 ? "" : "s")} for \"{query}\""
                     + (results.Count >= 10_000 ? " (showing first 10,000)" : "");
        }
        catch (OperationCanceledException) { /* superseded by a newer query */ }
        catch (Exception ex) { Status = $"⚠️ {ex.Message}"; }
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
        if (!SettingsStore.Instance.Settings.CalculateFolderSizes || Page != PageKind.Folder) return;

        var cts = new CancellationTokenSource();
        _sizeCts = cts;
        var dirs = Items.Where(i => i.IsDirectory && !i.FolderSizeKnown).ToList();
        if (dirs.Count == 0) return;

        _ = Task.Run(() =>
        {
            foreach (var d in dirs)
            {
                if (cts.IsCancellationRequested) return;
                long size = DirSize(d.FullPath, cts.Token);
                if (cts.IsCancellationRequested) return;
                var item = d;
                App.Current?.Dispatcher.BeginInvoke(() => item.SetFolderSize(size));
            }
        }, cts.Token);
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
