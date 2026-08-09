using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using RainExplorer.Controls;
using RainExplorer.Helpers;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using RainExplorer.Views;

namespace RainExplorer;

public partial class MainWindow : Window
{
    private sealed record NodePinMenuTarget(string Key, string Name, bool Dynamic);
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _windowPlacementTimer;
    private readonly bool _skipInitialTab;
    private readonly bool _persistWindowPlacement;
    private NativeRect? _lastNormalPixelBounds;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public MainWindow() : this(skipInitialTab: false, persistWindowPlacement: true)
    {
    }

    private MainWindow(bool skipInitialTab, bool persistWindowPlacement)
    {
        _skipInitialTab = skipInitialTab;
        _persistWindowPlacement = persistWindowPlacement;
        _vm = new MainViewModel();
        InitializeComponent();
        DataContext = _vm;
        ApplySavedWindowPlacement();
        SourceInitialized += (_, _) => ApplyNativeWindowPlacement();
        _windowPlacementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _windowPlacementTimer.Tick += (_, _) =>
        {
            _windowPlacementTimer.Stop();
            SaveWindowPlacement();
        };
        _vm.CloseWindowRequested += Close;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSplit)) UpdateSplitLayout();
            if (e.PropertyName == nameof(MainViewModel.IsSettingsOpen)) UpdateBlur();
        };

        // Navigation shortcuts act on the active pane's active tab.
        AddShortcut(Key.Left, ModifierKeys.Alt, () => _vm.ActivePane.SelectedTab?.BackCommand.Execute(null));
        AddShortcut(Key.Right, ModifierKeys.Alt, () => _vm.ActivePane.SelectedTab?.ForwardCommand.Execute(null));
        AddShortcut(Key.Up, ModifierKeys.Alt, () => _vm.ActivePane.SelectedTab?.UpCommand.Execute(null));
        AddShortcut(Key.F5, ModifierKeys.None, () => _vm.ActivePane.SelectedTab?.RefreshCommand.Execute(null));

        // Tab shortcuts (Ctrl+Tab / Ctrl+Shift+Tab handled in OnPreviewKeyDown).
        InputBindings.Add(new KeyBinding(_vm.NewTabCommand, Key.T, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_vm.CloseTabCommand, Key.W, ModifierKeys.Control));

        Loaded += (_, _) =>
        {
            if (!_skipInitialTab) _vm.EnsureFirstTab();
            UpdateSplitLayout();
            ApplyAmbient();
            // Restore the collapsed sidebar state without animating on first paint.
            if (SettingsStore.Instance.Settings.SidebarCollapsed) Sidebar.Width = 0;
        };
        LocationChanged += (_, _) => OnWindowPlacementChanged();
        SizeChanged += (_, _) => OnWindowPlacementChanged();
        StateChanged += (_, _) =>
        {
            UpdateMaximizeState();
            OnWindowPlacementChanged();
        };

        // Live-toggle the ambient orb when the setting changes.
        SettingsStore.Instance.Settings.PropertyChanged += OnSettingChanged;

        // The activity popup owns its own dismiss logic (see ActivityButton_Click) — any
        // click elsewhere in the window closes it.
        AddHandler(PreviewMouseDownEvent, (MouseButtonEventHandler)Window_PreviewMouseDown, true);
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool insideFlyout = ActivityPopup.Child?.IsMouseOver == true;
        if (ActivityPopup.IsOpen && !insideFlyout
            && !IsDescendantOf(e.OriginalSource as DependencyObject, ActivityButton))
            ActivityPopup.IsOpen = false;
    }

    private static bool IsDescendantOf(DependencyObject? d, DependencyObject ancestor)
    {
        while (d is not null)
        {
            if (ReferenceEquals(d, ancestor)) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void OnWindowPlacementChanged()
    {
        RepositionPopup(ActivityPopup);
        CaptureNormalPixelBounds();
        if (!_persistWindowPlacement || !IsLoaded || WindowState != WindowState.Normal) return;
        _windowPlacementTimer.Stop();
        _windowPlacementTimer.Start();
    }

    private static void RepositionPopup(System.Windows.Controls.Primitives.Popup popup)
    {
        if (!popup.IsOpen) return;
        double offset = popup.HorizontalOffset;
        popup.HorizontalOffset = offset + 0.1;
        popup.HorizontalOffset = offset;
    }

    private void ApplySavedWindowPlacement()
    {
        var settings = SettingsStore.Instance.Settings;
        bool hasNativeBounds = settings.WindowPixelLeft.HasValue && settings.WindowPixelTop.HasValue
            && settings.WindowPixelWidth is > 0 && settings.WindowPixelHeight is > 0;
        if (hasNativeBounds)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            return;
        }

        double virtualWidth = SystemParameters.VirtualScreenWidth;
        double virtualHeight = SystemParameters.VirtualScreenHeight;
        Width = Math.Clamp(settings.WindowWidth, MinWidth, Math.Max(MinWidth, virtualWidth));
        Height = Math.Clamp(settings.WindowHeight, MinHeight, Math.Max(MinHeight, virtualHeight));

        if (settings.WindowLeft is double left && settings.WindowTop is double top)
        {
            var saved = new Rect(left, top, Width, Height);
            var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                virtualWidth, virtualHeight);
            if (saved.IntersectsWith(virtualScreen))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = Math.Clamp(left, virtualScreen.Left, virtualScreen.Right - Width);
                Top = Math.Clamp(top, virtualScreen.Top, virtualScreen.Bottom - Height);
            }
        }

        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void ApplyNativeWindowPlacement()
    {
        var settings = SettingsStore.Instance.Settings;
        if (settings.WindowPixelLeft is not int left || settings.WindowPixelTop is not int top
            || settings.WindowPixelWidth is not > 0 || settings.WindowPixelHeight is not > 0)
            return;

        var rect = new NativeRect
        {
            Left = left,
            Top = top,
            Right = left + settings.WindowPixelWidth.Value,
            Bottom = top + settings.WindowPixelHeight.Value,
        };
        // If that monitor was disconnected, use WPF's safe default placement instead of
        // resurrecting the window off-screen.
        if (MonitorFromRect(ref rect, 0) == IntPtr.Zero)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        IntPtr handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height,
            SwpNoZOrder | SwpNoActivate);
        _lastNormalPixelBounds = rect;
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void CaptureNormalPixelBounds()
    {
        if (WindowState != WindowState.Normal) return;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rect)
            && rect.Width > 0 && rect.Height > 0)
            _lastNormalPixelBounds = rect;
    }

    private void SaveWindowPlacement()
    {
        CaptureNormalPixelBounds();
        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (bounds.Width < MinWidth || bounds.Height < MinHeight ||
            double.IsNaN(bounds.Left) || double.IsNaN(bounds.Top))
            return;

        var settings = SettingsStore.Instance.Settings;
        if (_lastNormalPixelBounds is { } pixels)
        {
            settings.WindowPixelLeft = pixels.Left;
            settings.WindowPixelTop = pixels.Top;
            settings.WindowPixelWidth = pixels.Width;
            settings.WindowPixelHeight = pixels.Height;
        }
        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
    }

    private void AddShortcut(Key key, ModifierKeys mods, Action action) =>
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => action()), key, mods));

    /// <summary>Open <paramref name="folder"/> in a new tab of the active pane, optionally
    /// highlighting <paramref name="select"/> once it loads. Used when a second launch is
    /// forwarded to this (the single) running instance instead of starting a new process.</summary>
    public void OpenPathInNewTab(string folder, string? select)
    {
        App.SelectPath = select;
        _vm.ActivePane.NewTab(folder, activate: true);
    }

    /// <summary>Create a second Rain Explorer window and move the live tab into it.</summary>
    internal static void OpenDetachedTab(PaneViewModel source, TabViewModel tab, Point screenPosition)
    {
        var window = new MainWindow(skipInitialTab: true, persistWindowPlacement: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowState = WindowState.Normal,
        };
        double width = double.IsNaN(window.Width) ? 1120 : window.Width;
        double maxLeft = SystemParameters.VirtualScreenLeft
            + Math.Max(0, SystemParameters.VirtualScreenWidth - width);
        window.Left = Math.Clamp(screenPosition.X - 120,
            SystemParameters.VirtualScreenLeft,
            maxLeft);
        window.Top = Math.Clamp(screenPosition.Y - 22,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80);
        window.Show();
        if (!window._vm.LeftPane.TransferTabFrom(source, tab,
                activate: true, preserveGroup: false))
        {
            window.Close();
            return;
        }
        window.Activate();
    }

    // Ctrl+Tab / Ctrl+Shift+Tab cycle tabs in the active pane.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Esc closes the settings overlay.
        if (e.Key == Key.Escape && _vm.IsSettingsOpen)
        {
            _vm.IsSettingsOpen = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                _vm.PrevTabCommand.Execute(null);
            else
                _vm.NextTabCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Undo/redo file operations — but let text fields keep their own Ctrl+Z/Y.
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && Keyboard.FocusedElement is not TextBox)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (e.Key == Key.Z && !shift) { DoUndo(); e.Handled = true; return; }
            if (e.Key == Key.Y || (e.Key == Key.Z && shift)) { DoRedo(); e.Handled = true; return; }
        }

        base.OnPreviewKeyDown(e);
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => DoUndo();
    private void RedoButton_Click(object sender, RoutedEventArgs e) => DoRedo();

    private void DoUndo()
    {
        if (!UndoService.Instance.CanUndo) return;
        string? err = UndoService.Instance.Undo();
        _vm.RefreshAll(afterKnownOperation: true);
        if (err is not null && _vm.ActivePane.SelectedTab is { } t) t.Status = $"⚠️ {err}";
    }

    private void DoRedo()
    {
        if (!UndoService.Instance.CanRedo) return;
        string? err = UndoService.Instance.Redo();
        _vm.RefreshAll(afterKnownOperation: true);
        if (err is not null && _vm.ActivePane.SelectedTab is { } t) t.Status = $"⚠️ {err}";
    }

    // ---- Split layout: give the right pane + splitter width only when split -
    private void UpdateSplitLayout()
    {
        if (_vm.IsSplit)
        {
            SplitterCol.Width = new GridLength(6);
            RightCol.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            SplitterCol.Width = new GridLength(0);
            RightCol.Width = new GridLength(0);
        }
    }

    // ---- Window chrome buttons ---------------------------------------------
    private void MinButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaxButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        _windowPlacementTimer.Stop();
        bool isLastWindow = Application.Current.Windows.OfType<MainWindow>()
            .All(window => ReferenceEquals(window, this) || !window.IsVisible);
        if (_persistWindowPlacement || isLastWindow) SaveWindowPlacement();
        if (isLastWindow) _vm.SaveSession();
        base.OnClosing(e);
    }

    // StaysOpen="True" on ActivityPopup (see XAML) disables WPF's own outside-click
    // light-dismiss, which otherwise always closes the popup before this Click even fires
    // (it needs a MouseUp; the light-dismiss reacts to MouseDown), making a "close" click
    // indistinguishable from a fresh one and instantly reopening it. Window_PreviewMouseDown
    // handles the "click elsewhere closes it" half; this is purely the toggle.
    private void ActivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActivityPopup.IsOpen) { ActivityPopup.IsOpen = false; return; }
        ActivityPopup.IsOpen = true;
        ActivityService.Instance.MarkSeen();
    }

    private void ClearActivity_Click(object sender, RoutedEventArgs e) =>
        ActivityService.Instance.Clear();

    // ===================== Sidebar tree =====================
    private static SidebarNode? NodeFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as SidebarNode;

    // Navigate when a selectable node is chosen.
    private void Sidebar_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // Ignore selection changes we made ourselves while syncing the highlight to the
        // active tab — only a real user pick should navigate.
        if (_vm.SuppressSidebarNav) return;
        if (e.NewValue is SidebarNode n && n.IsSelectable && !string.IsNullOrEmpty(n.Path))
            _vm.NavigateTo(n.Path);
    }

    // The (+) on a list header: browse for a folder and pin it to THAT list.
    private void AddPin_Click(object sender, RoutedEventArgs e)
    {
        string key = NodeFrom(sender)?.GroupKey ?? "quick";
        var dlg = new OpenFolderDialog { Title = "Pin a folder to this list" };
        string cur = _vm.ActivePane.SelectedTab?.CurrentPath ?? "";
        if (Directory.Exists(cur)) dlg.InitialDirectory = cur;
        if (dlg.ShowDialog() == true && Directory.Exists(dlg.FolderName))
            MainViewModel.PinTo(key, dlg.FolderName);
    }

    // ---- Section header: collapse + list management ------------------------
    private void Header_Click(object sender, MouseButtonEventArgs e)
    {
        if (NodeFrom(sender) is { IsHeader: true } n)
        {
            MainViewModel.ToggleGroupCollapsed(n.GroupKey);
            e.Handled = true;
        }
    }

    private void NewList_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("New list", "List name:", "") { Owner = this };
        if (dlg.ShowDialog() == true) MainViewModel.AddCustomGroup(dlg.Value);
    }

    private void RenameList_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { } n) return;
        var dlg = new InputDialog("Rename list", "List name:", n.Name) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
            MainViewModel.RenameGroup(n.GroupKey, dlg.Value);
    }

    private void DeleteList_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { } n) return;
        if (n.IsCustomHeader && !ConfirmDialog.Ask(this, "Delete sidebar list",
                $"Delete “{n.Name}” and its pinned shortcuts? The folders themselves will not be deleted.",
                "Delete")) return;
        MainViewModel.DeleteGroup(n.GroupKey);
    }

    // ---- Sidebar collapse toggle (animated) --------------------------------
    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        bool collapsed = !SettingsStore.Instance.Settings.SidebarCollapsed;
        SettingsStore.Instance.Settings.SidebarCollapsed = collapsed;
        var anim = new DoubleAnimation(collapsed ? 0 : 220, TimeSpan.FromMilliseconds(210))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Sidebar.BeginAnimation(FrameworkElement.WidthProperty, anim);
    }

    private void NodeOpen_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is { } n && !string.IsNullOrEmpty(n.Path)) _vm.NavigateTo(n.Path);
    }

    private void NodeOpenNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is { } n && !string.IsNullOrEmpty(n.Path))
            _vm.ActivePane.NewTab(n.Path, activate: true);
    }

    private void NodePin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || NodeFrom(sender) is not { } n || !Directory.Exists(n.Path)) return;
        e.Handled = true;
        var target = item.Tag as NodePinMenuTarget
                     ?? new NodePinMenuTarget("quick", SettingsStore.Instance.Settings.QuickAccessName, false);
        if (MainViewModel.IsPinnedTo(target.Key, n.Path)) MainViewModel.UnpinFrom(target.Key, n.Path);
        else MainViewModel.PinTo(target.Key, n.Path, n.Name, n.IconKey);
    }

    private void NodeContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not SidebarNode node || node.IsPinned) return;
        var fixedItem = menu.Items.OfType<MenuItem>().FirstOrDefault(m =>
            Equals(m.Tag, "pinTarget") || m.Tag is NodePinMenuTarget { Dynamic: false });
        if (fixedItem is null) return;

        foreach (var old in menu.Items.OfType<MenuItem>()
                     .Where(m => m.Tag is NodePinMenuTarget { Dynamic: true }).ToList())
            menu.Items.Remove(old);
        fixedItem.Items.Clear();
        var targets = MainViewModel.PinTargets();
        bool isDir = Directory.Exists(node.Path);
        if (targets.Count == 0)
        {
            fixedItem.Header = "No sidebar lists";
            fixedItem.IsEnabled = false;
            return;
        }

        if (targets.Count(t => t.Key != "quick") > 2)
        {
            fixedItem.Header = "Pin to sidebar";
            fixedItem.IsEnabled = isDir;
            fixedItem.Tag = new NodePinMenuTarget("", "", false);
            foreach (var target in targets)
                fixedItem.Items.Add(CreateNodePinItem(target, node, dynamic: false));
            return;
        }

        var primary = targets.FirstOrDefault(t => t.Key == "quick") ?? targets[0];
        ConfigureNodePinItem(fixedItem, primary, node, dynamic: false);
        int insert = menu.Items.IndexOf(fixedItem) + 1;
        foreach (var target in targets.Where(t => t.Key != primary.Key))
            menu.Items.Insert(insert++, CreateNodePinItem(target, node, dynamic: true));
    }

    private MenuItem CreateNodePinItem(MainViewModel.SidebarPinTarget target,
        SidebarNode node, bool dynamic)
    {
        var item = new MenuItem { DataContext = node };
        ConfigureNodePinItem(item, target, node, dynamic);
        item.Click += NodePin_Click;
        return item;
    }

    private static void ConfigureNodePinItem(MenuItem item, MainViewModel.SidebarPinTarget target,
        SidebarNode node, bool dynamic)
    {
        bool pinned = MainViewModel.IsPinnedTo(target.Key, node.Path);
        item.Header = pinned ? $"Unpin from {target.Name}" : $"Pin to {target.Name}";
        item.IsEnabled = Directory.Exists(node.Path);
        item.Tag = new NodePinMenuTarget(target.Key, target.Name, dynamic);
    }

    private void NodeUnpin_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is { } n) MainViewModel.UnpinFrom(n.GroupKey, n.Path);
    }

    private void NodeChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { } n) return;
        var dlg = new IconPickerDialog(n.IconKey) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.SelectedKey is { } key)
            MainViewModel.SetPinnedIcon(n.GroupKey, n.Path, key);
    }

    private void NodeRename_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { } n) return;
        var dlg = new InputDialog("Rename pin", "Display name:", n.Name) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
            MainViewModel.RenamePinned(n.GroupKey, n.Path, dlg.Value);
    }

    private void NodeRefresh_Click(object sender, RoutedEventArgs e) => NodeFrom(sender)?.Refresh();

    // ---- Drop files onto a sidebar folder/pin/drive ------------------------
    private SidebarNode? _sidebarDropTarget;

    // ---- Drag a pinned item to reorder it within its list ------------------
    private const string PinDragFormat = "RainExplorerPinReorder";
    private const string SectionDragFormat = "RainExplorerSectionReorder";
    private Point _pinDragStart;
    private SidebarNode? _pinDragNode;
    private SidebarNode? _sectionDragNode;

    private void SidebarTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pinDragStart = e.GetPosition(null);
        var n = NodeUnder(e.OriginalSource);
        _pinDragNode = n is { Kind: NodeKind.Pinned } ? n : null;
        _sectionDragNode = n is { IsHeader: true } ? n : null;
    }

    private void SidebarTree_MouseMove(object sender, MouseEventArgs e)
    {
        if ((_pinDragNode is null && _sectionDragNode is null)
            || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _pinDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _pinDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var node = _pinDragNode ?? _sectionDragNode!;
        _pinDragNode = null;
        _sectionDragNode = null;
        var data = node.IsHeader
            ? new DataObject(SectionDragFormat, node.GroupKey)
            : new DataObject(PinDragFormat, node.GroupKey + "|" + node.Path);
        try { DragDrop.DoDragDrop(SidebarTree, data, DragDropEffects.Move); }
        catch { /* drag cancelled */ }
        finally { SetSidebarDropTarget(null); HideInsertion(); }
    }

    // ---- Insertion line shown while reordering a pin -----------------------
    private InsertionAdorner? _insertion;

    private void ShowInsertion(TreeViewItem tvi, bool after)
    {
        var layer = AdornerLayer.GetAdornerLayer(SidebarTree);
        if (layer is null) return;
        if (_insertion is null) { _insertion = new InsertionAdorner(SidebarTree); layer.Add(_insertion); }
        try
        {
            double top = tvi.TransformToAncestor(SidebarTree).Transform(new Point(0, 0)).Y;
            _insertion.SetY(after ? top + tvi.ActualHeight : top);
        }
        catch { /* container detached */ }
    }

    private void HideInsertion()
    {
        if (_insertion is null) return;
        AdornerLayer.GetAdornerLayer(SidebarTree)?.Remove(_insertion);
        _insertion = null;
    }

    private static TreeViewItem? TreeViewItemUnder(object? source)
    {
        DependencyObject? d = source as DependencyObject;
        while (d is not null and not TreeViewItem) d = VisualTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }

    private static (string key, string path) ParsePinDrag(DragEventArgs e)
    {
        string s = e.Data.GetData(PinDragFormat) as string ?? "";
        int i = s.IndexOf('|');
        return i < 0 ? ("", s) : (s[..i], s[(i + 1)..]);
    }

    private void Sidebar_DragOver(object sender, DragEventArgs e)
    {
        // Reorder complete sidebar sections by dragging their headers.
        if (e.Data.GetDataPresent(SectionDragFormat))
        {
            string sourceKey = e.Data.GetData(SectionDragFormat) as string ?? "";
            var tvi = TreeViewItemUnder(e.OriginalSource);
            var target = tvi?.DataContext as SidebarNode;
            string targetKey = target?.GroupKey ?? "";
            bool ok = !string.IsNullOrEmpty(targetKey)
                      && !string.Equals(sourceKey, targetKey, StringComparison.OrdinalIgnoreCase);
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            SetSidebarDropTarget(null);
            if (ok && tvi is not null) ShowInsertion(tvi, DropsAfter(e.OriginalSource, e));
            else HideInsertion();
            e.Handled = true;
            return;
        }

        // Reordering a pin: only valid when hovering another pin in the SAME list. Show an
        // insertion line (not the "drop into" highlight) so it reads as a reorder, not a move.
        if (e.Data.GetDataPresent(PinDragFormat))
        {
            var (srcKey, srcPath) = ParsePinDrag(e);
            var tvi = TreeViewItemUnder(e.OriginalSource);
            var t = tvi?.DataContext as SidebarNode;
            bool reorder = t is { Kind: NodeKind.Pinned } && t.GroupKey == srcKey
                           && !string.Equals(t.Path, srcPath, StringComparison.OrdinalIgnoreCase);
            bool moveToList = t is { IsHeader: true, IsPinnedHeader: true }
                              && t.GroupKey != srcKey;
            bool ok = reorder || moveToList;
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            SetSidebarDropTarget(null);   // never paint the folder-drop highlight while reordering
            if (reorder && tvi is not null) ShowInsertion(tvi, DropsAfter(e.OriginalSource, e));
            else HideInsertion();
            e.Handled = true;
            return;
        }

        var node = NodeUnder(e.OriginalSource);
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (node is { IsHeader: true, IsPinnedHeader: true }
            && files is { Length: > 0 } && files.All(Directory.Exists))
        {
            e.Effects = DragDropEffects.Link;
            SetSidebarDropTarget(node);
            e.Handled = true;
            return;
        }
        string? dest = SidebarDropDir(node);
        var eff = FileDropService.EffectFor(files, dest, e.KeyStates);
        e.Effects = eff;
        SetSidebarDropTarget(eff == DragDropEffects.None ? null : node);
        e.Handled = true;
    }

    private void Sidebar_DragLeave(object sender, DragEventArgs e)
    {
        SetSidebarDropTarget(null);
        HideInsertion();
    }

    private void Sidebar_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetSidebarDropTarget(null);
        HideInsertion();

        // Complete section reorder.
        if (e.Data.GetDataPresent(SectionDragFormat))
        {
            string sourceKey = e.Data.GetData(SectionDragFormat) as string ?? "";
            string targetKey = NodeUnder(e.OriginalSource)?.GroupKey ?? "";
            if (!string.IsNullOrEmpty(targetKey))
                MainViewModel.ReorderGroup(sourceKey, targetKey, DropsAfter(e.OriginalSource, e));
            return;
        }

        // Pin reorder within a list.
        if (e.Data.GetDataPresent(PinDragFormat))
        {
            var (srcKey, srcPath) = ParsePinDrag(e);
            var t = NodeUnder(e.OriginalSource);
            if (t is { Kind: NodeKind.Pinned } && t.GroupKey == srcKey)
                MainViewModel.ReorderPin(srcKey, srcPath, t.Path, DropsAfter(e.OriginalSource, e));
            else if (t is { IsHeader: true, IsPinnedHeader: true })
                MainViewModel.MovePin(srcKey, t.GroupKey, srcPath);
            return;
        }

        var node = NodeUnder(e.OriginalSource);
        if (node is { IsHeader: true, IsPinnedHeader: true }
            && e.Data.GetData(DataFormats.FileDrop) is string[] pinFolders
            && pinFolders.Length > 0 && pinFolders.All(Directory.Exists))
        {
            foreach (string folder in pinFolders) MainViewModel.PinTo(node.GroupKey, folder);
            return;
        }
        string? dest = SidebarDropDir(node);
        if (dest is null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        bool move = FileDropService.EffectFor(files, dest, e.KeyStates) == DragDropEffects.Move;
        string? err = FileDropService.Perform(files, dest, move);
        // A move emptied the source folder — refresh the active tab if it's showing it.
        if (err is null && move) _ = _vm.ActivePane.SelectedTab?.ReloadAsync();
    }

    // True if the cursor is in the lower half of the hovered row (drop after it).
    private static bool DropsAfter(object? source, DragEventArgs e)
    {
        DependencyObject? d = source as DependencyObject;
        while (d is not null and not TreeViewItem) d = VisualTreeHelper.GetParent(d);
        if (d is TreeViewItem tvi && tvi.ActualHeight > 0)
            return e.GetPosition(tvi).Y > tvi.ActualHeight / 2;
        return false;
    }

    // A droppable target = a real-directory node (pin / drive / subfolder), not a header or Home/Drives token.
    private static string? SidebarDropDir(SidebarNode? n)
    {
        if (n is null || n.IsHeader || n.Kind == NodeKind.Special) return null;
        return Directory.Exists(n.Path) ? n.Path : null;
    }

    private void SetSidebarDropTarget(SidebarNode? node)
    {
        if (ReferenceEquals(_sidebarDropTarget, node)) return;
        if (_sidebarDropTarget is not null) _sidebarDropTarget.IsDropTarget = false;
        _sidebarDropTarget = node;
        if (_sidebarDropTarget is not null) _sidebarDropTarget.IsDropTarget = true;
    }

    private static SidebarNode? NodeUnder(object? source)
    {
        DependencyObject? d = source as DependencyObject;
        while (d is not null and not TreeViewItem) d = VisualTreeHelper.GetParent(d);
        return (d as TreeViewItem)?.DataContext as SidebarNode;
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ShowAmbientBackground)) ApplyAmbient();
        // App.OnSettingsChanged repaints the palette synchronously first; we then
        // fade the freshly-themed window in so the swap glides instead of popping.
        if (e.PropertyName == nameof(AppSettings.Theme)) PlayThemeFade();
    }

    private void PlayThemeFade()
    {
        var anim = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        RootBorder.BeginAnimation(OpacityProperty, anim);
    }

    private void ApplyAmbient() =>
        Orb.Visibility = SettingsStore.Instance.Settings.ShowAmbientBackground
            ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateBlur()
    {
        BodyGrid.Effect = _vm.IsSettingsOpen
            ? new BlurEffect { Radius = 12, RenderingBias = RenderingBias.Performance }
            : null;
    }

    private void UpdateMaximizeState()
    {
        if (WindowState == WindowState.Maximized)
        {
            RootBorder.Margin = new Thickness(7);
            MaxIcon.Data = (Geometry)FindResource("Ic.restore");
            MaxButton.ToolTip = "Restore";
        }
        else
        {
            RootBorder.Margin = new Thickness(0);
            MaxIcon.Data = (Geometry)FindResource("Ic.square");
            MaxButton.ToolTip = "Maximize";
        }
    }
}
