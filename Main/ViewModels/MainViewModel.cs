using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RainExplorer.Helpers;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.ViewModels;

/// <summary>
/// Window-level state: one or two browsing panes, the active pane, and the
/// shared sidebar. Tabs live in <see cref="PaneViewModel"/>; per-folder
/// navigation lives in <see cref="TabViewModel"/>.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly FileSystemService _fs = new();
    private readonly DispatcherTimer _sessionSaveTimer;
    private bool _restoringSession;
    private bool _disposed;

    public ObservableCollection<SidebarNode> SidebarNodes { get; } = new();

    /// <summary>Fires when the last tab of the last pane is closed.</summary>
    public event Action? CloseWindowRequested;

    public static string HomePath => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Where new tabs open: the configured default folder, or Home.</summary>
    public static string StartFolder
    {
        get
        {
            string f = SettingsStore.Instance.Settings.DefaultFolder;
            return !string.IsNullOrWhiteSpace(f) && Directory.Exists(f) ? f : HomePath;
        }
    }

    /// <summary>New-tab target: the configured default folder, else the Home dashboard page.</summary>
    public static string StartTarget
    {
        get
        {
            string f = SettingsStore.Instance.Settings.DefaultFolder;
            return !string.IsNullOrWhiteSpace(f) && Directory.Exists(f) ? f : TabViewModel.HomeToken;
        }
    }

    public MainViewModel()
    {
        _sessionSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(650),
        };
        _sessionSaveTimer.Tick += OnSessionSaveTimerTick;
        LeftPane = CreatePane();
        _activePane = LeftPane;
        LeftPane.IsActive = true;

        ToggleSplitCommand = new RelayCommand(_ => ToggleSplit());
        OpenSettingsCommand = new RelayCommand(_ => IsSettingsOpen = true);
        CloseSettingsCommand = new RelayCommand(_ => IsSettingsOpen = false);
        NewTabCommand = new RelayCommand(_ => ActivePane.NewTab(activate: true));
        CloseTabCommand = new RelayCommand(_ => ActivePane.CloseTab(ActivePane.SelectedTab));
        NextTabCommand = new RelayCommand(_ => ActivePane.CycleTab(+1));
        PrevTabCommand = new RelayCommand(_ => ActivePane.CycleTab(-1));
        SeedQuickAccessDefaults();
        NormalizeSidebarSections();
        RebuildSidebar();

        // Toggling "show hidden files" re-reads every open tab; pin changes rebuild the
        // sidebar and refresh any Home dashboards that are already open.
        SettingsStore.Instance.Settings.PropertyChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.ShowHiddenFiles)
            or nameof(AppSettings.FoldersFirst) or nameof(AppSettings.SizeFormat)
            or nameof(AppSettings.ShowFileExtensions) or nameof(AppSettings.CalculateFolderSizes))
            ReloadAllTabs();
        if (e.PropertyName is nameof(AppSettings.Pinned)
            or nameof(AppSettings.CustomGroups)
            or nameof(AppSettings.SidebarOrder)
            or nameof(AppSettings.ShowQuickAccessInSidebar)
            or nameof(AppSettings.ShowDrivesInSidebar)
            or nameof(AppSettings.QuickAccessName)
            or nameof(AppSettings.DrivesName)
            or nameof(AppSettings.QuickAccessCollapsed)
            or nameof(AppSettings.DrivesCollapsed))
            RebuildSidebar();
        if (e.PropertyName == nameof(AppSettings.Pinned))
            RefreshHomeTabs();
        if (e.PropertyName == nameof(AppSettings.PreserveOpenTabsOnClose)
            && !SettingsStore.Instance.Settings.PreserveOpenTabsOnClose)
        {
            _sessionSaveTimer.Stop();
            SettingsStore.Instance.Settings.SavedSession = null;
            SettingsStore.Instance.Flush();
        }
        else if (e.PropertyName == nameof(AppSettings.PreserveOpenTabsOnClose))
        {
            ScheduleSessionSave();
        }
    }

    private void OnWorkspaceStateChanged(PaneViewModel pane) => ScheduleSessionSave();

    private void ScheduleSessionSave()
    {
        if (_disposed || _restoringSession
            || !SettingsStore.Instance.Settings.PreserveOpenTabsOnClose)
            return;
        _sessionSaveTimer.Stop();
        _sessionSaveTimer.Start();
    }

    private void OnSessionSaveTimerTick(object? sender, EventArgs e)
    {
        _sessionSaveTimer.Stop();
        if (_disposed || _restoringSession) return;
        int visibleWindowCount = Application.Current?.Windows.OfType<MainWindow>()
            .Count(window => window.IsVisible) ?? 0;
        // Each window has its own view model, while the persisted format describes one
        // window. Let a lone window save live; multiple windows save on final close.
        if (visibleWindowCount <= 1) SaveSession();
    }

    private void RebuildSidebar()
    {
        foreach (SidebarNode node in SidebarNodes)
            node.TreeChanged -= SyncSidebarSelection;
        SidebarNodes.Clear();
        foreach (var s in _fs.GetSidebarNodes())
        {
            s.TreeChanged += SyncSidebarSelection;
            SidebarNodes.Add(s);
        }
        SyncSidebarSelection();   // a rebuild drops selection state — restore it for the active tab
    }

    // ===================== Sidebar selection sync =====================
    // Keep the highlighted sidebar row in step with where the active tab actually is, no
    // matter how it got there (file-list click, breadcrumb, Back/Up, a new tab). Without
    // this the sidebar could keep "All drives" lit after you'd opened C:, so clicking
    // "All drives" again did nothing (it was still the selected item).

    private TabViewModel? _syncedTab;
    private void RefreshActiveTabHook()
    {
        var tab = ActivePane?.SelectedTab;
        if (!ReferenceEquals(tab, _syncedTab))
        {
            if (_syncedTab is not null) _syncedTab.PropertyChanged -= OnActiveTabPropChanged;
            _syncedTab = tab;
            if (_syncedTab is not null) _syncedTab.PropertyChanged += OnActiveTabPropChanged;
        }
        SyncSidebarSelection();
    }

    private void OnActiveTabPropChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.CurrentPath) or nameof(TabViewModel.Page))
            SyncSidebarSelection();
    }

    private void SyncSidebarSelection()
    {
        string? target = ActiveSidebarTarget();
        foreach (var node in SidebarNodes) ApplyNodeSelection(node, target);
    }

    /// <summary>Programmatic selection changes already point at the active tab and must
    /// not navigate it again. Comparing targets avoids a delayed global suppression flag
    /// that could accidentally swallow a real click.</summary>
    public bool IsActiveSidebarTarget(SidebarNode node)
    {
        string? target = ActiveSidebarTarget();
        return target is not null && node.IsSelectable && PathMatches(node.Path, target);
    }

    private string? ActiveSidebarTarget()
    {
        var tab = ActivePane?.SelectedTab;
        return tab?.Page switch
        {
            PageKind.Home => TabViewModel.HomeToken,
            PageKind.Drives => TabViewModel.DrivesToken,
            PageKind.Folder => tab.CurrentPath,
            _ => null,
        };
    }

    private static void ApplyNodeSelection(SidebarNode node, string? target)
    {
        node.IsSelected = target is not null && node.IsSelectable && PathMatches(node.Path, target);
        foreach (var c in node.Children) ApplyNodeSelection(c, target);
    }

    private static bool PathMatches(string a, string b) =>
        string.Equals(a?.TrimEnd('\\', '/'), b?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// On first run, seed the user's pin list with the standard Quick Access places
    /// (Desktop, Documents, Downloads, Pictures, Music) so they appear — but as real,
    /// removable pins. Once seeded, anything the user unpins stays unpinned.
    /// </summary>
    private static void SeedQuickAccessDefaults()
    {
        var store = SettingsStore.Instance;
        var settings = store.Settings;
        // Never seed when an existing settings file failed to parse — that path also
        // arrives here with QuickAccessSeeded==false, and seeding would overwrite the
        // user's (recoverable) pins with the defaults.
        if (store.LoadFailed) return;
        if (settings.QuickAccessSeeded) return;
        settings.QuickAccessSeeded = true;

        foreach (var d in FileSystemService.DefaultQuickAccess())
        {
            if (settings.Pinned.Any(p => string.Equals(p.Path, d.Path, StringComparison.OrdinalIgnoreCase)))
                continue;
            settings.Pinned.Add(d);
        }
        // Fire a Pinned change so the store persists both the new pins and the seed flag.
        // (Our own Pinned->RebuildSidebar handler isn't attached yet, so this won't double-build.)
        settings.NotifyPinnedChanged();
    }

    /// <summary>Navigate the active tab to a sidebar node's path/token.</summary>
    public void NavigateTo(string path)
    {
        if (!string.IsNullOrEmpty(path)) _ = ActivePane.SelectedTab?.NavigateAsync(path, true);
    }

    /// <summary>Pin the active tab's current folder (if it's a real directory).</summary>
    public void PinCurrentFolder()
    {
        string? p = ActivePane.SelectedTab?.CurrentPath;
        if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) Pin(p);
    }

    // ---- Sidebar list helpers (default "quick" + custom lists) -------------

    /// <summary>The pin list for a sidebar group key ("quick"/empty = default, "custom:&lt;id&gt;" = custom).</summary>
    public sealed record SidebarPinTarget(string Key, string Name);

    private static string CustomKey(SidebarGroup group) => "custom:" + group.Id;

    /// <summary>Upgrade legacy/index-based data and ensure every section appears once in the persisted order.</summary>
    private static void NormalizeSidebarSections()
    {
        var s = SettingsStore.Instance.Settings;
        bool changed = false;
        foreach (var group in s.CustomGroups)
        {
            if (!string.IsNullOrWhiteSpace(group.Id)) continue;
            group.Id = Guid.NewGuid().ToString("N");
            changed = true;
        }

        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "quick", "drives" };
        foreach (var group in s.CustomGroups) valid.Add(CustomKey(group));

        var normalized = new List<string>();
        foreach (string raw in s.SidebarOrder)
        {
            string key = raw;
            if (raw.StartsWith("custom:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(raw.AsSpan(7), out int legacy)
                && legacy >= 0 && legacy < s.CustomGroups.Count)
                key = CustomKey(s.CustomGroups[legacy]);
            if (valid.Contains(key) && !normalized.Contains(key, StringComparer.OrdinalIgnoreCase))
                normalized.Add(key);
        }

        if (!normalized.Contains("quick", StringComparer.OrdinalIgnoreCase)) normalized.Insert(0, "quick");
        foreach (var group in s.CustomGroups)
        {
            string key = CustomKey(group);
            if (normalized.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            int drives = normalized.FindIndex(k => k.Equals("drives", StringComparison.OrdinalIgnoreCase));
            normalized.Insert(drives < 0 ? normalized.Count : drives, key);
        }
        if (!normalized.Contains("drives", StringComparer.OrdinalIgnoreCase)) normalized.Add("drives");

        if (!s.SidebarOrder.SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase))
        {
            s.SidebarOrder = normalized;
            changed = true;
        }
        if (changed) s.NotifyPinnedChanged();
    }

    public static List<PinnedItem>? GroupItems(string key)
    {
        var s = SettingsStore.Instance.Settings;
        if (string.IsNullOrEmpty(key) || key == "quick") return s.Pinned;
        return TryCustomGroup(key, out var group) ? group.Items : null;
    }

    private static bool TryCustomGroup(string key, out SidebarGroup group)
    {
        group = null!;
        if (key is null || !key.StartsWith("custom:", StringComparison.OrdinalIgnoreCase)) return false;
        string id = key[7..];
        group = SettingsStore.Instance.Settings.CustomGroups
            .FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase))!;
        return group is not null;
    }

    /// <summary>Find a pin by path across the default list and every custom list.</summary>
    private static PinnedItem? FindPin(string path)
    {
        var s = SettingsStore.Instance.Settings;
        var hit = s.Pinned.FirstOrDefault(p => Same(p.Path, path));
        if (hit is not null) return hit;
        foreach (var g in s.CustomGroups)
            if (g.Items.FirstOrDefault(p => Same(p.Path, path)) is { } h) return h;
        return null;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Collapse/expand a sidebar section by its group key.</summary>
    public static void ToggleGroupCollapsed(string key)
    {
        var s = SettingsStore.Instance.Settings;
        if (key == "quick") s.QuickAccessCollapsed = !s.QuickAccessCollapsed;
        else if (key == "drives") s.DrivesCollapsed = !s.DrivesCollapsed;
        else if (TryCustomGroup(key, out var group))
        {
            group.Collapsed = !group.Collapsed;
            s.NotifyPinnedChanged();
        }
    }

    /// <summary>Create a new empty custom list.</summary>
    public static void AddCustomGroup(string? name = null)
    {
        var s = SettingsStore.Instance.Settings;
        var group = new SidebarGroup
        {
            Name = string.IsNullOrWhiteSpace(name) ? UniqueGroupName(s) : name!,
        };
        s.CustomGroups.Add(group);
        NormalizeSidebarSections();
        s.NotifyPinnedChanged();
    }

    private static string UniqueGroupName(AppSettings s)
    {
        int n = 2;
        string name = "Quick access 2";
        while (s.CustomGroups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"Quick access {++n}";
        return name;
    }

    public static void RenameGroup(string key, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        var s = SettingsStore.Instance.Settings;
        string name = newName.Trim();
        if (key == "quick") s.QuickAccessName = name;
        else if (key == "drives") s.DrivesName = name;
        else if (TryCustomGroup(key, out var group))
        {
            group.Name = name;
            s.NotifyPinnedChanged();
        }
    }

    public static void DeleteGroup(string key)
    {
        var s = SettingsStore.Instance.Settings;
        if (key == "quick") { s.ShowQuickAccessInSidebar = false; return; }
        if (key == "drives") { s.ShowDrivesInSidebar = false; return; }
        if (!TryCustomGroup(key, out var group)) return;
        s.CustomGroups.Remove(group);
        s.SidebarOrder.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        s.NotifyPinnedChanged();
    }

    /// <summary>Move an entire sidebar section before or after another one.</summary>
    public static void ReorderGroup(string fromKey, string toKey, bool after)
    {
        if (Same(fromKey, toKey)) return;
        var s = SettingsStore.Instance.Settings;
        NormalizeSidebarSections();
        int from = s.SidebarOrder.FindIndex(k => Same(k, fromKey));
        int to = s.SidebarOrder.FindIndex(k => Same(k, toKey));
        if (from < 0 || to < 0) return;
        string moved = s.SidebarOrder[from];
        s.SidebarOrder.RemoveAt(from);
        to = s.SidebarOrder.FindIndex(k => Same(k, toKey));
        if (after) to++;
        s.SidebarOrder.Insert(Math.Clamp(to, 0, s.SidebarOrder.Count), moved);
        s.NotifyPinnedChanged();
    }

    /// <summary>Reorder a pin within its list, dropping it before/after the target pin.</summary>
    public static void ReorderPin(string key, string fromPath, string toPath, bool after)
    {
        var items = GroupItems(key);
        if (items is null || Same(fromPath, toPath)) return;
        var moved = items.FirstOrDefault(p => Same(p.Path, fromPath));
        if (moved is null) return;

        items.Remove(moved);
        int to = items.FindIndex(p => Same(p.Path, toPath));
        if (to < 0) to = items.Count;
        else if (after) to++;
        to = Math.Clamp(to, 0, items.Count);
        items.Insert(to, moved);
        SettingsStore.Instance.Settings.NotifyPinnedChanged();
    }

    /// <summary>Change a pinned item's custom icon (any list).</summary>
    public static void SetPinnedIcon(string key, string path, string iconKey)
    {
        var pin = GroupItems(key)?.FirstOrDefault(p => Same(p.Path, path));
        if (pin is null || string.Equals(pin.IconKey, iconKey, StringComparison.Ordinal)) return;
        pin.IconKey = iconKey;
        SettingsStore.Instance.Settings.NotifyPinnedChanged();
    }

    /// <summary>Rename a pinned item's display label (any list).</summary>
    public static void RenamePinned(string key, string path, string newName)
    {
        var pin = GroupItems(key)?.FirstOrDefault(p => Same(p.Path, path));
        if (pin is null || string.IsNullOrWhiteSpace(newName) ||
            string.Equals(pin.Name, newName, StringComparison.Ordinal)) return;
        pin.Name = newName;
        SettingsStore.Instance.Settings.NotifyPinnedChanged();
    }

    /// <summary>Pin a folder to the default Quick Access list (no-op if already pinned).</summary>
    public static void Pin(string path, string? name = null, string iconKey = "folder") =>
        PinTo("quick", path, name, iconKey);

    /// <summary>Pin a folder to a specific sidebar list (no-op if already pinned there).</summary>
    public static void PinTo(string key, string path, string? name = null, string iconKey = "folder")
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        var items = GroupItems(key);
        if (items is null || items.Any(p => Same(p.Path, path))) return;
        items.Add(new PinnedItem
        {
            Path = path,
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) : name!,
            IconKey = iconKey,
        });
        SettingsStore.Instance.Settings.NotifyPinnedChanged();
    }

    /// <summary>Remove a pin from a specific list (falls back to searching all lists).</summary>
    public static void UnpinFrom(string key, string path)
    {
        var items = GroupItems(key);
        int removed = items?.RemoveAll(p => Same(p.Path, path)) ?? 0;
        if (removed == 0)   // group changed/unknown — remove wherever it is
        {
            var s = SettingsStore.Instance.Settings;
            removed = s.Pinned.RemoveAll(p => Same(p.Path, path));
            foreach (var g in s.CustomGroups) removed += g.Items.RemoveAll(p => Same(p.Path, path));
        }
        if (removed > 0) SettingsStore.Instance.Settings.NotifyPinnedChanged();
    }

    /// <summary>Remove a pin from the default Quick Access list (by path).</summary>
    public static void Unpin(string path) => UnpinFrom("quick", path);

    public static bool IsPinned(string path) => FindPin(path) is not null;

    public static bool IsPinnedTo(string key, string path) =>
        GroupItems(key)?.Any(p => Same(p.Path, path)) == true;

    /// <summary>Visible pin-capable sections, in their current sidebar order.</summary>
    public static IReadOnlyList<SidebarPinTarget> PinTargets()
    {
        NormalizeSidebarSections();
        var s = SettingsStore.Instance.Settings;
        var result = new List<SidebarPinTarget>();
        foreach (string key in s.SidebarOrder)
        {
            if (key == "quick" && s.ShowQuickAccessInSidebar)
                result.Add(new SidebarPinTarget(key, s.QuickAccessName));
            else if (TryCustomGroup(key, out var group))
                result.Add(new SidebarPinTarget(key, group.Name));
        }
        return result;
    }

    /// <summary>Move a pin between lists (used when a pinned row is dropped on another header).</summary>
    public static void MovePin(string fromKey, string toKey, string path)
    {
        if (Same(fromKey, toKey)) return;
        var source = GroupItems(fromKey);
        var target = GroupItems(toKey);
        var pin = source?.FirstOrDefault(p => Same(p.Path, path));
        if (pin is null || target is null) return;
        source!.Remove(pin);
        if (!target.Any(p => Same(p.Path, path))) target.Add(pin);
        SettingsStore.Instance.Settings.NotifyPinnedChanged();
    }

    private void ReloadAllTabs(bool afterKnownOperation = false)
    {
        foreach (var t in LeftPane.Tabs)
            _ = afterKnownOperation ? t.ReloadAfterOperationAsync() : t.ReloadAsync();
        if (RightPane is not null)
            foreach (var t in RightPane.Tabs)
                _ = afterKnownOperation ? t.ReloadAfterOperationAsync() : t.ReloadAsync();
    }

    private void RefreshHomeTabs()
    {
        foreach (var tab in LeftPane.Tabs)
            tab.RefreshHome();
        if (RightPane is not null)
        {
            foreach (var tab in RightPane.Tabs)
                tab.RefreshHome();
        }
    }

    /// <summary>Re-read every open tab (e.g. after an undo/redo touched the filesystem).</summary>
    public void RefreshAll(bool afterKnownOperation = false) => ReloadAllTabs(afterKnownOperation);

    /// <summary>Release settings and tab resources when their window closes.</summary>
    public void Dispose()
    {
        _disposed = true;
        _sessionSaveTimer.Stop();
        _sessionSaveTimer.Tick -= OnSessionSaveTimerTick;
        SettingsStore.Instance.Settings.PropertyChanged -= OnSettingsChanged;
        if (_syncedTab is not null) _syncedTab.PropertyChanged -= OnActiveTabPropChanged;
        foreach (SidebarNode node in SidebarNodes)
            node.TreeChanged -= SyncSidebarSelection;
        LeftPane.Dispose();
        RightPane?.Dispose();
    }

    public PaneViewModel LeftPane { get; }

    private PaneViewModel? _rightPane;
    public PaneViewModel? RightPane { get => _rightPane; private set => Set(ref _rightPane, value); }

    public bool IsSplit => RightPane is not null;

    private PaneViewModel _activePane;
    public PaneViewModel ActivePane
    {
        get => _activePane;
        private set
        {
            if (Set(ref _activePane, value))
            {
                LeftPane.IsActive = _activePane == LeftPane;
                if (RightPane is not null) RightPane.IsActive = _activePane == RightPane;
                RefreshActiveTabHook();
            }
        }
    }

    private bool _isSettingsOpen;
    /// <summary>Whether the full-page settings overlay is showing.</summary>
    public bool IsSettingsOpen { get => _isSettingsOpen; set => Set(ref _isSettingsOpen, value); }

    public ICommand ToggleSplitCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand CloseSettingsCommand { get; }
    public ICommand NewTabCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand NextTabCommand { get; }
    public ICommand PrevTabCommand { get; }

    private PaneViewModel CreatePane()
    {
        var pane = new PaneViewModel(_fs);
        pane.RequestActivate += p => ActivePane = p;
        pane.EmptyRequested += OnPaneEmpty;
        pane.ProjectStateChanged += SaveActiveProject;
        pane.WorkspaceStateChanged += OnWorkspaceStateChanged;
        // When the active pane switches tabs, re-sync the sidebar to the new tab's location.
        pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PaneViewModel.SelectedTab) && ReferenceEquals(pane, ActivePane))
                RefreshActiveTabHook();
        };
        return pane;
    }

    public PaneViewModel EnsureFirstTab()
    {
        if (LeftPane.Tabs.Count == 0)
        {
            if (App.LaunchFolder is null && RestoreSavedSession()) return ActivePane;
            string start = App.LaunchFolder is { } f && Directory.Exists(f) ? f : StartTarget;
            LeftPane.NewTab(start, activate: true);
        }
        return LeftPane;
    }

    private bool RestoreSavedSession()
    {
        var settings = SettingsStore.Instance.Settings;
        var session = settings.PreserveOpenTabsOnClose ? settings.SavedSession : null;
        if (session is null) return false;

        _restoringSession = true;
        try
        {
            RestorePane(LeftPane, session.LeftTabs, session.LeftPinnedTabs,
                session.LeftTabGroups, session.LeftTabGroupNames, session.LeftSelectedIndex);
            LeftPane.ActiveProjectId = session.LeftProjectId;
            if (LeftPane.Tabs.Count == 0) return false;

            if (session.RightTabs.Any(IsRestorableTarget))
            {
                var right = CreatePane();
                RestorePane(right, session.RightTabs, session.RightPinnedTabs,
                    session.RightTabGroups, session.RightTabGroupNames, session.RightSelectedIndex);
                right.ActiveProjectId = session.RightProjectId;
                if (right.Tabs.Count > 0)
                {
                    RightPane = right;
                    OnPropertyChanged(nameof(IsSplit));
                }
                else
                {
                    right.Dispose();
                }
            }

            ActivePane = session.ActivePaneIsRight && RightPane is not null ? RightPane : LeftPane;
            return true;
        }
        finally
        {
            _restoringSession = false;
        }
    }

    private static void RestorePane(PaneViewModel pane, IReadOnlyList<string> paths,
        IReadOnlyList<bool>? pinnedTabs, IReadOnlyList<string?>? tabGroups,
        IReadOnlyList<string?>? tabGroupNames, int selectedIndex)
    {
        for (int index = 0; index < paths.Count; index++)
        {
            string path = paths[index];
            if (!IsRestorableTarget(path)) continue;
            bool pinned = pinnedTabs is not null && index < pinnedTabs.Count && pinnedTabs[index];
            string? groupId = tabGroups is not null && index < tabGroups.Count ? tabGroups[index] : null;
            string? groupName = tabGroupNames is not null && index < tabGroupNames.Count ? tabGroupNames[index] : null;
            pane.NewTab(path, activate: false, pinned: pinned, groupId: groupId, groupName: groupName);
        }
        if (pane.Tabs.Count > 0)
            pane.SelectedTab = pane.Tabs[Math.Clamp(selectedIndex, 0, pane.Tabs.Count - 1)];
    }

    private static bool IsRestorableTarget(string? path) =>
        path is TabViewModel.HomeToken or TabViewModel.DrivesToken
        || (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path));

    /// <summary>Persist the current pane/tab snapshot immediately before the window closes.</summary>
    public void SaveSession()
    {
        _sessionSaveTimer.Stop();
        var settings = SettingsStore.Instance.Settings;

        SaveActiveProject(LeftPane);
        SaveActiveProject(RightPane);
        if (!settings.PreserveOpenTabsOnClose)
        {
            SettingsStore.Instance.Flush();
            return;
        }

        var left = CapturePane(LeftPane);
        var right = CapturePane(RightPane);

        settings.SavedSession = new ExplorerSession
        {
            LeftTabs = left.Paths,
            LeftPinnedTabs = left.Pinned,
            LeftTabGroups = left.Groups,
            LeftTabGroupNames = left.GroupNames,
            LeftSelectedIndex = left.SelectedIndex,
            LeftProjectId = LeftPane.ActiveProjectId,
            RightTabs = right.Paths,
            RightPinnedTabs = right.Pinned,
            RightTabGroups = right.Groups,
            RightTabGroupNames = right.GroupNames,
            RightSelectedIndex = right.SelectedIndex,
            RightProjectId = RightPane?.ActiveProjectId,
            ActivePaneIsRight = RightPane is not null && ReferenceEquals(ActivePane, RightPane),
        };
        SettingsStore.Instance.Flush();
    }

    private static void SaveActiveProject(PaneViewModel? pane)
    {
        if (pane?.ActiveProjectId is not { Length: > 0 } id) return;
        var settings = SettingsStore.Instance.Settings;
        var project = settings.TabProjects.FirstOrDefault(p => p.Id == id);
        if (project is null) return;
        var snapshot = pane.CaptureProject();
        project.Tabs = snapshot.Tabs;
        project.SelectedIndex = snapshot.SelectedIndex;
        settings.NotifyTabProjectsChanged();
    }

    private static (List<string> Paths, List<bool> Pinned, List<string?> Groups,
        List<string?> GroupNames, int SelectedIndex) CapturePane(PaneViewModel? pane)
    {
        var paths = new List<string>();
        var pinned = new List<bool>();
        var groups = new List<string?>();
        var groupNames = new List<string?>();
        int selected = 0;
        if (pane is null) return (paths, pinned, groups, groupNames, selected);
        foreach (var tab in pane.Tabs)
        {
            string target = tab.RestoreTarget;
            if (!IsRestorableTarget(target)) continue;
            if (ReferenceEquals(tab, pane.SelectedTab)) selected = paths.Count;
            paths.Add(target);
            pinned.Add(tab.IsPinned);
            groups.Add(tab.GroupId);
            groupNames.Add(tab.IsGrouped ? tab.GroupName : null);
        }
        return (paths, pinned, groups, groupNames, selected);
    }

    private void ToggleSplit()
    {
        if (IsSplit)
        {
            // Collapse: drop the right pane, focus the left.
            RightPane?.Dispose();
            RightPane = null;
            OnPropertyChanged(nameof(IsSplit));
            ActivePane = LeftPane;
        }
        else
        {
            var right = CreatePane();
            // Open the right pane at the same folder the active pane is showing.
            string start = ActivePane.SelectedTab?.CurrentPath ?? HomePath;
            right.NewTab(start, activate: true);
            RightPane = right;
            OnPropertyChanged(nameof(IsSplit));
            ActivePane = right;
        }
    }

    private void OnPaneEmpty(PaneViewModel pane)
    {
        if (IsSplit)
        {
            // Closing the last tab of one pane collapses the split onto the other.
            PaneViewModel survivor = pane == LeftPane ? RightPane! : LeftPane;

            if (pane == LeftPane)
            {
                // Move the right pane's tabs into the left so LeftPane is always present.
                LeftPane.AdoptTabsFrom(RightPane!);
                survivor = LeftPane;
            }

            if (RightPane is not null && !ReferenceEquals(RightPane, survivor))
                RightPane.Dispose();
            RightPane = null;
            OnPropertyChanged(nameof(IsSplit));
            ActivePane = survivor;
        }
        else
        {
            CloseWindowRequested?.Invoke();
        }
    }
}
