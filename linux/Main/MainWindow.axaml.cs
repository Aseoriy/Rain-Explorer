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
                SettingsOverlay.Refresh();
                SettingsOverlay.AnimateOpening();
            }
            else SettingsOverlay.StopAnimations();
        }
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
        ActivityPopup.IsOpen = !ActivityPopup.IsOpen;
        if (ActivityPopup.IsOpen) ActivityService.Instance.MarkSeen();
    }

    private void OnActivityShowRequested()
    {
        if (!IsActive) return;
        ActivityPopup.IsOpen = true;
        ActivityService.Instance.MarkSeen();
    }

    private void ActivityPopup_Closed(object? sender, EventArgs e) => ActivityService.Instance.MarkSeen();
    private void ClearActivity_Click(object? sender, RoutedEventArgs e) => ActivityService.Instance.Clear();

    private void HideActivity_Click(object? sender, RoutedEventArgs e) => ActivityPopup.IsOpen = false;

    private void ActivityPause_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ActivityEntry entry }) ActivityService.Instance.RequestPauseToggle(entry);
    }

    private void ActivityCancel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ActivityEntry entry }) ActivityService.Instance.RequestCancel(entry);
    }

    // ---- Keyboard ------------------------------------------------------------------

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool shift = mods.HasFlag(KeyModifiers.Shift);
        bool alt = mods.HasFlag(KeyModifiers.Alt);
        bool inText = FocusManager?.GetFocusedElement() is TextBox;

        if (e.Key == Key.Escape && _vm.IsSettingsOpen)
        {
            _vm.IsSettingsOpen = false;
            e.Handled = true;
            return;
        }

        if (ctrl && !alt)
        {
            switch (e.Key)
            {
                case Key.T when shift: _vm.ActivePane.ReopenClosedTab(); e.Handled = true; return;
                case Key.T: _vm.ActivePane.NewTab(activate: true); e.Handled = true; return;
                case Key.W: _vm.ActivePane.CloseTab(_vm.ActivePane.SelectedTab); e.Handled = true; return;
                case Key.Tab: _vm.ActivePane.CycleTab(shift ? -1 : 1); e.Handled = true; return;
                case Key.PageDown: _vm.ActivePane.CycleTab(1); e.Handled = true; return;
                case Key.PageUp: _vm.ActivePane.CycleTab(-1); e.Handled = true; return;
                case Key.OemComma: _vm.IsSettingsOpen = !_vm.IsSettingsOpen; e.Handled = true; return;
                case Key.H:
                    SettingsStore.Instance.Settings.ShowHiddenFiles = !SettingsStore.Instance.Settings.ShowHiddenFiles;
                    e.Handled = true;
                    return;
                case Key.B:
                    SettingsStore.Instance.Settings.SidebarCollapsed = !SettingsStore.Instance.Settings.SidebarCollapsed;
                    e.Handled = true;
                    return;
                case Key.Z when !inText:
                    RunUndo(redo: shift);
                    e.Handled = true;
                    return;
                case Key.Y when !inText:
                    RunUndo(redo: true);
                    e.Handled = true;
                    return;
            }
        }

        if (_vm.IsSettingsOpen) return;
        if (ActivePaneView.HandleGlobalKey(e)) e.Handled = true;
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
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.Dispose();
        base.OnClosed(e);
    }
}
