using System.Collections.ObjectModel;
using System.IO;
using RainExplorer.Services;
using RainExplorer.ViewModels;

namespace RainExplorer.Models;

public enum NodeKind { Header, Special, Place, Pinned, Drive, Folder }

/// <summary>
/// One row in the sidebar tree. Real-directory nodes are lazily expandable:
/// a placeholder child gives them an expander chevron, and the real subfolders
/// are enumerated the first time the node is expanded.
/// </summary>
public sealed class SidebarNode : ObservableObject
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public NodeKind Kind { get; init; }

    private string _iconKey = "folder";
    public string IconKey { get => _iconKey; set => Set(ref _iconKey, value); }

    public bool IsHeader => Kind == NodeKind.Header;
    public bool IsPinned => Kind == NodeKind.Pinned;
    /// <summary>The "Pinned" header row carries an inline add (+) affordance.</summary>
    public bool IsPinnedHeader { get; init; }
    /// <summary>True for headers — they shouldn't be selectable/navigable.</summary>
    public bool IsSelectable => Kind != NodeKind.Header;

    public bool CanExpand { get; init; }
    public ObservableCollection<SidebarNode> Children { get; } = new();

    private bool _loaded;
    private static SidebarNode NewPlaceholder() => new() { Kind = NodeKind.Folder, Name = "" };

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value) && value) LoadChildren(); }
    }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private bool _isDropTarget;
    /// <summary>True while a file drag hovers this node — its row paints an accent highlight.</summary>
    public bool IsDropTarget { get => _isDropTarget; set => Set(ref _isDropTarget, value); }

    /// <summary>A lazily-expandable folder node (used for places, drives, pins and their subfolders).</summary>
    public static SidebarNode Folder(string name, string path, string iconKey, NodeKind kind)
    {
        var node = new SidebarNode { Name = name, Path = path, IconKey = iconKey, Kind = kind, CanExpand = true };
        node.Children.Add(NewPlaceholder());   // gives the expander chevron before the first load
        return node;
    }

    public static SidebarNode HeaderNode(string name, bool pinnedHeader = false) =>
        new() { Name = name, Kind = NodeKind.Header, IsPinnedHeader = pinnedHeader };

    public static SidebarNode SpecialNode(string name, string token, string iconKey) =>
        new() { Name = name, Path = token, IconKey = iconKey, Kind = NodeKind.Special };

    private void LoadChildren()
    {
        if (_loaded || !CanExpand) return;
        _loaded = true;
        Children.Clear();   // drop the placeholder
        try
        {
            bool showHidden = SettingsStore.Instance.Settings.ShowHiddenFiles;
            foreach (var dir in Directory.EnumerateDirectories(Path)
                         .OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var di = new DirectoryInfo(dir);
                    if (!showHidden &&
                        (di.Attributes.HasFlag(FileAttributes.Hidden) ||
                         di.Attributes.HasFlag(FileAttributes.System)))
                        continue;
                    Children.Add(Folder(di.Name, di.FullName, "folder", NodeKind.Folder));
                }
                catch { /* entry vanished — skip */ }
            }
        }
        catch { /* unreadable folder — leave empty */ }
    }

    /// <summary>Re-enumerate this folder's children (used by the Refresh menu item).</summary>
    public void Refresh()
    {
        _loaded = false;
        Children.Clear();
        Children.Add(NewPlaceholder());
        if (_isExpanded) LoadChildren();
    }
}
