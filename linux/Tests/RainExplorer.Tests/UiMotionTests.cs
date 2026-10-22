using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RainExplorer.Controls;
using RainExplorer.Helpers;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using Xunit;

namespace RainExplorer.Tests;

public sealed partial class UiTests
{
    private static async Task RenderFor(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        } while (DateTime.UtcNow < until);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task TabsAnimateArrivalReorderAndRemovalAndSurviveQuickChanges()
    {
        var (window, vm, pane) = Boot(SampleFolder());
        try
        {
            await RenderFor(300);
            var source = vm.LeftPane;
            var first = source.SelectedTab!;
            var bar = pane.FindControl<ListBox>("TabBar")!;
            var layer = pane.FindControl<Canvas>("TabAnimationLayer")!;
            var second = source.NewTab(TabViewModel.HomeToken);
            await RenderFor(30);
            var arriving = (Control)bar.ContainerFromItem(second)!;
            Assert.InRange(arriving.Opacity, 0.01, 0.999);
            Assert.NotNull(arriving.RenderTransform);
            Assert.InRange(arriving.RenderTransform!.Value.M22, 0.96, 0.9999);
            await RenderFor(200);
            Assert.Equal(1, arriving.Opacity);
            Assert.Null(arriving.RenderTransform);

            source.MoveTab(second, 0);
            await RenderFor(30);
            var moving = (Control)bar.ContainerFromItem(second)!;
            Assert.True(Math.Abs(moving.RenderTransform?.Value.M31 ?? 0) > 0.5,
                "Reordering must animate from the previous visual position.");
            await RenderFor(200);
            Assert.Null(moving.RenderTransform);

            source.CloseTab(second);
            Assert.DoesNotContain(second, source.Tabs);
            var removal = Assert.Single(layer.Children);
            await RenderFor(30);
            Assert.InRange(removal.Opacity, 0.001, 0.999);
            Assert.Null(removal.RenderTransform);
            await RenderFor(160);
            Assert.Empty(layer.Children);

            var third = source.NewTab(TabViewModel.HomeToken);
            await RenderFor(20);
            source.TogglePin(third);
            source.TogglePin(third);
            source.GroupTabs(first, third);
            await RenderFor(20);
            source.SelectedTab = source.NewTab(TabViewModel.DrivesToken);
            await RenderFor(20);
            source.SelectedTab = first;
            await RenderFor(20);
            source.CloseTab(third);
            await RenderFor(300);
            foreach (var row in new[] { bar, pane.FindControl<ListBox>("GroupTabBar")! })
                foreach (var tab in row.Items.OfType<TabViewModel>())
                    if (row.ContainerFromItem(tab) is Control container)
                    {
                        Assert.Equal(1, container.Opacity);
                        Assert.Null(container.RenderTransform);
                    }
            Assert.Empty(layer.Children);
            var plus = pane.FindControl<Button>("NewTabButton")!;
            var last = (Control)bar.ContainerFromItem(bar.Items.OfType<TabViewModel>().Last())!;
            Assert.Equal(last.TranslatePoint(default, bar)!.Value.X + last.Bounds.Width + 4,
                ((TranslateTransform)plus.RenderTransform!).X, precision: 1);
            Snapshot(window, "22-tabs-after-interrupted-motion");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DraggedTabsMakeRoomSmoothlyAndEscapeRestoresTheirPositions()
    {
        var (window, vm, pane) = Boot(SampleFolder());
        try
        {
            var first = vm.LeftPane.SelectedTab!;
            var second = vm.LeftPane.NewTab(TabViewModel.HomeToken);
            var third = vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            await RenderFor(300);
            var from = TabPoint(window, pane, first);
            var to = TabPoint(window, pane, third, 0.9);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Vector(8, 0), RawInputModifiers.LeftMouseButton);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            var bar = pane.FindControl<ListBox>("TabBar")!;
            var neighbour = (Control)bar.ContainerFromItem(second)!;
            double slotWidth = ((AdaptiveTabPanel)bar.ItemsPanelRoot!).Slots()[0].Width;
            await RenderFor(20);
            double offset = neighbour.RenderTransform!.Value.M31;
            Assert.InRange(offset, -slotWidth + 0.1, -0.1);
            Assert.Equal(new[] { first, second, third }, vm.LeftPane.Tabs);
            Assert.Equal(0, ((Control)bar.ContainerFromItem(first)!).Opacity);
            await RenderFor(180);
            WaitFor(() => Math.Abs(neighbour.RenderTransform!.Value.M31 + slotWidth) < 0.05, timeoutMs: 500);
            Assert.Equal(-slotWidth, neighbour.RenderTransform.Value.M31, precision: 1);
            Snapshot(window, "24-tab-drag-neighbour-preview");

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(to, MouseButton.Left);
            await RenderFor(250);
            Assert.Equal(new[] { first, second, third }, vm.LeftPane.Tabs);
            Assert.False(first.IsDragging);
            Assert.Null(neighbour.RenderTransform);
            Assert.Equal(1, ((Control)bar.ContainerFromItem(first)!).Opacity);

            Drag(window, TabPoint(window, pane, first), TabPoint(window, pane, third, 0.9));
            await RenderFor(250);
            Assert.Equal(new[] { second, third, first }, vm.LeftPane.Tabs);
            foreach (var tab in vm.LeftPane.Tabs)
            {
                var container = (Control)bar.ContainerFromItem(tab)!;
                Assert.Null(container.RenderTransform);
                Assert.Equal(1, container.Opacity);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProjectPopupAnimatesOpeningAndFinishesClosing()
    {
        var (window, _, pane) = Boot();
        var menu = new ContextMenu { ItemsSource = new[] { new MenuItem { Header = "Test project" } } };
        Motion.AnimateProjectMenu(menu);
        var openingFrames = new List<double>();
        var closingFrames = new List<double>();
        menu.TemplateApplied += (_, _) =>
        {
            var surface = menu.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "menuSurface");
            surface.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.OpacityProperty) openingFrames.Add(surface.Opacity);
            };
        };
        menu.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.OpacityProperty) closingFrames.Add(menu.Opacity);
        };
        try
        {
            menu.Open(pane.FindControl<Button>("ProjectsButton")!);
            await RenderFor(200);
            var surface = menu.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "menuSurface");
            Assert.Contains(openingFrames, opacity => opacity > 0 && opacity < 1);
            Assert.Equal(1, surface.Opacity);
            Assert.Null(surface.RenderTransform);
            menu.Close();
            Assert.True(menu.IsOpen);
            await RenderFor(180);
            Assert.Contains(closingFrames, opacity => opacity > 0 && opacity < 1);
            Assert.False(menu.IsOpen);
            Assert.Equal(1, menu.Opacity);
            Assert.Null(menu.RenderTransform);
            menu.Open(pane.FindControl<Button>("ProjectsButton")!);
            await RenderFor(20);
            menu.Close();
            await RenderFor(160);
            Assert.False(menu.IsOpen);
        }
        finally { menu.Close(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task TabHoverPreviewFadesInWithoutRestarting()
    {
        var (window, vm, pane) = Boot();
        try
        {
            await RenderFor(300);
            var bar = pane.FindControl<ListBox>("TabBar")!;
            var container = (Control)bar.ContainerFromItem(vm.LeftPane.SelectedTab!)!;
            var tooltip = Assert.IsType<ToolTip>(ToolTip.GetTip(container));
            for (int opening = 0; opening < 2; opening++)
            {
                ToolTip.SetIsOpen(container, true);
                var frames = new List<double>();
                for (int i = 0; i < 35; i++)
                {
                    await RenderFor(10);
                    frames.Add(tooltip.Opacity);
                }
                Assert.Contains(frames, opacity => opacity > 0 && opacity < 1);
                Assert.True(frames.Zip(frames.Skip(1), (before, after) => after >= before - .001).All(value => value),
                    $"Preview fade restarted: {string.Join(", ", frames.Select(value => value.ToString("F3")))}");
                Assert.Equal(1, tooltip.Opacity);
                ToolTip.SetIsOpen(container, false);
                await RenderFor(200);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingTabDoesNotFlashBeforeItsSnapshotIsRemoved()
    {
        var (window, vm, pane) = Boot();
        try
        {
            var closing = vm.LeftPane.NewTab(TabViewModel.HomeToken);
            await RenderFor(300);
            vm.LeftPane.CloseTab(closing);
            var layer = pane.FindControl<Canvas>("TabAnimationLayer")!;
            var ghost = Assert.Single(layer.Children.OfType<Image>());
            var opacities = new List<double>();
            ghost.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.OpacityProperty && ghost.GetVisualParent() == layer)
                    opacities.Add(ghost.Opacity);
            };
            await RenderFor(250);
            Assert.Empty(layer.Children);
            Assert.Contains(opacities, opacity => opacity > 0 && opacity < 1);
            Assert.True(opacities.Zip(opacities.Skip(1), (before, after) => after <= before + .001).All(value => value),
                $"Closing tab brightened before removal: {string.Join(", ", opacities.Select(value => value.ToString("F3")))}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingTabKeepsSlidingNeighbourInsideItsViewport()
    {
        var (window, vm, pane) = Boot();
        try
        {
            var closing = vm.LeftPane.NewTab(TabViewModel.HomeToken);
            var survivor = vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            await RenderFor(300);
            var bar = pane.FindControl<ListBox>("TabBar")!;
            vm.LeftPane.CloseTab(closing);
            await RenderFor(20);
            var container = (Control)bar.ContainerFromItem(survivor)!;
            foreach (var ancestor in container.GetVisualAncestors().Where(visual => visual.ClipToBounds))
            {
                var end = container.TranslatePoint(new Point(container.Bounds.Width, 0), ancestor)!.Value;
                Assert.True(end.X <= ancestor.Bounds.Width + .5,
                    $"Sliding tab clipped by {ancestor.GetType().Name}: right={end.X}, viewport={ancestor.Bounds.Width}.");
            }
            Snapshot(window, "tab-close-sliding-neighbour");
            await RenderFor(200);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TabHoverPreviewFitsWholeContentsWithoutTooltipClipping()
    {
        var (window, vm, pane) = Boot(SampleFolder());
        try
        {
            await RenderFor(350);
            var tab = vm.LeftPane.SelectedTab!;
            Assert.True(tab.HasPreviewImage);
            var host = pane.FindControl<Panel>("TabContentHost")!;
            Assert.Equal(host.Bounds.Width / host.Bounds.Height,
                tab.PreviewImage!.PixelSize.Width / (double)tab.PreviewImage.PixelSize.Height, precision: 2);
            var bar = pane.FindControl<ListBox>("TabBar")!;
            var container = (Control)bar.ContainerFromItem(tab)!;
            var tooltip = Assert.IsType<ToolTip>(ToolTip.GetTip(container));
            ToolTip.SetIsOpen(container, true);
            await RenderFor(200);
            var preview = tooltip.GetVisualDescendants().OfType<Image>().Single(i => i.Name == "TabPreviewImage");
            Assert.Equal(Stretch.Uniform, preview.Stretch);
            Assert.Same(tab.PreviewImage, preview.Source);
            var card = preview.FindAncestorOfType<Border>()!;
            Assert.True(card.Bounds.Width >= 480, $"Preview card width: {card.Bounds.Width}");
            Assert.True(tooltip.Bounds.Width >= 484, $"Tooltip width: {tooltip.Bounds.Width}");
            var top = TopLevel.GetTopLevel(tooltip)!;
            var origin = preview.TranslatePoint(default, top)!.Value;
            Assert.True(origin.X >= 0 && origin.X + preview.Bounds.Width <= top.Bounds.Width,
                "The tooltip must include both edges of the preview card.");
            Snapshot(top, "23-tab-hover-preview");
            ToolTip.SetIsOpen(container, false);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HeightRevealCanReverseAndCloseWithoutLeavingLayoutOverrides()
    {
        var content = new Border { Child = new Border { Width = 240, Height = 80 } };
        ExpandCollapseBehavior.SetIsExpanded(content, false);
        var window = new Window { Content = content, Width = 300, Height = 160 };
        window.Show();
        try
        {
            await RenderFor(50);
            Assert.False(content.IsVisible);
            ExpandCollapseBehavior.SetIsExpanded(content, true);
            await RenderFor(30);
            Assert.True(content.IsVisible);
            Assert.InRange(content.Height, 0.01, 79.99);
            ExpandCollapseBehavior.SetIsExpanded(content, false);
            await RenderFor(20);
            ExpandCollapseBehavior.SetIsExpanded(content, true);
            await RenderFor(200);
            Assert.True(double.IsNaN(content.Height));
            Assert.True(content.IsVisible);
            Assert.Equal(1, content.Opacity);
            ExpandCollapseBehavior.SetIsExpanded(content, false);
            await RenderFor(170);
            Assert.False(content.IsVisible);
            Assert.True(double.IsNaN(content.Height));
            Assert.Equal(0, content.Opacity);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ClosingTabsKeepsWidthsUntilPointerLeavesOrViewportChanges()
    {
        var (window, vm, pane) = Boot(SampleFolder());
        try
        {
            for (int i = 0; i < 6; i++) vm.LeftPane.NewTab(TabViewModel.HomeToken);
            Settle();
            var bar = pane.FindControl<ListBox>("TabBar")!;
            var panel = (AdaptiveTabPanel)bar.ItemsPanelRoot!;
            var first = vm.LeftPane.Tabs[0];
            double initial = ((Control)bar.ContainerFromItem(first)!).Bounds.Width;
            panel.LockCurrentWidths();
            vm.LeftPane.CloseTab(vm.LeftPane.Tabs[^1]);
            Settle();
            Assert.Equal(initial, ((Control)bar.ContainerFromItem(first)!).Bounds.Width, precision: 2);
            panel.UnlockWidths();
            Settle();
            Assert.True(((Control)bar.ContainerFromItem(first)!).Bounds.Width > initial);
            panel.LockCurrentWidths();
            window.Width = 900;
            Settle();
            Assert.True(((Control)bar.ContainerFromItem(first)!).Bounds.Width < initial);
        }
        finally { window.Close(); }
    }

    private static byte[] BitmapPixels(Bitmap bitmap)
    {
        int stride = bitmap.PixelSize.Width * 4;
        int length = stride * bitmap.PixelSize.Height;
        var memory = Marshal.AllocHGlobal(length);
        try
        {
            bitmap.CopyPixels(new PixelRect(bitmap.PixelSize), memory, length, stride);
            var pixels = new byte[length];
            Marshal.Copy(memory, pixels, 0, length);
            return pixels;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    public static IEnumerable<object[]> ClosingTabSnapshots()
    {
        foreach (string theme in ThemeService.Names) yield return [theme, false, false];
        yield return ["Violet", true, false];
        yield return ["Light", true, false];
        yield return ["Violet", false, true];
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingTabKeepsNeighbourSlideMovingTowardItsNewPosition(bool interrupted)
    {
        var (window, vm, pane) = Boot();
        try
        {
            var first = vm.LeftPane.Tabs[0];
            var second = vm.LeftPane.NewTab(TabViewModel.HomeToken);
            var survivor = vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            await RenderFor(300);
            var bar = pane.FindControl<ListBox>("TabBar")!;
            if (interrupted)
            {
                vm.LeftPane.CloseTab(first);
                await RenderFor(30);
            }
            double before = ((Control)bar.ContainerFromItem(survivor)!).TranslatePoint(default, bar)!.Value.X;
            vm.LeftPane.CloseTab(interrupted ? second : first);
            Dispatcher.UIThread.RunJobs();
            var container = (Control)bar.ContainerFromItem(survivor)!;
            double start = container.TranslatePoint(default, bar)!.Value.X;
            Assert.InRange(start, container.Bounds.X, before + .5);
            double previous = start;
            for (int i = 0; i < 5; i++)
            {
                await RenderFor(20);
                double current = container.TranslatePoint(default, bar)!.Value.X;
                Assert.InRange(current, container.Bounds.X, previous + .5);
                previous = current;
            }
            await RenderFor(250);
            Assert.Null(container.RenderTransform);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(8, 3)]
    public async Task CloseButtonKeepsTheFadingTabAtItsRenderedPosition(int count, int index)
    {
        var (window, vm, pane) = Boot();
        try
        {
            for (int i = 1; i < count; i++) vm.LeftPane.NewTab(TabViewModel.HomeToken);
            await RenderFor(300);
            var tab = vm.LeftPane.Tabs[index];
            vm.LeftPane.SelectedTab = tab;
            await RenderFor(200);
            var bar = pane.FindControl<ListBox>("TabBar")!;
            var layer = pane.FindControl<Canvas>("TabAnimationLayer")!;
            var container = (Control)bar.ContainerFromItem(tab)!;
            var close = container.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabClose"));
            var point = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            var origin = container.TranslatePoint(default, layer)!.Value;
            var size = container.Bounds.Size;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var ghost = Assert.Single(layer.Children.OfType<Image>());
            Assert.Equal(origin.X, Canvas.GetLeft(ghost), precision: 1);
            Assert.Equal(origin.Y, Canvas.GetTop(ghost), precision: 1);
            Assert.Equal(size.Width, ghost.Width, precision: 1);
            Assert.Equal(size.Height, ghost.Height, precision: 1);
            await RenderFor(30);
            var visibleOrigin = ghost.TranslatePoint(default, layer)!.Value;
            var visibleEnd = ghost.TranslatePoint(new Point(ghost.Bounds.Width, ghost.Bounds.Height), layer)!.Value;
            Assert.True(Math.Abs(visibleOrigin.X - origin.X) < .1 && Math.Abs(visibleOrigin.Y - origin.Y) < .1
                && Math.Abs(visibleEnd.X - visibleOrigin.X - size.Width) < .1
                && Math.Abs(visibleEnd.Y - visibleOrigin.Y - size.Height) < .1,
                $"Closing tab moved or shrank: original={origin}/{size}, visible={visibleOrigin}/{visibleEnd}.");
            Directory.CreateDirectory(SnapshotDir);
            using (var frame = window.CaptureRenderedFrame())
                frame!.Save(Path.Combine(SnapshotDir, $"tab-close-in-place-{count}-{index}.png"));
            await RenderFor(250);
            Assert.Empty(layer.Children);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingTabPreservesAnInterruptedArrivalOrSlide(bool slide)
    {
        var (window, vm, pane) = Boot();
        try
        {
            var first = vm.LeftPane.Tabs[0];
            await RenderFor(300);
            if (slide) vm.LeftPane.NewTab(TabViewModel.HomeToken);
            var closing = vm.LeftPane.NewTab(TabViewModel.HomeToken);
            if (slide)
            {
                await RenderFor(250);
                vm.LeftPane.CloseTab(first);
            }
            await RenderFor(30);
            var bar = pane.FindControl<ListBox>("TabBar")!;
            var layer = pane.FindControl<Canvas>("TabAnimationLayer")!;
            var container = (Control)bar.ContainerFromItem(closing)!;
            var origin = container.TranslatePoint(default, layer)!.Value;
            var end = container.TranslatePoint(new Point(container.Bounds.Width, container.Bounds.Height), layer)!.Value;
            double opacity = container.Opacity;
            vm.LeftPane.CloseTab(closing);
            var ghost = layer.Children.OfType<Image>().Last();
            Assert.Equal(origin.X, Canvas.GetLeft(ghost), precision: 1);
            Assert.Equal(origin.Y, Canvas.GetTop(ghost), precision: 1);
            Assert.Equal(end.X - origin.X, ghost.Width, precision: 1);
            Assert.Equal(end.Y - origin.Y, ghost.Height, precision: 1);
            Assert.Equal(opacity, ghost.Opacity, precision: 2);
            await RenderFor(250);
            Assert.Empty(layer.Children);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [MemberData(nameof(ClosingTabSnapshots))]
    public async Task ClosingTabSnapshotKeepsTheOriginalIconAndTextSize(string theme, bool groupChild, bool pinned)
    {
        var (window, vm, pane) = Boot(SampleFolder());
        try
        {
            ThemeService.ApplyTheme(theme);
            var closing = vm.LeftPane.NewTab(TabViewModel.HomeToken);
            if (groupChild) vm.LeftPane.GroupTabs(closing, vm.LeftPane.Tabs[0]);
            if (pinned) vm.LeftPane.TogglePin(closing);
            vm.LeftPane.SelectedTab = closing;
            await RenderFor(300);
            var bar = pane.FindControl<ListBox>(groupChild ? "GroupTabBar" : "TabBar")!;
            var container = (Control)bar.ContainerFromItem(closing)!;
            var size = new PixelSize((int)Math.Ceiling(container.Bounds.Width), (int)Math.Ceiling(container.Bounds.Height));
            using var before = new RenderTargetBitmap(size, new Vector(96, 96));
            before.Render(container);
            Directory.CreateDirectory(SnapshotDir);
            before.Save(Path.Combine(SnapshotDir, $"tab-close-live-{theme}-{groupChild}-{pinned}.png"));
            vm.LeftPane.CloseTab(closing);
            var layer = pane.FindControl<Canvas>("TabAnimationLayer")!;
            var ghost = layer.Children.OfType<Image>().Single(image => Math.Abs(image.Height - container.Bounds.Height) < .1);
            // Isolate the rendered image from the fade animation.
            Motion.Cancel(ghost);
            ghost.RenderTransform = null;
            ghost.Opacity = 1;
            ghost.Measure(container.Bounds.Size);
            ghost.Arrange(new Rect(container.Bounds.Size));
            using var after = new RenderTargetBitmap(size, new Vector(96, 96));
            after.Render(ghost);
            after.Save(Path.Combine(SnapshotDir, $"tab-close-snapshot-{theme}-{groupChild}-{pinned}.png"));
            byte[] expected = BitmapPixels(before), actual = BitmapPixels(after);
            double meanDifference = expected.Zip(actual, (a, b) => Math.Abs(a - b)).Average();
            Assert.True(meanDifference < .01,
                $"Closing tab changed its rendered appearance: mean pixel difference {meanDifference:F2}.");
        }
        finally { ThemeService.ApplyTheme(SettingsStore.Instance.Settings.Theme); window.Close(); }
    }

}
