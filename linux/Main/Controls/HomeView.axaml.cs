using Avalonia.Controls;
using Avalonia.Interactivity;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;

namespace RainExplorer.Controls;

/// <summary>The Home dashboard: pinned folders, drives and recently opened items.</summary>
public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private async void Entry_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DriveVM { Drive: { } drive } } && TopLevel.GetTopLevel(this) is MainWindow window)
            await window.OpenDriveAsync(drive);
        else if (sender is Control { DataContext: { } entry } && DataContext is TabViewModel tab)
            tab.OpenEntryCommand.Execute(entry);
    }

    private void Entry_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: DriveVM { Drive: { } drive } } driveControl
            && TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.ShowDriveMenu(driveControl, drive);
            e.Handled = true;
            return;
        }
        if (sender is not Control { DataContext: FileItem item } control) return;
        var pane = (TopLevel.GetTopLevel(this)?.DataContext as MainViewModel)?.ActivePane;
        var items = new List<Control>();
        if (item.IsDirectory)
        {
            items.Add(MenuFactory.Item("Open in new tab", "plus", () => pane?.NewTab(item.FullPath, activate: true)));
            items.Add(MenuFactory.Item("Open in Terminal", "terminal", () => TerminalLauncher.TryOpen(item.FullPath, out _)));
        }
        else if (Path.GetDirectoryName(item.FullPath) is { } folder)
        {
            items.Add(MenuFactory.Item("Show in folder", "folder-open", () =>
            {
                if (DataContext is TabViewModel tab) _ = tab.NavigateAsync(folder, true);
            }));
        }
        items.Add(MenuFactory.Item("Copy path", "copy", () => _ = ClipboardService.SetTextAsync(TopLevel.GetTopLevel(this), item.FullPath)));
        new ContextMenu { ItemsSource = items }.Open(control);
        e.Handled = true;
    }
}
