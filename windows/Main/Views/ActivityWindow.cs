using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using RainExplorer.Controls;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// The activity center popped out into its own window: drag it anywhere (another monitor too), resize it,
/// keep it on top, or Dock it back into the title-bar flyout. One window serves every Rain window. Its
/// place, size and open state are remembered. Port of the Linux ActivityWindow.
/// </summary>
public sealed class ActivityWindow : Window
{
    private static ActivityWindow? _current;
    private bool _closingForShutdown;
    /// <summary>The Rain window it was popped out from; Dock returns there while it's open.</summary>
    private WeakReference<MainWindow>? _home;

    private static AppSettings Settings => SettingsStore.Instance.Settings;

    /// <summary>The open activity window, if any.</summary>
    public static ActivityWindow? Current => _current;

    private ActivityWindow()
    {
        Title = "Activity — Rain Explorer";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = true;
        MinWidth = 360;
        MinHeight = 240;
        Width = Math.Max(MinWidth, Settings.ActivityWindowWidth);
        Height = Math.Max(MinHeight, Settings.ActivityWindowHeight);
        Topmost = Settings.ActivityWindowTopmost;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontSize = 13;
        SetResourceReference(FontFamilyProperty, "AppFont");
        SetResourceReference(ForegroundProperty, "Text");
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/rain-cloud.png")); }
        catch { /* no icon is fine */ }

        var view = new ActivityCenterView { IsWindowMode = true };
        view.SetHeaderInset(34);   // room for the close button on the header row
        view.DockRequested += (_, _) => Dock();
        view.DetailsRequested += (_, entry) => ActivityDetailsDialog.ShowFor(this, entry);
        view.DragHandle.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        };

        var glyph = new Path { Width = 14, Height = 14 };
        glyph.SetResourceReference(StyleProperty, "IconPath");
        glyph.SetResourceReference(Path.DataProperty, "Ic.x");
        var close = new Button
        {
            Width = 30, Height = 28, Margin = new Thickness(4, 8, 8, 0), Content = glyph,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Close",
        };
        close.SetResourceReference(StyleProperty, "CloseButton");
        glyph.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = close });
        System.Windows.Automation.AutomationProperties.SetName(close, "Close the activity window");
        close.Click += (_, _) => Close();

        var content = new Grid();
        content.Children.Add(new Border { Padding = new Thickness(6, 6, 6, 12), Child = view });
        content.Children.Add(close);
        var card = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = content,
        };
        card.SetResourceReference(Border.BackgroundProperty, "MenuBg");
        card.SetResourceReference(Border.BorderBrushProperty, "Line2");
        Content = card;

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
        Activated += (_, _) => ActivityService.Instance.MarkSeen();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ActivityWindowTopmost)) Topmost = Settings.ActivityWindowTopmost;
    }

    private void SaveBounds()
    {
        if (WindowState != WindowState.Normal) return;
        Settings.ActivityWindowLeft = Left;
        Settings.ActivityWindowTop = Top;
        Settings.ActivityWindowWidth = ActualWidth;
        Settings.ActivityWindowHeight = ActualHeight;
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
        window.PlaceNear(near);
        Settings.ActivityCenterPoppedOut = true;
        Settings.ActivityWindowOpen = true;
        window.Show();
        ActivityService.Instance.MarkSeen();
        return window;
    }

    private void PlaceNear(Window? near)
    {
        if (Settings.ActivityWindowLeft is double left && Settings.ActivityWindowTop is double top)
        {
            // Only reuse it if it's still on the desktop (a monitor may have been unplugged).
            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (desktop.Contains(new Point(left + 40, top + 20)))
            {
                Left = left;
                Top = top;
                return;
            }
        }
        if (near is null || near.WindowState == WindowState.Minimized)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }
        // Next to the title bar's top-right corner, where the flyout appears.
        double nearLeft = near.WindowState == WindowState.Maximized ? SystemParameters.WorkArea.Left : near.Left;
        double nearTop = near.WindowState == WindowState.Maximized ? SystemParameters.WorkArea.Top : near.Top;
        Left = Math.Max(SystemParameters.VirtualScreenLeft, nearLeft + near.ActualWidth - Width - 16);
        Top = Math.Max(SystemParameters.VirtualScreenTop, nearTop + 48);
    }

    /// <summary>Dock: close the window and show the activity center as the flyout again.</summary>
    private void Dock()
    {
        Settings.ActivityCenterPoppedOut = false;
        Settings.ActivityWindowOpen = false;
        _current = null;
        Close();
        MainWindow? main = _home is not null && _home.TryGetTarget(out var home) && home.IsVisible ? home : null;
        main ??= Application.Current?.Windows.OfType<MainWindow>().LastOrDefault(w => w.IsVisible);
        if (main is null) return;
        main.Activate();
        main.OpenActivityFlyout();
    }

    /// <summary>Rain is exiting (last main window closed): close the window but remember it was open.</summary>
    public static void CloseForShutdown()
    {
        if (_current is not { } window) return;
        window._closingForShutdown = true;
        window.Close();
    }
}
