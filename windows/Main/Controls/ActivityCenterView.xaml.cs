using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Controls;

/// <summary>
/// The activity center's header and list, shared by the title-bar flyout and the pop-out
/// <see cref="Views.ActivityWindow"/>. In the flyout the header offers Pin (stay open) and Pop out; in the
/// window it offers On top and Dock. The host decides what Hide, Pop out, Dock and View details do.
/// Port of the Linux ActivityCenterView.
/// </summary>
public partial class ActivityCenterView : UserControl
{
    public static readonly DependencyProperty IsWindowModeProperty = DependencyProperty.Register(
        nameof(IsWindowMode), typeof(bool), typeof(ActivityCenterView),
        new PropertyMetadata(false, (d, _) => ((ActivityCenterView)d).ApplyMode()));

    /// <summary>True when hosted in the pop-out window (shows On top / Dock instead of Pin / Pop out).</summary>
    public bool IsWindowMode
    {
        get => (bool)GetValue(IsWindowModeProperty);
        set => SetValue(IsWindowModeProperty, value);
    }

    public event EventHandler? HideRequested;
    public event EventHandler? PopOutRequested;
    public event EventHandler? DockRequested;
    public event EventHandler<ActivityEntry>? DetailsRequested;

    private static AppSettings Settings => SettingsStore.Instance.Settings;

    public ActivityCenterView()
    {
        InitializeComponent();
        ApplyMode();
        SyncToggles();
        Loaded += (_, _) =>
        {
            Settings.PropertyChanged -= OnSettingsChanged;
            Settings.PropertyChanged += OnSettingsChanged;
            SyncToggles();
        };
        Unloaded += (_, _) => Settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.ActivityCenterPinned) or nameof(AppSettings.ActivityWindowTopmost))
            SyncToggles();
    }

    private void ApplyMode()
    {
        bool window = IsWindowMode;
        PinToggle.Visibility = window ? Visibility.Collapsed : Visibility.Visible;
        PopOutButton.Visibility = window ? Visibility.Collapsed : Visibility.Visible;
        HideButton.Visibility = window ? Visibility.Collapsed : Visibility.Visible;   // the window has its own close button
        OnTopToggle.Visibility = window ? Visibility.Visible : Visibility.Collapsed;
        DockButton.Visibility = window ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncToggles()
    {
        PinToggle.IsChecked = Settings.ActivityCenterPinned;
        OnTopToggle.IsChecked = Settings.ActivityWindowTopmost;
    }

    /// <summary>The header, which the pop-out window uses as its drag handle.</summary>
    internal FrameworkElement DragHandle => HeaderRow;

    /// <summary>Keep the header's buttons clear of something drawn at its right end (the window's close button).</summary>
    internal void SetHeaderInset(double right) => HeaderRow.Margin = new Thickness(8, 8, 6 + right, 8);

    private void PinToggle_Click(object sender, RoutedEventArgs e) => Settings.ActivityCenterPinned = PinToggle.IsChecked == true;
    private void OnTopToggle_Click(object sender, RoutedEventArgs e) => Settings.ActivityWindowTopmost = OnTopToggle.IsChecked == true;
    private void Hide_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);
    private void PopOut_Click(object sender, RoutedEventArgs e) => PopOutRequested?.Invoke(this, EventArgs.Empty);
    private void Dock_Click(object sender, RoutedEventArgs e) => DockRequested?.Invoke(this, EventArgs.Empty);
    private void Clear_Click(object sender, RoutedEventArgs e) => ActivityService.Instance.Clear();

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActivityEntry entry) ActivityService.Instance.RequestPauseToggle(entry);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActivityEntry entry) ActivityService.Instance.RequestCancel(entry);
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActivityEntry entry) DetailsRequested?.Invoke(this, entry);
    }
}
