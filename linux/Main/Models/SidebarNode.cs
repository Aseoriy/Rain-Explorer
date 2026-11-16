using System.Collections.ObjectModel;
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
    public bool IsNotHeader => Kind != NodeKind.Header;
    public bool IsPinned => Kind == NodeKind.Pinned;
    /// <summary>The "Pinned" header row carries an inline add (+) affordance.</summary>
    public bool IsPinnedHeader { get; init; }
    /// <summary>True for headers — they shouldn't be selectable/navigable.</summary>
    public bool IsSelectable => Kind != NodeKind.Header;

    /// <summary>Which sidebar list this node belongs to: "quick", "drives", or "custom:&lt;stable id&gt;".
    /// Carried by both section headers and pinned items so handlers know the target list.</summary>
    public string GroupKey { get; init; } = "";

    /// <summary>True for a custom-list header (gets rename/delete affordances).</summary>
    public bool IsCustomHeader { get; init; }

    /// <summary>True for the Drives section header (gets a "Hide drives" affordance).</summary>
    public bool IsDrivesHeader => Kind == NodeKind.Header && GroupKey == "drives";

    /// <summary>Collapsed state shown on a section header (drives the chevron + hides items).</summary>
    public bool IsCollapsed { get; init; }

    /// <summary>Optional mount point a drive node can be unmounted/ejected from.</summary>
    public bool IsRemovable { get; init; }

    public MountEntry? Drive { get; init; }
    public bool HasDriveAction => Drive?.HasAction == true;
    public string DriveActionText => Drive?.ActionText ?? "";
    public string DriveActionIcon => Drive?.CanMount == true ? "plus" : "arrow-up";

    public static SidebarNode DriveNode(MountEntry drive)
    {
        var node = new SidebarNode
        {
            Name = drive.Label, Path = drive.MountPoint, IconKey = "hard-drive", Kind = NodeKind.Drive,
            GroupKey = "drives", IsRemovable = drive.IsRemovable, Drive = drive, CanExpand = drive.IsMounted,
        };
        if (node.CanExpand) node.Children.Add(NewPlaceholder());
        return node;
    }

    public bool CanExpand { get; init; }
    public ObservableCollection<SidebarNode> Children { get; } = new();
    internal event Action? TreeChanged;

    private bool _loaded;
    private int _loadVersion;
    private CancellationTokenSource? _loadCts;
    private static SidebarNode NewPlaceholder() => new() { Kind = NodeKind.Folder, Name = "" };

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value) && value) LoadChildren(); }
    }

    private bool _isActive;
    /// <summary>Whether this node matches the active tab and receives the location highlight.</summary>
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    private bool _isDropTarget;
    /// <summary>True while a file drag hovers this node — its row paints an accent highlight.</summary>
    public bool IsDropTarget { get => _isDropTarget; set => Set(ref _isDropTarget, value); }

    /// <summary>A lazily-expandable folder node (used for places, drives, pins and their subfolders).</summary>
    public static SidebarNode Folder(string name, string path, string iconKey, NodeKind kind,
        string groupKey = "", bool removable = false)
    {
        var node = new SidebarNode
        {
            Name = name, Path = path, IconKey = iconKey, Kind = kind, CanExpand = true,
            GroupKey = groupKey, IsRemovable = removable,
        };
        node.Children.Add(NewPlaceholder());   // gives the expander chevron before the first load
        return node;
    }

    public static SidebarNode HeaderNode(string name, bool pinnedHeader = false,
        string groupKey = "", bool collapsed = false, bool customHeader = false) =>
        new()
        {
            Name = name, Kind = NodeKind.Header, IsPinnedHeader = pinnedHeader,
            GroupKey = groupKey, IsCollapsed = collapsed, IsCustomHeader = customHeader,
        };

    public static SidebarNode SpecialNode(string name, string token, string iconKey, string groupKey = "") =>
        new() { Name = name, Path = token, IconKey = iconKey, Kind = NodeKind.Special, GroupKey = groupKey };

    // ---- "Always smooth animations": subfolders read ahead so the first expand animates ----------

    private static readonly SemaphoreSlim PrefetchGate = new(2);
    private static readonly TimeSpan PrefetchLifetime = TimeSpan.FromMinutes(2);
    private const int PrefetchChildLimit = 150;
    private List<(string Name, string FullName)>? _prefetched;
    private DateTime _prefetchedAt;
    private int _prefetching;

    /// <summary>Read this folder's subfolders in the background so expanding it can show them at once.</summary>
    public void Prefetch()
    {
        if (!CanExpand || _loaded || _prefetched is not null || string.IsNullOrEmpty(Path)
            || Interlocked.Exchange(ref _prefetching, 1) == 1 || MountService.IsOnNetworkMount(Path))
        {
            return;
        }
        bool showHidden = SettingsStore.Instance.Settings.ShowHiddenFiles;
        _ = Task.Run(async () =>
        {
            await PrefetchGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var list = ReadDirectories(Path, showHidden, CancellationToken.None);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _prefetched = list;
                    _prefetchedAt = DateTime.UtcNow;
                });
            }
            catch { /* unreadable: the normal load will report nothing either */ }
            finally
            {
                PrefetchGate.Release();
                Interlocked.Exchange(ref _prefetching, 0);
            }
        });
    }

    private static List<(string Name, string FullName)> ReadDirectories(string path, bool showHidden, CancellationToken token)
    {
        var result = new List<(string Name, string FullName)>();
        foreach (var dir in Directory.EnumerateDirectories(path))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var di = new DirectoryInfo(dir);
                if (!showHidden && di.Name.StartsWith('.')) continue;
                result.Add((di.Name, di.FullName));
            }
            catch { /* entry vanished — skip */ }
        }
        result.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return result;
    }

    private void AddLoadedChildren(List<(string Name, string FullName)> directories)
    {
        foreach (var directory in directories)
            AddChild(Folder(directory.Name, directory.FullName, "folder", NodeKind.Folder, GroupKey));
        TreeChanged?.Invoke();
        if (SettingsStore.Instance.Settings.AlwaysSmoothAnimations)
            foreach (SidebarNode child in Children.Take(PrefetchChildLimit)) child.Prefetch();
    }

    private async void LoadChildren()
    {
        if (_loaded || !CanExpand) return;
        _loaded = true;
        ClearChildren();   // drop the placeholder

        // Read ahead recently: show them now, synchronously, so the expand animation measures real rows.
        if (_prefetched is { } ready && DateTime.UtcNow - _prefetchedAt < PrefetchLifetime)
        {
            _prefetched = null;
            AddLoadedChildren(ready);
            return;
        }
        _prefetched = null;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        CancellationToken token = cts.Token;
        int version = ++_loadVersion;
        bool showHidden = SettingsStore.Instance.Settings.ShowHiddenFiles;

        List<(string Name, string FullName)> directories = [];
        try
        {
            directories = await Task.Run(() => ReadDirectories(Path, showHidden, token), token);
        }
        catch (OperationCanceledException) { return; }
        catch { /* unreadable folder — leave empty */ }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
            cts.Dispose();
        }

        if (version != _loadVersion) return;
        AddLoadedChildren(directories);
    }

    /// <summary>Re-enumerate this folder's children (used by the Refresh menu item).</summary>
    public void Refresh()
    {
        _loadVersion++;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _loaded = false;
        _prefetched = null;
        ClearChildren();
        Children.Add(NewPlaceholder());
        TreeChanged?.Invoke();
        if (_isExpanded) LoadChildren();
    }

    private void AddChild(SidebarNode child)
    {
        child.TreeChanged += OnChildTreeChanged;
        Children.Add(child);
    }

    private void ClearChildren()
    {
        foreach (SidebarNode child in Children)
            child.TreeChanged -= OnChildTreeChanged;
        Children.Clear();
    }

    private void OnChildTreeChanged() => TreeChanged?.Invoke();
}
