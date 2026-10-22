using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RainExplorer.Controls;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using RainExplorer.Views;
using Xunit;

namespace RainExplorer.Tests;

/// <summary>
/// Drives the real MainWindow on Avalonia's headless platform (Skia rendering, no
/// display): navigation, keyboard file commands, undo, and screenshots that are saved
/// next to the test output for visual review (RAIN_SNAPSHOT_DIR overrides the folder).
/// </summary>
public sealed partial class UiTests
{
    private static readonly string SnapshotDir =
        Environment.GetEnvironmentVariable("RAIN_SNAPSHOT_DIR") is { Length: > 0 } dir ? dir
            : Path.Combine(TestEnvironment.Root, "snapshots");

    private static (MainWindow Window, MainViewModel Vm, PaneView Pane) Boot(string? folder = null)
    {
        var settings = SettingsStore.Instance.Settings;
        settings.RenameMode = RenameMode.Dialog;
        settings.DeleteBehavior = DeleteBehavior.Recycle;
        settings.ViewLayout = ViewLayout.Details;
        settings.ShowPreviewPane = false;
        ThemeService.ApplyTheme(settings.Theme);
        ThemeService.ApplyDensity(settings.Density);
        ThemeService.ApplyFont(settings.FontFamily);

        var window = new MainWindow { Width = 1200, Height = 760 };
        window.Show();
        var vm = (MainViewModel)window.DataContext!;
        Assert.False(vm.IsSettingsOpen);
        Assert.False(window.FindControl<SettingsView>("SettingsOverlay")!.IsVisible);
        var pane = window.GetVisualDescendants().OfType<PaneView>().First();
        if (folder is not null) Navigate(vm, folder);
        Settle();
        return (window, vm, pane);
    }

    private static void Navigate(MainViewModel vm, string folder)
    {
        var task = vm.ActivePane.SelectedTab!.NavigateAsync(folder, true);
        WaitFor(() => task.IsCompleted);
    }

    private static void Settle()
    {
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Thread.Sleep(15);
        }
    }

    private static void WaitFor(Func<bool> condition, int timeoutMs = 4000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "Timed out waiting for the UI.");
    }

    private static void Snapshot(TopLevel top, string name)
    {
        Directory.CreateDirectory(SnapshotDir);
        Settle();
        if (top is MainWindow window)
            WaitFor(() =>
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                var settings = window.FindControl<SettingsView>("SettingsOverlay")!;
                return window.FindControl<Border>("RootBorder")!.Opacity == 1
                    && window.GetVisualDescendants().OfType<PaneView>().All(p => FileList(p).Opacity == 1)
                    && (!settings.IsVisible ||
                        settings.FindControl<Grid>("SettingsSurface") is { Opacity: 1, RenderTransform: null }
                        && settings.GetVisualDescendants().OfType<ScrollViewer>()
                            .Where(p => p.Classes.Contains("pane") && p.IsVisible)
                            .All(p => p.Content is Control { Opacity: 1, RenderTransform: null }));
            });
        using var frame = top.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(SnapshotDir, name + ".png"));
    }

    private static ListBox FileList(PaneView pane) => pane.FindControl<ListBox>("FileList")!;

    private static Point TabPoint(Window window, PaneView pane, TabViewModel tab, double fraction = 0.2)
    {
        Settle();
        var bar = pane.FindControl<ListBox>("TabBar")!;
        var container = (Control)bar.ContainerFromItem(tab)!;
        return container.TranslatePoint(new Point(container.Bounds.Width * fraction, container.Bounds.Height / 2), window)!.Value;
    }

    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Vector(8, 0), RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
        Settle();
    }

    private static void Select(PaneView pane, string name)
    {
        var list = FileList(pane);
        var item = list.Items.OfType<FileItem>().First(i => i.Name == name);
        list.SelectedItems!.Clear();
        list.SelectedItem = item;
        list.Focus();
        Settle();
    }

    private static Window? TopDialog(Window owner) =>
        (Application.Current!.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?
            .Windows.LastOrDefault(w => w != owner)
        ?? owner.OwnedWindows.LastOrDefault();

    private static string SampleFolder()
    {
        string dir = TestEnvironment.NewFolder("ui");
        Directory.CreateDirectory(Path.Combine(dir, "Projects"));
        Directory.CreateDirectory(Path.Combine(dir, "Photos"));
        File.WriteAllText(Path.Combine(dir, "notes.md"), "# Notes\n\nRain Explorer on Linux.\n");
        File.WriteAllText(Path.Combine(dir, "build.sh"), "#!/bin/sh\necho build\n");
        File.WriteAllBytes(Path.Combine(dir, "archive.tar.gz"), new byte[2048]);
        File.WriteAllText(Path.Combine(dir, ".hidden-config"), "secret=1");
        File.CreateSymbolicLink(Path.Combine(dir, "notes-link.md"), "notes.md");
        using (var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://RainExplorer/Assets/rain-256.png")))
        using (var file = File.Create(Path.Combine(dir, "Photos", "rain.png")))
            icon.CopyTo(file);
        return dir;
    }

    [AvaloniaFact]
    public void HomeDashboardAndFolderViewRender()
    {
        var (window, vm, pane) = Boot();
        Assert.True(vm.ActivePane.SelectedTab!.IsHomeView);
        Snapshot(window, "01-home");

        string dir = SampleFolder();
        Navigate(vm, dir);
        Settle();
        var names = FileList(pane).Items.OfType<FileItem>().Select(i => i.Name).ToList();
        Assert.Equal(new[] { "Photos", "Projects", "archive.tar.gz", "build.sh", "notes-link.md", "notes.md" }, names);
        Snapshot(window, "02-folder-details");

        SettingsStore.Instance.Settings.ShowHiddenFiles = true;
        WaitFor(() => FileList(pane).Items.OfType<FileItem>().Any(i => i.Name == ".hidden-config"));
        SettingsStore.Instance.Settings.ShowHiddenFiles = false;
        WaitFor(() => FileList(pane).Items.OfType<FileItem>().All(i => i.Name != ".hidden-config"));
        window.Close();
    }

    [AvaloniaFact]
    public void GridLayoutAndPreviewRender()
    {
        string dir = SampleFolder();
        var (window, vm, pane) = Boot(Path.Combine(dir, "Photos"));
        SettingsStore.Instance.Settings.ViewLayout = ViewLayout.Grid;
        SettingsStore.Instance.Settings.ShowPreviewPane = true;
        WaitFor(() => FileList(pane).Items.OfType<FileItem>().Single().HasGridThumbnail);
        Select(pane, "rain.png");
        var preview = pane.FindControl<PreviewPane>("Preview")!;
        WaitFor(() => preview.FindControl<Image>("ImageView")!.IsVisible);
        Snapshot(window, "03-grid-preview");
        SettingsStore.Instance.Settings.ViewLayout = ViewLayout.Details;
        SettingsStore.Instance.Settings.ShowPreviewPane = false;
        window.Close();
    }

    [AvaloniaFact]
    public void InlineRenameWithF2()
    {
        string dir = SampleFolder();
        var (window, vm, pane) = Boot(dir);
        SettingsStore.Instance.Settings.RenameMode = RenameMode.Inline;
        Select(pane, "notes.md");

        window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
        Settle();
        var box = FileList(pane).GetVisualDescendants().OfType<TextBox>().First(t => t.IsVisible);
        Assert.Equal("notes", box.SelectedText);   // extension stays unselected
        Snapshot(window, "04-inline-rename");
        window.KeyTextInput("journal");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        WaitFor(() => File.Exists(Path.Combine(dir, "journal.md")));
        Assert.False(File.Exists(Path.Combine(dir, "notes.md")));
        WaitFor(() => UndoService.Instance.CanUndo);

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        WaitFor(() => File.Exists(Path.Combine(dir, "notes.md")));
        window.Close();
    }

    [AvaloniaFact]
    public void NewFolderDialogWithShortcut()
    {
        string dir = SampleFolder();
        var (window, vm, pane) = Boot(dir);
        FileList(pane).Focus();

        window.KeyPress(Key.N, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.N, "N");
        WaitFor(() => TopDialog(window) is not null);
        var dialog = TopDialog(window)!;
        Snapshot(dialog, "05-new-folder-dialog");
        dialog.KeyTextInput("Music Library");
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        WaitFor(() => Directory.Exists(Path.Combine(dir, "Music Library")));
        window.Close();
    }

    [AvaloniaFact]
    public void DeleteMovesToTrashAndUndoRestores()
    {
        string dir = SampleFolder();
        var (window, vm, pane) = Boot(dir);
        Select(pane, "build.sh");

        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        WaitFor(() => !File.Exists(Path.Combine(dir, "build.sh")));
        WaitFor(() => FileList(pane).Items.OfType<FileItem>().All(i => i.Name != "build.sh"));
        Assert.Contains(Directory.GetFiles(Path.Combine(TrashService.HomeTrash, "files")),
            f => TrashService.OriginalPathOf(f) == Path.Combine(dir, "build.sh"));

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        WaitFor(() => File.Exists(Path.Combine(dir, "build.sh")));
        window.Close();
    }

    [AvaloniaFact]
    public void CopyAndPasteBetweenFolders()
    {
        string dir = SampleFolder();
        var (window, vm, pane) = Boot(dir);
        Select(pane, "notes.md");
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        Settle();

        Navigate(vm, Path.Combine(dir, "Projects"));
        FileList(pane).Focus();
        window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
        WaitFor(() => File.Exists(Path.Combine(dir, "Projects", "notes.md")));
        Assert.True(File.Exists(Path.Combine(dir, "notes.md")));
        window.Close();
    }

    [AvaloniaFact]
    public void TabsSplitAndSettingsRender()
    {
        string dir = SampleFolder();
        var (window, vm, pane) = Boot(dir);
        window.KeyPress(Key.T, RawInputModifiers.Control, PhysicalKey.T, "t");
        Settle();
        Assert.Equal(2, vm.ActivePane.Tabs.Count);
        vm.ToggleSplitCommand.Execute(null);
        Settle();
        Assert.True(vm.IsSplit);
        Snapshot(window, "06-tabs-split");
        vm.ToggleSplitCommand.Execute(null);

        vm.IsSettingsOpen = true;
        Assert.True(window.FindControl<SettingsView>("SettingsOverlay")!.IsVisible);
        Snapshot(window, "07-settings");
        vm.IsSettingsOpen = false;
        Assert.False(window.FindControl<SettingsView>("SettingsOverlay")!.IsVisible);

        SettingsStore.Instance.Settings.Theme = "Light";
        ThemeService.ApplyTheme("Light");
        Snapshot(window, "08-light-theme");
        SettingsStore.Instance.Settings.Theme = "Violet";
        ThemeService.ApplyTheme("Violet");
        window.Close();
    }

    [AvaloniaFact]
    public void NavigationRemainsUsableAtMinimumWidthAndInSplitPanes()
    {
        var (window, vm, pane) = Boot(SampleFolder());
        var address = pane.FindControl<BreadcrumbBar>("AddressBar")!;
        var search = pane.FindControl<Border>("SearchPill")!;
        Assert.Equal(address.TranslatePoint(default, pane)!.Value.Y, search.TranslatePoint(default, pane)!.Value.Y);

        window.Width = window.MinWidth;
        Settle();
        AssertToolbarFits(pane);
        Assert.True(search.TranslatePoint(default, pane)!.Value.Y > address.TranslatePoint(default, pane)!.Value.Y);
        Snapshot(window, "12-minimum-width");

        vm.ToggleSplitCommand.Execute(null);
        Settle();
        foreach (var splitPane in window.GetVisualDescendants().OfType<PaneView>())
            AssertToolbarFits(splitPane);
        Snapshot(window, "13-minimum-width-split");

        vm.ToggleSplitCommand.Execute(null);
        window.Width = 1200;
        Settle();
        Assert.Equal(address.TranslatePoint(default, pane)!.Value.Y, search.TranslatePoint(default, pane)!.Value.Y);
        window.Close();

        static void AssertToolbarFits(PaneView pane)
        {
            var address = pane.FindControl<BreadcrumbBar>("AddressBar")!;
            var search = pane.FindControl<Border>("SearchPill")!;
            Assert.True(address.Bounds.Width >= 90, $"Path width: {address.Bounds.Width}");
            Assert.True(search.Bounds.Width >= 70, $"Search width: {search.Bounds.Width}");
            var scroll = address.FindControl<ScrollViewer>("CrumbScroll")!;
            Assert.True(scroll.Offset.X >= Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width) - 1,
                "The current folder must remain visible after the pane is resized.");
            var current = address.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("current"));
            var crumbOrigin = current.TranslatePoint(default, scroll)!.Value;
            Assert.True(crumbOrigin.X < scroll.Bounds.Width && crumbOrigin.X + current.Bounds.Width > 0,
                $"Current folder is outside the breadcrumb viewport: {crumbOrigin.X}, width {scroll.Bounds.Width}");
            foreach (Control control in new Control[] { address, search, pane.FindControl<StackPanel>("ToolbarActions")! })
            {
                var origin = control.TranslatePoint(default, pane)!.Value;
                Assert.True(origin.X >= 0 && origin.X + control.Bounds.Width <= pane.Bounds.Width + 1,
                    $"{control.Name} extends outside its pane.");
            }
        }
    }

    [AvaloniaFact]
    public void BreadcrumbShowsMountRootOverflowAndDashboardPages()
    {
        var (window, vm, pane) = Boot();
        var address = pane.FindControl<BreadcrumbBar>("AddressBar")!;
        var strip = address.FindControl<StackPanel>("CrumbStrip")!;
        Assert.Contains(strip.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Home");

        Navigate(vm, TabViewModel.DrivesToken);
        Settle();
        Assert.Contains(strip.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "All drives");

        Navigate(vm, "/");
        Settle();
        Assert.Single(strip.Children.OfType<Button>(), b => b.Content is string);
        Assert.Contains(strip.Children.OfType<Button>(), b => b.Tag is string p && p == "/");

        string deep = Path.Combine(TestEnvironment.NewFolder("crumbs"), "one", "two", "three", "four", "five", "six");
        Directory.CreateDirectory(deep);
        Navigate(vm, deep);
        Settle();
        Assert.Contains(strip.Children.OfType<Button>(), b => Equals(b.Content, "…"));
        var current = Assert.Single(strip.Children.OfType<Button>(), b => b.Classes.Contains("current"));
        Assert.Equal(deep, current.Tag);
        Assert.Equal("six", current.Content);
        var root = strip.Children.OfType<Button>().First();
        Assert.Equal(MountService.GetMountPoint(deep), root.Tag);

        window.KeyPress(Key.L, RawInputModifiers.Control, PhysicalKey.L, "l");
        Settle();
        Assert.True(address.IsEditing);
        window.KeyTextInput(Path.GetDirectoryName(deep)!);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        WaitFor(() => vm.ActivePane.SelectedTab!.CurrentPath == Path.GetDirectoryName(deep));
        Assert.False(address.IsEditing);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TabsDragReorderAndGroupByHovering()
    {
        var (window, vm, pane) = Boot(SampleFolder());
        var source = vm.LeftPane;
        var first = source.SelectedTab!;
        var second = source.NewTab(TabViewModel.HomeToken);
        var third = source.NewTab(TabViewModel.DrivesToken);
        Drag(window, TabPoint(window, pane, first), TabPoint(window, pane, third, 0.9));
        Assert.Equal(new[] { second, third, first }, source.Tabs);
        Assert.False(first.IsDragging);
        WaitFor(() => first.HasPreviewImage);

        var from = TabPoint(window, pane, first);
        // Windows arms grouping at 30–48% facing-edge overlap. The midpoint
        // belongs to ordinary reordering even when the pointer pauses there.
        var to = TabPoint(window, pane, second, 0.8);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Vector(8, 0), RawInputModifiers.LeftMouseButton);
        Assert.True(first.IsDragging, "The second tab drag did not start.");
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        Assert.True(first.IsDragging, "The drag lost capture before grouping.");
        await Task.Delay(650);
        Assert.True(second.IsGroupDropTarget);
        window.MouseUp(to, MouseButton.Left);
        Settle();
        Assert.True(first.IsGrouped);
        Assert.Equal(second.GroupId, first.GroupId);
        Assert.Equal(2, source.ActiveGroupTabs.Count);
        Assert.False(second.IsGroupDropTarget);
        Assert.True(pane.FindControl<ListBox>("GroupTabBar")!.IsEffectivelyVisible);
        Snapshot(window, "09-tab-group");
        window.Close();
    }

    [AvaloniaFact]
    public void TabDragAcrossSplitPanesPreservesTheLiveTab()
    {
        var (window, vm, pane) = Boot(SampleFolder());
        var source = vm.LeftPane;
        var moved = source.SelectedTab!;
        var previous = source.NewTab(TabViewModel.HomeToken);
        source.SelectedTab = moved;
        vm.ToggleSplitCommand.Execute(null);
        Settle();
        var other = window.FindControl<PaneView>("RightPaneView")!;
        var target = vm.RightPane!;
        Point drop = other.TranslatePoint(new Point(other.Bounds.Width / 2, 130), window)!.Value;
        Drag(window, TabPoint(window, pane, moved), drop);
        Assert.DoesNotContain(moved, source.Tabs);
        Assert.Contains(moved, target.Tabs);
        Assert.Same(moved, target.SelectedTab);
        Assert.Same(previous, source.SelectedTab);
        Assert.Same(target, vm.ActivePane);
        window.Close();
    }

    [AvaloniaFact]
    public void TearOffWindowAdoptsLiveGroupAndProjectRestoresItsState()
    {
        var (window, vm, _) = Boot(SampleFolder());
        var source = vm.LeftPane;
        var first = source.SelectedTab!;
        var second = source.NewTab(TabViewModel.HomeToken);
        var outside = source.NewTab(TabViewModel.DrivesToken);
        source.GroupTabs(first, second);
        source.RenameGroup(first, "Work");
        var snapshot = source.CaptureProject();
        using (var restored = new PaneViewModel(new FileSystemService()))
        {
            restored.LoadProject(snapshot.Tabs, snapshot.SelectedIndex);
            WaitFor(() => restored.Tabs.All(t => !t.Busy));
            Assert.Equal(source.Tabs.Select(t => t.RestoreTarget), restored.Tabs.Select(t => t.RestoreTarget));
            Assert.Equal(2, restored.ActiveGroupTabs.Count);
            Assert.Equal("Work", restored.SelectedTab!.GroupName);
        }

        var torn = new MainWindow(source, source.SelectedTopTab!);
        torn.Show();
        Settle();
        var adopted = ((MainViewModel)torn.DataContext!).LeftPane;
        Assert.Equal(new[] { outside }, source.Tabs);
        Assert.Contains(first, adopted.Tabs);
        Assert.Contains(second, adopted.Tabs);
        Assert.Same(first, adopted.SelectedTab);
        Assert.Equal("Work", adopted.SelectedTab!.GroupName);
        Snapshot(torn, "10-torn-off-group");
        torn.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void DesktopThemeSvgIconsRenderAtBothSizesAndMissingIconsFallBack()
    {
        var (window, _, pane) = Boot(SampleFolder());
        var folder = FileList(pane).Items.OfType<FileItem>().First(i => i.Name == "Projects");
        Assert.EndsWith(".svg", IconThemeService.FindIcon("folder", 48));
        _ = folder.Icon;
        _ = folder.LargeIcon;
        WaitFor(() => folder.HasIcon && folder.HasLargeIcon);
        Assert.Equal(new PixelSize(48, 48), folder.Icon!.PixelSize);
        Assert.Equal(new PixelSize(128, 128), folder.LargeIcon!.PixelSize);
        Assert.Null(IconThemeService.FindIcon("rain-icon-that-does-not-exist", 48));
        window.Close();
    }

    [AvaloniaFact]
    public void PropertiesMeasuresTheLinkAndCannotChangeItsTargetPermissions()
    {
        string dir = SampleFolder();
        string target = Path.Combine(dir, "Projects");
        File.WriteAllBytes(Path.Combine(target, "large.bin"), new byte[1024 * 1024]);
        string link = Path.Combine(dir, "project-link");
        Directory.CreateSymbolicLink(link, target);
        var (window, _, pane) = Boot(dir);
        Select(pane, "project-link");
        window.KeyPress(Key.Enter, RawInputModifiers.Alt, PhysicalKey.Enter, null);
        WaitFor(() => TopDialog(window) is PropertiesDialog);
        var dialog = (PropertiesDialog)TopDialog(window)!;
        Assert.False(dialog.FindControl<TextBlock>("ContainsLabel")!.IsVisible);
        Assert.False(dialog.FindControl<CheckBox>("OwnerWrite")!.IsEnabled);
        Assert.False(dialog.FindControl<Button>("HashButton")!.IsVisible);
        long linkSize = System.Text.Encoding.UTF8.GetByteCount(target);
        Assert.Contains($"({linkSize:N0} bytes)", dialog.FindControl<SelectableTextBlock>("SizeText")!.Text);
        Snapshot(dialog, "11-link-properties");
        dialog.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void EveryThemeRepaintsTheWindowAndSettings()
    {
        var (window, vm, _) = Boot();
        var overlay = window.FindControl<SettingsView>("SettingsOverlay")!;
        foreach (string theme in ThemeService.Names)
        {
            SettingsStore.Instance.Settings.Theme = theme;
            ThemeService.ApplyTheme(theme);
            vm.IsSettingsOpen = true;
            Settle();
            Assert.True(overlay.IsVisible);
            var title = overlay.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Settings");
            Assert.Equal(Application.Current!.Resources["Text"], title.Foreground);
            Snapshot(window, "theme-" + theme.ToLowerInvariant());
            vm.IsSettingsOpen = false;
            Assert.False(overlay.IsVisible);
        }
        SettingsStore.Instance.Settings.Theme = "Violet";
        ThemeService.ApplyTheme("Violet");
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsStayInSyncAcrossWindowsAndStopListeningAfterClose()
    {
        var (first, _, _) = Boot();
        var (second, _, _) = Boot();
        var firstBox = first.FindControl<SettingsView>("SettingsOverlay")!.FindControl<ComboBox>("LayoutBox")!;
        var secondBox = second.FindControl<SettingsView>("SettingsOverlay")!.FindControl<ComboBox>("LayoutBox")!;
        SettingsStore.Instance.Settings.ViewLayout = ViewLayout.Grid;
        Assert.Equal("Grid", firstBox.SelectedItem!.ToString());
        Assert.Equal("Grid", secondBox.SelectedItem!.ToString());
        first.Close();
        SettingsStore.Instance.Settings.ViewLayout = ViewLayout.Details;
        Assert.Equal("Grid", firstBox.SelectedItem!.ToString());
        Assert.Equal("Details", secondBox.SelectedItem!.ToString());
        second.Close();
    }

    [AvaloniaFact]
    public void TearingOffTheLastTabShowsItsDestinationBeforeClosingTheSource()
    {
        var (window, vm, _) = Boot(SampleFolder());
        var moved = vm.LeftPane.SelectedTab!;
        MainWindow? destination = null;
        window.Closed += (_, _) => Assert.True(destination?.IsVisible,
            "The destination must be visible before the last source window closes.");
        destination = new MainWindow(vm.LeftPane, moved);
        Assert.True(window.IsVisible);
        destination.Show();
        Settle();
        Assert.False(window.IsVisible);
        Assert.Same(moved, ((MainViewModel)destination.DataContext!).LeftPane.SelectedTab);
        Assert.True(destination.IsVisible);
        destination.Close();
    }
}
