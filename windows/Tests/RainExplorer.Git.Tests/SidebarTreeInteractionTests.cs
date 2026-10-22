using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using RainExplorer.Models;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class SidebarTreeInteractionTests
{
    [Fact]
    public void ExpandingAndCollapsingParentPreservesActiveChildHighlight()
    {
        RunSta(() =>
        {
            var parentNode = SidebarNode.Folder("Rain", @"E:\Downloads\Rain", "folder", NodeKind.Pinned);
            var activeChildNode = SidebarNode.Folder("Boom Cloom ShoomaDoom My Room",
                @"E:\Downloads\Rain\Boom Cloom ShoomaDoom My Room", "folder", NodeKind.Folder);
            activeChildNode.IsActive = true;
            var tree = new TreeView();
            var parent = new TreeViewItem { DataContext = parentNode };
            var activeChild = new TreeViewItem { DataContext = activeChildNode };
            parent.Items.Add(activeChild);
            tree.Items.Add(parent);

            MainWindow.ToggleSidebarExpansion(parent);

            Assert.True(parent.IsExpanded);
            Assert.True(activeChildNode.IsActive);
            Assert.False(parentNode.IsActive);
            Assert.False(MainWindow.ShouldNavigateSidebarSelection(userInitiated: false, parentNode));
            activeChild.IsSelected = true;

            MainWindow.ToggleSidebarExpansion(parent);

            Assert.False(parent.IsExpanded);
            Assert.False(activeChild.IsSelected);
            Assert.True(parent.IsSelected);
            Assert.True(activeChildNode.IsActive);
            Assert.False(parentNode.IsActive);
            Assert.False(MainWindow.ShouldNavigateSidebarSelection(userInitiated: false, parentNode));
        });
    }

    [Fact]
    public void OnlyExplicitParentOrChildSelectionIsNavigationIntent()
    {
        var parent = SidebarNode.Folder("Rain", @"E:\Downloads\Rain", "folder", NodeKind.Pinned);
        var child = SidebarNode.Folder("Boom Cloom ShoomaDoom My Room",
            @"E:\Downloads\Rain\Boom Cloom ShoomaDoom My Room", "folder", NodeKind.Folder);

        Assert.False(MainWindow.ShouldNavigateSidebarSelection(userInitiated: false, parent));
        Assert.False(MainWindow.ShouldNavigateSidebarSelection(userInitiated: false, child));
        Assert.True(MainWindow.ShouldNavigateSidebarSelection(userInitiated: true, parent));
        Assert.True(MainWindow.ShouldNavigateSidebarSelection(userInitiated: true, child));
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
