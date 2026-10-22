using RainExplorer.Services;
using RainExplorer.ViewModels;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class PaneViewModelTabTests
{
    [Fact]
    public void SelectingUngroupedTabDoesNotRebuildTabRows()
    {
        using var pane = NewPaneWithTabs(out var first, out var second, out _);
        pane.SelectedTab = first;
        var changes = new List<string?>();
        pane.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        pane.SelectedTab = second;

        Assert.Same(second, pane.SelectedTab);
        Assert.Same(second, pane.SelectedTopTab);
        Assert.False(first.IsActive);
        Assert.True(second.IsActive);
        Assert.Contains(nameof(PaneViewModel.SelectedTab), changes);
        Assert.Contains(nameof(PaneViewModel.SelectedTopTab), changes);
        Assert.DoesNotContain(nameof(PaneViewModel.TopLevelTabs), changes);
        Assert.DoesNotContain(nameof(PaneViewModel.ActiveGroupTabs), changes);
    }

    [Fact]
    public void CloseInactiveRightTabPreservesSelectedInstance()
    {
        using var pane = NewPaneWithTabs(out var first, out var second, out var third);
        pane.SelectedTab = second;
        int selectionChanges = 0;
        pane.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PaneViewModel.SelectedTab)) selectionChanges++;
        };

        pane.CloseTab(third);

        Assert.Equal([first, second], pane.Tabs);
        Assert.Same(second, pane.SelectedTab);
        Assert.Equal(0, selectionChanges);
    }

    [Fact]
    public void CloseInactiveLeftTabPreservesSelectedInstance()
    {
        using var pane = NewPaneWithTabs(out var first, out var second, out var third);
        pane.SelectedTab = third;

        pane.CloseTab(first);

        Assert.Equal([second, third], pane.Tabs);
        Assert.Same(third, pane.SelectedTab);
    }

    [Fact]
    public void CloseActiveTabReturnsToMostRecentlySelectedTab()
    {
        using var pane = NewPaneWithTabs(out var first, out var second, out var third);
        pane.SelectedTab = first;
        pane.SelectedTab = second;
        pane.SelectedTab = third;

        pane.CloseTab(third);

        Assert.Same(second, pane.SelectedTab);
        Assert.False(third.IsActive);
        Assert.True(second.IsActive);

        pane.CloseTab(second);

        Assert.Same(first, pane.SelectedTab);
        Assert.False(second.IsActive);
        Assert.True(first.IsActive);
    }

    [Fact]
    public void ClosingNewlyActivatedTabReturnsToTabUsedBeforeIt()
    {
        using var pane = NewPaneWithTabs(out _, out var previous, out _);
        pane.SelectedTab = previous;
        var opened = AddTab(pane, new FileSystemService());
        pane.SelectedTab = opened;

        pane.CloseTab(opened);

        Assert.Same(previous, pane.SelectedTab);
    }

    [Fact]
    public void RepeatedCloseTargetsExactInstanceOnlyOnce()
    {
        using var pane = NewPaneWithTabs(out var first, out var second, out var third);
        pane.SelectedTab = first;

        pane.CloseTab(second);
        pane.CloseTab(second);

        Assert.Equal([first, third], pane.Tabs);
        Assert.Same(first, pane.SelectedTab);
    }

    [Fact]
    public void CloseBackgroundGroupPreservesSelectedInstance()
    {
        using var pane = NewPaneWithTabs(out var first, out var second, out var third);
        first.GroupId = "group";
        second.GroupId = "group";
        pane.RefreshGroupLeaders();
        pane.SelectedTab = third;

        pane.CloseGroup(first);

        Assert.Equal([third], pane.Tabs);
        Assert.Same(third, pane.SelectedTab);
    }

    [Fact]
    public void ClosingOneOfTwoGroupedTabsDissolvesTheRemainingGroup()
    {
        using var pane = NewPaneWithTabs(out var first, out var second, out _);
        first.GroupId = "group";
        second.GroupId = "group";
        pane.RefreshGroupLeaders();

        pane.CloseTab(second);

        Assert.Null(first.GroupId);
        Assert.False(first.IsGrouped);
        Assert.False(first.IsGroupLeader);
    }

    [Fact]
    public void RestoringGroupMembersSequentiallyKeepsTheirGroupIdentity()
    {
        using var pane = new PaneViewModel(new FileSystemService());

        var first = pane.NewTab(TabViewModel.HomeToken, activate: false,
            groupId: "group", groupName: "Saved group");
        var second = pane.NewTab(TabViewModel.HomeToken, activate: false,
            groupId: "group", groupName: "Saved group");

        Assert.Equal("group", first.GroupId);
        Assert.Equal("group", second.GroupId);
        Assert.True(first.IsGroupLeader);
        Assert.Equal(2, first.GroupCount);
    }

    [Fact]
    public void ReorderingGroupMemberKeepsItInTheGroup()
    {
        using var pane = NewPaneWithTabs(out var first, out var second, out var third);
        first.GroupId = "group";
        second.GroupId = "group";
        pane.RefreshGroupLeaders();

        Assert.True(pane.MoveTabWithinGroup(first, second, after: true));

        Assert.Equal([second, first, third], pane.Tabs);
        Assert.Equal("group", first.GroupId);
        Assert.Equal("group", second.GroupId);
        Assert.True(second.IsGroupLeader);
    }

    [Fact]
    public void MovingTabOutOfGroupKeepsTheRemainingGroupAndExtractsTheTab()
    {
        var fileSystem = new FileSystemService();
        using var pane = new PaneViewModel(fileSystem);
        var first = AddTab(pane, fileSystem);
        var extracted = AddTab(pane, fileSystem);
        var third = AddTab(pane, fileSystem);
        var outside = AddTab(pane, fileSystem);
        first.GroupId = "group";
        extracted.GroupId = "group";
        third.GroupId = "group";
        pane.RefreshGroupLeaders();

        Assert.True(pane.MoveTabOutOfGroup(extracted, outside, after: false));

        Assert.Null(extracted.GroupId);
        Assert.Equal("group", first.GroupId);
        Assert.Equal("group", third.GroupId);
        Assert.Equal([first, third, extracted, outside], pane.Tabs);
    }

    [Fact]
    public void MovingTabBesideGroupTreatsGroupAsOneTopLevelSlot()
    {
        using var pane = NewPaneWithTabs(out var first, out var second, out var third);
        second.GroupId = "group";
        third.GroupId = "group";
        pane.RefreshGroupLeaders();
        var fourth = AddTab(pane, new FileSystemService());

        Assert.True(pane.MoveTabBesideTopLevel(fourth, second, after: false));

        Assert.Equal([first, fourth, second, third], pane.Tabs);
        Assert.Equal("group", second.GroupId);
        Assert.Equal("group", third.GroupId);
    }

    [Fact]
    public void MovingGroupReordersEveryMemberAsOneBlock()
    {
        using var pane = NewPaneWithTabs(out var first, out var second, out var third);
        first.GroupId = "group";
        second.GroupId = "group";
        pane.RefreshGroupLeaders();

        Assert.True(pane.MoveGroupBesideTopLevel(first, third, after: true));

        Assert.Equal([third, first, second], pane.Tabs);
        Assert.True(first.IsGroupLeader);
        Assert.Equal("group", second.GroupId);
    }

    [Fact]
    public void TransferringGroupPreservesMembersAndUnrelatedSourceSelection()
    {
        var fileSystem = new FileSystemService();
        using var source = new PaneViewModel(fileSystem);
        using var target = new PaneViewModel(fileSystem);
        var first = AddTab(source, fileSystem);
        var second = AddTab(source, fileSystem);
        var outside = AddTab(source, fileSystem);
        var existing = AddTab(target, fileSystem);
        first.GroupId = "group";
        second.GroupId = "group";
        source.RefreshGroupLeaders();
        source.SelectedTab = outside;

        Assert.True(target.TransferGroupFrom(source, first, targetIndex: 0, activate: true));

        Assert.Equal([outside], source.Tabs);
        Assert.Same(outside, source.SelectedTab);
        Assert.Equal([first, second, existing], target.Tabs);
        Assert.Same(first, target.SelectedTab);
        Assert.Equal("group", first.GroupId);
        Assert.Equal("group", second.GroupId);
    }

    [Fact]
    public void TransferringOneOfTwoGroupedTabsDissolvesTheSourceOrphan()
    {
        var fileSystem = new FileSystemService();
        using var source = new PaneViewModel(fileSystem);
        using var target = new PaneViewModel(fileSystem);
        var first = AddTab(source, fileSystem);
        var second = AddTab(source, fileSystem);
        AddTab(source, fileSystem);
        AddTab(target, fileSystem);
        first.GroupId = "group";
        second.GroupId = "group";
        source.RefreshGroupLeaders();

        Assert.True(target.TransferTabFrom(source, first, preserveGroup: false));

        Assert.Null(second.GroupId);
        Assert.False(second.IsGrouped);
        Assert.Null(first.GroupId);
    }

    [Fact]
    public void TransferringActiveTabReturnsSourceToPreviousSelection()
    {
        var fileSystem = new FileSystemService();
        using var source = new PaneViewModel(fileSystem);
        using var target = new PaneViewModel(fileSystem);
        var previous = AddTab(source, fileSystem);
        var moved = AddTab(source, fileSystem);
        AddTab(source, fileSystem);
        AddTab(target, fileSystem);
        source.SelectedTab = previous;
        source.SelectedTab = moved;

        Assert.True(target.TransferTabFrom(source, moved, activate: false));

        Assert.Same(previous, source.SelectedTab);
        Assert.Contains(moved, target.Tabs);
    }

    private static PaneViewModel NewPaneWithTabs(
        out TabViewModel first, out TabViewModel second, out TabViewModel third)
    {
        var fileSystem = new FileSystemService();
        var pane = new PaneViewModel(fileSystem);
        first = AddTab(pane, fileSystem);
        second = AddTab(pane, fileSystem);
        third = AddTab(pane, fileSystem);
        return pane;
    }

    private static TabViewModel AddTab(PaneViewModel pane, FileSystemService fileSystem)
    {
        var tab = new TabViewModel(fileSystem) { CurrentPath = TabViewModel.HomeToken };
        pane.Tabs.Add(tab);
        return tab;
    }
}
