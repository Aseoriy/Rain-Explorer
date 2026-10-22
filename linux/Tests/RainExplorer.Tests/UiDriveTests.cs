using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
    private static async Task WaitForDrive(MainViewModel vm, Func<MountEntry, bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!vm.SidebarNodes.Any(n => n.Drive is { } drive && drive.UDisksPath == FakeUDisks.BlockPath && condition(drive))
            && DateTime.UtcNow < until)
        {
            await vm.RefreshDrivesAsync();
            await RenderFor(30);
        }
        Assert.Contains(vm.SidebarNodes, n => n.Drive is { } drive && drive.UDisksPath == FakeUDisks.BlockPath && condition(drive));
        await RenderFor(80);
    }

    private static Button DriveCard(MainWindow window) => window.GetVisualDescendants().OfType<DrivesView>()
        .SelectMany(v => v.GetVisualDescendants().OfType<Button>()).First(b =>
            b.DataContext is DriveVM { Drive.UDisksPath: FakeUDisks.BlockPath }
            && AutomationProperties.GetName(b) == "My USB");

    [AvaloniaFact]
    public async Task RemovableDriveSidebarMountAndOpenThenUnmountAndBusyRemoval()
    {
        var original = DriveActions.CreateClient;
        var fake = new FakeUDisks();
        DriveActions.CreateClient = () => fake;
        var settings = SettingsStore.Instance.Settings;
        bool shown = settings.ShowDrivesInSidebar, collapsed = settings.DrivesCollapsed;
        settings.ShowDrivesInSidebar = true; settings.DrivesCollapsed = false;
        MountService.UpdateDevices(DriveActions.ToEntries(fake.Objects));
        var (window, vm, _) = Boot(TabViewModel.DrivesToken);
        try
        {
            await WaitForDrive(vm, d => d.CanMount);
            var button = window.FindControl<TreeView>("SidebarTree")!.GetVisualDescendants().OfType<Button>()
                .First(b => b.DataContext is SidebarNode { Drive.UDisksPath: FakeUDisks.BlockPath }
                    && AutomationProperties.GetName(b) == "Mount");
            Assert.Equal("Mount", AutomationProperties.GetName(button));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForDrive(vm, d => d.IsMounted);
            Assert.Equal(TabViewModel.DrivesToken, vm.ActivePane.SelectedTab!.CurrentPath);
            Assert.Contains("was mounted", vm.ActivePane.SelectedTab.Status);
            DriveCard(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await RenderFor(120);
            Assert.Equal(fake.MountPoint, vm.ActivePane.SelectedTab.CurrentPath);

            Navigate(vm, TabViewModel.DrivesToken);
            var menu = window.ShowDriveMenu(DriveCard(window), fake.Entry);
            var items = menu.ItemsSource!.OfType<MenuItem>().ToList();
            Assert.Contains(items, i => (string?)i.Header == "Unmount");
            Assert.Contains(items, i => (string?)i.Header == "Safely remove");
            items.First(i => (string?)i.Header == "Unmount").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
            await WaitForDrive(vm, d => d.CanMount);
            Assert.True(vm.ActivePane.SelectedTab.Drives.Single(d => d.Label == "My USB").HasAction);
            Assert.Equal("Mount", vm.ActivePane.SelectedTab.Drives.Single(d => d.Label == "My USB").ActionText);

            DriveCard(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForDrive(vm, d => d.IsMounted);
            Navigate(vm, TabViewModel.DrivesToken);
            fake.FailPath = FakeUDisks.BlockPath; fake.FailMethod = "Unmount";
            await window.RunDriveActionAsync(fake.Entry, DriveAction.SafelyRemove);
            Assert.Contains("in use", vm.ActivePane.SelectedTab.Status);
            Assert.DoesNotContain(fake.Calls, c => c.Method == "PowerOff");
            fake.FailPath = null;
            Navigate(vm, fake.MountPoint);
            await window.RunDriveActionAsync(fake.Entry, DriveAction.SafelyRemove);
            Assert.Equal(PageKind.Home, vm.ActivePane.SelectedTab.Page);
            Assert.DoesNotContain(vm.SidebarNodes, n => n.Drive?.UDisksPath == FakeUDisks.BlockPath);
            Assert.DoesNotContain(vm.ActivePane.SelectedTab.Drives, d => d.Label == "My USB");
            Assert.Contains("safely removed", vm.ActivePane.SelectedTab.Status);
        }
        finally
        {
            window.Close(); DriveActions.CreateClient = original; MountService.UpdateDevices([]);
            settings.ShowDrivesInSidebar = shown; settings.DrivesCollapsed = collapsed;
        }
    }

    [AvaloniaFact]
    public async Task RemovableDriveHotplugAndActionsRenderAcrossAllThemesAndWindows()
    {
        var original = DriveActions.CreateClient;
        var fake = new FakeUDisks();
        DriveActions.CreateClient = () => fake;
        var settings = SettingsStore.Instance.Settings;
        string oldTheme = settings.Theme;
        bool shown = settings.ShowDrivesInSidebar, collapsed = settings.DrivesCollapsed;
        settings.ShowDrivesInSidebar = true; settings.DrivesCollapsed = false;
        MountService.UpdateDevices([]);
        var (window, vm, _) = Boot(TabViewModel.DrivesToken);
        var (other, otherVm, _) = Boot(TabViewModel.HomeToken);
        try
        {
            await WaitForDrive(vm, d => d.CanMount);
            await WaitForDrive(otherVm, d => d.CanMount);
            foreach (string theme in ThemeService.Names)
            {
                settings.Theme = theme; ThemeService.ApplyTheme(theme);
                await RenderFor(80);
                var card = DriveCard(window);
                window.GetVisualDescendants().OfType<DrivesView>().First(v => v.IsVisible).GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd(); await RenderFor(80);
                Assert.True(card.IsVisible);
                var viewer = window.GetVisualDescendants().OfType<DrivesView>().First(v => v.IsVisible).GetVisualDescendants().OfType<ScrollViewer>().First();
                var top = card.TranslatePoint(default, viewer)!.Value;
                Assert.InRange(top.Y + card.Bounds.Height, 0, viewer.Bounds.Height);
                var lastDrive = vm.ActivePane.SelectedTab!.Drives.Last();
                var lastCard = viewer.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.DataContext, lastDrive)
                    && AutomationProperties.GetName(b) == lastDrive.Label);
                Assert.InRange(lastCard.TranslatePoint(default, viewer)!.Value.Y + lastCard.Bounds.Height, 0, viewer.Bounds.Height);
                Snapshot(window, "80-removable-unmounted-" + theme.ToLowerInvariant());
            }
            await window.RunDriveActionAsync(fake.Entry, DriveAction.Mount);
            await WaitForDrive(otherVm, d => d.IsMounted);
            settings.Theme = "Violet"; ThemeService.ApplyTheme("Violet");
            window.GetVisualDescendants().OfType<DrivesView>().First(v => v.IsVisible).GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd(); await RenderFor(80);
            Snapshot(window, "81-removable-mounted");
            other.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is DriveVM { Label: "My USB" }).BringIntoView();
            await RenderFor(80);
            Snapshot(other, "82-removable-home");
            window.Width = 760; await RenderFor(80);
            Snapshot(window, "83-removable-narrow");
            fake.Objects.Clear();
            await vm.RefreshDrivesAsync(); await otherVm.RefreshDrivesAsync();
            Assert.DoesNotContain(vm.SidebarNodes, n => n.Drive?.UDisksPath == FakeUDisks.BlockPath);
            Assert.DoesNotContain(otherVm.ActivePane.SelectedTab!.Drives, d => d.Label == "My USB");
        }
        finally
        {
            window.Close(); other.Close(); DriveActions.CreateClient = original; MountService.UpdateDevices([]);
            settings.ShowDrivesInSidebar = shown; settings.DrivesCollapsed = collapsed;
            settings.Theme = oldTheme; ThemeService.ApplyTheme(oldTheme);
        }
    }
}
