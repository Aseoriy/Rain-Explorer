using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
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
        RebuildSidebar();

        // Toggling "show hidden files" re-reads every open tab; pin changes rebuild the sidebar.
        SettingsStore.Instance.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.ShowHiddenFiles)
                or nameof(AppSettings.FoldersFirst) or nameof(AppSettings.SizeFormat)
                or nameof(AppSettings.ShowFileExtensions) or nameof(AppSettings.CalculateFolderSizes))
                ReloadAllTabs();
            if (e.PropertyName is nameof(AppSettings.Pinned)
                or nameof(AppSettings.ShowDrivesInSidebar)
                or nameof(AppSettings.QuickAccessCollapsed)
                or nameof(AppSettings.DrivesCollapsed))
                RebuildSidebar();
        };
    }

    private void RebuildSidebar()
    {
        SidebarNodes.Clear();
        foreach (var s in _fs.GetSidebarNodes()) SidebarNodes.Add(s);
    }

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

    /// <summary>The pin list for a sidebar group key ("quick"/""→default, "custom:N"→a custom list).</summary>
    public static List<PinnedItem>? GroupItems(string key)
    {
        var s = SettingsStore.Instance.Settings;
        if (string.IsNullOrEmpty(key) || key == "quick") return s.Pinned;
        if (TryCustomIndex(key, out int i)) return s.CustomGroups[i].Items;
        return null;
    }

    private static bool TryCustomIndex(string key, out int i)
    {
        i = -1;
        if (key is null || !key.StartsWith("custom:")) return false;
        return int.TryParse(key.AsSpan(7), out i)
               && i >= 0 && i < SettingsStore.Instance.Settings.CustomGroups.Count;
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
        else if (TryCustomIndex(key, out int i))
        {
            s.CustomGroups[i].Collapsed = !s.CustomGroups[i].Collapsed;
            s.NotifyPinnedChanged();
        }
    }

    /// <summary>Create a new empty custom list.</summary>
    public static void AddCustomGroup(string? name = null)
    {
        var s = SettingsStore.Instance.Settings;
        s.CustomGroups.Add(new SidebarGroup
        {
            Name = string.IsNullOrWhiteSpace(name) ? UniqueGroupName(s) : name!,
        });
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
        if (!TryCustomIndex(key, out int i) || string.IsNullOrWhiteSpace(newName)) return;
        SettingsStore.Instance.Settings.CustomGroups[i].Name = newName.Trim();
        SettingsStore.Instance.Settings.NotifyPinnedChanged();
    }

    public static void DeleteGroup(string key)
    {
        if (!TryCustomIndex(key, out int i)) return;
        SettingsStore.Instance.Settings.CustomGroups.RemoveAt(i);
        SettingsStore.Instance.Settings.NotifyPinnedChanged();
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
    public static void SetPinnedIcon(string path, string iconKey)
    {
        var pin = FindPin(path);
        if (pin is null || string.Equals(pin.IconKey, iconKey, StringComparison.Ordinal)) return;
        pin.IconKey = iconKey;
        SettingsStore.Instance.Settings.NotifyPinnedChanged();
    }

    /// <summary>Rename a pinned item's display label (any list).</summary>
    public static void RenamePinned(string path, string newName)
    {
        var pin = FindPin(path);
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
        if (string.IsNullOrWhiteSpace(path)) return;
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

    private void ReloadAllTabs()
    {
        foreach (var t in LeftPane.Tabs) _ = t.ReloadAsync();
        if (RightPane is not null)
            foreach (var t in RightPane.Tabs) _ = t.ReloadAsync();
    }

    /// <summary>Re-read every open tab (e.g. after an undo/redo touched the filesystem).</summary>
    public void RefreshAll() => ReloadAllTabs();

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
        return pane;
    }

    public PaneViewModel EnsureFirstTab()
    {
        if (LeftPane.Tabs.Count == 0)
        {
            string start = App.LaunchFolder is { } f && Directory.Exists(f) ? f : StartTarget;
            LeftPane.NewTab(start, activate: true);
        }
        return LeftPane;
    }

    private void ToggleSplit()
    {
        if (IsSplit)
        {
            // Collapse: drop the right pane, focus the left.
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
                var tabs = RightPane!.Tabs.ToList();
                RightPane.Tabs.Clear();
                foreach (var t in tabs) LeftPane.Tabs.Add(t);
                LeftPane.SelectedTab = LeftPane.Tabs.FirstOrDefault();
                survivor = LeftPane;
            }

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
