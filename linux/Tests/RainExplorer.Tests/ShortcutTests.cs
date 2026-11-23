using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RainExplorer.Controls;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.Views;
using Xunit;

namespace RainExplorer.Tests;

/// <summary>Customizable shortcuts: the service and the Settings → Shortcuts page.</summary>
public sealed class ShortcutServiceTests
{
    private static KeyEventArgs Key(Key key, KeyModifiers mods = KeyModifiers.None) =>
        new() { Key = key, KeyModifiers = mods, RoutedEvent = InputElement.KeyDownEvent };

    [Fact]
    public void ActionIdsAreUniqueAndDefaultsParse()
    {
        Assert.Equal(ShortcutService.Actions.Count, ShortcutService.Actions.Select(a => a.Id).Distinct().Count());
        foreach (var action in ShortcutService.Actions)
            foreach (string text in action.Defaults)
                Assert.True(ShortcutService.TryParse(text, out _), $"{action.Id}: {text}");
    }

    [Fact]
    public void NoTwoDefaultsShareAKey()
    {
        var seen = new Dictionary<string, string>();
        foreach (var action in ShortcutService.Actions)
            foreach (string text in action.Defaults)
            {
                ShortcutService.TryParse(text, out var g);
                string key = ShortcutService.Describe(g);
                Assert.False(seen.ContainsKey(key), $"{key} is used by {seen.GetValueOrDefault(key)} and {action.Id}");
                seen[key] = action.Id;
            }
    }

    [Fact]
    public void TypingOnlyAllowsSafeShortcuts()
    {
        SettingsStore.Instance.Settings.ShortcutOverrides.Clear();
        Assert.Equal("tab.new", ShortcutService.Match(Key(Avalonia.Input.Key.T, KeyModifiers.Control), typing: true)?.Id);
        Assert.Equal("nav.refresh", ShortcutService.Match(Key(Avalonia.Input.Key.F5), typing: true)?.Id);
        Assert.Null(ShortcutService.Match(Key(Avalonia.Input.Key.Delete), typing: true));        // deletes text instead
        Assert.Null(ShortcutService.Match(Key(Avalonia.Input.Key.Space), typing: true));
        Assert.Null(ShortcutService.Match(Key(Avalonia.Input.Key.C, KeyModifiers.Control), typing: true)); // copies text
        Assert.Null(ShortcutService.Match(Key(Avalonia.Input.Key.Back), typing: true));          // backspace in the box
        Assert.Equal("nav.up", ShortcutService.Match(Key(Avalonia.Input.Key.Back), typing: false)?.Id);
        Assert.Equal("file.delete", ShortcutService.Match(Key(Avalonia.Input.Key.Delete), typing: false)?.Id);
        Assert.Equal("file.deletePermanent", ShortcutService.Match(Key(Avalonia.Input.Key.Delete, KeyModifiers.Shift), typing: false)?.Id);
        Assert.Equal("tab.prev", ShortcutService.Match(Key(Avalonia.Input.Key.Tab, KeyModifiers.Control | KeyModifiers.Shift), typing: false)?.Id);
    }

    [Fact]
    public void OverridesReplaceDefaultsAndReset()
    {
        var overrides = SettingsStore.Instance.Settings.ShortcutOverrides;
        overrides.Clear();
        try
        {
            ShortcutService.TryParse("Ctrl+K", out var ctrlK);
            ShortcutService.Set("tab.new", ctrlK);
            Assert.Equal("tab.new", ShortcutService.Match(Key(Avalonia.Input.Key.K, KeyModifiers.Control), false)?.Id);
            Assert.Null(ShortcutService.Match(Key(Avalonia.Input.Key.T, KeyModifiers.Control), false));
            Assert.True(ShortcutService.IsCustomized("tab.new"));
            Assert.Equal("New tab (Ctrl+K)", ShortcutHints.Instance["tab.new"]);

            ShortcutService.Set("view.preview", null);   // turned off
            Assert.Null(ShortcutService.Match(Key(Avalonia.Input.Key.Space), false));
            Assert.Equal("None", ShortcutService.DescribeAction("view.preview"));

            ShortcutService.Reset("tab.new");
            Assert.Equal("tab.new", ShortcutService.Match(Key(Avalonia.Input.Key.T, KeyModifiers.Control), false)?.Id);
            ShortcutService.ResetAll();
            Assert.Empty(overrides);
            Assert.Equal("view.preview", ShortcutService.Match(Key(Avalonia.Input.Key.Space), false)?.Id);
        }
        finally
        {
            overrides.Clear();
        }
    }

    [Fact]
    public void ConflictsAndBlockedKeysAreReported()
    {
        SettingsStore.Instance.Settings.ShortcutOverrides.Clear();
        ShortcutService.TryParse("Ctrl+C", out var ctrlC);
        Assert.Equal("file.copy", ShortcutService.ConflictFor("tab.new", ctrlC)?.Id);
        Assert.Null(ShortcutService.ConflictFor("file.copy", ctrlC));
        Assert.NotNull(ShortcutService.Validate(new KeyGesture(Avalonia.Input.Key.Q)));             // plain letter
        Assert.NotNull(ShortcutService.Validate(new KeyGesture(Avalonia.Input.Key.Escape)));
        Assert.Null(ShortcutService.Validate(new KeyGesture(Avalonia.Input.Key.Q, KeyModifiers.Control)));
        Assert.Null(ShortcutService.Validate(new KeyGesture(Avalonia.Input.Key.F9)));
    }

    [Theory]
    [InlineData("Ctrl+OemComma", "Ctrl+,")]
    [InlineData("Delete", "Del")]
    [InlineData("Ctrl+OemTilde", "Ctrl+`")]
    [InlineData("Shift+F2", "Shift+F2")]
    [InlineData("Ctrl+PageDown", "Ctrl+Page Down")]
    [InlineData("Back", "Backspace")]
    public void KeysAreDescribedReadably(string text, string expected)
    {
        Assert.True(ShortcutService.TryParse(text, out var gesture));
        Assert.Equal(expected, ShortcutService.Describe(gesture));
    }

    [Fact]
    public void OverridesSurviveSaving()
    {
        var s = new AppSettings();
        s.ShortcutOverrides["tab.new"] = "Ctrl+K";
        s.ShortcutOverrides["view.preview"] = "";
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(s))!;
        Assert.Equal("Ctrl+K", back.ShortcutOverrides["tab.new"]);
        Assert.Equal("", back.ShortcutOverrides["view.preview"]);
    }
}

public sealed partial class UiTests
{
    [AvaloniaFact]
    public void ShortcutsCanBeChangedInSettingsAndUsedRightAway()
    {
        var overrides = SettingsStore.Instance.Settings.ShortcutOverrides;
        overrides.Clear();
        var (window, vm, _) = Boot();
        try
        {
            vm.IsSettingsOpen = true;
            var settings = window.FindControl<SettingsView>("SettingsOverlay")!;
            settings.OpenPage(SettingsView.ShortcutsPage);
            Settle();
            var rows = settings.FindControl<StackPanel>("ShortcutRows")!;
            var newTabRow = rows.GetVisualDescendants().OfType<Grid>()
                .First(g => g.Classes.Contains("row") && g.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "New tab"));
            var change = newTabRow.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == "Change");
            change.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle();
            Assert.True(ShortcutService.IsRecording);

            // Escape cancels recording without closing Settings.
            window.KeyPress(Avalonia.Input.Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Settle();
            Assert.False(ShortcutService.IsRecording);
            Assert.True(vm.IsSettingsOpen);

            // Record Ctrl+K for New tab.
            newTabRow = rows.GetVisualDescendants().OfType<Grid>()
                .First(g => g.Classes.Contains("row") && g.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "New tab"));
            newTabRow.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == "Change")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle();
            window.KeyPress(Avalonia.Input.Key.K, RawInputModifiers.Control, PhysicalKey.K, "k");
            Settle();
            Assert.Equal("Ctrl+K", overrides["tab.new"]);
            Snapshot(window, "95-shortcuts-settings");

            vm.IsSettingsOpen = false;
            WaitFor(() => !settings.IsVisible);
            int tabs = vm.ActivePane.Tabs.Count;
            window.KeyPress(Avalonia.Input.Key.K, RawInputModifiers.Control, PhysicalKey.K, "k");
            Settle();
            Assert.Equal(tabs + 1, vm.ActivePane.Tabs.Count);
            window.KeyPress(Avalonia.Input.Key.T, RawInputModifiers.Control, PhysicalKey.T, "t");
            Settle();
            Assert.Equal(tabs + 1, vm.ActivePane.Tabs.Count);   // Ctrl+T no longer opens a tab
        }
        finally
        {
            ShortcutService.IsRecording = false;
            ShortcutService.ResetAll();
            vm.IsSettingsOpen = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void F1OpensTheShortcutsSheet()
    {
        SettingsStore.Instance.Settings.ShortcutOverrides.Clear();
        var (window, _, _) = Boot();
        try
        {
            window.KeyPress(Avalonia.Input.Key.F1, RawInputModifiers.None, PhysicalKey.F1, null);
            Settle();
            var sheet = Assert.IsType<ShortcutsSheet>(TopDialog(window));
            var text = string.Join("\n", sheet.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("New tab", text);
            Assert.Contains("Ctrl+T", text);
            Snapshot(sheet, "96-shortcuts-sheet");
            sheet.Close();
        }
        finally
        {
            window.Close();
        }
    }
}
