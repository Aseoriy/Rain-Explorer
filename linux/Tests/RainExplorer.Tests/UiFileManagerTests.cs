using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RainExplorer.Controls;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using RainExplorer.Views;
using Tmds.DBus.Protocol;
using Xunit;

namespace RainExplorer.Tests;

public sealed partial class UiTests
{
    private static FileManagerRequest Reveal(FileManagerAction action, params string[] paths) =>
        FileManagerRequest.Resolve(action, paths.Select(PathUtil.ToFileUri).ToArray(), "");

    private static string[] SelectedPaths(PaneView pane) =>
        FileList(pane).SelectedItems!.OfType<FileItem>().Select(item => item.FullPath).Order(StringComparer.Ordinal).ToArray();

    [AvaloniaFact]
    public async Task ShowItemsSelectsLiteralAndHiddenNamesReusesTabsAndClearsFilters()
    {
        string dir = SampleFolder();
        string literal = Path.Combine(dir, "space # percent% é.txt"); File.WriteAllText(literal, "literal");
        string link = Path.Combine(dir, "broken-link"); File.CreateSymbolicLink(link, "missing");
        string hidden = Path.Combine(dir, ".hidden-config");
        string folder = Path.Combine(dir, "Projects");
        var (window, vm, pane) = Boot(dir);
        bool oldHidden = SettingsStore.Instance.Settings.ShowHiddenFiles;
        try
        {
            SettingsStore.Instance.Settings.ShowHiddenFiles = false;
            var tab = vm.ActivePane.SelectedTab!;
            tab.Filter = "does not match";
            int count = vm.ActivePane.Tabs.Count;
            await window.ShowFileManagerRequestAsync(Reveal(FileManagerAction.ShowItems, literal, hidden, link, folder));
            Assert.Same(tab, vm.ActivePane.SelectedTab);
            Assert.Equal(count, vm.ActivePane.Tabs.Count);
            Assert.Equal("", tab.Filter);
            Assert.Equal(new[] { literal, hidden, link, folder }.Order(StringComparer.Ordinal), SelectedPaths(pane));
            Assert.False(SettingsStore.Instance.Settings.ShowHiddenFiles);
            Snapshot(window, "52-reveal-selected-items");
            await tab.ReloadAsync();
            Assert.NotNull(tab.Find(hidden));
            Assert.False(SettingsStore.Instance.Settings.ShowHiddenFiles);
            await window.ShowFileManagerRequestAsync(Reveal(FileManagerAction.ShowItems, literal));
            Assert.Equal(new[] { literal }, SelectedPaths(pane));
            Assert.Null(tab.Find(hidden));
            Assert.Equal(count, vm.ActivePane.Tabs.Count);
            await window.ShowFileManagerRequestAsync(Reveal(FileManagerAction.ShowFolders, dir));
            Assert.Empty(SelectedPaths(pane));
            Assert.Equal(dir, tab.CurrentPath);
            Navigate(vm, dir);
            Assert.Null(tab.Find(hidden));
        }
        finally { window.Close(); SettingsStore.Instance.Settings.ShowHiddenFiles = oldHidden; }
    }

    [AvaloniaFact]
    public async Task RevealAcrossFoldersUsesActiveSplitPaneAndPreservesLiteralFolderWhitespace()
    {
        string first = SampleFolder();
        string second = Path.Combine(TestEnvironment.NewFolder("reveal-parent"), "folder with space ");
        Directory.CreateDirectory(second);
        string one = Path.Combine(first, "notes.md"), two = Path.Combine(second, "%23.txt");
        File.WriteAllText(two, "literal percent");
        var (window, vm, left) = Boot(first);
        try
        {
            vm.ToggleSplitCommand.Execute(null);
            Assert.Same(vm.RightPane, vm.ActivePane);
            var right = window.FindControl<PaneView>("RightPaneView")!;
            int leftCount = vm.LeftPane.Tabs.Count;
            await window.ShowFileManagerRequestAsync(Reveal(FileManagerAction.ShowItems, one, two));
            Assert.Equal(leftCount, vm.LeftPane.Tabs.Count);
            Assert.Equal(first, vm.LeftPane.SelectedTab!.CurrentPath);
            Assert.Empty(SelectedPaths(left));
            var tabs = vm.RightPane!.Tabs.ToArray();
            Assert.Single(tabs, tab => tab.CurrentPath == first);
            Assert.Single(tabs, tab => tab.CurrentPath == second);
            Assert.Equal(second, vm.RightPane.SelectedTab!.CurrentPath);
            Assert.Equal(new[] { two }, SelectedPaths(right));
            await window.ShowFileManagerRequestAsync(Reveal(FileManagerAction.ShowFolders, second));
            Assert.Equal(tabs.Length, vm.RightPane.Tabs.Count);
            Assert.Empty(SelectedPaths(right));
            right.SelectPathsWhenLoaded([two]);
            vm.RightPane.SelectedTab = tabs.First(tab => tab.CurrentPath == first);
            Settle();
            Assert.Empty(SelectedPaths(right));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RealDesktopBusRevealsItemsAndPropertiesWithoutWaitingForDialogClose()
    {
        string dir = SampleFolder(), path = Path.Combine(dir, "notes.md");
        var (window, vm, pane) = Boot();
        using var bus = new PrivateSessionBus();
        using var service = new FileManagerService(async request =>
            await Dispatcher.UIThread.InvokeAsync(() => window.ShowFileManagerRequestAsync(request)));
        try
        {
            await service.StartAsync(bus.Address);
            using var client = new DBusConnection(bus.Address); await client.ConnectAsync();
            await client.CallMethodAsync(FileManagerTests.RevealMessage(client, "ShowItems", [PathUtil.ToFileUri(path)]))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(dir, vm.ActivePane.SelectedTab!.CurrentPath);
            Assert.Equal(new[] { path }, SelectedPaths(pane));
            await client.CallMethodAsync(FileManagerTests.RevealMessage(client, "ShowItemProperties", [PathUtil.ToFileUri(path)]))
                .WaitAsync(TimeSpan.FromSeconds(5));
            WaitFor(() => TopDialog(window) is PropertiesDialog);
            Assert.True(TopDialog(window)!.IsVisible);
            TopDialog(window)!.Close();
            await client.CallMethodAsync(FileManagerTests.RevealMessage(client, "ShowFolders", [PathUtil.ToFileUri(dir)]));
            Assert.Empty(SelectedPaths(pane));
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ShowInFolderPreferenceSyncsAcrossWindowsAndFitsSmallSettingsPane()
    {
        var settings = SettingsStore.Instance.Settings;
        bool oldEnabled = settings.HandleShowInFolder;
        string oldTheme = settings.Theme;
        settings.HandleShowInFolder = false;
        var (first, firstVm, _) = Boot();
        var (second, secondVm, _) = Boot();
        try
        {
            firstVm.IsSettingsOpen = secondVm.IsSettingsOpen = true;
            var page = first.FindControl<SettingsView>("SettingsOverlay")!;
            var other = second.FindControl<SettingsView>("SettingsOverlay")!;
            var box = page.FindControl<CheckBox>("ShowInFolderBox")!;
            Assert.False(box.IsChecked);
            box.IsChecked = true;
            Assert.True(settings.HandleShowInFolder);
            Assert.True(other.FindControl<CheckBox>("ShowInFolderBox")!.IsChecked);
            settings.HandleShowInFolder = false;
            Assert.False(box.IsChecked);
            Assert.Contains("Off", page.FindControl<TextBlock>("ShowInFolderStatus")!.Text);
            second.Close();
            page.FindControl<ListBox>("SectionList")!.SelectedIndex = 3;
            await RenderFor(250);
            first.Width = 760; first.Height = 480;
            await RenderFor(80);
            var scroll = page.FindControl<ScrollViewer>("PaneAdvanced")!;
            var card = (Border)((StackPanel)((Grid)box.Parent!).Parent!).Parent!;
            foreach (string theme in new[] { "Violet", "Light" })
            {
                settings.Theme = theme; ThemeService.ApplyTheme(theme);
                card.BringIntoView(); await RenderFor(250);
                var presenter = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>()
                    .First(p => ReferenceEquals(p.TemplatedParent, scroll));
                var origin = card.TranslatePoint(default, presenter)!.Value;
                Assert.InRange(origin.Y, 0, presenter.Bounds.Height - card.Bounds.Height);
                Assert.True(card.Bounds.Width <= presenter.Bounds.Width);
                Snapshot(first, "53-reveal-settings-" + theme.ToLowerInvariant());
            }
        }
        finally
        {
            first.Close(); second.Close(); settings.HandleShowInFolder = oldEnabled;
            settings.Theme = oldTheme; ThemeService.ApplyTheme(oldTheme);
        }
    }
}
