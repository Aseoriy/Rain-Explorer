using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using RainExplorer.Controls;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// The activity center popped out into its own window: drag it anywhere (another monitor too), resize it,
/// keep it on top, or Dock it back into the title-bar flyout. One window serves every Rain window. Its
/// place, size and open state are remembered.
/// </summary>
public sealed class ActivityWindow : DialogWindow
{
    private static ActivityWindow? _current;
    private bool _closingForShutdown;
    /// <summary>The Rain window it was popped out from; Dock returns there while it's open.</summary>
    private WeakReference<MainWindow>? _home;
    private readonly ActivityCenterView _view;

    private static AppSettings Settings => SettingsStore.Instance.Settings;

    /// <summary>The open activity window, if any.</summary>
    public static ActivityWindow? Current => _current;

    private ActivityWindow()
    {
        Title = "Activity — Rain Explorer";
        CanResize = true;
        ShowInTaskbar = true;
        MinWidth = 340;
        MinHeight = 240;
        Width = Math.Max(MinWidth, Settings.ActivityWindowWidth);
        Height = Math.Max(MinHeight, Settings.ActivityWindowHeight);
        Topmost = Settings.ActivityWindowTopmost;
        WindowStartupLocation = WindowStartupLocation.Manual;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://RainExplorer/Assets/rain-256.png"))); }
        catch { /* no icon is fine */ }

        _view = new ActivityCenterView { IsWindowMode = true };
        _view.DockRequested += (_, _) => Dock();
        _view.DetailsRequested += (_, entry) => _ = ActivityDetailsDialog.ShowFor(this, entry);

        var close = new Button
        {
            Width = 30, Height = 28, Margin = new Thickness(4, 6, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Content = new Icon { Kind = "x", Width = 14, Height = 14 },
        };
        ApplyTheme(close, "CloseButton");
        ToolTip.SetTip(close, "Close");
        AutomationProperties.SetName(close, "Close the activity window");
        close.Click += (_, _) => Close();

        // Bottom-right resize grip (the window is frameless with Rain's own title bar).
        var grip = new Border
        {
            Width = 14, Height = 14, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.BottomRightCorner),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
        };
        grip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginResizeDrag(WindowEdge.SouthEast, e);
        };

        var content = new Panel();
        content.Children.Add(new Border { Padding = new Thickness(6, 6, 6, 10), Child = _view });
        content.Children.Add(close);
        content.Children.Add(grip);
        _closeButton = close;
        Content = Card(content, new Thickness(0));
        _view.DragHandle.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1) BeginMoveDrag(e);
        };

        ApplyChrome();
        Settings.PropertyChanged += OnSettingsChanged;
        Closing += (_, _) => SaveBounds();
        Closed += (_, _) =>
        {
            Settings.PropertyChanged -= OnSettingsChanged;
            // A window that was already replaced (Dock, then Pop out again) must not touch the new one's state.
            if (!ReferenceEquals(_current, this)) return;
            _current = null;
            // Closed by the user: remember that. Closed because Rain is exiting: reopen next launch.
            if (!_closingForShutdown) Settings.ActivityWindowOpen = false;
        };
        Opened += (_, _) => ActivityService.Instance.MarkSeen();
    }

    private readonly Button _closeButton;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ActivityWindowTopmost)) Topmost = Settings.ActivityWindowTopmost;
        else if (e.PropertyName == nameof(AppSettings.UseSystemTitleBar)) ApplyChrome();
    }

    /// <summary>Follow Settings → Use the system title bar, like the main window.</summary>
    private void ApplyChrome()
    {
        bool system = Settings.UseSystemTitleBar;
        SystemDecorations = system ? SystemDecorations.Full : SystemDecorations.None;
        _closeButton.IsVisible = !system;
        _view.SetHeaderInset(system ? 0 : 34);   // room for the close button on the header row
    }

    private void SaveBounds()
    {
        if (WindowState != WindowState.Normal) return;
        Settings.ActivityWindowLeft = Position.X / RenderScaling;
        Settings.ActivityWindowTop = Position.Y / RenderScaling;
        Settings.ActivityWindowWidth = Bounds.Width;
        Settings.ActivityWindowHeight = Bounds.Height;
        SettingsStore.Instance.Flush();
    }

    /// <summary>
    /// Show the activity window (creating it if needed) and bring it forward. The first time it opens next to
    /// <paramref name="near"/>; after that it reopens where it was left.
    /// </summary>
    public static ActivityWindow ShowOrActivate(Window? near = null)
    {
        if (_current is { } existing)
        {
            if (near is MainWindow nearMain) existing._home = new WeakReference<MainWindow>(nearMain);
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            ActivityService.Instance.MarkSeen();
            return existing;
        }

        var window = new ActivityWindow();
        if (near is MainWindow home) window._home = new WeakReference<MainWindow>(home);
        _current = window;
        window.Position = window.StartPosition(near);
        Settings.ActivityCenterPoppedOut = true;
        Settings.ActivityWindowOpen = true;
        window.Show();
        return window;
    }

    private PixelPoint StartPosition(Window? near)
    {
        double scale = near?.RenderScaling ?? 1;
        if (Settings.ActivityWindowLeft is double left && Settings.ActivityWindowTop is double top)
        {
            var saved = new PixelPoint((int)(left * scale), (int)(top * scale));
            // Only reuse it if it's still on a connected screen (a monitor may have been unplugged).
            var screens = (near ?? this).Screens;
            if (screens.All.Any(s => s.WorkingArea.Contains(saved + new PixelPoint(40, 20)))) return saved;
        }
        if (near is null) return new PixelPoint(80, 80);
        // Next to the title bar's top-right corner, where the flyout appears.
        int x = near.Position.X + (int)((near.Bounds.Width - Width - 16) * scale);
        int y = near.Position.Y + (int)(48 * scale);
        return new PixelPoint(Math.Max(0, x), Math.Max(0, y));
    }

    /// <summary>Dock: close the window and show the activity center as the flyout again.</summary>
    private void Dock()
    {
        Settings.ActivityCenterPoppedOut = false;
        Settings.ActivityWindowOpen = false;
        _current = null;   // closing may finish later; a new pop-out must not find this window
        Close();
        MainWindow? main = _home is not null && _home.TryGetTarget(out var home) && home.IsVisible ? home : null;
        main ??= (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            .OfType<MainWindow>().LastOrDefault(w => w.IsVisible);
        main?.ShowActivityFlyout();
    }

    /// <summary>Rain is exiting (last main window closed): close the window but remember it was open.</summary>
    public static void CloseForShutdown()
    {
        if (_current is not { } window) return;
        // Flag the window itself: Avalonia may finish closing it after Close() returns.
        window._closingForShutdown = true;
        window.Close();
    }
}
