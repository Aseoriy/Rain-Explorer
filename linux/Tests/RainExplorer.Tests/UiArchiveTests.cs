using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RainExplorer.Controls;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

public sealed partial class UiTests
{
    private static MenuItem ArchiveMenuItem(PaneView pane, string name)
    {
        var top = FileList(pane).ContextMenu!.Items.OfType<MenuItem>().ToArray();
        return top.Concat(top.SelectMany(item => item.Items.OfType<MenuItem>())).Single(item => item.Name == name);
    }

    private static async Task OpenArchiveMenu(PaneView pane)
    {
        var list = FileList(pane);
        list.ContextMenu!.Close();
        list.RaiseEvent(new ContextRequestedEventArgs());
        await RenderFor(200);
        Assert.True(list.ContextMenu.IsOpen, "File context menu did not open.");
    }

    [AvaloniaFact]
    public async Task ArchiveMenuMatchesSelectionAndProviderCapabilities()
    {
        using var env = new ArchiveTestEnvironment("ark", "xarchiver");
        string dir = SampleFolder();
        File.CreateSymbolicLink(Path.Combine(dir, "archive-link.zip"), "archive.tar.gz");
        File.WriteAllText(Path.Combine(dir, "second.ZIP"), "archive");
        var (window, vm, pane) = Boot(dir);
        try
        {
            Assert.Equal("ark", ArchiveLauncher.Resolve(out _)?.Id);
            Select(pane, "archive.tar.gz");
            await OpenArchiveMenu(pane);
            Assert.Equal("Open in Ark", ArchiveMenuItem(pane, "ArchiveOpen").Header);
            Assert.True(ArchiveMenuItem(pane, "ArchiveExtract").IsVisible);
            Assert.True(ArchiveMenuItem(pane, "ArchiveExtractHere").IsEnabled);
            Assert.True(ArchiveMenuItem(pane, "ArchiveCompress").IsVisible);
            Snapshot(window, "50-archive-menu");

            FileList(pane).ContextMenu!.Close();
            FileList(pane).SelectedItems!.Add(FileList(pane).Items.OfType<FileItem>().Single(item => item.Name == "second.ZIP"));
            await OpenArchiveMenu(pane);
            Assert.False(ArchiveMenuItem(pane, "ArchiveOpen").IsVisible);
            Assert.True(ArchiveMenuItem(pane, "ArchiveExtract").IsVisible);
            SettingsStore.Instance.Settings.ArchiveManager = "xarchiver";
            await OpenArchiveMenu(pane);
            Assert.False(ArchiveMenuItem(pane, "ArchiveExtractHere").IsVisible);
            Assert.True(ArchiveMenuItem(pane, "ArchiveExtractTo").IsEnabled);

            foreach (string name in new[] { "notes.md", "Projects", "archive-link.zip" })
            {
                FileList(pane).ContextMenu!.Close();
                Select(pane, name);
                await OpenArchiveMenu(pane);
                Assert.False(ArchiveMenuItem(pane, "ArchiveOpen").IsVisible);
                Assert.False(ArchiveMenuItem(pane, "ArchiveExtract").IsVisible);
                Assert.Equal(name != "archive-link.zip", ArchiveMenuItem(pane, "ArchiveCompress").IsVisible);
            }
            FileList(pane).ContextMenu!.Close();
            FileList(pane).SelectedItems!.Clear();
            await OpenArchiveMenu(pane);
            Assert.False(ArchiveMenuItem(pane, "ArchiveCompress").IsVisible);
            FileList(pane).ContextMenu!.Close();
            Navigate(vm, FileSystemService.TrashToken);
            await OpenArchiveMenu(pane);
            Assert.False(ArchiveMenuItem(pane, "ArchiveCompress").IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ArchiveMenuLaunchesSelectedItemsAndExplainsMissingApps()
    {
        using var env = new ArchiveTestEnvironment("ark");
        string dir = SampleFolder();
        var (window, vm, pane) = Boot(dir);
        try
        {
            Select(pane, "archive.tar.gz");
            await OpenArchiveMenu(pane);
            ArchiveMenuItem(pane, "ArchiveExtractTo").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            WaitFor(() => File.Exists(env.Capture) && new FileInfo(env.Capture).Length > 0);
            var args = Encoding.UTF8.GetString(File.ReadAllBytes(env.Capture)).Split('\0')[..^1];
            Assert.Equal(new[] { "--batch", "--autodestination", "--autosubfolder", "--dialog", "--", Path.Combine(dir, "archive.tar.gz") }, args);
            WaitFor(() => vm.ActivePane.SelectedTab!.Status == "Extraction opened in Ark.");
            Assert.Equal("Extraction opened in Ark.", vm.ActivePane.SelectedTab!.Status);

            FileList(pane).ContextMenu!.Close();
            Select(pane, "notes.md");
            FileList(pane).SelectedItems!.Add(FileList(pane).Items.OfType<FileItem>().Single(item => item.Name == "Projects"));
            await OpenArchiveMenu(pane);
            File.Delete(env.Capture);
            ArchiveMenuItem(pane, "ArchiveCompress").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            WaitFor(() => File.Exists(env.Capture) && new FileInfo(env.Capture).Length > 0);
            args = Encoding.UTF8.GetString(File.ReadAllBytes(env.Capture)).Split('\0')[..^1];
            Assert.Equal(new[] { "--add", "--dialog", "--changetofirstpath", "--", Path.Combine(dir, "notes.md"), Path.Combine(dir, "Projects") }, args);
            WaitFor(() => vm.ActivePane.SelectedTab!.Status == "Compression opened in Ark.");

            FileList(pane).ContextMenu!.Close();
            File.Delete(env.Capture);
            File.Delete(Path.Combine(env.Root, "ark"));
            await OpenArchiveMenu(pane);
            ArchiveMenuItem(pane, "ArchiveCompress").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            WaitFor(() => vm.ActivePane.SelectedTab!.Status.Contains("software manager"));
            Assert.Contains("software manager", vm.ActivePane.SelectedTab!.Status);
            Assert.False(File.Exists(env.Capture));
            Assert.True(File.Exists(Path.Combine(dir, "notes.md")));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ArchivePreferenceSyncsAcrossWindowsAndFitsSmallSettingsPane()
    {
        using var env = new ArchiveTestEnvironment("ark", "file-roller");
        string oldTheme = SettingsStore.Instance.Settings.Theme;
        var (first, firstVm, _) = Boot();
        var (second, secondVm, _) = Boot();
        try
        {
            firstVm.IsSettingsOpen = secondVm.IsSettingsOpen = true;
            var settings = first.FindControl<SettingsView>("SettingsOverlay")!;
            var other = second.FindControl<SettingsView>("SettingsOverlay")!;
            var box = settings.FindControl<ComboBox>("ArchiveAppBox")!;
            var otherBox = other.FindControl<ComboBox>("ArchiveAppBox")!;
            box.SelectedItem = box.Items.Single(item => item!.ToString() == "File Roller");
            Assert.Equal("file-roller", SettingsStore.Instance.Settings.ArchiveManager);
            Assert.Equal("File Roller", otherBox.SelectedItem!.ToString());
            SettingsStore.Instance.Settings.ArchiveManager = "engrampa";
            Assert.Equal("Engrampa (not installed)", box.SelectedItem!.ToString());
            Assert.Contains("no longer installed", settings.FindControl<TextBlock>("ArchiveAppStatus")!.Text);
            box.SelectedIndex = 0;
            Assert.Equal("", SettingsStore.Instance.Settings.ArchiveManager);
            Assert.Equal("Automatic (Ark)", otherBox.SelectedItem!.ToString());
            second.Close();
            settings.FindControl<ListBox>("SectionList")!.SelectedIndex = 2;
            await RenderFor(250);
            var scroll = settings.FindControl<ScrollViewer>("PaneFiles")!;
            foreach (var theme in new[] { "Violet", "Light" })
            {
                SettingsStore.Instance.Settings.Theme = theme;
                ThemeService.ApplyTheme(theme);
                first.Width = 760; first.Height = 480;
                await RenderFor(80);
                var card = (Border)((StackPanel)box.Parent!).Parent!;
                card.BringIntoView();
                await RenderFor(80);
                var presenter = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>()
                    .First(p => ReferenceEquals(p.TemplatedParent, scroll));
                var origin = box.TranslatePoint(default, presenter)!.Value;
                Assert.InRange(origin.Y, 0, presenter.Bounds.Height - box.Bounds.Height);
                Assert.True(box.Bounds.Width <= presenter.Bounds.Width);
                var cardOrigin = card.TranslatePoint(default, presenter)!.Value;
                Assert.InRange(cardOrigin.Y, 0, presenter.Bounds.Height - card.Bounds.Height);
                Snapshot(first, "51-archive-settings-" + theme.ToLowerInvariant());
            }
        }
        finally
        {
            first.Close(); second.Close();
            SettingsStore.Instance.Settings.Theme = oldTheme;
            ThemeService.ApplyTheme(oldTheme);
        }
    }
}
