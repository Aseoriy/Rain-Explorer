using System.Text.Json;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using Xunit;

namespace RainExplorer.Tests;

public sealed partial class UiTests
{
    private static DispatcherTimer AmbientPulse(MainWindow window) =>
        (DispatcherTimer)typeof(MainWindow).GetField("_ambientPulse", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    [AvaloniaFact]
    public async Task PerformanceToggleUpdatesAllWindowsPersistsAndKeepsTabMotion()
    {
        var settings = SettingsStore.Instance.Settings;
        bool oldMode = settings.PerformanceMode, oldGlow = settings.ShowAmbientBackground;
        settings.PerformanceMode = false;
        settings.ShowAmbientBackground = true;
        var (window, vm, pane) = Boot();
        var other = new MainWindow();
        other.Show();
        PlaceHeadlessWindowBeside(other);
        var otherVm = (MainViewModel)other.DataContext!;
        try
        {
            vm.IsSettingsOpen = true;
            otherVm.IsSettingsOpen = true;
            await RenderFor(300);
            Assert.IsType<BlurEffect>(window.FindControl<Grid>("BodyGrid")!.Effect);
            Assert.True(AmbientPulse(window).IsEnabled);
            var toggle = window.FindControl<Controls.SettingsView>("SettingsOverlay")!
                .FindControl<CheckBox>("PerformanceModeToggle")!;
            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.PerformanceMode);
            foreach (var w in new[] { window, other })
            {
                Assert.False(w.FindControl<Panel>("Orb")!.IsVisible);
                Assert.False(AmbientPulse(w).IsEnabled);
                Assert.Null(w.FindControl<Grid>("BodyGrid")!.Effect);
            }
            Assert.True(settings.ShowAmbientBackground); // Preserve the user's glow preference.
            SettingsStore.Instance.Flush();
            string path = Path.Combine(TestEnvironment.Root, "config", "RainExplorer", "settings.json");
            Assert.True(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))!.PerformanceMode);
            toggle.FindAncestorOfType<Border>()!.BringIntoView();
            await RenderFor(50);
            Snapshot(window, "28-performance-mode-settings");

            vm.IsSettingsOpen = false;
            var tab = vm.LeftPane.NewTab(TabViewModel.DrivesToken);
            await RenderFor(30);
            var bar = pane.FindControl<ListBox>("TabBar")!;
            Assert.InRange(((Control)bar.ContainerFromItem(tab)!).Opacity, 0.01, 0.999);
            await RenderFor(200);
            var first = vm.LeftPane.Tabs.First();
            Drag(window, TabPoint(window, pane, first), TabPoint(window, pane, tab, 0.9));
            await RenderFor(250);
            Assert.Same(first, vm.LeftPane.Tabs.Last());

            toggle.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            foreach (var w in new[] { window, other })
            {
                Assert.True(w.FindControl<Panel>("Orb")!.IsVisible);
                Assert.True(AmbientPulse(w).IsEnabled);
            }
            Assert.IsType<BlurEffect>(other.FindControl<Grid>("BodyGrid")!.Effect);
            settings.ShowAmbientBackground = false;
            settings.PerformanceMode = true;
            settings.PerformanceMode = false;
            Assert.False(window.FindControl<Panel>("Orb")!.IsVisible);
        }
        finally
        {
            window.Close(); other.Close();
            Assert.False(AmbientPulse(window).IsEnabled);
            Assert.False(AmbientPulse(other).IsEnabled);
            settings.PerformanceMode = oldMode;
            settings.ShowAmbientBackground = oldGlow;
        }
    }
}
