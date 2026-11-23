using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using RainExplorer.Controls;
using RainExplorer.Services;
using RainExplorer.Views;
using Xunit;

namespace RainExplorer.Tests;

public sealed partial class UiTests
{
    [AvaloniaFact]
    public void WhatsNewShowsOnceAfterAnUpdateNotAFreshInstall()
    {
        var settings = SettingsStore.Instance.Settings;
        string before = settings.LastWhatsNewShown;
        try
        {
            settings.LastWhatsNewShown = "";
            Assert.False(WhatsNewDialog.ShouldShowAfterUpdate(firstRun: true));
            Assert.Equal(WhatsNewDialog.Release, settings.LastWhatsNewShown);
            Assert.False(WhatsNewDialog.ShouldShowAfterUpdate(firstRun: false));

            settings.LastWhatsNewShown = "2.0.0-beta-2-era";
            Assert.True(WhatsNewDialog.ShouldShowAfterUpdate(firstRun: false));
        }
        finally
        {
            settings.LastWhatsNewShown = before;
        }
    }

    [AvaloniaFact]
    public void WhatsNewShowMeOpensTheFeature()
    {
        SettingsStore.Instance.Settings.ShortcutOverrides.Clear();
        var (window, vm, pane) = Boot();
        try
        {
            window.ShowWhatsNew();
            Settle();
            var dialog = Assert.IsType<WhatsNewDialog>(TopDialog(window));
            Assert.Equal(WhatsNewDialog.Release, SettingsStore.Instance.Settings.LastWhatsNewShown);
            var text = string.Join("\n", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("Press Ctrl+` to run a command", text);
            Snapshot(dialog, "97-whats-new");

            // "Show me" on the backup tile: Settings opens on Advanced with that card in view.
            var showMe = dialog.GetVisualDescendants().OfType<Button>()
                .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Show me: Back up your setup");
            Click(showMe);
            Settle();
            Assert.False(dialog.IsVisible);
            Assert.True(vm.IsSettingsOpen);
            var settingsView = window.FindControl<SettingsView>("SettingsOverlay")!;
            WaitFor(() => settingsView.FindControl<ScrollViewer>("PaneAdvanced")!.IsVisible);
            Assert.True(settingsView.FindControl<Button>("RestoreButton")!.IsEffectivelyVisible);

            // Each target works without throwing.
            foreach (string target in WhatsNewDialog.Features.Select(f => f.Target).OfType<string>())
            {
                window.ShowMe(target);
                Settle();
            }
            Assert.True(pane.IsCommandBoxOpen || vm.IsSettingsOpen);
        }
        finally
        {
            vm.IsSettingsOpen = false;
            window.Close();
        }
    }
}
