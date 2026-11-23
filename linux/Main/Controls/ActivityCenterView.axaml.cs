using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Controls;

/// <summary>
/// The activity center's header and list, shared by the title-bar flyout and the pop-out
/// <see cref="Views.ActivityWindow"/>. In the flyout the header offers Pin (stay open) and Pop out; in the
/// window it offers On top and Dock. The host decides what Hide, Pop out, Dock and View details do.
/// </summary>
public partial class ActivityCenterView : UserControl
{
    public static readonly StyledProperty<bool> IsWindowModeProperty =
        AvaloniaProperty.Register<ActivityCenterView, bool>(nameof(IsWindowMode));

    /// <summary>True when hosted in the pop-out window (shows On top / Dock instead of Pin / Pop out).</summary>
    public bool IsWindowMode
    {
        get => GetValue(IsWindowModeProperty);
        set => SetValue(IsWindowModeProperty, value);
    }

    public event EventHandler? HideRequested;
    public event EventHandler? PopOutRequested;
    public event EventHandler? DockRequested;
    public event EventHandler<ActivityEntry>? DetailsRequested;

    private static AppSettings Settings => SettingsStore.Instance.Settings;
    private bool _syncing;

    public ActivityCenterView()
    {
        InitializeComponent();
        PinToggle.IsCheckedChanged += (_, _) =>
        {
            if (!_syncing) Settings.ActivityCenterPinned = PinToggle.IsChecked == true;
        };
        OnTopToggle.IsCheckedChanged += (_, _) =>
        {
            if (!_syncing) Settings.ActivityWindowTopmost = OnTopToggle.IsChecked == true;
        };
        ApplyMode();
        SyncToggles();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsWindowModeProperty) ApplyMode();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Settings.PropertyChanged -= OnSettingsChanged;
        Settings.PropertyChanged += OnSettingsChanged;
        SyncToggles();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.ActivityCenterPinned) or nameof(AppSettings.ActivityWindowTopmost))
            SyncToggles();
    }

    private void ApplyMode()
    {
        bool window = IsWindowMode;
        PinToggle.IsVisible = !window;
        PopOutButton.IsVisible = !window;
        HideButton.IsVisible = !window;   // the window has its own close button
        OnTopToggle.IsVisible = window;
        DockButton.IsVisible = window;
    }

    private void SyncToggles()
    {
        _syncing = true;
        PinToggle.IsChecked = Settings.ActivityCenterPinned;
        OnTopToggle.IsChecked = Settings.ActivityWindowTopmost;
        _syncing = false;
    }

    /// <summary>The header, which the pop-out window uses as its drag handle.</summary>
    internal Control DragHandle => HeaderRow;

    /// <summary>Keep the header's buttons clear of something drawn at its right end (the window's close button).</summary>
    internal void SetHeaderInset(double right) => HeaderRow.Margin = new Thickness(8, 8, 6 + right, 8);

    private void Hide_Click(object? sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);
    private void PopOut_Click(object? sender, RoutedEventArgs e) => PopOutRequested?.Invoke(this, EventArgs.Empty);
    private void Dock_Click(object? sender, RoutedEventArgs e) => DockRequested?.Invoke(this, EventArgs.Empty);
    private void Clear_Click(object? sender, RoutedEventArgs e) => ActivityService.Instance.Clear();

    private void Pause_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ActivityEntry entry }) ActivityService.Instance.RequestPauseToggle(entry);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ActivityEntry entry }) ActivityService.Instance.RequestCancel(entry);
    }

    private void Details_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ActivityEntry entry }) DetailsRequested?.Invoke(this, entry);
    }
}
