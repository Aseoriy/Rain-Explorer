using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RainExplorer.Models;

public enum RenameMode { Dialog, Inline }
public enum ViewDensity { Comfortable, Compact }
public enum SizeFormat { Binary, Decimal }

/// <summary>How the file list lays out its items.</summary>
public enum ViewLayout { Details, List, Tiles, Grid }

/// <summary>
/// User preferences, persisted as JSON. Observable so the settings panel and
/// the rest of the app react live to changes.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private RenameMode _renameMode = RenameMode.Dialog;
    public RenameMode RenameMode { get => _renameMode; set => Set(ref _renameMode, value); }

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

    private bool _singleClickToOpen;
    /// <summary>Open items with a single click instead of a double-click.</summary>
    public bool SingleClickToOpen { get => _singleClickToOpen; set => Set(ref _singleClickToOpen, value); }

    private bool _confirmDelete = true;
    /// <summary>Ask for confirmation before sending items to the Recycle Bin.</summary>
    public bool ConfirmDelete { get => _confirmDelete; set => Set(ref _confirmDelete, value); }

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

    private double _previewPaneWidth = 340;
    /// <summary>Remembered width of the preview pane (px), restored across restarts.</summary>
    public double PreviewPaneWidth { get => _previewPaneWidth; set => Set(ref _previewPaneWidth, value); }

    /// <summary>One-time flag: the default Quick Access places have been seeded into <see cref="Pinned"/>.
    /// Once set, removed defaults stay removed.</summary>
    public bool QuickAccessSeeded { get; set; }

    /// <summary>User-pinned Quick Access folders shown in the sidebar.</summary>
    public List<PinnedItem> Pinned { get; set; } = new();

    /// <summary>Raise a change for <see cref="Pinned"/> (list mutations don't auto-notify) to trigger save + sidebar rebuild.</summary>
    public void NotifyPinnedChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pinned)));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
