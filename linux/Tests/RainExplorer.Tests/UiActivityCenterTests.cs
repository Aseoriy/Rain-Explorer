using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using RainExplorer.Controls;
using RainExplorer.Services;
using RainExplorer.Views;
using Xunit;

namespace RainExplorer.Tests;

/// <summary>Activity center: pin the flyout, pop it out into its own window, dock it back.</summary>
public sealed partial class UiTests
{
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void ResetActivityCenter()
    {
        var s = SettingsStore.Instance.Settings;
        ActivityWindow.CloseForShutdown();
        s.ActivityCenterPinned = false;
        s.ActivityCenterPoppedOut = false;
        s.ActivityWindowOpen = false;
        s.ActivityWindowTopmost = true;
        s.ActivityWindowLeft = null;
        s.ActivityWindowTop = null;
    }

    [AvaloniaFact]
    public void PinningKeepsTheActivityFlyoutOpen()
    {
        ResetActivityCenter();
        var (window, _, _) = Boot();
        try
        {
            var service = ActivityService.Instance;
            var entry = service.Begin("Copy", "3 items", "copy");
            service.Complete(entry, true);
            var popup = window.FindControl<Popup>("ActivityPopup")!;
            var flyout = window.FindControl<ActivityCenterView>("ActivityFlyout")!;

            Click(window.FindControl<Button>("ActivityButton")!);
            Settle();
            Assert.True(popup.IsOpen);
            Assert.True(popup.IsLightDismissEnabled);   // unpinned: clicking elsewhere closes it

            var pin = flyout.FindControl<ToggleButton>("PinToggle")!;
            pin.IsChecked = true;
            Settle();
            Assert.True(SettingsStore.Instance.Settings.ActivityCenterPinned);
            Assert.False(popup.IsLightDismissEnabled);  // pinned: stays open
            Snapshot(window, "90-activity-flyout-pinned");

            pin.IsChecked = false;
            Settle();
            Assert.True(popup.IsLightDismissEnabled);
            popup.IsOpen = false;
        }
        finally
        {
            ResetActivityCenter();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ActivityCenterPopsOutDocksBackAndClosesWithRain()
    {
        ResetActivityCenter();
        var (window, _, _) = Boot();
        var settings = SettingsStore.Instance.Settings;
        try
        {
            var popup = window.FindControl<Popup>("ActivityPopup")!;
            var flyout = window.FindControl<ActivityCenterView>("ActivityFlyout")!;
            Click(window.FindControl<Button>("ActivityButton")!);
            Settle();

            // Pop out: the flyout closes and a separate window opens.
            Click(flyout.FindControl<Button>("PopOutButton")!);
            Settle();
            Assert.False(popup.IsOpen);
            var activityWindow = Assert.IsType<ActivityWindow>(ActivityWindow.Current);
            Assert.True(activityWindow.IsVisible);
            Assert.True(settings.ActivityCenterPoppedOut);
            Assert.True(settings.ActivityWindowOpen);
            Assert.True(activityWindow.Topmost);
            Assert.True(activityWindow.CanResize);
            var view = activityWindow.GetVisualDescendantsOfType<ActivityCenterView>();
            Assert.True(view.IsWindowMode);
            Assert.False(view.FindControl<Button>("PopOutButton")!.IsVisible);
            Assert.True(view.FindControl<Button>("DockButton")!.IsVisible);
            Snapshot(activityWindow, "91-activity-window");

            // On top follows the toggle.
            view.FindControl<ToggleButton>("OnTopToggle")!.IsChecked = false;
            Settle();
            Assert.False(activityWindow.Topmost);
            Assert.False(settings.ActivityWindowTopmost);

            // Popped out and on top, the title-bar button closes the window (the flyout stays shut)…
            view.FindControl<ToggleButton>("OnTopToggle")!.IsChecked = true;
            Settle();
            Click(window.FindControl<Button>("ActivityButton")!);
            Settle();
            Assert.False(popup.IsOpen);
            Assert.Null(ActivityWindow.Current);
            Assert.True(settings.ActivityCenterPoppedOut);
            Assert.False(settings.ActivityWindowOpen);

            // …and brings it back on the next click.
            Click(window.FindControl<Button>("ActivityButton")!);
            Settle();
            Assert.False(popup.IsOpen);
            activityWindow = Assert.IsType<ActivityWindow>(ActivityWindow.Current);

            // Closing it with X keeps the popped-out mode but remembers it's closed.
            activityWindow.Close();
            Settle();
            Assert.Null(ActivityWindow.Current);
            Assert.True(settings.ActivityCenterPoppedOut);
            Assert.False(settings.ActivityWindowOpen);

            // Dock puts it back in the flyout.
            Click(window.FindControl<Button>("ActivityButton")!);
            Settle();
            var reopened = Assert.IsType<ActivityWindow>(ActivityWindow.Current);
            Click(reopened.GetVisualDescendantsOfType<ActivityCenterView>().FindControl<Button>("DockButton")!);
            Settle();
            Assert.Null(ActivityWindow.Current);
            Assert.False(settings.ActivityCenterPoppedOut);
            Assert.True(popup.IsOpen);
            popup.IsOpen = false;

            // Rain closing with the window open: it closes too, and reopens next launch.
            Click(window.FindControl<Button>("ActivityButton")!);
            Settle();
            Click(flyout.FindControl<Button>("PopOutButton")!);
            Settle();
            Assert.NotNull(ActivityWindow.Current);
            Assert.True(settings.ActivityWindowOpen);
            window.Close();
            Settle();
            Assert.Null(ActivityWindow.Current);
            Assert.True(settings.ActivityWindowOpen);
            Assert.True(settings.ActivityWindowLeft is not null && settings.ActivityWindowWidth >= 340);
        }
        finally
        {
            ResetActivityCenter();
            settings.ActivityWindowTopmost = true;
            if (window.IsVisible) window.Close();
        }
    }
}

internal static class VisualTestExtensions
{
    public static T GetVisualDescendantsOfType<T>(this Avalonia.Visual root) where T : Avalonia.Visual =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>().First();
}

public sealed partial class UiTests
{
    private static void RealClick(Window window, Control target)
    {
        var point = target.TranslatePoint(new Avalonia.Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        window.MouseUp(point, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
        Settle();
    }

    [AvaloniaFact]
    public void ClickingTheActivityButtonAgainClosesTheFlyout()
    {
        ResetActivityCenter();
        var (window, _, pane) = Boot();
        try
        {
            var popup = window.FindControl<Popup>("ActivityPopup")!;
            var button = window.FindControl<Button>("ActivityButton")!;
            RealClick(window, button);
            Assert.True(popup.IsOpen);

            RealClick(window, button);              // the same button closes it...
            WaitFor(() => !popup.IsOpen);
            Thread.Sleep(400); Settle();
            Assert.False(popup.IsOpen);             // ...and it stays closed

            RealClick(window, button);              // and opens it again later
            Assert.True(popup.IsOpen);

            RealClick(window, pane);                // clicking elsewhere still closes it
            WaitFor(() => !popup.IsOpen);
        }
        finally
        {
            ResetActivityCenter();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheSettingsGearOpensAndClosesSettings()
    {
        var (window, vm, _) = Boot();
        try
        {
            var gear = window.FindControl<Button>("SettingsButton")!;
            var overlay = window.FindControl<SettingsView>("SettingsOverlay")!;
            RealClick(window, gear);
            Assert.True(vm.IsSettingsOpen);
            Assert.True(overlay.IsVisible);

            RealClick(window, gear);
            Assert.False(vm.IsSettingsOpen);
            Assert.False(overlay.IsHitTestVisible);   // stops taking clicks at once, then fades
            WaitFor(() => !overlay.IsVisible);
            Assert.Equal(1, overlay.Opacity);

            RealClick(window, gear);                   // Escape still closes it too
            window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
            Settle();
            Assert.False(vm.IsSettingsOpen);
            WaitFor(() => !overlay.IsVisible);
        }
        finally
        {
            vm.IsSettingsOpen = false;
            window.Close();
        }
    }
}
