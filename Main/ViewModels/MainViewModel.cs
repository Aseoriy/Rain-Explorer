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
            if (e.PropertyName == nameof(AppSettings.Pinned))
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
        var settings = SettingsStore.Instance.Settings;
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

    /// <summary>Change a pinned item's custom icon.</summary>
    public static void SetPinnedIcon(string path, string iconKey)
    {
        var pin = SettingsStore.Instance.Settings.Pinned
            .FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (pin is null || string.Equals(pin.IconKey, iconKey, StringComparison.Ordinal)) return;
        pin.IconKey = iconKey;
        SettingsStore.Instance.Settings.NotifyPinnedChanged();
    }

    /// <summary>Rename a pinned item's display label.</summary>
    public static void RenamePinned(string path, string newName)
    {
        var pin = SettingsStore.Instance.Settings.Pinned
            .FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (pin is null || string.IsNullOrWhiteSpace(newName) ||
            string.Equals(pin.Name, newName, StringComparison.Ordinal)) return;
        pin.Name = newName;
        SettingsStore.Instance.Settings.NotifyPinnedChanged();
    }

    /// <summary>Pin a folder to Quick Access (no-op if already pinned).</summary>
    public static void Pin(string path, string? name = null, string iconKey = "folder")
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var settings = SettingsStore.Instance.Settings;
        if (settings.Pinned.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        settings.Pinned.Add(new PinnedItem
        {
            Path = path,
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) : name!,
            IconKey = iconKey,
        });
        settings.NotifyPinnedChanged();
    }

    /// <summary>Remove a pinned Quick Access entry by path.</summary>
    public static void Unpin(string path)
    {
        var settings = SettingsStore.Instance.Settings;
        int removed = settings.Pinned.RemoveAll(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) settings.NotifyPinnedChanged();
    }

    public static bool IsPinned(string path) =>
        SettingsStore.Instance.Settings.Pinned.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));

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
