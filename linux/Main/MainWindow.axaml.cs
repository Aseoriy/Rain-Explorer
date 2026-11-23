using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RainExplorer.Controls;
using RainExplorer.Helpers;
using Avalonia.Animation.Easings;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using RainExplorer.Views;

namespace RainExplorer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _ambientPulse = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _ambientClock = new();
    private const double SidebarWidth = 220;

    public MainWindow() : this(null, null) { }

    /// <param name="adoptFrom">When tearing off a tab: the pane it currently lives in.</param>
    /// <param name="adoptTab">The live tab to move into this new window.</param>
    public MainWindow(PaneViewModel? adoptFrom, TabViewModel? adoptTab, bool? adoptGroup = null)
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        var settings = SettingsStore.Instance.Settings;
        _ambientPulse.Tick += (_, _) =>
        {
            // The 22-second pulse changes by less than one 8-bit alpha step per tick.
            // It should not repaint the entire idle window at the tab animation rate.
            double phase = _ambientClock.Elapsed.TotalSeconds % 44;
            Orb.Opacity = 0.82 + 0.18 * (phase <= 22 ? phase / 22 : (44 - phase) / 22);
        };
        RestoreBounds(settings);
        ApplyChrome();
        ApplyAmbient();
        ApplySettingsEffect();
        ApplySidebar(animate: false);
        ApplySplit();

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.CloseWindowRequested += Close;
        settings.PropertyChanged += OnSettingsChanged;
        ActivityService.Instance.ShowRequested += OnActivityShowRequested;
        ApplyActivityPin();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        SidebarTree.AddHandler(DragDrop.DragOverEvent, Sidebar_DragOver);
        SidebarTree.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearSidebarDropFeedback());
        SidebarTree.AddHandler(DragDrop.DropEvent, Sidebar_Drop);
        SidebarTree.AddHandler(PointerPressedEvent, Sidebar_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        SidebarTree.AddHandler(PointerMovedEvent, Sidebar_PointerMoved, RoutingStrategies.Tunnel);
        SidebarTree.AddHandler(PointerReleasedEvent, (_, _) => _sidebarPress = null, RoutingStrategies.Tunnel);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) UpdateMaxIcon();
        };

        if (adoptFrom is not null && adoptTab is not null)
        {
            // Transferring the last tab closes its source window. Show the destination
            // first so OnLastWindowClose cannot shut down the process during adoption.
            void AdoptTab(object? sender, EventArgs e)
            {
                Opened -= AdoptTab;
                if (adoptGroup ?? adoptTab.IsGroupLeader) _vm.LeftPane.TransferGroupFrom(adoptFrom, adoptTab);
                else _vm.LeftPane.TransferTabFrom(adoptFrom, adoptTab, preserveGroup: false);
                if (_vm.LeftPane.Tabs.Count == 0) _vm.EnsureFirstTab();
            }
            Opened += AdoptTab;
            return;
        }

        _vm.EnsureFirstTab();
        if (App.SelectPath is { } select)
        {
            App.SelectPath = null;
            LeftPaneView.SelectPathWhenLoaded(select);
        }
    }

    /// <summary>A tab dragged out of a window becomes a new window at the drop point.</summary>
    public void TearOffTab(PaneViewModel source, TabViewModel tab, PixelPoint screenPoint, bool? wholeGroup = null)
    {
        var window = new MainWindow(source, tab, wholeGroup)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(screenPoint.X - 120, screenPoint.Y - 20),
            Width = Math.Max(MinWidth, Bounds.Width * 0.8),
            Height = Math.Max(MinHeight, Bounds.Height * 0.8),
        };
        window.Show();
        _ = Motion.Arrive(window.RootBorder, scale: 0.985, y: 0, opacity: 0.72, milliseconds: 170);
        window.RootBorder.RenderTransformOrigin = new RelativePoint(0.5, 0.08, RelativeUnit.Relative);
    }

    internal bool TryGetTabSplitSide(PixelPoint screen, PaneViewModel source, TabViewModel tab,
        bool wholeGroup, out bool onLeft)
    {
        onLeft = false;
        if (!_vm.CanDockTab(source, tab, wholeGroup) || !PaneHost.IsEffectivelyVisible) return false;
        var point = PaneHost.PointToClient(screen);
        // Leave the tab strips and navigation controls to their existing drop flow.
        double top = LeftPaneView.FindControl<Grid>("TabContentHost")!
            .TranslatePoint(default, PaneHost)?.Y ?? 100;
        if (point.Y < top || point.Y > PaneHost.Bounds.Height || point.X < 0 || point.X > PaneHost.Bounds.Width)
            return false;
        // A side should remain easy to reach in a maximized window. A fixed
        // 100px strip made a tab visibly held on one side miss the split target.
        double edge = PaneHost.Bounds.Width * 0.30;
        onLeft = point.X <= edge;
        return onLeft || point.X >= PaneHost.Bounds.Width - edge;
    }

    internal void ShowTabSplitPreview(TabViewModel tab, bool onLeft)
    {
        TabSplitPreview.Width = Math.Max(0, (PaneHost.Bounds.Width - 6) / 2 - 16);
        TabSplitPreview.HorizontalAlignment = onLeft
            ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
        TabSplitPreviewLabel.Text = $"Release to move {tab.TopLevelTitle} to the {(onLeft ? "left" : "right")}";
        TabSplitPreviewImage.Source = tab.PreviewImage;
        TabSplitPreview.IsVisible = true;
    }

    internal void ClearTabSplitPreview()
    {
        TabSplitPreview.IsVisible = false;
        TabSplitPreviewImage.Source = null;
    }

    internal bool DockTab(PaneViewModel source, TabViewModel tab, bool onLeft, bool wholeGroup) =>
        _vm.DockTab(source, tab, onLeft, wholeGroup);

    private PaneView ActivePaneView =>
        ReferenceEquals(_vm.ActivePane, _vm.RightPane) && _vm.IsSplit ? RightPaneView : LeftPaneView;

    /// <summary>A second launch handed us a folder: open it in a new tab and come forward.</summary>
    public void OpenForwardedTarget(string? folder, string? select)
    {
        if (folder is not null) _vm.ActivePane.NewTab(folder, activate: true, revealPaths: select is null ? null : [select]);
        if (select is not null) ActivePaneView.SelectPathWhenLoaded(select);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public async Task ShowFileManagerRequestAsync(FileManagerRequest request)
    {
        _vm.IsSettingsOpen = false;
        if (request.Action == FileManagerAction.ShowItemProperties)
        {
            OpenForwardedTarget(null, null);
            foreach (string path in request.Paths) _ = ShowRequestedProperties(path);
            return;
        }

        var pane = _vm.ActivePane;
        foreach (var target in request.Targets())
        {
            var tab = pane.Tabs.FirstOrDefault(tab => !tab.IsSearchView && PathUtil.Comparer.Equals(tab.RestoreTarget, target.Folder))
                ?? pane.NewTab(TabViewModel.HomeToken, activate: true);
            pane.SelectedTab = tab;
            await tab.NavigateAsync(target.Folder, true, target.Selection);
            if (!pane.Tabs.Contains(tab) || !IsVisible) return;
            if (!PathUtil.Comparer.Equals(tab.CurrentPath, target.Folder) || tab.IsWarning)
                throw new IOException(tab.Status);
            if (target.Selection.Any(path => tab.Find(path) is null))
                throw new IOException("A requested item disappeared or could not be read.");
            pane.SelectedTab = tab;
            var view = ReferenceEquals(pane, _vm.RightPane) ? RightPaneView : LeftPaneView;
            view.SelectPathsWhenLoaded(target.Selection);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        }
        OpenForwardedTarget(null, null);
    }

    private async Task ShowRequestedProperties(string path)
    {
        try { await PropertiesDialog.ShowFor(this, path); }
        catch (Exception ex) { if (IsVisible && _vm.ActivePane.SelectedTab is { } tab) tab.Status = $"⚠️ {ex.Message}"; }
    }

    // ---- Window chrome ---------------------------------------------------------

    private void ApplyChrome()
    {
        bool system = SettingsStore.Instance.Settings.UseSystemTitleBar;
        SystemDecorations = system ? SystemDecorations.Full : SystemDecorations.None;
        WindowButtons.IsVisible = !system;
        ResizeGrips.IsVisible = !system && WindowState != WindowState.Maximized;
        RootBorder.BorderThickness = system || WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
    }

    private void UpdateMaxIcon()
    {
        MaxIcon.Kind = WindowState == WindowState.Maximized ? "restore" : "square";
        ApplyChrome();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (SettingsStore.Instance.Settings.UseSystemTitleBar) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
        BeginMoveDrag(e);
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SettingsStore.Instance.Settings.UseSystemTitleBar) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        ToggleMaximize();
    }

    private void Grip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string tag } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        WindowEdge edge = tag switch
        {
            "W" => WindowEdge.West,
            "E" => WindowEdge.East,
            "N" => WindowEdge.North,
            "S" => WindowEdge.South,
            "NW" => WindowEdge.NorthWest,
            "NE" => WindowEdge.NorthEast,
            "SW" => WindowEdge.SouthWest,
            _ => WindowEdge.SouthEast,
        };
        BeginResizeDrag(edge, e);
        e.Handled = true;
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void MinButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxButton_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void RestoreBounds(AppSettings s)
    {
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        if (s.WindowLeft is double left && s.WindowTop is double top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)left, (int)top);
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveBounds()
    {
        var s = SettingsStore.Instance.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Bounds.Width;
            s.WindowHeight = Bounds.Height;
            s.WindowLeft = Position.X;
            s.WindowTop = Position.Y;
        }
    }

    // ---- Settings-driven layout ------------------------------------------------

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.UseSystemTitleBar): ApplyChrome(); break;
            case nameof(AppSettings.ActivityCenterPinned): ApplyActivityPin(); break;
            case nameof(AppSettings.ShowAmbientBackground): ApplyAmbient(); break;
            case nameof(AppSettings.PerformanceMode):
                ApplyAmbient();
                ApplySettingsEffect();
                break;
            case nameof(AppSettings.SidebarCollapsed): ApplySidebar(animate: true); break;
            case nameof(AppSettings.Theme):
                _ = Motion.Play(RootBorder, 300, new CubicEaseOut(),
                    [Motion.Frame(0, (OpacityProperty, 0.55)), Motion.Frame(1, (OpacityProperty, 1))]);
                break;
        }
    }

    private void ApplyAmbient()
    {
        var s = SettingsStore.Instance.Settings;
        Orb.IsVisible = s.ShowAmbientBackground && !s.PerformanceMode;
        if (Orb.IsVisible) { _ambientClock.Start(); _ambientPulse.Start(); }
        else { _ambientPulse.Stop(); _ambientClock.Stop(); }
    }

    private void ApplySettingsEffect() =>
        BodyGrid.Effect = _vm.IsSettingsOpen && !SettingsStore.Instance.Settings.PerformanceMode
            ? new Avalonia.Media.BlurEffect { Radius = 14 } : null;

    private void ApplySidebar(bool animate)
    {
        bool collapsed = SettingsStore.Instance.Settings.SidebarCollapsed;
        Sidebar.Width = collapsed ? 0 : SidebarWidth;
        Sidebar.BorderThickness = collapsed ? new Thickness(0) : new Thickness(0, 0, 1, 0);
    }

    private void SidebarToggle_Click(object? sender, RoutedEventArgs e)
    {
        var s = SettingsStore.Instance.Settings;
        s.SidebarCollapsed = !s.SidebarCollapsed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsSplit) or nameof(MainViewModel.RightPane))
            ApplySplit();
        if (e.PropertyName == nameof(MainViewModel.IsSettingsOpen))
        {
            // The body is blurred behind the settings scrim, like the Windows build.
            ApplySettingsEffect();
            if (_vm.IsSettingsOpen)
            {
                Motion.Cancel(SettingsOverlay);
                SettingsOverlay.Opacity = 1;
                SettingsOverlay.IsHitTestVisible = true;
                SettingsOverlay.IsVisible = true;
                SettingsOverlay.Refresh();
                SettingsOverlay.AnimateOpening();
            }
            else
            {
                SettingsOverlay.StopAnimations();
                CloseSettingsOverlay();
            }
        }
    }

    /// <summary>Fade Settings out (it stops taking clicks at once), then hide it.</summary>
    private void CloseSettingsOverlay()
    {
        if (!SettingsOverlay.IsVisible) return;
        SettingsOverlay.IsHitTestVisible = false;
        if (SettingsStore.Instance.Settings.PerformanceMode)
        {
            SettingsOverlay.IsVisible = false;
            return;
        }
        _ = Motion.Play(SettingsOverlay, 140, new CubicEaseIn(),
            [Motion.Frame(0, (OpacityProperty, SettingsOverlay.Opacity)), Motion.Frame(1, (OpacityProperty, 0d))], () =>
            {
                if (_vm.IsSettingsOpen) return;   // reopened meanwhile
                SettingsOverlay.IsVisible = false;
                SettingsOverlay.Opacity = 1;
            });
    }

    private void ApplySplit()
    {
        var columns = PaneHost.ColumnDefinitions;
        RightPaneView.IsVisible = _vm.IsSplit;
        if (_vm.IsSplit)
        {
            columns[0].Width = new GridLength(1, GridUnitType.Star);
            columns[1].Width = new GridLength(5);
            columns[2].Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            columns[0].Width = new GridLength(1, GridUnitType.Star);
            columns[1].Width = new GridLength(0);
            columns[2].Width = new GridLength(0);
        }
    }

    // ---- Sidebar ---------------------------------------------------------------

    private async void SidebarRow_Tapped(object? sender, TappedEventArgs e)
    {
        if (_sidebarSuppressTap) return;
        if (sender is not Control { DataContext: SidebarNode node }) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (node.IsHeader)
        {
            MainViewModel.ToggleGroupCollapsed(node.GroupKey);
            return;
        }
        if (node.Drive is { } drive)
        {
            await OpenDriveAsync(drive);
            SidebarTree.SelectedItem = null;
            return;
        }
        if (string.IsNullOrEmpty(node.Path)) return;
        if (node.Path is not (TabViewModel.HomeToken or TabViewModel.DrivesToken) && !Directory.Exists(node.Path))
        {
            ShowStatus($"⚠️ {node.Name} isn't available right now.");
            return;
        }
        _vm.NavigateTo(node.Path);
        SidebarTree.SelectedItem = null;
    }

    private void SidebarAddPin_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SidebarNode node }) return;
        string? path = _vm.ActivePane.SelectedTab?.CurrentPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowStatus("⚠️ Open a folder first, then use + to pin it.");
            return;
        }
        MainViewModel.PinTo(node.GroupKey, path);
    }

    private void SidebarRow_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: SidebarNode node } control) return;
        if (node.Drive is { } drive)
        {
            ShowDriveMenu(control, drive);
            e.Handled = true;
            return;
        }
        var items = new List<Control>();

        if (node.IsHeader)
        {
            items.Add(MenuFactory.Item("New list…", "plus", async () =>
            {
                string? name = await InputDialog.Ask(this, "New list", "List name:", "Quick access 2");
                if (name is not null) MainViewModel.AddCustomGroup(name);
            }, iconBrush: "AccentBright"));
            items.Add(MenuFactory.Item("Rename list…", "pencil", async () =>
            {
                string? name = await InputDialog.Ask(this, "Rename list", "List name:", node.Name);
                if (!string.IsNullOrWhiteSpace(name)) MainViewModel.RenameGroup(node.GroupKey, name);
            }));
            items.Add(MenuFactory.Item("Delete / hide list", "x", async () =>
            {
                if (node.IsCustomHeader && !await ConfirmDialog.Ask(this, "Delete list",
                        $"Delete the list \u201c{node.Name}\u201d? The folders themselves are not touched.",
                        "Delete", "Cancel", danger: true))
                    return;
                MainViewModel.DeleteGroup(node.GroupKey);
            }, iconBrush: "Danger"));
        }
        else
        {
            bool real = node.Kind != NodeKind.Special || node.Path == FileSystemService.TrashToken;
            items.Add(MenuFactory.Item("Open", "arrow-right", () => _vm.NavigateTo(node.Path), iconBrush: "AccentBright"));
            items.Add(MenuFactory.Item("Open in new tab", "plus", () => _vm.ActivePane.NewTab(node.Path, activate: true)));
            items.Add(new Separator());
            if (node.IsPinned)
            {
                items.Add(MenuFactory.Item("Change icon…", "palette", async () =>
                {
                    string? icon = await IconPickerDialog.Ask(this, node.IconKey);
                    if (icon is not null) MainViewModel.SetPinnedIcon(node.GroupKey, node.Path, icon);
                }));
                items.Add(MenuFactory.Item("Rename…", "pencil", async () =>
                {
                    string? name = await InputDialog.Ask(this, "Rename", "Name shown in the sidebar:", node.Name);
                    if (!string.IsNullOrWhiteSpace(name)) MainViewModel.RenamePinned(node.GroupKey, node.Path, name.Trim());
                }));
                items.Add(MenuFactory.Item("Unpin", "x", () => MainViewModel.UnpinFrom(node.GroupKey, node.Path),
                    iconBrush: "Danger"));
            }
            else if (real && node.Kind != NodeKind.Special)
            {
                var targets = MainViewModel.PinTargets();
                var primary = targets.FirstOrDefault(t => t.Key == "quick") ?? targets.FirstOrDefault();
                if (primary is not null)
                    items.Add(MenuFactory.Item($"Pin to {primary.Name}", "star",
                        () => MainViewModel.PinTo(primary.Key, node.Path), iconBrush: "AccentBright",
                        enabled: !MainViewModel.IsPinnedTo(primary.Key, node.Path)));
                foreach (var target in targets.Where(t => t.Key != primary?.Key))
                    items.Add(MenuFactory.Item($"Pin to {target.Name}", "star",
                        () => MainViewModel.PinTo(target.Key, node.Path), iconBrush: "AccentBright",
                        enabled: !MainViewModel.IsPinnedTo(target.Key, node.Path)));
            }
            if (node.Path == FileSystemService.TrashToken)
                items.Add(MenuFactory.Item("Empty Trash…", "trash", () => ActivePaneView.EmptyTrash(), iconBrush: "Danger"));
            if (items[^1] is not Separator) items.Add(new Separator());
            items.Add(MenuFactory.Item("Refresh", "refresh", () =>
            {
                if (node.CanExpand) node.Refresh();
                _vm.RefreshAll();
            }));
        }

        new ContextMenu { ItemsSource = items }.Open(control);
        e.Handled = true;
    }

    private bool _driveOperationRunning;

    private async void SidebarDriveAction_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: SidebarNode { Drive: { } drive } })
            await RunDriveActionAsync(drive, drive.DefaultAction);
    }

    internal async Task OpenDriveAsync(MountEntry drive, bool newTab = false)
    {
        string? path = drive.MountPoint;
        if (!drive.IsMounted)
        {
            var result = await RunDriveActionAsync(drive, DriveAction.Mount);
            if (result is null || result.Error is not null) return;
            path = result.MountPoint;
        }
        if (string.IsNullOrEmpty(path)) return;
        if (newTab) _vm.ActivePane.NewTab(path, activate: true);
        else _vm.NavigateTo(path);
    }

    internal async Task<DriveActionResult?> RunDriveActionAsync(MountEntry drive, DriveAction action)
    {
        if (_driveOperationRunning) return null;
        _driveOperationRunning = true;
        var tab = _vm.ActivePane.SelectedTab;
        if (tab is not null) tab.Status = action switch
        {
            DriveAction.Mount => $"Mounting {drive.Label}…",
            DriveAction.Unmount => $"Unmounting {drive.Label}…",
            _ => $"Removing {drive.Label}…",
        };
        try
        {
            var result = await DriveActions.RunAsync(drive, action);
            await _vm.RefreshDrivesAsync(afterOperation: true);
            if (tab is not null) tab.Status = result.Error is { } error ? $"⚠️ {error}" : action switch
            {
                DriveAction.Mount => $"{drive.Label} was mounted.",
                DriveAction.Unmount => $"{drive.Label} was unmounted.",
                _ => $"{drive.Label} can now be safely removed.",
            };
            return result;
        }
        finally { _driveOperationRunning = false; }
    }

    internal ContextMenu ShowDriveMenu(Control control, MountEntry drive)
    {
        var items = new List<Control>
        {
            MenuFactory.Item(drive.CanMount ? "Mount and open" : "Open", "arrow-right", () => _ = OpenDriveAsync(drive)),
            MenuFactory.Item("Open in new tab", "plus", () => _ = OpenDriveAsync(drive, newTab: true)),
        };
        if (drive.HasAction) items.Add(new Separator());
        if (drive.CanMount)
            items.Add(MenuFactory.Item("Mount", "plus", () => _ = RunDriveActionAsync(drive, DriveAction.Mount)));
        if (drive.CanUnmount)
            items.Add(MenuFactory.Item("Unmount", "x", () => _ = RunDriveActionAsync(drive, DriveAction.Unmount)));
        if (drive.CanSafelyRemove)
            items.Add(MenuFactory.Item(drive.CanEject ? "Eject" : "Safely remove", "arrow-up",
                () => _ = RunDriveActionAsync(drive, DriveAction.SafelyRemove)));
        if (drive.IsMounted)
        {
            items.Add(new Separator());
            foreach (var target in MainViewModel.PinTargets())
                items.Add(MenuFactory.Item($"Pin to {target.Name}", "star",
                    () => MainViewModel.PinTo(target.Key, drive.MountPoint), iconBrush: "AccentBright",
                    enabled: !MainViewModel.IsPinnedTo(target.Key, drive.MountPoint)));
        }
        items.Add(new Separator());
        items.Add(MenuFactory.Item("Refresh", "refresh", () =>
        {
            if (control.DataContext is SidebarNode node && node.CanExpand) node.Refresh();
            _ = _vm.RefreshDrivesAsync();
        }));
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(control);
        return menu;
    }

    private void ShowStatus(string text)
    {
        if (_vm.ActivePane.SelectedTab is { } tab) tab.Status = text;
    }

    // ---- Undo / activity ---------------------------------------------------------

    private void UndoButton_Click(object? sender, RoutedEventArgs e) => RunUndo(redo: false);
    private void RedoButton_Click(object? sender, RoutedEventArgs e) => RunUndo(redo: true);

    private void RunUndo(bool redo)
    {
        string? error = redo ? UndoService.Instance.Redo() : UndoService.Instance.Undo();
        if (error is not null) ShowStatus($"⚠️ {error}");
        _vm.RefreshAll(afterKnownOperation: true);
    }

    private void ActivityButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SettingsStore.Instance.Settings.ActivityCenterPoppedOut)
        {
            // Popped out: the button closes the window when it's in front (it stays on top by default),
            // otherwise brings it forward.
            ToggleActivityCenter();
            return;
        }
        // Clicking the button again closes it (with a fade). If a click elsewhere-style dismissal just
        // closed it on this same press, leave it closed instead of reopening.
        if (ButtonToggle.JustClosed(ActivityButton)) return;
        if (ActivityPopup.IsOpen) CloseActivityFlyout();
        else
        {
            ApplyActivityPin();
            ActivityPopup.IsOpen = true;
            ActivityService.Instance.MarkSeen();
        }
    }

    private bool _activityFading;

    /// <summary>Fade the flyout out, then close it.</summary>
    private void CloseActivityFlyout()
    {
        if (!ActivityPopup.IsOpen || _activityFading) return;
        if (ActivityPopup.Child is not Control card || SettingsStore.Instance.Settings.PerformanceMode)
        {
            ActivityPopup.IsOpen = false;
            return;
        }
        _activityFading = true;
        _ = Motion.Play(card, 130, new CubicEaseIn(),
            [Motion.Frame(0, (OpacityProperty, card.Opacity)), Motion.Frame(1, (OpacityProperty, 0d))], () =>
            {
                _activityFading = false;
                ActivityPopup.IsOpen = false;
                card.Opacity = 1;
            });
    }

    private void OnActivityShowRequested()
    {
        if (!IsActive) return;
        if (SettingsStore.Instance.Settings.ActivityCenterPoppedOut) Views.ActivityWindow.ShowOrActivate(this);
        else ShowActivityFlyout();
    }

    /// <summary>Open the title-bar activity flyout (also used when the pop-out window docks back).</summary>
    public void ShowActivityFlyout()
    {
        ApplyActivityPin();
        ActivityPopup.IsOpen = true;
        ActivityService.Instance.MarkSeen();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Pinned: the flyout stays open when you click elsewhere (Hide or the button closes it).</summary>
    private void ApplyActivityPin() =>
        ActivityPopup.IsLightDismissEnabled = !SettingsStore.Instance.Settings.ActivityCenterPinned;

    private void ActivityPopup_Closed(object? sender, EventArgs e)
    {
        ButtonToggle.NoteClosed(ActivityButton);
        ActivityService.Instance.MarkSeen();
    }

    private void ActivityFlyout_HideRequested(object? sender, EventArgs e) => CloseActivityFlyout();

    private void ActivityFlyout_PopOutRequested(object? sender, EventArgs e)
    {
        ActivityPopup.IsOpen = false;
        Views.ActivityWindow.ShowOrActivate(this);
    }

    private void ActivityFlyout_DetailsRequested(object? sender, ActivityEntry entry)
    {
        if (!SettingsStore.Instance.Settings.ActivityCenterPinned) ActivityPopup.IsOpen = false;
        _ = Views.ActivityDetailsDialog.ShowFor(this, entry);
    }

    // ---- Keyboard ------------------------------------------------------------------

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ShortcutService.IsRecording)
        {
            // Settings → Shortcuts is waiting for a key: it gets this one, and nothing else fires.
            ShortcutService.Recorder?.Invoke(e);
            e.Handled = true;
            return;
        }
        bool inText = FocusManager?.GetFocusedElement() is TextBox;

        if (e.Key == Key.Escape && _vm.IsSettingsOpen)
        {
            if (!SettingsOverlay.HandleEscape()) _vm.IsSettingsOpen = false;   // a search clears first
            e.Handled = true;
            return;
        }

        // Every customizable shortcut goes through ShortcutService (Settings → Shortcuts).
        if (ShortcutService.Match(e, inText) is { } action)
        {
            if (_vm.IsSettingsOpen && action.Id == "nav.search")
            {
                SettingsOverlay.FocusSearch();
                e.Handled = true;
                return;
            }
            if (RunWindowShortcut(action.Id) || (!_vm.IsSettingsOpen && ActivePaneView.RunShortcut(action.Id)))
            {
                e.Handled = true;
                return;
            }
        }

        if (_vm.IsSettingsOpen) return;
        if (ActivePaneView.HandleGlobalKey(e)) e.Handled = true;
    }

    /// <summary>Shortcuts that act on the window rather than the active pane. True when handled.</summary>
    private bool RunWindowShortcut(string id)
    {
        var settings = SettingsStore.Instance.Settings;
        switch (id)
        {
            case "tab.new": _vm.ActivePane.NewTab(activate: true); return true;
            case "tab.close": _vm.ActivePane.CloseTab(_vm.ActivePane.SelectedTab); return true;
            case "tab.reopen": _vm.ActivePane.ReopenClosedTab(); return true;
            case "tab.next": _vm.ActivePane.CycleTab(1); return true;
            case "tab.prev": _vm.ActivePane.CycleTab(-1); return true;
            case "app.settings": _vm.IsSettingsOpen = !_vm.IsSettingsOpen; return true;
            case "view.hidden": settings.ShowHiddenFiles = !settings.ShowHiddenFiles; return true;
            case "view.sidebar": settings.SidebarCollapsed = !settings.SidebarCollapsed; return true;
            case "edit.undo": RunUndo(redo: false); return true;
            case "edit.redo": RunUndo(redo: true); return true;
            case "app.activity": ToggleActivityCenter(); return true;
            case "app.shortcuts": ShowShortcutsSheet(); return true;
            default: return false;
        }
    }

    /// <summary>The "What's new" screen (after an update, or from Settings → About).</summary>
    public void ShowWhatsNew() => _ = Views.WhatsNewDialog.ShowFor(this, ShowMe);

    /// <summary>A What's new "Show me": open the feature it describes.</summary>
    internal void ShowMe(string target)
    {
        switch (target)
        {
            case "activity":
                _vm.IsSettingsOpen = false;
                if (SettingsStore.Instance.Settings.ActivityCenterPoppedOut) Views.ActivityWindow.ShowOrActivate(this);
                else if (!ActivityPopup.IsOpen) ShowActivityFlyout();
                break;
            case "shortcuts":
                _vm.IsSettingsOpen = true;
                SettingsOverlay.OpenPage(Controls.SettingsView.ShortcutsPage);
                break;
            case "commandBox":
                _vm.IsSettingsOpen = false;
                ActivePaneView.SetCommandBoxOpen(true);
                break;
            default:
                if (!target.StartsWith("settings:", StringComparison.Ordinal)) return;
                _vm.IsSettingsOpen = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => SettingsOverlay.Reveal(target["settings:".Length..]),
                    Avalonia.Threading.DispatcherPriority.Loaded);
                break;
        }
    }

    /// <summary>F1 / ?: the keyboard shortcuts sheet, with a way into Settings → Shortcuts.</summary>
    private void ShowShortcutsSheet() => _ = Views.ShortcutsSheet.ShowFor(this, () =>
    {
        _vm.IsSettingsOpen = true;
        SettingsOverlay.OpenPage(Controls.SettingsView.ShortcutsPage);
    });

    /// <summary>The activity center from the keyboard: same as its title-bar button.</summary>
    private void ToggleActivityCenter()
    {
        if (SettingsStore.Instance.Settings.ActivityCenterPoppedOut)
        {
            if (Views.ActivityWindow.Current is { WindowState: not WindowState.Minimized } open && (open.IsActive || open.Topmost)) open.Close();
            else Views.ActivityWindow.ShowOrActivate(this);
        }
        else if (ActivityPopup.IsOpen) CloseActivityFlyout();
        else ShowActivityFlyout();
    }

    // ---- Lifetime ------------------------------------------------------------------

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        SaveBounds();
        if (App.OpenMainWindowCount <= 1) _vm.SaveSession();
        else _vm.FlushActiveProjects();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _ambientPulse.Stop();
        _ambientClock.Stop();
        Motion.Cancel(RootBorder);
        SettingsStore.Instance.Settings.PropertyChanged -= OnSettingsChanged;
        ActivityService.Instance.ShowRequested -= OnActivityShowRequested;
        // The pop-out activity window must not keep Rain running after its last window closes.
        if (App.OpenMainWindowCount == 0) Views.ActivityWindow.CloseForShutdown();
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.Dispose();
        base.OnClosed(e);
    }
}
