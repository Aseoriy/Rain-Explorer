#pragma warning disable CS0618
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RainExplorer.Controls;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using Xunit;

namespace RainExplorer.Tests;

public sealed partial class UiTests
{
    [AvaloniaFact]
    public async Task MakeDefaultButtonRegistersFoldersAndEnablesRevealInOneClick()
    {
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        var settings = SettingsStore.Instance.Settings;
        bool enabled = settings.HandleShowInFolder;
        string oldTheme = settings.Theme;
        settings.HandleShowInFolder = false;
        using var bus = new PrivateSessionBus();
        string folder = TestEnvironment.NewFolder("default-button");
        DesktopFileManagerTests.FakeMimeCommand(folder);
        Environment.SetEnvironmentVariable("PATH", folder + ":" + oldPath);
        var (window, vm, _) = Boot();
        var (otherWindow, otherVm, _) = Boot();
        var integration = FileManagerIntegration.Instance;
        integration.Start(window.ShowFileManagerRequestAsync, bus.Address);
        try
        {
            vm.IsSettingsOpen = otherVm.IsSettingsOpen = true;
            var page = window.FindControl<SettingsView>("SettingsOverlay")!;
            var other = otherWindow.FindControl<SettingsView>("SettingsOverlay")!;
            page.FindControl<ListBox>("SectionList")!.SelectedIndex = 3;
            await RenderFor(250);
            var button = page.FindControl<Button>("DefaultFileManagerButton")!;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(button.IsEnabled);
            Assert.False(other.FindControl<Button>("DefaultFileManagerButton")!.IsEnabled);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (integration.IsMakingDefault && DateTime.UtcNow < until) await RenderFor(30);
            Assert.False(integration.IsMakingDefault);
            Assert.True(button.IsEnabled);
            Assert.True(settings.HandleShowInFolder);
            Assert.StartsWith("Active", integration.Status);
            Assert.Contains("default file manager for folders and Show in folder", integration.DefaultStatus);
            Assert.Equal(integration.DefaultStatus, other.FindControl<TextBlock>("DefaultFileManagerStatus")!.Text);
            Assert.Equal(2, File.ReadAllLines(Path.Combine(folder, "calls")).Length);
            foreach (string theme in ThemeService.Names)
            {
                settings.Theme = theme; ThemeService.ApplyTheme(theme);
                button.BringIntoView(); await RenderFor(100);
                Snapshot(window, "70-default-manager-" + theme.ToLowerInvariant());
            }
        }
        finally
        {
            settings.HandleShowInFolder = false;
            await integration.RetryAsync(); integration.Stop();
            window.Close(); otherWindow.Close();
            settings.HandleShowInFolder = enabled;
            settings.Theme = oldTheme; ThemeService.ApplyTheme(oldTheme);
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }

    private static DataObject SidebarData(string format, string value)
    {
        var data = new DataObject(); data.Set(format, value); return data;
    }

    private static DragEventArgs SidebarDrag(MainWindow window, SidebarNode target, IDataObject data,
        bool after, bool drop = false, KeyModifiers modifiers = KeyModifiers.None)
    {
        var tree = window.FindControl<TreeView>("SidebarTree")!;
        var row = tree.GetVisualDescendants().OfType<TreeViewItem>().First(c => ReferenceEquals(c.DataContext, target));
        var header = row.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Header");
        var point = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height * (after ? .8 : .2)), tree)!.Value;
        var args = new DragEventArgs(drop ? DragDrop.DropEvent : DragDrop.DragOverEvent, data, tree, point, modifiers);
        tree.RaiseEvent(args);
        return args;
    }

    [AvaloniaTheory]
    [InlineData("Violet")]
    [InlineData("Ocean")]
    [InlineData("Cyan")]
    [InlineData("Emerald")]
    [InlineData("Ember")]
    [InlineData("Rose")]
    [InlineData("Midnight")]
    [InlineData("Sunset")]
    [InlineData("Mono")]
    [InlineData("Light")]
    public async Task SidebarPinsReorderWithInsertionPreviewAndPersist(string theme)
    {
        using var fixture = new SidebarFixture();
        var settings = SettingsStore.Instance.Settings;
        settings.Theme = theme; ThemeService.ApplyTheme(theme);
        var (window, vm, _) = Boot();
        try
        {
            await RenderFor(120);
            var target = vm.SidebarNodes.First(n => n.Path == fixture.First);
            var data = SidebarData(MainWindow.PinDragFormat, "quick|" + fixture.Second);
            var drag = SidebarDrag(window, target, data, after: false);
            Assert.Equal(DragDropEffects.Move, drag.DragEffects);
            Assert.True(window.FindControl<Border>("SidebarInsertionLine")!.IsVisible);
            Assert.False(target.IsDropTarget);
            Snapshot(window, "71-sidebar-reorder-" + theme.ToLowerInvariant());
            SidebarDrag(window, target, data, after: false, drop: true);
            await RenderFor(80);
            Assert.Equal(new[] { fixture.Second, fixture.First }, settings.Pinned.Select(p => p.Path));
            Assert.False(window.FindControl<Border>("SidebarInsertionLine")!.IsVisible);
            using var persisted = JsonDocument.Parse(File.ReadAllText(Path.Combine(XdgDirs.ConfigHome, "RainExplorer", "settings.json")));
            Assert.Equal(fixture.Second, persisted.RootElement.GetProperty("Pinned")[0].GetProperty("Path").GetString());
            Assert.True(Directory.Exists(fixture.First)); Assert.True(Directory.Exists(fixture.Second));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SidebarGroupsReorderAndPinsMoveBetweenListsAndFolderDropsCreateShortcuts()
    {
        using var fixture = new SidebarFixture();
        var settings = SettingsStore.Instance.Settings;
        var (window, vm, _) = Boot();
        try
        {
            await RenderFor(120);
            var header = vm.SidebarNodes.First(n => n.GroupKey == fixture.Group && n.IsHeader);
            var data = SidebarData(MainWindow.PinDragFormat, "quick|" + fixture.First);
            Assert.Equal(DragDropEffects.Move, SidebarDrag(window, header, data, false).DragEffects);
            Assert.True(header.IsDropTarget);
            SidebarDrag(window, header, data, false, drop: true); await RenderFor(80);
            Assert.Single(settings.Pinned);
            var pin = Assert.Single(settings.CustomGroups[0].Items);
            Assert.Equal(fixture.First, pin.Path); Assert.Equal("My folder", pin.Name); Assert.Equal("star", pin.IconKey);
            var quick = vm.SidebarNodes.First(n => n.GroupKey == "quick" && n.IsHeader);
            data = SidebarData(MainWindow.SectionDragFormat, fixture.Group);
            SidebarDrag(window, quick, data, false, drop: true); await RenderFor(80);
            Assert.Equal(fixture.Group, settings.SidebarOrder[0]);
            header = vm.SidebarNodes.First(n => n.GroupKey == fixture.Group && n.IsHeader);
            data = SidebarData("text/uri-list", PathUtil.ToFileUri(fixture.Second) + "\r\n");
            Assert.Equal(DragDropEffects.Link, SidebarDrag(window, header, data, false).DragEffects);
            SidebarDrag(window, header, data, false, drop: true); await RenderFor(80);
            Assert.Equal(2, settings.CustomGroups[0].Items.Count);
            Assert.True(Directory.Exists(fixture.Second));
            Assert.True(Directory.Exists(fixture.First));
            Assert.Contains(settings.Pinned, p => p.Path == fixture.Second);
            Snapshot(window, "72-sidebar-customized");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SidebarRejectsInvalidReordersAndCancelClearsPreview()
    {
        using var fixture = new SidebarFixture();
        var (window, vm, _) = Boot();
        try
        {
            await RenderFor(120);
            var node = vm.SidebarNodes.First(n => n.Path == fixture.First);
            var data = SidebarData(MainWindow.PinDragFormat, "quick|" + fixture.First);
            Assert.Equal(DragDropEffects.None, SidebarDrag(window, node, data, false).DragEffects);
            data = SidebarData(MainWindow.PinDragFormat, "quick|" + fixture.Second);
            SidebarDrag(window, node, data, true);
            var tree = window.FindControl<TreeView>("SidebarTree")!;
            tree.RaiseEvent(new DragEventArgs(DragDrop.DragLeaveEvent, data, tree, default, KeyModifiers.None));
            Assert.False(window.FindControl<Border>("SidebarInsertionLine")!.IsVisible);
            Assert.Equal(fixture.First, SettingsStore.Instance.Settings.Pinned[0].Path);
        }
        finally { window.Close(); }
    }

    private sealed class SidebarFixture : IDisposable
    {
        private readonly AppSettings _settings = SettingsStore.Instance.Settings;
        private readonly List<PinnedItem> _pins;
        private readonly List<SidebarGroup> _groups;
        private readonly List<string> _order;
        private readonly bool _quick, _collapsed, _sidebar;
        private readonly string _theme;
        public string First { get; } = TestEnvironment.NewFolder("sidebar First|literal");
        public string Second { get; } = TestEnvironment.NewFolder("sidebar second");
        public string Group => "custom:" + _settings.CustomGroups[0].Id;
        public SidebarFixture()
        {
            _pins = _settings.Pinned; _groups = _settings.CustomGroups; _order = _settings.SidebarOrder;
            _quick = _settings.ShowQuickAccessInSidebar; _collapsed = _settings.QuickAccessCollapsed;
            _sidebar = _settings.SidebarCollapsed; _theme = _settings.Theme;
            _settings.Pinned = [new() { Path = First, Name = "My folder", IconKey = "star" }, new() { Path = Second, Name = "Second folder" }];
            _settings.CustomGroups = [new() { Name = "My list" }];
            _settings.SidebarOrder = ["quick", Group, "drives"];
            _settings.ShowQuickAccessInSidebar = true; _settings.QuickAccessCollapsed = false; _settings.SidebarCollapsed = false;
            _settings.NotifyPinnedChanged();
        }
        public void Dispose()
        {
            _settings.Pinned = _pins; _settings.CustomGroups = _groups; _settings.SidebarOrder = _order;
            _settings.ShowQuickAccessInSidebar = _quick; _settings.QuickAccessCollapsed = _collapsed;
            _settings.SidebarCollapsed = _sidebar; _settings.Theme = _theme;
            _settings.NotifyPinnedChanged(); ThemeService.ApplyTheme(_theme);
        }
    }
}
