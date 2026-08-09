using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using RainExplorer.Helpers;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.ViewModels;

/// <summary>
/// One browsing pane: its own set of tabs and active tab. The window may show
/// one pane, or two side by side in split view. Each pane is fully independent.
/// </summary>
public sealed class PaneViewModel : ObservableObject, IDisposable
{
    private sealed record ClosedTab(string Target, bool IsPinned, string? GroupId, string? GroupName);

    private readonly FileSystemService _fs;
    private readonly Stack<ClosedTab> _closedTabs = new();
    private bool _loadingProject;
    private bool _batchingTabStructure;

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    /// <summary>Ungrouped tabs plus one representative tab for each group.</summary>
    public IReadOnlyList<TabViewModel> TopLevelTabs =>
        Tabs.Where(tab => !tab.IsGrouped || tab.IsGroupLeader).ToList();

    /// <summary>Members of the group containing the selected tab, shown on the second row.</summary>
    public IReadOnlyList<TabViewModel> ActiveGroupTabs =>
        SelectedTab?.GroupId is { Length: > 0 } id
            ? Tabs.Where(tab => tab.GroupId == id).ToList()
            : Array.Empty<TabViewModel>();

    public bool HasActiveGroup => ActiveGroupTabs.Count > 0;

    /// <summary>The top-row selection is the group representative while a child tab is active.</summary>
    public TabViewModel? SelectedTopTab
    {
        get
        {
            if (SelectedTab?.GroupId is not { Length: > 0 } id) return SelectedTab;
            return Tabs.FirstOrDefault(tab => tab.GroupId == id && tab.IsGroupLeader) ?? SelectedTab;
        }
        set
        {
            if (value is null) return;
            if (!value.IsGrouped)
            {
                SelectedTab = value;
                return;
            }

            if (SelectedTab?.GroupId == value.GroupId) return;
            SelectedTab = Tabs.FirstOrDefault(tab => tab.GroupId == value.GroupId) ?? value;
        }
    }

    /// <summary>Active tab finished navigating (drives the list animation).</summary>
    public event Action? ActiveContentsChanged;

    /// <summary>The pane has no tabs left (window decides: collapse split or close).</summary>
    public event Action<PaneViewModel>? EmptyRequested;

    /// <summary>The active project's tab snapshot changed and should be persisted.</summary>
    public event Action<PaneViewModel>? ProjectStateChanged;

    public PaneViewModel(FileSystemService fs)
    {
        _fs = fs;
        NewTabCommand = new RelayCommand(_ => NewTab(activate: true));
        CloseTabCommand = new RelayCommand(p => CloseTab(p as TabViewModel ?? SelectedTab));
        CloseGroupCommand = new RelayCommand(p => CloseGroup(p as TabViewModel ?? SelectedTab));
        ActivateCommand = new RelayCommand(_ => RequestActivate?.Invoke(this));
    }

    /// <summary>Raised when this pane wants to become the active pane (e.g. user clicked in it).</summary>
    public event Action<PaneViewModel>? RequestActivate;

    private TabViewModel? _selectedTab;
    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (ReferenceEquals(_selectedTab, value)) return;
            if (_selectedTab is not null) _selectedTab.ContentsChanged -= OnActiveContentsChanged;
            if (!Set(ref _selectedTab, value)) return;
            if (_selectedTab is not null) _selectedTab.ContentsChanged += OnActiveContentsChanged;
            NotifyTabRows();
            NotifyProjectStateChanged();
        }
    }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    private string? _activeProjectId;
    /// <summary>The tab project currently shown in this pane, if its tabs came from one.</summary>
    public string? ActiveProjectId
    {
        get => _activeProjectId;
        set
        {
            if (Set(ref _activeProjectId, value)) NotifyProjectStateChanged();
        }
    }

    public ICommand NewTabCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand CloseGroupCommand { get; }
    public ICommand ActivateCommand { get; }

    private void OnActiveContentsChanged() => ActiveContentsChanged?.Invoke();

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.CurrentPath) or nameof(TabViewModel.IsPinned)
            or nameof(TabViewModel.GroupId) or nameof(TabViewModel.GroupName))
            NotifyProjectStateChanged();
        if (e.PropertyName is nameof(TabViewModel.GroupId) or nameof(TabViewModel.GroupName))
            NotifyTabRows();
    }

    private void NotifyProjectStateChanged()
    {
        if (!_loadingProject && !_batchingTabStructure && ActiveProjectId is { Length: > 0 })
            ProjectStateChanged?.Invoke(this);
    }

    public bool HasClosedTabs => _closedTabs.Count > 0;

    public TabViewModel NewTab(string? path = null, bool activate = true, bool pinned = false,
        string? groupId = null, string? groupName = null)
    {
        var tab = new TabViewModel(_fs)
        {
            IsPinned = pinned,
            GroupId = pinned ? null : groupId,
            GroupName = string.IsNullOrWhiteSpace(groupName) ? "Tab group" : groupName,
        };
        tab.PropertyChanged += OnTabPropertyChanged;
        Tabs.Add(tab);
        RefreshGroupLeaders();
        var navigation = tab.NavigateAsync(path ?? MainViewModel.StartTarget, true);
        if (activate) SelectedTab = tab;
        _ = navigation;
        return tab;
    }

    public void CloseTab(TabViewModel? tab)
    {
        if (tab is null) return;
        int idx = Tabs.IndexOf(tab);
        if (idx < 0) return;

        if (tab == SelectedTab) tab.ContentsChanged -= OnActiveContentsChanged;
        tab.PropertyChanged -= OnTabPropertyChanged;
        if (IsRestorableTarget(tab.CurrentPath))
        {
            _closedTabs.Push(new ClosedTab(tab.CurrentPath, tab.IsPinned, tab.GroupId, tab.GroupName));
            OnPropertyChanged(nameof(HasClosedTabs));
        }
        Tabs.Remove(tab);
        tab.Dispose();
        RefreshGroupLeaders();
        NotifyProjectStateChanged();

        if (Tabs.Count == 0)
        {
            EmptyRequested?.Invoke(this);
            return;
        }
        SelectedTab = Tabs[Math.Min(idx, Tabs.Count - 1)];
    }

    /// <summary>Open a copy of a tab at its current location.</summary>
    public TabViewModel? DuplicateTab(TabViewModel? tab)
    {
        if (tab is null || !IsRestorableTarget(tab.CurrentPath)) return null;
        return NewTab(tab.CurrentPath, activate: true, pinned: tab.IsPinned,
            groupId: tab.GroupId, groupName: tab.GroupName);
    }

    public void TogglePin(TabViewModel? tab)
    {
        if (tab is null) return;
        if (!tab.IsPinned && tab.IsGrouped) RemoveFromGroup(tab);
        tab.IsPinned = !tab.IsPinned;
        NormalizePinnedTabs();
    }

    /// <summary>Move a tab while keeping compact pinned tabs in their own left-hand group.</summary>
    public bool MoveTab(TabViewModel? tab, int targetIndex)
    {
        if (tab is null) return false;
        NormalizePinnedTabs();
        int from = Tabs.IndexOf(tab);
        if (from < 0) return false;

        int pinnedCount = Tabs.Count(t => t.IsPinned);
        if (tab.IsPinned)
            targetIndex = Math.Clamp(targetIndex, 0, Math.Max(0, pinnedCount - 1));
        else
            targetIndex = Math.Clamp(targetIndex, pinnedCount, Math.Max(pinnedCount, Tabs.Count - 1));

        if (from == targetIndex) return false;
        Tabs.Move(from, targetIndex);
        RefreshGroupLeaders();
        NotifyProjectStateChanged();
        return true;
    }

    /// <summary>Take a serializable snapshot of the currently open tabs for a project.</summary>
    public (List<TabProjectTab> Tabs, int SelectedIndex) CaptureProject()
    {
        var tabs = new List<TabProjectTab>();
        int selected = 0;
        foreach (var tab in Tabs)
        {
            if (!IsRestorableTarget(tab.CurrentPath)) continue;
            if (ReferenceEquals(tab, SelectedTab)) selected = tabs.Count;
            tabs.Add(new TabProjectTab
            {
                Target = tab.CurrentPath,
                IsPinned = tab.IsPinned,
                GroupId = tab.GroupId,
                GroupName = tab.GroupName,
            });
        }
        return (tabs, selected);
    }

    /// <summary>Replace this pane's tabs with a project's remembered workspace.</summary>
    public void LoadProject(IReadOnlyList<TabProjectTab> projectTabs, int selectedIndex)
    {
        _loadingProject = true;
        try
        {
            SelectedTab = null;
            foreach (var tab in Tabs)
            {
                tab.PropertyChanged -= OnTabPropertyChanged;
                tab.Dispose();
            }
            Tabs.Clear();
            foreach (var tab in projectTabs.Where(t => IsRestorableTarget(t.Target)))
                NewTab(tab.Target, activate: false, pinned: tab.IsPinned,
                    groupId: tab.GroupId, groupName: tab.GroupName);

            if (Tabs.Count == 0)
                NewTab(MainViewModel.StartTarget, activate: true);
            else
                SelectedTab = Tabs[Math.Clamp(selectedIndex, 0, Tabs.Count - 1)];
            NormalizePinnedTabs();
            RefreshGroupLeaders();
        }
        finally
        {
            _loadingProject = false;
        }
    }

    private void NormalizePinnedTabs()
    {
        var ordered = Tabs.Where(t => t.IsPinned).Concat(Tabs.Where(t => !t.IsPinned)).ToList();
        for (int index = 0; index < ordered.Count; index++)
        {
            int current = Tabs.IndexOf(ordered[index]);
            if (current != index) Tabs.Move(current, index);
        }
    }

    public void GroupTabs(TabViewModel? dragged, TabViewModel? target)
    {
        if (dragged is null || target is null || ReferenceEquals(dragged, target)
            || dragged.IsPinned || target.IsPinned)
            return;

        _batchingTabStructure = true;
        try
        {
            if (dragged.IsGrouped && dragged.GroupId != target.GroupId) RemoveFromGroup(dragged);
            string groupId = target.GroupId ?? Guid.NewGuid().ToString("N");
            string groupName = target.IsGrouped ? target.GroupName : "Tab group";
            int sourceIndex = Tabs.IndexOf(dragged);
            int lastExistingIndex = target.IsGrouped
                ? Tabs.Select((tab, index) => (tab, index))
                    .Where(pair => pair.tab.GroupId == groupId)
                    .Max(pair => pair.index)
                : Tabs.IndexOf(target);
            dragged.GroupId = groupId;
            dragged.GroupName = groupName;
            target.GroupId = groupId;
            target.GroupName = groupName;

            int targetIndex = sourceIndex < lastExistingIndex ? lastExistingIndex : lastExistingIndex + 1;
            MoveTab(dragged, targetIndex);
        }
        finally
        {
            _batchingTabStructure = false;
        }
        RefreshGroupLeaders();
        SelectedTab = dragged;
        NotifyProjectStateChanged();
    }

    /// <summary>Extract a grouped tab and place it beside a top-row tab in one layout update.</summary>
    public bool MoveTabOutOfGroup(TabViewModel? tab, TabViewModel? target, bool after)
    {
        if (tab?.GroupId is not { Length: > 0 } sourceGroupId || tab.IsPinned) return false;

        var originalGroupMembers = Tabs
            .Where(candidate => candidate.GroupId == sourceGroupId && !ReferenceEquals(candidate, tab))
            .ToList();
        bool targetWasSourceGroup = ReferenceEquals(target, tab) || target?.GroupId == sourceGroupId;
        bool changed = false;

        _batchingTabStructure = true;
        try
        {
            RemoveFromGroup(tab);
            changed = true;

            int insertionIndex;
            if (targetWasSourceGroup && originalGroupMembers.Count > 0)
            {
                var memberIndexes = originalGroupMembers.Select(Tabs.IndexOf).Where(index => index >= 0).ToList();
                insertionIndex = after ? memberIndexes.Max() + 1 : memberIndexes.Min();
            }
            else if (target is null || ReferenceEquals(target, tab))
            {
                insertionIndex = Tabs.Count;
            }
            else if (target.GroupId is { Length: > 0 } targetGroupId)
            {
                var memberIndexes = Tabs.Select((candidate, index) => (candidate, index))
                    .Where(pair => pair.candidate.GroupId == targetGroupId)
                    .Select(pair => pair.index)
                    .ToList();
                insertionIndex = after ? memberIndexes.Max() + 1 : memberIndexes.Min();
            }
            else
            {
                insertionIndex = Tabs.IndexOf(target);
                if (insertionIndex < 0) insertionIndex = Tabs.Count;
                else if (after) insertionIndex++;
            }

            int sourceIndex = Tabs.IndexOf(tab);
            if (sourceIndex >= 0 && sourceIndex < insertionIndex) insertionIndex--;
            changed |= MoveTab(tab, insertionIndex);
        }
        finally
        {
            _batchingTabStructure = false;
        }

        RefreshGroupLeaders();
        SelectedTab = tab;
        NotifyProjectStateChanged();
        return changed;
    }

    public TabViewModel? NewTabInGroup(TabViewModel? member)
    {
        if (member is null || !member.IsGrouped) return null;
        int lastGroupIndex = Tabs
            .Select((candidate, index) => (candidate, index))
            .Where(pair => pair.candidate.GroupId == member.GroupId)
            .Max(pair => pair.index);
        var tab = NewTab(activate: true, groupId: member.GroupId, groupName: member.GroupName);
        MoveTab(tab, lastGroupIndex + 1);
        return tab;
    }

    public TabViewModel? NewTabInActiveGroup() => NewTabInGroup(SelectedTab);

    public void RenameGroup(TabViewModel? member, string name)
    {
        if (member?.GroupId is not { Length: > 0 } id || string.IsNullOrWhiteSpace(name)) return;
        foreach (var tab in Tabs.Where(tab => tab.GroupId == id)) tab.GroupName = name.Trim();
        RefreshGroupLeaders();
        NotifyProjectStateChanged();
    }

    public void RemoveFromGroup(TabViewModel? tab)
    {
        if (tab?.GroupId is not { Length: > 0 } id) return;
        tab.GroupId = null;
        tab.IsGroupLeader = false;
        if (Tabs.Count(candidate => candidate.GroupId == id) < 2)
        {
            foreach (var remaining in Tabs.Where(candidate => candidate.GroupId == id))
            {
                remaining.GroupId = null;
                remaining.IsGroupLeader = false;
            }
        }
        RefreshGroupLeaders();
        NotifyProjectStateChanged();
    }

    public void ClearGroup(TabViewModel? member)
    {
        if (member?.GroupId is not { Length: > 0 } id) return;
        foreach (var tab in Tabs.Where(candidate => candidate.GroupId == id).ToList())
        {
            tab.GroupId = null;
            tab.IsGroupLeader = false;
        }
        RefreshGroupLeaders();
        NotifyProjectStateChanged();
    }

    public void CloseGroup(TabViewModel? member)
    {
        if (member?.GroupId is not { Length: > 0 } id) return;
        foreach (var tab in Tabs.Where(candidate => candidate.GroupId == id).ToList())
            CloseTab(tab);
    }

    public void RefreshGroupLeaders()
    {
        foreach (var tab in Tabs)
        {
            tab.IsGroupLeader = false;
            tab.GroupCount = 0;
        }
        foreach (var group in Tabs.Where(tab => tab.IsGrouped).GroupBy(tab => tab.GroupId).ToList())
        {
            var members = group.ToList();
            members[0].IsGroupLeader = true;
            members[0].GroupCount = members.Count;
        }
        NotifyTabRows();
    }

    /// <summary>Move a live tab from another pane without recreating its navigation state.</summary>
    public bool TransferTabFrom(PaneViewModel source, TabViewModel tab, int targetIndex = -1,
        bool activate = true, bool preserveGroup = true)
    {
        if (ReferenceEquals(source, this)) return MoveTab(tab, targetIndex);
        int sourceIndex = source.Tabs.IndexOf(tab);
        if (sourceIndex < 0) return false;
        bool wasSelected = ReferenceEquals(source.SelectedTab, tab);

        if (wasSelected) source.SelectedTab = null;
        tab.PropertyChanged -= source.OnTabPropertyChanged;
        source.Tabs.Remove(tab);
        source.RefreshGroupLeaders();
        source.NotifyProjectStateChanged();
        if (source.Tabs.Count > 0 && wasSelected)
            source.SelectedTab = source.Tabs[Math.Min(sourceIndex, source.Tabs.Count - 1)];

        if (!preserveGroup)
        {
            tab.GroupId = null;
            tab.IsGroupLeader = false;
            tab.GroupCount = 0;
        }

        tab.PropertyChanged += OnTabPropertyChanged;
        if (targetIndex < 0 || targetIndex > Tabs.Count) targetIndex = Tabs.Count;
        Tabs.Insert(targetIndex, tab);
        NormalizePinnedTabs();
        RefreshGroupLeaders();
        if (activate) SelectedTab = tab;
        NotifyProjectStateChanged();

        if (source.Tabs.Count == 0) source.EmptyRequested?.Invoke(source);
        return true;
    }

    private void NotifyTabRows()
    {
        if (_batchingTabStructure) return;
        OnPropertyChanged(nameof(TopLevelTabs));
        OnPropertyChanged(nameof(ActiveGroupTabs));
        OnPropertyChanged(nameof(HasActiveGroup));
        OnPropertyChanged(nameof(SelectedTopTab));
    }

    /// <summary>Move another pane's live tabs here when a split pane collapses.</summary>
    public void AdoptTabsFrom(PaneViewModel source)
    {
        if (ReferenceEquals(this, source)) return;
        var movedTabs = source.Tabs.ToList();
        var selected = source.SelectedTab;
        string? projectId = source.ActiveProjectId;

        _loadingProject = true;
        source._loadingProject = true;
        try
        {
            source.SelectedTab = null;
            foreach (var tab in movedTabs) tab.PropertyChanged -= source.OnTabPropertyChanged;
            source.Tabs.Clear();

            foreach (var tab in movedTabs)
            {
                tab.PropertyChanged += OnTabPropertyChanged;
                Tabs.Add(tab);
            }
            SelectedTab = selected ?? Tabs.FirstOrDefault();
            ActiveProjectId = projectId;
        }
        finally
        {
            source._loadingProject = false;
            _loadingProject = false;
        }
        NotifyProjectStateChanged();
    }

    public void Dispose()
    {
        SelectedTab = null;
        foreach (var tab in Tabs)
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
            tab.Dispose();
        }
        Tabs.Clear();
    }

    /// <summary>Close every non-pinned tab except <paramref name="keep"/>.</summary>
    public void CloseOtherTabs(TabViewModel? keep)
    {
        if (keep is null) return;
        foreach (var tab in Tabs.Where(t => t != keep && !t.IsPinned).ToList())
            CloseTab(tab);
        SelectedTab = keep;
    }

    /// <summary>Close non-pinned tabs positioned to the right of <paramref name="tab"/>.</summary>
    public void CloseTabsToRight(TabViewModel? tab)
    {
        if (tab is null) return;
        int index = Tabs.IndexOf(tab);
        if (index < 0) return;
        foreach (var candidate in Tabs.Skip(index + 1).Where(t => !t.IsPinned).ToList())
            CloseTab(candidate);
        SelectedTab = tab;
    }

    /// <summary>Restore the most recently closed tab in this pane.</summary>
    public TabViewModel? ReopenClosedTab()
    {
        while (_closedTabs.Count > 0)
        {
            var closed = _closedTabs.Pop();
            if (!IsRestorableTarget(closed.Target)) continue;
            OnPropertyChanged(nameof(HasClosedTabs));
            return NewTab(closed.Target, activate: true, pinned: closed.IsPinned,
                groupId: closed.GroupId, groupName: closed.GroupName);
        }
        OnPropertyChanged(nameof(HasClosedTabs));
        return null;
    }

    private static bool IsRestorableTarget(string? path) =>
        path is TabViewModel.HomeToken or TabViewModel.DrivesToken
        || (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path));

    public void CycleTab(int dir)
    {
        if (Tabs.Count < 2 || SelectedTab is null) return;
        int idx = Tabs.IndexOf(SelectedTab);
        SelectedTab = Tabs[(idx + dir + Tabs.Count) % Tabs.Count];
    }
}
