using Avalonia.Controls;
using Avalonia.Interactivity;
using RainExplorer.ViewModels;

namespace RainExplorer.Controls;

/// <summary>The All drives page: storage usage and removable-device actions.</summary>
public partial class DrivesView : UserControl
{
    public DrivesView() => InitializeComponent();

    private async void Drive_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DriveVM drive }) return;
        if (drive.Drive is { } entry && TopLevel.GetTopLevel(this) is MainWindow window)
            await window.OpenDriveAsync(entry);
        else if (DataContext is TabViewModel tab) tab.OpenEntryCommand.Execute(drive);
    }

    private async void DriveAction_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: DriveVM { Drive: { } drive } } button
            || TopLevel.GetTopLevel(this) is not MainWindow window) return;
        button.IsEnabled = false;
        try { await window.RunDriveActionAsync(drive, drive.DefaultAction); }
        finally { button.IsEnabled = true; }
    }

    private void Drive_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: DriveVM { Drive: { } drive } } control
            && TopLevel.GetTopLevel(this) is MainWindow window)
        {
            window.ShowDriveMenu(control, drive);
            e.Handled = true;
        }
    }
}
