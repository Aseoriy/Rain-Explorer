using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using RainExplorer.Helpers;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using RainExplorer.Views;

namespace RainExplorer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
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
            _vm.EnsureFirstTab();
            UpdateSplitLayout();
            ApplyAmbient();
            // Restore the collapsed sidebar state without animating on first paint.
            if (SettingsStore.Instance.Settings.SidebarCollapsed) Sidebar.Width = 0;
        };
        StateChanged += (_, _) => UpdateMaximizeState();

        // Live-toggle the ambient orb when the setting changes.
        SettingsStore.Instance.Settings.PropertyChanged += OnSettingChanged;
    }

    private void AddShortcut(Key key, ModifierKeys mods, Action action) =>
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => action()), key, mods));

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

    private void DoUndo()
    {
        if (!UndoService.Instance.CanUndo) return;
        string? err = UndoService.Instance.Undo();
        _vm.RefreshAll();
        if (err is not null && _vm.ActivePane.SelectedTab is { } t) t.Status = $"⚠️ {err}";
    }

    private void DoRedo()
    {
        if (!UndoService.Instance.CanRedo) return;
        string? err = UndoService.Instance.Redo();
        _vm.RefreshAll();
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

    private void ActivityButton_Click(object sender, RoutedEventArgs e)
    {
        ActivityPopup.IsOpen = !ActivityPopup.IsOpen;
        if (ActivityPopup.IsOpen) ActivityService.Instance.MarkSeen();
    }

    private void ClearActivity_Click(object sender, RoutedEventArgs e) =>
        ActivityService.Instance.Clear();

    // ===================== Sidebar tree =====================
    private static SidebarNode? NodeFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as SidebarNode;

    // Navigate when a selectable node is chosen.
    private void Sidebar_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
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
        MainViewModel.AddCustomGroup(dlg.ShowDialog() == true ? dlg.Value : null);
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
        if (NodeFrom(sender) is { } n) MainViewModel.DeleteGroup(n.GroupKey);
    }

    private void HideDrives_Click(object sender, RoutedEventArgs e) =>
        SettingsStore.Instance.Settings.ShowDrivesInSidebar = false;

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
        if (NodeFrom(sender) is { } n && !string.IsNullOrEmpty(n.Path))
            MainViewModel.Pin(n.Path, n.Name, n.IconKey);
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
            MainViewModel.SetPinnedIcon(n.Path, key);
    }

    private void NodeRename_Click(object sender, RoutedEventArgs e)
    {
        if (NodeFrom(sender) is not { } n) return;
        var dlg = new InputDialog("Rename pin", "Display name:", n.Name) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Value))
            MainViewModel.RenamePinned(n.Path, dlg.Value);
    }

    private void NodeRefresh_Click(object sender, RoutedEventArgs e) => NodeFrom(sender)?.Refresh();

    // ---- Drop files onto a sidebar folder/pin/drive ------------------------
    private SidebarNode? _sidebarDropTarget;

    // ---- Drag a pinned item to reorder it within its list ------------------
    private const string PinDragFormat = "RainExplorerPinReorder";
    private Point _pinDragStart;
    private SidebarNode? _pinDragNode;

    private void SidebarTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pinDragStart = e.GetPosition(null);
        var n = NodeUnder(e.OriginalSource);
        _pinDragNode = n is { Kind: NodeKind.Pinned } ? n : null;
    }

    private void SidebarTree_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pinDragNode is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _pinDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _pinDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var node = _pinDragNode;
        _pinDragNode = null;
        var data = new DataObject(PinDragFormat, node.GroupKey + "|" + node.Path);
        try { DragDrop.DoDragDrop(SidebarTree, data, DragDropEffects.Move); }
        catch { /* drag cancelled */ }
        finally { SetSidebarDropTarget(null); }
    }

    private static (string key, string path) ParsePinDrag(DragEventArgs e)
    {
        string s = e.Data.GetData(PinDragFormat) as string ?? "";
        int i = s.IndexOf('|');
        return i < 0 ? ("", s) : (s[..i], s[(i + 1)..]);
    }

    private void Sidebar_DragOver(object sender, DragEventArgs e)
    {
        // Reordering a pin: only valid when hovering another pin in the SAME list.
        if (e.Data.GetDataPresent(PinDragFormat))
        {
            var (srcKey, srcPath) = ParsePinDrag(e);
            var t = NodeUnder(e.OriginalSource);
            bool ok = t is { Kind: NodeKind.Pinned } && t.GroupKey == srcKey
                      && !string.Equals(t.Path, srcPath, StringComparison.OrdinalIgnoreCase);
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            SetSidebarDropTarget(ok ? t : null);
            e.Handled = true;
            return;
        }

        var node = NodeUnder(e.OriginalSource);
        string? dest = SidebarDropDir(node);
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        var eff = FileDropService.EffectFor(files, dest, e.KeyStates);
        e.Effects = eff;
        SetSidebarDropTarget(eff == DragDropEffects.None ? null : node);
        e.Handled = true;
    }

    private void Sidebar_DragLeave(object sender, DragEventArgs e) => SetSidebarDropTarget(null);

    private void Sidebar_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetSidebarDropTarget(null);

        // Pin reorder within a list.
        if (e.Data.GetDataPresent(PinDragFormat))
        {
            var (srcKey, srcPath) = ParsePinDrag(e);
            var t = NodeUnder(e.OriginalSource);
            if (t is { Kind: NodeKind.Pinned } && t.GroupKey == srcKey)
                MainViewModel.ReorderPin(srcKey, srcPath, t.Path, DropsAfter(e.OriginalSource, e));
            return;
        }

        var node = NodeUnder(e.OriginalSource);
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
