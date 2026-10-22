using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using RainExplorer.Controls;
using RainExplorer.Helpers;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using Xunit;

namespace RainExplorer.Tests;

public sealed partial class UiTests
{
    private static void PlaceHeadlessWindowBeside(MainWindow window)
    {
        // HeadlessWindowImpl ignores Window.Position in PointToScreen. Separate
        // the rendered surfaces explicitly so screen hit tests are unambiguous.
        window.Width = 2600;
        window.FindControl<Border>("RootBorder")!.Margin = new Thickness(1400, 0, 0, 0);
    }

    private static MainWindow[] VisibleRainWindows() =>
        ((IEnumerable<PaneView>)typeof(PaneView).GetField("LoadedPaneViews", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!).Select(p => TopLevel.GetTopLevel(p)).OfType<MainWindow>().Distinct().ToArray();

    [AvaloniaFact]
    public async Task BlankSpaceAndChromeClicksClearSelectionButModifiersAndScrollbarsPreserveIt()
    {
        var (window, vm, pane) = Boot(SampleFolder());
        try
        {
            await RenderFor(300);
            var list = FileList(pane);
            var file = list.Items.OfType<FileItem>().First();
            var blank = list.TranslatePoint(new Point(list.Bounds.Width - 45, list.Bounds.Height - 30), window)!.Value;
            list.SelectedItem = file;
            window.MouseDown(blank, MouseButton.Left);
            window.MouseUp(blank, MouseButton.Left);
            Assert.Empty(list.SelectedItems!);

            list.SelectedItem = file;
            window.MouseDown(blank, MouseButton.Left, RawInputModifiers.Control);
            window.MouseUp(blank, MouseButton.Left, RawInputModifiers.Control);
            Assert.True(list.SelectedItems!.Contains(file));

            var header = pane.FindControl<Grid>("HeaderRow")!;
            var chrome = header.TranslatePoint(new Point(20, header.Bounds.Height / 2), window)!.Value;
            window.MouseDown(chrome, MouseButton.Left);
            window.MouseUp(chrome, MouseButton.Left);
            Assert.Empty(list.SelectedItems!);

            list.SelectedItem = file;
            for (int i = 0; i < 80; i++) File.WriteAllText(Path.Combine(Path.GetDirectoryName(file.FullPath)!, $"scroll-{i}.txt"), "test");
            var reload = vm.LeftPane.SelectedTab!.ReloadAsync();
            WaitFor(() => reload.IsCompleted);
            Assert.True(list.Items.Count > 70);
            await RenderFor(700);
            file = list.Items.OfType<FileItem>().First();
            list.SelectedItem = file;
            var scrollbar = list.GetVisualDescendants().OfType<ScrollBar>().First(s => s.IsEffectivelyVisible && s.Bounds.Width > 0 && s.Bounds.Height > 0);
            var scrollPoint = scrollbar.TranslatePoint(new Point(scrollbar.Bounds.Width / 2, scrollbar.Bounds.Height / 2), window)!.Value;
            Assert.NotNull((window.InputHitTest(scrollPoint) as Visual)?.FindAncestorOfType<ScrollBar>(includeSelf: true));
            Assert.True(list.SelectedItems!.Contains(file), "Scrollbar fixture did not select the current row.");
            window.MouseDown(scrollPoint, MouseButton.Left);
            Assert.True(list.SelectedItems!.Contains(file), "Scrollbar press cleared selection.");
            window.MouseUp(scrollPoint, MouseButton.Left);
            Assert.True(list.SelectedItems!.Contains(file), "Scrollbar release cleared selection.");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DragCardUsesEveryThemeAndSuppressesPreviewsUntilRelease()
    {
        var (window, vm, pane) = Boot();
        try
        {
            var first = vm.LeftPane.SelectedTab!;
            vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            await RenderFor(300);
            var bar = pane.FindControl<ListBox>("TabBar")!;
            var from = TabPoint(window, pane, first, 0.5);
            var container = (Control)bar.ContainerFromItem(first)!;
            ToolTip.SetIsOpen(container, true);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Vector(8, 0), RawInputModifiers.LeftMouseButton);
            Assert.False(ToolTip.GetIsOpen(container));
            var popup = Assert.Single(pane.FindControl<Canvas>("TabAnimationLayer")!.Children.OfType<Popup>());
            Assert.False(popup.IsHitTestVisible);
            var surface = Assert.IsType<Border>(popup.Child);
            var card = Assert.IsType<Border>(surface.Child);
            foreach (string theme in ThemeService.Names)
            {
                ThemeService.ApplyTheme(theme);
                await RenderFor(30);
                Assert.Same(Application.Current!.Resources["BgRaised"], surface.Background);
                Assert.Same(Application.Current!.Resources["Glass4"], card.Background);
                Assert.Equal(window.ActualThemeVariant, surface.ActualThemeVariant);
                Assert.Equal(255, Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color.A);
            }
            foreach (var c in bar.GetRealizedContainers()) Assert.False(c.GetValue(ToolTip.ServiceEnabledProperty));
            await RenderFor(650);
            Assert.All(bar.GetRealizedContainers(), c => Assert.False(ToolTip.GetIsOpen(c)));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(from, MouseButton.Left);
            Assert.Empty(pane.FindControl<Canvas>("TabAnimationLayer")!.Children.OfType<Popup>());
            Assert.All(bar.GetRealizedContainers(), c => Assert.True(c.GetValue(ToolTip.ServiceEnabledProperty)));
        }
        finally { ThemeService.ApplyTheme(SettingsStore.Instance.Settings.Theme); window.Close(); }
    }

    [AvaloniaFact]
    public async Task CrossingTheNextTabsCentreMakesRoomWithoutAccidentallyGrouping()
    {
        var (window, vm, pane) = Boot();
        try
        {
            var first = vm.LeftPane.SelectedTab!;
            var second = vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            var third = vm.LeftPane.NewTab(SampleFolder());
            await RenderFor(300);
            var from = TabPoint(window, pane, first, 0.5);
            var to = TabPoint(window, pane, second, 0.55);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Vector(8, 0), RawInputModifiers.LeftMouseButton);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            await RenderFor(650);
            var bar = pane.FindControl<ListBox>("TabBar")!;
            var neighbour = (Control)bar.ContainerFromItem(second)!;
            Assert.True(neighbour.RenderTransform!.Value.M31 < -neighbour.Bounds.Width + 1);
            Assert.False(second.IsGroupDropTarget);
            Snapshot(window, "30-tab-crossing-midpoint");
            window.MouseUp(to, MouseButton.Left);
            await RenderFor(200);
            Assert.Equal(new[] { second, first, third }, vm.LeftPane.Tabs);
            Assert.False(first.IsGrouped);

            var surface = pane.FindControl<Panel>("TopTabDropSurface")!;
            var pastEnd = surface.TranslatePoint(new Point(surface.Bounds.Width - 8, 12), window)!.Value;
            Drag(window, TabPoint(window, pane, second, 0.5), pastEnd);
            Assert.Equal(new[] { first, third, second }, vm.LeftPane.Tabs);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DraggingBetweenWindowsPreservesLiveTabsAndUsesTheDestinationInsertionGap()
    {
        var (source, vm, pane) = Boot(SampleFolder());
        var (destination, destVm, destPane) = Boot();
        try
        {
            source.Position = new PixelPoint(20, 20);
            PlaceHeadlessWindowBeside(destination);
            var moved = vm.LeftPane.SelectedTab!;
            vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            var existing = destVm.LeftPane.SelectedTab!;
            await RenderFor(350);
            var from = TabPoint(source, pane, moved, 0.5);
            var targetLocal = TabPoint(destination, destPane, existing, 0.1);
            var to = source.PointToClient(destination.PointToScreen(targetLocal));
            source.MouseDown(from, MouseButton.Left);
            source.MouseMove(from + new Vector(8, 0), RawInputModifiers.LeftMouseButton);
            source.MouseMove(to, RawInputModifiers.LeftMouseButton);
            await RenderFor(160);
            var bar = destPane.FindControl<ListBox>("TabBar")!;
            Assert.True((((Control)bar.ContainerFromItem(existing)!).RenderTransform?.Value.M31 ?? 0) > 10,
                $"No incoming gap: dragging={moved.IsDragging}, from={from}, to={to}, target={targetLocal}, source={source.Position}, dest={destination.Position}, bar={bar.Bounds}, ghost={pane.FindControl<Canvas>("TabAnimationLayer")!.Children.Count}");
            Assert.False(((Control)bar.ContainerFromItem(existing)!).GetValue(ToolTip.ServiceEnabledProperty));
            source.MouseUp(to, MouseButton.Left);
            await RenderFor(250);
            Assert.DoesNotContain(moved, vm.LeftPane.Tabs);
            Assert.Equal(new[] { moved, existing }, destVm.LeftPane.Tabs);
            Assert.Same(moved, destVm.LeftPane.SelectedTab);
            Assert.False(moved.IsDragging);
            Assert.All(bar.GetRealizedContainers(), c => Assert.True(c.GetValue(ToolTip.ServiceEnabledProperty)));
        }
        finally { source.Close(); destination.Close(); }
    }

    [AvaloniaFact]
    public async Task DraggingBelowTheStripTearsOffAndTheLastTabCanMoveBackToAnotherWindow()
    {
        var (window, vm, pane) = Boot(SampleFolder());
        var before = VisibleRainWindows();
        try
        {
            var moved = vm.LeftPane.SelectedTab!;
            var remaining = vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            await RenderFor(300);
            var contentPoint = pane.TranslatePoint(new Point(300, 250), window)!.Value;
            Drag(window, TabPoint(window, pane, moved, 0.5), contentPoint);
            await RenderFor(300);
            var torn = Assert.Single(VisibleRainWindows().Except(before));
            Assert.Equal(new[] { remaining }, vm.LeftPane.Tabs);
            var tornVm = (MainViewModel)torn.DataContext!;
            Assert.Same(moved, Assert.Single(tornVm.LeftPane.Tabs));
            PlaceHeadlessWindowBeside(torn);
            window.Position = new PixelPoint(20, 20);
            var tornPane = torn.FindControl<PaneView>("LeftPaneView")!;
            await RenderFor(250);
            var to = torn.PointToClient(window.PointToScreen(TabPoint(window, pane, remaining, 0.1)));
            Drag(torn, TabPoint(torn, tornPane, moved, 0.5), to);
            Assert.False(torn.IsVisible);
            Assert.Same(moved, vm.LeftPane.SelectedTab);
            Assert.Contains(moved, vm.LeftPane.Tabs);
            Assert.True(window.IsVisible);
        }
        finally
        {
            foreach (var extra in VisibleRainWindows().Except(before)) extra.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task GroupHeaderTransfersItsMembersAndChildDragDoesNotTransferTheWholeGroup()
    {
        var (window, vm, pane) = Boot();
        var (targetWindow, targetVm, targetPane) = Boot();
        try
        {
            window.Position = new PixelPoint(20, 20);
            PlaceHeadlessWindowBeside(targetWindow);
            var first = vm.LeftPane.SelectedTab!;
            var second = vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            vm.LeftPane.NewTab(SampleFolder());
            vm.LeftPane.GroupTabs(first, second);
            vm.LeftPane.SelectedTab = first;
            await RenderFor(350);
            var to = window.PointToClient(targetWindow.PointToScreen(TabPoint(targetWindow, targetPane, targetVm.LeftPane.SelectedTab!, 0.1)));
            Drag(window, TabPoint(window, pane, first.IsGroupLeader ? first : second, 0.5), to);
            await RenderFor(250);
            Assert.Contains(first, targetVm.LeftPane.Tabs);
            Assert.Contains(second, targetVm.LeftPane.Tabs);
            Assert.Equal(first.GroupId, second.GroupId);
            Assert.NotNull(first.GroupId);

            var childBar = targetPane.FindControl<ListBox>("GroupTabBar")!;
            Assert.True(childBar.ContainerFromItem(first) is Control, $"Missing group child: selected={targetVm.LeftPane.SelectedTab?.TopLevelTitle}, members={targetVm.LeftPane.ActiveGroupTabs.Count}, group={first.GroupId}, barItems={childBar.Items.Count}");
            var child = (Control)childBar.ContainerFromItem(first)!;
            var childPoint = child.TranslatePoint(new Point(child.Bounds.Width / 2, child.Bounds.Height / 2), targetWindow)!.Value;
            var back = targetWindow.PointToClient(window.PointToScreen(TabPoint(window, pane, vm.LeftPane.SelectedTab!, 0.1)));
            Drag(targetWindow, childPoint, back);
            Assert.Contains(first, vm.LeftPane.Tabs);
            Assert.Contains(second, targetVm.LeftPane.Tabs);
            Assert.False(first.IsGrouped);
        }
        finally { window.Close(); targetWindow.Close(); }
    }

    [AvaloniaFact]
    public async Task TearingOffTheGroupsFirstChildMovesOnlyThatChild()
    {
        var (window, vm, pane) = Boot();
        var before = VisibleRainWindows();
        try
        {
            var first = vm.LeftPane.SelectedTab!;
            var second = vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            vm.LeftPane.GroupTabs(first, second);
            vm.LeftPane.SelectedTab = first;
            await RenderFor(350);
            var childBar = pane.FindControl<ListBox>("GroupTabBar")!;
            var leader = first.IsGroupLeader ? first : second;
            var child = (Control)childBar.ContainerFromItem(leader)!;
            var from = child.TranslatePoint(new Point(child.Bounds.Width / 2, child.Bounds.Height / 2), window)!.Value;
            var to = pane.TranslatePoint(new Point(300, 250), window)!.Value;
            Drag(window, from, to);
            await RenderFor(250);
            var torn = Assert.Single(VisibleRainWindows().Except(before));
            Assert.Same(leader, Assert.Single(((MainViewModel)torn.DataContext!).LeftPane.Tabs));
            Assert.Single(vm.LeftPane.Tabs);
            Assert.DoesNotContain(leader, vm.LeftPane.Tabs);
            Assert.False(leader.IsGrouped);
        }
        finally
        {
            foreach (var extra in VisibleRainWindows().Except(before)) extra.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PinnedTabsKeepTheirBoundaryWhenDraggingAcrossRegularTabs()
    {
        var (window, vm, pane) = Boot();
        try
        {
            var pinned = vm.LeftPane.SelectedTab!;
            vm.LeftPane.TogglePin(pinned);
            var first = vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            var second = vm.LeftPane.NewTab(SampleFolder());
            await RenderFor(300);
            Drag(window, TabPoint(window, pane, second, 0.5), TabPoint(window, pane, pinned, 0.5));
            Assert.Equal(new[] { pinned, second, first }, vm.LeftPane.Tabs);
            Drag(window, TabPoint(window, pane, pinned, 0.5), TabPoint(window, pane, first, 0.8));
            Assert.Equal(new[] { pinned, second, first }, vm.LeftPane.Tabs);
            Assert.True(pinned.IsPinned);
            Assert.False(pinned.IsDragging);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TabDragSurvivesTheListsPressCaptureBeingReleasedBeforeTheDragStarts()
    {
        var (window, vm, pane) = Boot();
        try
        {
            var first = vm.LeftPane.SelectedTab!;
            var second = vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            await RenderFor(300);
            IPointer? pointer = null;
            window.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            var bar = pane.FindControl<ListBox>("TabBar")!;
            var from = TabPoint(window, pane, first, 0.5);
            var to = TabPoint(window, pane, second, 0.8);
            window.MouseDown(from, MouseButton.Left);
            pointer!.Capture(bar);
            pointer.Capture(null);
            window.MouseMove(from + new Vector(8, 0), RawInputModifiers.LeftMouseButton);
            Assert.True(first.IsDragging);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.MouseUp(to, MouseButton.Left);
            Assert.Equal(new[] { second, first }, vm.LeftPane.Tabs);
            Assert.False(first.IsDragging);
            Assert.Null(pointer.Captured);
        }
        finally { window.Close(); }
    }
    private static Point SplitEdgePoint(MainWindow window, bool onLeft)
    {
        var host = window.FindControl<Grid>("PaneHost")!;
        return host.TranslatePoint(new Point(onLeft ? 20 : host.Bounds.Width - 20, host.Bounds.Height / 2), window)!.Value;
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HoldingATabAtEitherEdgePreviewsAndCreatesASplitWithLiveTabs(bool onLeft)
    {
        var (window, vm, pane) = Boot(SampleFolder());
        try
        {
            var dragged = vm.LeftPane.SelectedTab!;
            var remaining = vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            vm.LeftPane.SelectedTab = dragged;
            await RenderFor(300);
            var from = TabPoint(window, pane, dragged, 0.5);
            var to = SplitEdgePoint(window, onLeft);
            IPointer? pointer = null;
            window.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Vector(8, 0), RawInputModifiers.LeftMouseButton);
            Assert.Same(remaining, vm.LeftPane.DisplayedTab);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            var preview = window.FindControl<Border>("TabSplitPreview")!;
            Assert.False(preview.IsVisible);
            await RenderFor(500);
            Assert.True(preview.IsVisible);
            Assert.Same(remaining, vm.LeftPane.DisplayedTab);
            Assert.Same(dragged, vm.LeftPane.SelectedTab);
            Assert.False(vm.IsSplit);
            Assert.Equal(onLeft ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right,
                preview.HorizontalAlignment);
            Assert.NotNull(window.FindControl<Image>("TabSplitPreviewImage")!.Source);
            foreach (string theme in ThemeService.Names)
            {
                ThemeService.ApplyTheme(theme);
                await RenderFor(30);
                Assert.Same(Application.Current!.Resources["BgRaised"], preview.Background);
                Assert.Same(Application.Current.Resources["AccentLine"], preview.BorderBrush);
                Snapshot(window, $"split-preview-{(onLeft ? "left" : "right")}-{theme.ToLowerInvariant()}");
            }
            window.MouseUp(to, MouseButton.Left);
            await RenderFor(200);
            Assert.True(vm.IsSplit);
            var destination = onLeft ? vm.LeftPane : vm.RightPane!;
            var survivor = onLeft ? vm.RightPane! : vm.LeftPane;
            Assert.Same(dragged, Assert.Single(destination.Tabs));
            Assert.Same(remaining, Assert.Single(survivor.Tabs));
            Assert.Same(dragged, destination.DisplayedTab);
            Assert.Same(destination, vm.ActivePane);
            Assert.False(dragged.IsDragging);
            Assert.Null(pointer!.Captured);
            Assert.False(preview.IsVisible);
            Snapshot(window, onLeft ? "split-drop-left" : "split-drop-right");
        }
        finally { ThemeService.ApplyTheme(SettingsStore.Instance.Settings.Theme); window.Close(); }
    }

    [AvaloniaFact]
    public async Task LeavingTheEdgeCancelsPreviewAndReturningToTheHeaderRestoresContents()
    {
        var (window, vm, pane) = Boot(SampleFolder());
        try
        {
            var dragged = vm.LeftPane.SelectedTab!;
            var remaining = vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            vm.LeftPane.SelectedTab = dragged;
            await RenderFor(300);
            var from = TabPoint(window, pane, dragged, 0.5);
            var edge = SplitEdgePoint(window, false);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(edge, RawInputModifiers.LeftMouseButton);
            await RenderFor(500);
            var preview = window.FindControl<Border>("TabSplitPreview")!;
            Assert.True(preview.IsVisible);
            window.MouseMove(from, RawInputModifiers.LeftMouseButton);
            Assert.False(preview.IsVisible);
            Assert.Same(dragged, vm.LeftPane.DisplayedTab);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(from, MouseButton.Left);
            Assert.False(vm.IsSplit);
            Assert.Equal(new[] { dragged, remaining }, vm.LeftPane.Tabs);
            Assert.Same(dragged, vm.LeftPane.SelectedTab);
            Assert.False(dragged.IsDragging);
            await RenderFor(450);
            Assert.False(preview.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task EscapeCancelsAnArmedSplitAndSingleTabDraggingShowsBlankContents()
    {
        var (window, vm, pane) = Boot(SampleFolder());
        try
        {
            var dragged = vm.LeftPane.SelectedTab!;
            await RenderFor(300);
            var from = TabPoint(window, pane, dragged, 0.5);
            var edge = SplitEdgePoint(window, false);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(edge, RawInputModifiers.LeftMouseButton);
            Assert.Null(vm.LeftPane.DisplayedTab);
            Assert.False(pane.FindControl<Grid>("TabContentHost")!.IsVisible);
            Assert.Empty(FileList(pane).Items);
            await RenderFor(500);
            Assert.False(window.FindControl<Border>("TabSplitPreview")!.IsVisible);
            Snapshot(window, "tab-drag-empty-source");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(edge, MouseButton.Left);
            Assert.Same(dragged, vm.LeftPane.DisplayedTab);
            Assert.True(pane.FindControl<Grid>("TabContentHost")!.IsVisible);

            vm.LeftPane.NewTab(TabViewModel.DrivesToken, activate: false);
            await RenderFor(200);
            from = TabPoint(window, pane, dragged, 0.5);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(edge, RawInputModifiers.LeftMouseButton);
            await RenderFor(500);
            Assert.True(window.FindControl<Border>("TabSplitPreview")!.IsVisible);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(edge, MouseButton.Left);
            Assert.False(vm.IsSplit);
            Assert.Same(dragged, vm.LeftPane.DisplayedTab);
            Assert.False(window.FindControl<Border>("TabSplitPreview")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupHeadersDockTogetherButChildTabsDockIndividually(bool childDrag)
    {
        var (window, vm, pane) = Boot();
        try
        {
            var first = vm.LeftPane.SelectedTab!;
            var second = vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            var remaining = vm.LeftPane.NewTab(SampleFolder());
            vm.LeftPane.GroupTabs(first, second);
            vm.LeftPane.SelectedTab = first;
            await RenderFor(300);
            var leader = first.IsGroupLeader ? first : second;
            var bar = pane.FindControl<ListBox>(childDrag ? "GroupTabBar" : "TabBar")!;
            var container = (Control)bar.ContainerFromItem(leader)!;
            var from = container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)!.Value;
            var to = SplitEdgePoint(window, false);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            Assert.NotSame(leader, vm.LeftPane.DisplayedTab);
            if (!childDrag) Assert.Same(remaining, vm.LeftPane.DisplayedTab);
            await RenderFor(500);
            Assert.True(window.FindControl<Border>("TabSplitPreview")!.IsVisible);
            window.MouseUp(to, MouseButton.Left);
            Assert.True(vm.IsSplit);
            Assert.Contains(leader, vm.RightPane!.Tabs);
            if (childDrag)
            {
                Assert.Single(vm.RightPane.Tabs);
                Assert.False(leader.IsGrouped);
                Assert.Contains(ReferenceEquals(leader, first) ? second : first, vm.LeftPane.Tabs);
            }
            else
            {
                Assert.Equal(2, vm.RightPane.Tabs.Count);
                Assert.Equal(first.GroupId, second.GroupId);
                Assert.NotNull(first.GroupId);
                Assert.Same(first, vm.RightPane.SelectedTab);
            }
            Assert.Contains(remaining, vm.LeftPane.Tabs);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task EdgeDropIntoAnExistingSplitReusesItsPaneAndCollapsesAnEmptySource()
    {
        var (window, vm, pane) = Boot(SampleFolder());
        try
        {
            var dragged = vm.LeftPane.SelectedTab!;
            vm.ToggleSplitCommand.Execute(null);
            var existing = vm.RightPane!.SelectedTab!;
            await RenderFor(300);
            var from = TabPoint(window, pane, dragged, 0.5);
            var to = SplitEdgePoint(window, false);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            await RenderFor(500);
            Assert.True(window.FindControl<Border>("TabSplitPreview")!.IsVisible);
            window.MouseUp(to, MouseButton.Left);
            Assert.False(vm.IsSplit);
            Assert.Equal(new[] { existing, dragged }, vm.LeftPane.Tabs);
            Assert.Same(dragged, vm.LeftPane.SelectedTab);
            Assert.True(window.IsVisible);
            Assert.False(window.FindControl<Border>("TabSplitPreview")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EdgeDropFromAnotherWindowCreatesASplitAndClosesTheEmptySource(bool onLeft)
    {
        var (sourceWindow, sourceVm, sourcePane) = Boot(SampleFolder());
        var (destination, vm, _) = Boot();
        try
        {
            PlaceHeadlessWindowBeside(destination);
            var dragged = sourceVm.LeftPane.SelectedTab!;
            var existing = vm.LeftPane.SelectedTab!;
            await RenderFor(300);
            var from = TabPoint(sourceWindow, sourcePane, dragged, 0.5);
            var to = sourceWindow.PointToClient(destination.PointToScreen(SplitEdgePoint(destination, onLeft)));
            sourceWindow.MouseDown(from, MouseButton.Left);
            sourceWindow.MouseMove(from + new Vector(8, 0), RawInputModifiers.LeftMouseButton);
            sourceWindow.MouseMove(to, RawInputModifiers.LeftMouseButton);
            await RenderFor(500);
            Assert.True(destination.FindControl<Border>("TabSplitPreview")!.IsVisible);
            sourceWindow.MouseUp(to, MouseButton.Left);
            Assert.False(sourceWindow.IsVisible);
            Assert.True(destination.IsVisible);
            Assert.True(vm.IsSplit);
            Assert.Same(dragged, Assert.Single((onLeft ? vm.LeftPane : vm.RightPane!).Tabs));
            Assert.Same(existing, Assert.Single((onLeft ? vm.RightPane! : vm.LeftPane).Tabs));
            Assert.False(dragged.IsDragging);
            Assert.False(destination.FindControl<Border>("TabSplitPreview")!.IsVisible);
        }
        finally { sourceWindow.Close(); destination.Close(); }
    }

    [AvaloniaFact]
    public async Task ChangingSidesRestartsTheHoverAndCaptureLossRestoresTheSource()
    {
        var (window, vm, pane) = Boot(SampleFolder());
        try
        {
            var dragged = vm.LeftPane.SelectedTab!;
            vm.LeftPane.NewTab(TabViewModel.DrivesToken, activate: false);
            await RenderFor(300);
            IPointer? pointer = null;
            window.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer,
                RoutingStrategies.Tunnel, handledEventsToo: true);
            var from = TabPoint(window, pane, dragged, 0.5);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(SplitEdgePoint(window, false), RawInputModifiers.LeftMouseButton);
            await RenderFor(500);
            var preview = window.FindControl<Border>("TabSplitPreview")!;
            Assert.True(preview.IsVisible);
            window.MouseMove(SplitEdgePoint(window, true), RawInputModifiers.LeftMouseButton);
            Assert.False(preview.IsVisible);
            await RenderFor(500);
            Assert.True(preview.IsVisible);
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, preview.HorizontalAlignment);
            pointer!.Capture(null);
            Assert.False(preview.IsVisible);
            Assert.False(dragged.IsDragging);
            Assert.Same(dragged, vm.LeftPane.DisplayedTab);
            Assert.False(vm.IsSplit);
            window.MouseUp(from, MouseButton.Left);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1200, false)]
    [InlineData(1200, true)]
    [InlineData(1815, false)]
    [InlineData(1815, true)]
    public async Task HoldingATabOverASideOfTheBrowsingAreaArmsTheSplit(double width, bool onLeft)
    {
        var (window, vm, pane) = Boot();
        try
        {
            window.Width = width;
            window.Height = width == 1815 ? 1003 : 760;
            var dragged = vm.LeftPane.SelectedTab!;
            var remaining = vm.LeftPane.NewTab(SampleFolder());
            vm.LeftPane.SelectedTab = dragged;
            await RenderFor(300);
            var from = TabPoint(window, pane, dragged, 0.5);
            var host = window.FindControl<Grid>("PaneHost")!;
            var to = host.TranslatePoint(new Point(host.Bounds.Width * (onLeft ? 0.25 : 0.75),
                host.Bounds.Height * 0.45), window)!.Value;
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Vector(8, 0), RawInputModifiers.LeftMouseButton);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            await RenderFor(500);
            Assert.True(window.FindControl<Border>("TabSplitPreview")!.IsVisible,
                $"No split preview at the {(onLeft ? "left" : "right")} quarter of a {width}px window.");
            if (width == 1815) Snapshot(window, onLeft ? "wide-side-preview-left" : "wide-side-preview-right");
            window.MouseUp(to, MouseButton.Left);
            Assert.True(vm.IsSplit);
            Assert.Same(dragged, Assert.Single((onLeft ? vm.LeftPane : vm.RightPane!).Tabs));
            Assert.Same(remaining, Assert.Single((onLeft ? vm.RightPane! : vm.LeftPane).Tabs));
        }
        finally { window.Close(); }
    }

}
