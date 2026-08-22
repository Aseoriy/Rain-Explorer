using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RainExplorer.Models;

// These enums are persisted to settings.json BY NAME ("Dialog", "Comfortable", …).
// The string<->enum converter is pinned directly on each type via [JsonConverter] so
// reading always works, regardless of whether a JsonStringEnumConverter happens to be
// registered on the serializer options — relying on the options collection silently
// failed in the published build, which threw on load and wiped the user's pins.
[JsonConverter(typeof(JsonStringEnumConverter<RenameMode>))]
public enum RenameMode { Dialog, Inline }
[JsonConverter(typeof(JsonStringEnumConverter<ViewDensity>))]
public enum ViewDensity { Comfortable, Compact }
[JsonConverter(typeof(JsonStringEnumConverter<SizeFormat>))]
public enum SizeFormat { Binary, Decimal }

/// <summary>What happens when the user deletes items.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeleteBehavior>))]
public enum DeleteBehavior
{
    /// <summary>Ask each time whether to recycle or permanently delete.</summary>
    Prompt,
    /// <summary>Always send to the Recycle Bin, no prompt.</summary>
    Recycle,
    /// <summary>Always delete permanently after one final in-app confirmation.</summary>
    Permanent,
}

/// <summary>How the file list lays out its items.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ViewLayout>))]
public enum ViewLayout { Details, List, Tiles, Grid }

/// <summary>Which shell "Open in Terminal" launches.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TerminalApp>))]
public enum TerminalApp { Cmd, PowerShell }

/// <summary>
/// User preferences, persisted as JSON. Observable so the settings panel and
/// the rest of the app react live to changes.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private RenameMode _renameMode = RenameMode.Dialog;
    public RenameMode RenameMode { get => _renameMode; set => Set(ref _renameMode, value); }

    private bool _selectFileNameWithoutExtensionOnRename = true;
    /// <summary>Select only the filename portion when renaming a file with an extension.</summary>
    public bool SelectFileNameWithoutExtensionOnRename
    {
        get => _selectFileNameWithoutExtensionOnRename;
        set => Set(ref _selectFileNameWithoutExtensionOnRename, value);
    }

    private bool _showHiddenFiles;
    public bool ShowHiddenFiles { get => _showHiddenFiles; set => Set(ref _showHiddenFiles, value); }

    private ViewDensity _density = ViewDensity.Comfortable;
    public ViewDensity Density { get => _density; set => Set(ref _density, value); }

    private string _defaultFolder = "";   // empty => Home
    public string DefaultFolder { get => _defaultFolder; set => Set(ref _defaultFolder, value); }

    private string _theme = "Violet";
    public string Theme { get => _theme; set => Set(ref _theme, value); }

    private bool _showAmbientBackground = true;
    /// <summary>Whether the themed ambient orb/background glow is drawn.</summary>
    public bool ShowAmbientBackground { get => _showAmbientBackground; set => Set(ref _showAmbientBackground, value); }

    private bool _showStatusBar = true;
    public bool ShowStatusBar { get => _showStatusBar; set => Set(ref _showStatusBar, value); }

    private bool _foldersFirst = true;
    /// <summary>Sort folders ahead of files regardless of sort column.</summary>
    public bool FoldersFirst { get => _foldersFirst; set => Set(ref _foldersFirst, value); }

    private string _fontFamily = "Segoe UI";
    /// <summary>App-wide UI font (paths/sizes keep the mono face).</summary>
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }

    private SizeFormat _sizeFormat = SizeFormat.Binary;
    /// <summary>Binary = 1024-based (KB/MB), Decimal = 1000-based.</summary>
    public SizeFormat SizeFormat { get => _sizeFormat; set => Set(ref _sizeFormat, value); }

    private bool _openFoldersInNewTab;
    /// <summary>Double-clicking a folder opens it in a new tab instead of navigating.</summary>
    public bool OpenFoldersInNewTab { get => _openFoldersInNewTab; set => Set(ref _openFoldersInNewTab, value); }

    private bool _preserveOpenTabsOnClose;
    /// <summary>Restore every open pane/tab after the app or computer is restarted.</summary>
    public bool PreserveOpenTabsOnClose { get => _preserveOpenTabsOnClose; set => Set(ref _preserveOpenTabsOnClose, value); }

    private bool _autoRefreshFolders = true;
    /// <summary>Refresh open folder views when their contents change outside Rain Explorer.</summary>
    public bool AutoRefreshFolders { get => _autoRefreshFolders; set => Set(ref _autoRefreshFolders, value); }

    /// <summary>Last window session. Written on close when tab preservation is enabled.</summary>
    public ExplorerSession? SavedSession { get; set; }

    private TerminalApp _terminalApp = TerminalApp.Cmd;
    /// <summary>Which shell "Open in Terminal" launches (cmd by default).</summary>
    public TerminalApp TerminalApp { get => _terminalApp; set => Set(ref _terminalApp, value); }

    private bool _gitIntegrationEnabled = true;
    /// <summary>Show Rain Explorer's local Git integration when a supported Git installation is available.</summary>
    public bool GitIntegrationEnabled
    {
        get => _gitIntegrationEnabled;
        set => Set(ref _gitIntegrationEnabled, value);
    }

    private string _gitExecutablePath = "";
    /// <summary>Optional full path to git.exe. Empty means automatic Git for Windows discovery.</summary>
    public string GitExecutablePath
    {
        get => _gitExecutablePath;
        set => Set(ref _gitExecutablePath, value);
    }

    private bool _gitAutoRefreshWhenViewOpen = true;
    public bool GitAutoRefreshWhenViewOpen
    {
        get => _gitAutoRefreshWhenViewOpen;
        set => Set(ref _gitAutoRefreshWhenViewOpen, value);
    }

    private string _gitDefaultCloneFolder = "";
    public string GitDefaultCloneFolder
    {
        get => _gitDefaultCloneFolder;
        set => Set(ref _gitDefaultCloneFolder, value);
    }

    public List<GitHubAccountMetadata> GitHubAccounts { get; set; } = new();
    public long? SelectedGitHubAccountId { get; set; }

    private bool _registerShellIntegration;
    /// <summary>"Open in Rain Explorer" verb registered in HKCU for folders/drives.</summary>
    public bool RegisterShellIntegration { get => _registerShellIntegration; set => Set(ref _registerShellIntegration, value); }

    private bool _setAsDefaultExplorer;
    /// <summary>Intercept the folder/drive open verb + Win+E so they launch Rain (HKCU, reversible).</summary>
    public bool SetAsDefaultExplorer { get => _setAsDefaultExplorer; set => Set(ref _setAsDefaultExplorer, value); }

    private bool _showDriveItemCounts;
    /// <summary>Compute (recursive) file counts on the Drives page. Resource-intensive; off by default.</summary>
    public bool ShowDriveItemCounts { get => _showDriveItemCounts; set => Set(ref _showDriveItemCounts, value); }

    private ViewLayout _viewLayout = ViewLayout.Details;
    /// <summary>How the file list lays out items (details rows, list, tiles, grid).</summary>
    public ViewLayout ViewLayout { get => _viewLayout; set => Set(ref _viewLayout, value); }

    private bool _showFileExtensions = true;
    /// <summary>Show file extensions in the list (off hides the trailing ".ext").</summary>
    public bool ShowFileExtensions { get => _showFileExtensions; set => Set(ref _showFileExtensions, value); }

    private bool _showCheckboxes;
    /// <summary>Show a selection checkbox on each row.</summary>
    public bool ShowCheckboxes { get => _showCheckboxes; set => Set(ref _showCheckboxes, value); }

    private bool _showCheckboxesOnSelectionOnly;
    /// <summary>Only reveal item checkboxes for rows that are currently selected.</summary>
    public bool ShowCheckboxesOnSelectionOnly { get => _showCheckboxesOnSelectionOnly; set => Set(ref _showCheckboxesOnSelectionOnly, value); }

    private bool _singleClickToOpen;
    /// <summary>Open items with a single click instead of a double-click.</summary>
    public bool SingleClickToOpen { get => _singleClickToOpen; set => Set(ref _singleClickToOpen, value); }

    private DeleteBehavior _deleteBehavior = DeleteBehavior.Prompt;
    /// <summary>Whether deleting prompts each time, always recycles, or permanently deletes after confirmation.</summary>
    public DeleteBehavior DeleteBehavior { get => _deleteBehavior; set => Set(ref _deleteBehavior, value); }

    private bool _warnOnExtensionChange = true;
    /// <summary>Warn when a rename changes a file's extension.</summary>
    public bool WarnOnExtensionChange { get => _warnOnExtensionChange; set => Set(ref _warnOnExtensionChange, value); }

    private bool _calculateFolderSizes;
    /// <summary>Compute folder sizes in the list (recursive; can be slow on big folders).</summary>
    public bool CalculateFolderSizes { get => _calculateFolderSizes; set => Set(ref _calculateFolderSizes, value); }

    private bool _sidebarCollapsed;
    /// <summary>Whether the left sidebar is collapsed to reclaim horizontal space.</summary>
    public bool SidebarCollapsed { get => _sidebarCollapsed; set => Set(ref _sidebarCollapsed, value); }

    private bool _showPreviewPane;
    /// <summary>Whether the right-hand file preview pane is shown (toggle with Space).</summary>
    public bool ShowPreviewPane { get => _showPreviewPane; set => Set(ref _showPreviewPane, value); }

    private bool _autoUpdate = true;
    /// <summary>Automatically check GitHub for a newer release on launch and offer to install it.</summary>
    public bool AutoUpdate { get => _autoUpdate; set => Set(ref _autoUpdate, value); }

    private bool _betaUpdates;
    /// <summary>Also accept pre-release ("-beta"/"-pre") builds when checking for updates.</summary>
    public bool BetaUpdates { get => _betaUpdates; set => Set(ref _betaUpdates, value); }

    private string _skippedUpdateVersion = "";
    /// <summary>A version the user chose to skip; the auto-check won't nag about it again
    /// (a manual "Check for updates" still surfaces it).</summary>
    public string SkippedUpdateVersion { get => _skippedUpdateVersion; set => Set(ref _skippedUpdateVersion, value); }

    private bool _rememberActivity = true;
    /// <summary>Persist the activity center log across restarts. When off, the log is
    /// in-memory only and resets every launch.</summary>
    public bool RememberActivity { get => _rememberActivity; set => Set(ref _rememberActivity, value); }

    private double _previewPaneWidth = 340;
    /// <summary>Remembered width of the preview pane (px), restored across restarts.</summary>
    public double PreviewPaneWidth { get => _previewPaneWidth; set => Set(ref _previewPaneWidth, value); }

    private double _windowWidth = 1140;
    public double WindowWidth { get => _windowWidth; set => Set(ref _windowWidth, value); }

    private double _windowHeight = 720;
    public double WindowHeight { get => _windowHeight; set => Set(ref _windowHeight, value); }

    private double? _windowLeft;
    public double? WindowLeft { get => _windowLeft; set => Set(ref _windowLeft, value); }

    private double? _windowTop;
    public double? WindowTop { get => _windowTop; set => Set(ref _windowTop, value); }

    private bool _windowMaximized;
    public bool WindowMaximized { get => _windowMaximized; set => Set(ref _windowMaximized, value); }

    // Native pixel bounds avoid WPF's cross-monitor DPI conversion changing the saved
    // monitor or shrinking the window when displays use different scaling.
    public int? WindowPixelLeft { get; set; }
    public int? WindowPixelTop { get; set; }
    public int? WindowPixelWidth { get; set; }
    public int? WindowPixelHeight { get; set; }

    /// <summary>One-time flag: the default Quick Access places have been seeded into <see cref="Pinned"/>.
    /// Once set, removed defaults stay removed.</summary>
    public bool QuickAccessSeeded { get; set; }

    /// <summary>User-pinned Quick Access folders shown in the sidebar (the default list).</summary>
    public List<PinnedItem> Pinned { get; set; } = new();

    /// <summary>Extra user-created sidebar lists (e.g. "Quick access 2"), each with its own pins.</summary>
    public List<SidebarGroup> CustomGroups { get; set; } = new();

    /// <summary>Named tab workspaces. Each project retains its own tab list and selected tab.</summary>
    public List<TabProject> TabProjects { get; set; } = new();

    /// <summary>Stable order of sidebar section keys ("quick", "drives", and "custom:&lt;id&gt;").</summary>
    public List<string> SidebarOrder { get; set; } = new();

    private bool _showQuickAccessInSidebar = true;
    public bool ShowQuickAccessInSidebar { get => _showQuickAccessInSidebar; set => Set(ref _showQuickAccessInSidebar, value); }

    private string _quickAccessName = "Quick access";
    public string QuickAccessName { get => _quickAccessName; set => Set(ref _quickAccessName, value); }

    private string _drivesName = "Drives";
    public string DrivesName { get => _drivesName; set => Set(ref _drivesName, value); }

    private bool _showDrivesInSidebar = true;
    /// <summary>Whether the Drives section is shown in the sidebar.</summary>
    public bool ShowDrivesInSidebar { get => _showDrivesInSidebar; set => Set(ref _showDrivesInSidebar, value); }

    private bool _quickAccessCollapsed;
    /// <summary>Collapsed state of the default Quick access section.</summary>
    public bool QuickAccessCollapsed { get => _quickAccessCollapsed; set => Set(ref _quickAccessCollapsed, value); }

    private bool _drivesCollapsed;
    /// <summary>Collapsed state of the Drives section.</summary>
    public bool DrivesCollapsed { get => _drivesCollapsed; set => Set(ref _drivesCollapsed, value); }

    /// <summary>Raise a change for <see cref="Pinned"/> (list mutations don't auto-notify) to trigger save + sidebar rebuild.</summary>
    public void NotifyPinnedChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pinned)));

    /// <summary>Raise a change after editing a project list entry so it is persisted.</summary>
    public void NotifyTabProjectsChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TabProjects)));

    /// <summary>Keep settings written by newer Rain versions or optional components intact.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
