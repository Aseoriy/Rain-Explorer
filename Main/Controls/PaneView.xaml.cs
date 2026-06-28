using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using RainExplorer.Views;

namespace RainExplorer.Controls;

/// <summary>
/// A single browsing pane: tab strip + toolbar + file list. DataContext is a
/// <see cref="PaneViewModel"/>. The window hosts one (or two, when split).
/// </summary>
public partial class PaneView : UserControl
{
    private PaneViewModel? _vm;
    private TabViewModel? _tab;
    private readonly FileOperationsService _ops = new();
    private bool _committing;
    private Point _dragStart;
    private bool _maybeDrag;
    private Point _marqueeStart;
    private bool _maybeMarquee;
    private bool _marqueeing;
    private FileItem? _dropTarget;
    private DragAdorner? _dragAdorner;
    private ViewBase? _detailsView;
    private ItemsPanelTemplate? _detailsPanel;
    private Style? _detailsRowStyle;
    private static readonly ActivityService Activity = ActivityService.Instance;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    private static string Summarize(IReadOnlyList<string> paths) =>
        paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar)) : $"{paths.Count} items";

    private static string FolderName(string dir)
    {
        string n = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(n) ? dir : n;
    }

    public PaneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        FileList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnHeaderClick));
        FileList.GiveFeedback += FileList_GiveFeedback;
        Loaded += (_, _) => { ApplyLayout(); ApplyPreviewVisibility(); };
        SettingsStore.Instance.Settings.PropertyChanged += OnSettingChanged;
    }

    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ViewLayout)) ApplyLayout();
        else if (e.PropertyName == nameof(AppSettings.ShowPreviewPane)) ApplyPreviewVisibility();
    }

    // ---- Preview pane ------------------------------------------------------
    private DispatcherTimer? _previewTimer;

    // Show/hide the preview column; remembers its width across sessions.
    private void ApplyPreviewVisibility()
    {
        if (PreviewCol is null || PreviewSplitterCol is null) return;
        if (SettingsStore.Instance.Settings.ShowPreviewPane)
        {
            double w = SettingsStore.Instance.Settings.PreviewPaneWidth;
            if (w < 240) w = 340;
            PreviewCol.Width = new GridLength(w);
            PreviewSplitterCol.Width = new GridLength(6);
            UpdatePreview();
        }
        else
        {
            PreviewCol.Width = new GridLength(0);
            PreviewSplitterCol.Width = new GridLength(0);
            Preview?.Clear();   // stop any playing media when the pane is hidden
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!SettingsStore.Instance.Settings.ShowPreviewPane) return;
        // Debounce so fast arrow-key scrubbing doesn't load every intermediate file.
        _previewTimer ??= CreatePreviewTimer();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private DispatcherTimer CreatePreviewTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        t.Tick += (_, _) => { t.Stop(); UpdatePreview(); };
        return t;
    }

    private void UpdatePreview()
    {
        if (Preview is null) return;
        var sel = FileList.SelectedItems;
        Preview.ShowItem(sel.Count == 1 ? sel[0] as FileItem : null, sel.Count);
    }

    private void PreviewSplitter_DragCompleted(object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (PreviewCol is not null && PreviewCol.ActualWidth >= 1)
            SettingsStore.Instance.Settings.PreviewPaneWidth = PreviewCol.ActualWidth;
    }

    // ---- View layout (Details / List / Tiles / Grid) -----------------------
    private void ApplyLayout()
    {
        if (FileList is null) return;
        // Capture the original Details setup once so we can restore it.
        _detailsView ??= FileList.View;
        _detailsPanel ??= FileList.ItemsPanel;
        _detailsRowStyle ??= FileList.ItemContainerStyle;

        switch (SettingsStore.Instance.Settings.ViewLayout)
        {
            case ViewLayout.List: SetIconLayout("ListItemTemplate"); break;
            case ViewLayout.Tiles: SetIconLayout("TileTemplateMedium"); break;
            case ViewLayout.Grid: SetIconLayout("TileTemplateLarge"); break;
            default:
                FileList.View = _detailsView;
                FileList.ItemTemplate = null;
                FileList.ItemsPanel = _detailsPanel;
                FileList.ItemContainerStyle = _detailsRowStyle;
                break;
        }
    }

    private void SetIconLayout(string templateKey)
    {
        FileList.View = null;
        FileList.ItemTemplate = (DataTemplate)FindResource(templateKey);
        FileList.ItemsPanel = (ItemsPanelTemplate)FindResource("IconWrapPanel");
        FileList.ItemContainerStyle = (Style)FindResource("TileItemStyle");
    }

    // ---- Toolbar dropdown buttons ------------------------------------------
    private void OpenButtonMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } b)
        {
            menu.PlacementTarget = b;
            menu.IsOpen = true;
        }
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string key }) _tab?.SortCommand.Execute(key);
    }

    private void ReverseSort_Click(object sender, RoutedEventArgs e) =>
        _tab?.SortCommand.Execute(_tab.SortKey);   // re-selecting the same key flips direction

    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name } &&
            Enum.TryParse<ViewLayout>(name, out var layout))
            SettingsStore.Instance.Settings.ViewLayout = layout;
    }

    // ---- Single-click to open (when enabled) -------------------------------
    private void FileList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeing) { EndMarquee(); e.Handled = true; return; }
        _maybeMarquee = false;
        if (!SettingsStore.Instance.Settings.SingleClickToOpen) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;       // let Ctrl/Shift extend selection
        if (Keyboard.FocusedElement is TextBox) return;            // inline rename in progress
        if (ItemFromPoint<FileItem>(e) is not { } item) return;
        if (FileList.SelectedItems.Count > 1) return;              // don't hijack multi-select
        if (item.IsDirectory && SettingsStore.Instance.Settings.OpenFoldersInNewTab)
            _vm?.NewTab(item.FullPath, activate: true);
        else
            _vm?.SelectedTab?.Open(item);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ActiveContentsChanged -= PlayListAnimation;
            _vm.PropertyChanged -= OnPanePropertyChanged;
        }
        _vm = DataContext as PaneViewModel;
        if (_vm is not null)
        {
            _vm.ActiveContentsChanged += PlayListAnimation;
            _vm.PropertyChanged += OnPanePropertyChanged;
            OnSelectedTabChanged();
        }
    }

    private void OnPanePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.SelectedTab)) OnSelectedTabChanged();
    }

    // Re-target sort-indicator + animation when the active tab changes.
    private void OnSelectedTabChanged()
    {
        if (_tab is not null) _tab.PropertyChanged -= OnTabPropertyChanged;
        _tab = _vm?.SelectedTab;
        if (_tab is not null) _tab.PropertyChanged += OnTabPropertyChanged;
        PlayListAnimation();
        RefreshSortIndicators();
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.SortKey) or nameof(TabViewModel.SortDir))
            RefreshSortIndicators();
    }

    // Show a ▲/▼ arrow on the active sort column.
    private void RefreshSortIndicators()
    {
        if (_tab is null) return;
        string arrow = _tab.SortDir >= 0 ? "  ▲" : "  ▼";
        ColName.Header = "Name" + (_tab.SortKey == "Name" ? arrow : "");
        ColDate.Header = "Date modified" + (_tab.SortKey == "Modified" ? arrow : "");
        ColCreated.Header = "Date created" + (_tab.SortKey == "Created" ? arrow : "");
        ColType.Header = "Type" + (_tab.SortKey == "Type" ? arrow : "");
        ColSize.Header = "Size" + (_tab.SortKey == "Size" ? arrow : "");
    }

    // ---- Activate this pane on any click -----------------------------------
    private void Root_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.ActivateCommand.Execute(null);

    // ---- Ctrl+L focuses the address bar in raw-edit mode (browser-style) ----
    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.L && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            AddressBar.BeginEdit();
            e.Handled = true;
        }
    }

    // ---- Navigation animation (subtle fade + slide up) ---------------------
    private void PlayListAnimation()
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slide = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        FileList.BeginAnimation(OpacityProperty, fade);
        ListTransform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    // ---- Sorting ------------------------------------------------------------
    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column is null) return;

        string key =
            header.Column == ColDate ? "Modified" :
            header.Column == ColCreated ? "Created" :
            header.Column == ColType ? "Type" :
            header.Column == ColSize ? "Size" : "Name";
        _vm?.SelectedTab?.SortCommand.Execute(key);
    }

    // ---- Open on double-click ----------------------------------------------
    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not FileItem item) return;
        // Optionally open folders in a new tab instead of navigating in place.
        if (item.IsDirectory && SettingsStore.Instance.Settings.OpenFoldersInNewTab)
            _vm?.NewTab(item.FullPath, activate: true);
        else
            _vm?.SelectedTab?.Open(item);
    }

    // ---- Middle-click a folder -> open in a new background tab in THIS pane -
    private void FileList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (ItemFromPoint<FileItem>(e) is { IsDirectory: true } dir)
        {
            _vm?.NewTab(dir.FullPath, activate: false);
            e.Handled = true;
        }
    }

    // ---- Middle-click a tab -> close it ------------------------------------
    private void TabBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (ItemFromPoint<TabViewModel>(e) is { } tab)
        {
            _vm?.CloseTab(tab);
            e.Handled = true;
        }
    }

    private static T? ItemFromPoint<T>(MouseButtonEventArgs e) where T : class
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null and not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        return (d as ListBoxItem)?.DataContext as T;
    }

    private static FileItem? ItemUnder(object? source)
    {
        DependencyObject? d = source as DependencyObject;
        while (d is not null and not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        return (d as ListBoxItem)?.DataContext as FileItem;
    }

    // ===================== Drag and drop =====================

    // Arm a potential drag when the press lands on a file row (not empty space,
    // where the list should rubber-band select instead).
    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        var hit = ItemFromPoint<FileItem>(e);
        _maybeDrag = hit is not null;
        // Pressing empty space (not a row, not a column header) arms a rubber-band marquee.
        _marqueeStart = e.GetPosition(FileList);
        _maybeMarquee = hit is null && _tab is { IsFolderView: true }
                        && !IsWithin<GridViewColumnHeader>(e.OriginalSource);
    }

    private static bool IsWithin<T>(object? source) where T : DependencyObject
    {
        DependencyObject? d = source as DependencyObject;
        while (d is not null and not T) d = VisualTreeHelper.GetParent(d);
        return d is T;
    }

    // Past the drag threshold, hand the current selection to the OS as a file drop
    // (works into Explorer, other apps, or the other pane).
    private void FileList_MouseMove(object sender, MouseEventArgs e)
    {
        // Rubber-band marquee takes precedence over the file-drag gesture.
        if (_marqueeing) { UpdateMarquee(e.GetPosition(FileList)); return; }
        if (_maybeMarquee && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(FileList);
            if (Math.Abs(p.X - _marqueeStart.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(p.Y - _marqueeStart.Y) >= SystemParameters.MinimumVerticalDragDistance)
                BeginMarquee(p);
            return;
        }

        if (!_maybeDrag || e.LeftButton != MouseButtonState.Pressed) return;
        if (Keyboard.FocusedElement is TextBox) return;   // inline rename in progress

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _maybeDrag = false;
        var items = SelectedItems();
        if (items.Count == 0) return;
        var paths = items.Select(i => i.FullPath).ToList();

        var data = new DataObject();
        var files = new StringCollection();
        files.AddRange(paths.ToArray());
        data.SetFileDropList(files);

        ShowDragGhost(items);
        try
        {
            var result = DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy | DragDropEffects.Move);
            // If the drop target moved the files out, our folder is now stale.
            if (result == DragDropEffects.Move) _ = _tab?.ReloadAsync();
        }
        catch { /* drag cancelled */ }
        finally { HideDragGhost(); SetDropTarget(null); }
    }

    // ---- Drag ghost: a translucent card that trails the cursor -------------
    private void ShowDragGhost(IReadOnlyList<FileItem> items)
    {
        var layer = AdornerLayer.GetAdornerLayer(FileList);
        if (layer is null) return;
        _dragAdorner = new DragAdorner(FileList, BuildDragVisual(items), layer);
    }

    private void HideDragGhost()
    {
        _dragAdorner?.Detach();
        _dragAdorner = null;
    }

    private void FileList_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (_dragAdorner is null) return;
        if (!GetCursorPos(out var p)) return;
        try
        {
            var rel = FileList.PointFromScreen(new Point(p.X, p.Y));
            _dragAdorner.SetPosition(rel.X + 14, rel.Y + 4);
        }
        catch { /* element detached mid-drag */ }
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    // Build the ghost: file icon + name (or "N items") in a frosted accent card.
    private FrameworkElement BuildDragVisual(IReadOnlyList<FileItem> items)
    {
        var first = items[0];
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        if (TryFindResource($"Ic.{first.IconKey}") is Geometry geo)
            row.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geo,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Stroke = first.IconBrush,
                StrokeThickness = 1.7,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0),
            });

        row.Children.Add(new TextBlock
        {
            Text = items.Count == 1 ? first.DisplayName : $"{items.Count} items",
            Foreground = (Brush)FindResource("Text"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 240,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var card = new Border
        {
            Background = (Brush)FindResource("Glass4"),
            BorderBrush = (Brush)FindResource("AccentLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 7, 13, 7),
            Child = row,
            SnapsToDevicePixels = true,
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.5, BlurRadius = 16, ShadowDepth = 3 },
        };
        return card;
    }

    // ---- Rubber-band marquee selection (drag over empty space) -------------
    private void BeginMarquee(Point start)
    {
        _maybeMarquee = false;
        _marqueeing = true;
        Marquee.Visibility = Visibility.Visible;
        FileList.CaptureMouse();
        UpdateMarquee(start);
    }

    private void UpdateMarquee(Point cur)
    {
        double x = Math.Min(cur.X, _marqueeStart.X);
        double y = Math.Min(cur.Y, _marqueeStart.Y);
        double w = Math.Abs(cur.X - _marqueeStart.X);
        double h = Math.Abs(cur.Y - _marqueeStart.Y);
        Canvas.SetLeft(Marquee, x);
        Canvas.SetTop(Marquee, y);
        Marquee.Width = w;
        Marquee.Height = h;

        // Select rows that intersect the band. Holding Ctrl/Shift keeps the prior selection.
        var band = new Rect(x, y, w, h);
        bool additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        foreach (var obj in FileList.Items)
        {
            if (FileList.ItemContainerGenerator.ContainerFromItem(obj) is not ListViewItem li || !li.IsVisible)
                continue;   // virtualized-away rows can't be hit-tested; that's fine
            Rect b;
            try { b = li.TransformToAncestor(FileList).TransformBounds(new Rect(0, 0, li.ActualWidth, li.ActualHeight)); }
            catch { continue; }
            bool hit = band.IntersectsWith(b);
            if (hit) li.IsSelected = true;
            else if (!additive) li.IsSelected = false;
        }
    }

    private void EndMarquee()
    {
        if (!_marqueeing) return;
        _marqueeing = false;
        Marquee.Visibility = Visibility.Collapsed;
        if (FileList.IsMouseCaptured) FileList.ReleaseMouseCapture();
    }

    private void FileList_LostMouseCapture(object sender, MouseEventArgs e) => EndMarquee();

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = ComputeDropEffect(e);
        // Highlight the folder row the cursor is over, so the drop target is obvious.
        SetDropTarget(ItemUnder(e.OriginalSource) is { IsDirectory: true } dir ? dir : null);
        e.Handled = true;
    }

    private void FileList_DragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    // Track which folder row paints the drop-target highlight (at most one at a time).
    private void SetDropTarget(FileItem? item)
    {
        if (ReferenceEquals(_dropTarget, item)) return;
        if (_dropTarget is not null) _dropTarget.IsDropTarget = false;
        _dropTarget = item;
        if (_dropTarget is not null) _dropTarget.IsDropTarget = true;
    }

    private void FileList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetDropTarget(null);   // clear the hover highlight
        if (_tab is null || !_tab.IsFolderView) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        string dest = DropTargetDir(e);
        if (string.IsNullOrEmpty(dest)) return;

        bool move = ComputeDropEffect(e) == DragDropEffects.Move;
        string? err = FileDropService.Perform(files, dest, move);
        if (err is not null) SetStatus($"⚠️ {err}");
        _ = _tab.ReloadAsync();
    }

    // ===================== Drop onto a tab header =====================
    // Drops the dragged files into that tab's folder (move/copy by the same rules).
    private TabViewModel? _tabDropTarget;

    private void TabBar_DragOver(object sender, DragEventArgs e)
    {
        var tab = TabUnder(e.OriginalSource);
        string? dest = tab is { IsFolderView: true } ? tab.CurrentPath : null;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        var eff = FileDropService.EffectFor(files, dest, e.KeyStates);
        e.Effects = eff;
        SetTabDropTarget(eff == DragDropEffects.None ? null : tab);
        e.Handled = true;
    }

    private void TabBar_DragLeave(object sender, DragEventArgs e) => SetTabDropTarget(null);

    private void TabBar_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var tab = TabUnder(e.OriginalSource);
        SetTabDropTarget(null);
        if (tab is null || !tab.IsFolderView) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        bool move = FileDropService.EffectFor(files, tab.CurrentPath, e.KeyStates) == DragDropEffects.Move;
        string? err = FileDropService.Perform(files, tab.CurrentPath, move);
        if (err is not null) SetStatus($"⚠️ {err}");
        _ = tab.ReloadAsync();
        if (_tab is not null && !ReferenceEquals(_tab, tab)) _ = _tab.ReloadAsync();   // source tab is now stale
    }

    private void SetTabDropTarget(TabViewModel? tab)
    {
        if (ReferenceEquals(_tabDropTarget, tab)) return;
        if (_tabDropTarget is not null) _tabDropTarget.IsDropTarget = false;
        _tabDropTarget = tab;
        if (_tabDropTarget is not null) _tabDropTarget.IsDropTarget = true;
    }

    private static TabViewModel? TabUnder(object? source)
    {
        DependencyObject? d = source as DependencyObject;
        while (d is not null and not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        return (d as ListBoxItem)?.DataContext as TabViewModel;
    }

    // Drop onto a folder row drops *into* that folder; elsewhere, the current folder.
    private string DropTargetDir(DragEventArgs e)
    {
        if (ItemUnder(e.OriginalSource) is { IsDirectory: true } dir) return dir.FullPath;
        return _tab?.CurrentPath ?? string.Empty;
    }

    // Ctrl = copy, Shift = move; otherwise move within a drive, copy across drives.
    private DragDropEffects ComputeDropEffect(DragEventArgs e)
    {
        if (_tab is null || !_tab.IsFolderView || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return DragDropEffects.None;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return DragDropEffects.None;

        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) return DragDropEffects.Copy;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) return DragDropEffects.Move;

        string dest = DropTargetDir(e);
        return SameRoot(files[0], dest) ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    private static bool SameRoot(string a, string b)
    {
        try { return string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // ===================== File operations (context menu) ====================

    private List<FileItem> SelectedItems() => FileList.SelectedItems.Cast<FileItem>().ToList();
    private List<string> SelectedPaths() => SelectedItems().Select(i => i.FullPath).ToList();
    private void SetStatus(string msg) { if (_tab is not null) _tab.Status = msg; }

    // Right-click selects the row under the cursor (unless it's already in the selection),
    // or clears selection when clicking empty space — so the menu targets the right items.
    private void FileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemFromPoint<FileItem>(e);
        if (item is null) FileList.SelectedItems.Clear();
        else if (!FileList.SelectedItems.Contains(item))
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItems.Add(item);
        }
    }

    private static readonly HashSet<string> ArchiveExts =
        new(StringComparer.OrdinalIgnoreCase) { ".zip" };

    private void FileContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems();
        int n = items.Count;
        bool has = n > 0;
        bool singleDir = n == 1 && items[0].IsDirectory;
        bool singleFile = n == 1 && !items[0].IsDirectory;
        bool anyZip = items.Any(i => !i.IsDirectory && ArchiveExts.Contains(Path.GetExtension(i.Name)));
        bool anyDir = items.Any(i => i.IsDirectory);

        MenuOpen.IsEnabled = n == 1;
        MenuOpenNewTab.IsEnabled = singleDir;
        MenuOpenWith.IsEnabled = singleFile;
        MenuOpenTerminal.IsEnabled = _tab is not null;

        // Pin toggles label/enabled based on whether the (folder) selection is pinned.
        MenuPin.IsEnabled = anyDir;
        bool allPinned = anyDir && items.Where(i => i.IsDirectory).All(i => MainViewModel.IsPinned(i.FullPath));
        MenuPin.Header = allPinned ? "Unpin from Quick Access" : "Pin to Quick Access";
        MenuCut.IsEnabled = has;
        MenuCopy.IsEnabled = has;
        MenuCopyPath.IsEnabled = has;
        MenuPaste.IsEnabled = ClipboardHasFiles();
        MenuCompress.IsEnabled = has;
        MenuExtract.IsEnabled = anyZip;
        MenuShortcut.IsEnabled = has;
        MenuRename.IsEnabled = n == 1;
        MenuDelete.IsEnabled = has;
        MenuProperties.IsEnabled = n == 1;
    }

    private static bool ClipboardHasFiles()
    {
        try { return Clipboard.ContainsFileDropList(); } catch { return false; }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileItem item) _vm?.SelectedTab?.Open(item);
    }

    // ---- Cut / Copy / Paste via the real Windows clipboard (interops w/ Explorer) ----
    private void Cut_Click(object sender, RoutedEventArgs e) => ClipboardPut(cut: true);
    private void Copy_Click(object sender, RoutedEventArgs e) => ClipboardPut(cut: false);

    private void ClipboardPut(bool cut)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;

        var data = new DataObject();
        var files = new StringCollection();
        files.AddRange(paths.ToArray());
        data.SetFileDropList(files);
        // "Preferred DropEffect": 2 = move (cut), 1 = copy. Explorer reads this on paste.
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 1)));
        try { Clipboard.SetDataObject(data, true); }
        catch (Exception ex) { SetStatus($"⚠️ {ex.Message}"); }
    }

    private void Paste_Click(object sender, RoutedEventArgs e) => PasteFromClipboard();

    private void PasteFromClipboard()
    {
        if (_tab is null) return;
        IDataObject? data;
        try { data = Clipboard.GetDataObject(); } catch { return; }
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop)) return;
        if (data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        bool move = false;
        if (data.GetDataPresent("Preferred DropEffect") &&
            data.GetData("Preferred DropEffect") is MemoryStream ms)
        {
            var b = new byte[4];
            ms.Position = 0;
            _ = ms.Read(b, 0, 4);
            move = (BitConverter.ToInt32(b, 0) & 2) == 2;
        }

        string? err = FileDropService.Perform(files, _tab.CurrentPath, move);
        if (err is not null) SetStatus($"⚠️ {err}");
        _ = _tab.ReloadAsync();
    }

    // ---- Delete (Recycle Bin) ----
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_tab is null) return;
        var paths = SelectedPaths();
        if (paths.Count == 0) return;

        if (SettingsStore.Instance.Settings.ConfirmDelete)
        {
            string what = paths.Count == 1
                ? $"“{Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar))}”"
                : $"{paths.Count} items";
            if (!ConfirmDialog.Ask(Window.GetWindow(this), "Delete",
                    $"Send {what} to the Recycle Bin?", "Delete", "Cancel", danger: true))
                return;
        }

        var act = Activity.Begin("Delete", Summarize(paths), "trash");
        string? err = _ops.Delete(paths);
        Activity.Complete(act, err is null, err);
        if (err is not null) SetStatus($"⚠️ {err}");
        else UndoService.Instance.Push(new RestoreFromBinAction(
            paths, paths.Count == 1 ? "Delete" : $"Delete ({paths.Count} items)"));
        _ = _tab.ReloadAsync();
    }

    // ---- Keyboard shortcuts, scoped to the file list (so typing in text boxes is unaffected) ----
    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (_tab is null) return;
        if (Keyboard.FocusedElement is TextBox) return;   // inline-rename in progress

        // Alt+Enter -> Properties (Alt makes the real key arrive as SystemKey).
        if (e.Key == Key.System && e.SystemKey == Key.Enter)
        {
            OpenSelectedProperties();
            e.Handled = true;
            return;
        }

        // Shift+F10 -> native Windows shell menu.
        if (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            ShowNativeMenu();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.C: ClipboardPut(cut: false); e.Handled = true; break;
                case Key.X: ClipboardPut(cut: true); e.Handled = true; break;
                case Key.V: PasteFromClipboard(); e.Handled = true; break;
            }
        }
        else if (Keyboard.Modifiers == ModifierKeys.None)
        {
            switch (e.Key)
            {
                case Key.Delete: DeleteSelected(); e.Handled = true; break;
                case Key.F2: BeginRename(); e.Handled = true; break;
                case Key.Back: _tab.UpCommand.Execute(null); e.Handled = true; break;
                case Key.Space:
                    SettingsStore.Instance.Settings.ShowPreviewPane =
                        !SettingsStore.Instance.Settings.ShowPreviewPane;
                    e.Handled = true;
                    break;
            }
        }
    }

    // Warn (when enabled) if a rename changes a file's extension. Returns false to abort.
    private bool AllowRename(string oldName, string newName, bool isDir)
    {
        if (isDir || !SettingsStore.Instance.Settings.WarnOnExtensionChange) return true;
        string oldExt = Path.GetExtension(oldName);
        string newExt = Path.GetExtension(newName);
        if (string.Equals(oldExt, newExt, StringComparison.OrdinalIgnoreCase)) return true;
        string from = string.IsNullOrEmpty(oldExt) ? "(none)" : oldExt;
        string to = string.IsNullOrEmpty(newExt) ? "(none)" : newExt;
        return ConfirmDialog.Ask(Window.GetWindow(this), "Change file extension",
            $"Changing the extension from {from} to {to} might make the file unusable. Continue?",
            "Change", "Cancel", danger: true);
    }

    // ---- Rename (dialog or inline, per Settings) ----
    private void Rename_Click(object sender, RoutedEventArgs e) => BeginRename();

    private void BeginRename()
    {
        if (_tab is null || FileList.SelectedItems.Count != 1) return;
        var item = (FileItem)FileList.SelectedItem;

        if (SettingsStore.Instance.Settings.RenameMode == RenameMode.Inline)
        {
            item.EditName = item.Name;
            item.IsEditing = true;
        }
        else
        {
            var dlg = new InputDialog("Rename", "New name:", item.Name) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                if (!AllowRename(item.Name, dlg.Value, item.IsDirectory)) return;
                var act = Activity.Begin("Rename", $"{item.Name} → {dlg.Value}", "pencil");
                string? err = _ops.Rename(item.FullPath, dlg.Value);
                Activity.Complete(act, err is null, err);
                if (err is not null) SetStatus($"⚠️ {err}");
                else PushRenameUndo(item.FullPath, dlg.Value);
                _ = _tab.ReloadAsync();
            }
        }
    }

    // ---- New folder (dialog or inline, per Settings) ----
    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_tab is null) return;

        if (SettingsStore.Instance.Settings.RenameMode == RenameMode.Inline)
        {
            var act = Activity.Begin("New folder", FolderName(_tab.CurrentPath), "folder-plus");
            var (err, created) = _ops.CreateFolder(_tab.CurrentPath, "New folder");
            Activity.Complete(act, err is null, err);
            if (err is not null) { SetStatus($"⚠️ {err}"); return; }
            if (created is not null)
            {
                act.Detail = Path.GetFileName(created);
                UndoService.Instance.Push(new RecycleAction(new[] { created }, "New folder"));
            }
            await _tab.ReloadAsync();
            if (created is not null && _tab.Find(created) is { } item)
            {
                item.EditName = item.Name;
                item.IsEditing = true;
            }
        }
        else
        {
            var dlg = new InputDialog("New Folder", "Folder name:", "New folder") { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            var act = Activity.Begin("New folder", dlg.Value, "folder-plus");
            var (err, created) = _ops.CreateFolder(_tab.CurrentPath, dlg.Value);
            Activity.Complete(act, err is null, err);
            if (err is not null) { SetStatus($"⚠️ {err}"); return; }
            if (created is not null) UndoService.Instance.Push(new RecycleAction(new[] { created }, "New folder"));
            await _tab.ReloadAsync();
        }
    }

    private void Properties_Click(object sender, RoutedEventArgs e) => OpenSelectedProperties();

    private void OpenSelectedProperties()
    {
        if (FileList.SelectedItem is not FileItem item) return;
        var dlg = new PropertiesDialog(item) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        if (dlg.Changed) _ = _tab?.ReloadAsync();
    }

    // ---- Native Windows shell items, rendered in our themed submenu --------
    private ShellContextMenu? _shellSession;

    private void ShowMore_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem more) return;
        if (_shellSession is not null) return;   // already populated this session

        var paths = SelectedPaths();
        if (paths.Count == 0 && _tab is not null && Directory.Exists(_tab.CurrentPath))
            paths = new List<string> { _tab.CurrentPath };

        more.Items.Clear();
        var owner = Window.GetWindow(this);
        _shellSession = owner is null ? null : ShellContextMenu.Create(paths, owner);
        if (_shellSession is null)
        {
            more.Items.Add(new MenuItem { Header = "Unavailable", IsEnabled = false });
            return;
        }

        var items = _shellSession.BuildItems();
        if (items.Count == 0) { more.Items.Add(new MenuItem { Header = "(no items)", IsEnabled = false }); return; }
        foreach (var c in items) more.Items.Add(c);
    }

    private void FileContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _shellSession?.Dispose();
        _shellSession = null;
        // Reset the placeholder so the submenu repopulates next time it opens.
        MenuShowMore.Items.Clear();
        MenuShowMore.Items.Add(new MenuItem { Header = "Loading…", IsEnabled = false });
    }

    // Shift+F10 still pops the raw OS menu (full fidelity).
    private void ShowNativeMenu()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0 && _tab is not null && Directory.Exists(_tab.CurrentPath))
            paths = new List<string> { _tab.CurrentPath };
        if (paths.Count == 0) return;
        if (Window.GetWindow(this) is { } owner) ShellContextMenu.Show(paths, owner);
    }

    // ---- Pin / unpin selected folders to Quick Access ----------------------
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        var dirs = SelectedItems().Where(i => i.IsDirectory).ToList();
        if (dirs.Count == 0) return;
        bool allPinned = dirs.All(i => MainViewModel.IsPinned(i.FullPath));
        foreach (var d in dirs)
        {
            if (allPinned) MainViewModel.Unpin(d.FullPath);
            else MainViewModel.Pin(d.FullPath, d.Name, d.IconKey);
        }
        SetStatus(allPinned ? "Unpinned from Quick Access" : "Pinned to Quick Access");
    }

    // ---- Open in new tab / open with / terminal ----------------------------
    private void OpenNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is FileItem { IsDirectory: true } dir)
            _vm?.NewTab(dir.FullPath, activate: true);
    }

    private void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not FileItem item || item.IsDirectory) return;
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe",
                $"shell32.dll,OpenAs_RunDLL {item.FullPath}") { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus($"⚠️ {ex.Message}"); }
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        string? dir = FileList.SelectedItem is FileItem { IsDirectory: true } d
            ? d.FullPath : _tab?.CurrentPath;
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            // Windows Terminal if present…
            Process.Start(new ProcessStartInfo("wt.exe")
            { ArgumentList = { "-d", dir }, UseShellExecute = true });
        }
        catch
        {
            // …otherwise fall back to PowerShell.
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe")
                { WorkingDirectory = dir, UseShellExecute = true });
            }
            catch (Exception ex) { SetStatus($"⚠️ {ex.Message}"); }
        }
    }

    // ---- Copy as path (quoted, newline-separated) --------------------------
    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, paths.Select(p => $"\"{p}\""))); }
        catch (Exception ex) { SetStatus($"⚠️ {ex.Message}"); }
    }

    // ---- Create .lnk shortcut(s) in the current folder ---------------------
    private void CreateShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_tab is null) return;
        var items = SelectedItems();
        if (items.Count == 0) return;
        try
        {
            Type? t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) { SetStatus("⚠️ Shortcuts aren't available on this system."); return; }
            dynamic shell = Activator.CreateInstance(t)!;
            foreach (var item in items)
            {
                string link = FileOperationsService.UniquePath(
                    Path.Combine(_tab.CurrentPath, item.Name + " - Shortcut.lnk"));
                dynamic sc = shell.CreateShortcut(link);
                sc.TargetPath = item.FullPath;
                sc.WorkingDirectory = Path.GetDirectoryName(item.FullPath) ?? _tab.CurrentPath;
                sc.Save();
            }
            _ = _tab.ReloadAsync();
        }
        catch (Exception ex) { SetStatus($"⚠️ {ex.Message}"); }
    }

    // ---- Compress selection to a .zip in the current folder ----------------
    private void Compress_Click(object sender, RoutedEventArgs e)
    {
        if (_tab is null) return;
        var items = SelectedItems();
        if (items.Count == 0) return;

        string baseName = items.Count == 1
            ? Path.GetFileNameWithoutExtension(items[0].Name)
            : Path.GetFileName(_tab.CurrentPath.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(baseName)) baseName = "Archive";
        string zipPath = FileOperationsService.UniquePath(
            Path.Combine(_tab.CurrentPath, baseName + ".zip"));

        var act = Activity.Begin("Compress",
            $"{items.Count} item{(items.Count == 1 ? "" : "s")} → {Path.GetFileName(zipPath)}", "package");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var item in items)
                {
                    if (item.IsDirectory) AddDirToZip(zip, item.FullPath, item.Name);
                    else zip.CreateEntryFromFile(item.FullPath, item.Name, CompressionLevel.Optimal);
                }
            }
            Activity.Complete(act, true);
            UndoService.Instance.Push(new RecycleAction(new[] { zipPath }, "Compress"));
            SetStatus($"Compressed {items.Count} item{(items.Count == 1 ? "" : "s")} → {Path.GetFileName(zipPath)}");
            _ = _tab.ReloadAsync();
        }
        catch (Exception ex) { Activity.Complete(act, false, ex.Message); SetStatus($"⚠️ {ex.Message}"); }
    }

    private static void AddDirToZip(ZipArchive zip, string dir, string entryRoot)
    {
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.Combine(entryRoot, Path.GetRelativePath(dir, file)).Replace('\\', '/');
            try { zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal); }
            catch { /* skip locked/unreadable */ }
        }
    }

    // ---- Extract selected .zip(s) into sibling folders ---------------------
    private void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_tab is null) return;
        var zips = SelectedItems()
            .Where(i => !i.IsDirectory && ArchiveExts.Contains(Path.GetExtension(i.Name)))
            .ToList();
        if (zips.Count == 0) return;

        var act = Activity.Begin("Extract", $"{zips.Count} archive{(zips.Count == 1 ? "" : "s")}", "archive");
        int ok = 0;
        string? lastErr = null;
        var extracted = new List<string>();
        foreach (var z in zips)
        {
            try
            {
                string dest = FileOperationsService.UniquePath(
                    Path.Combine(_tab.CurrentPath, Path.GetFileNameWithoutExtension(z.Name)));
                ZipFile.ExtractToDirectory(z.FullPath, dest);
                extracted.Add(dest);
                ok++;
            }
            catch (Exception ex) { lastErr = ex.Message; SetStatus($"⚠️ {ex.Message}"); }
        }
        Activity.Complete(act, ok == zips.Count, lastErr);
        if (extracted.Count > 0) UndoService.Instance.Push(new RecycleAction(extracted, "Extract"));
        if (ok > 0) { SetStatus($"Extracted {ok} archive{(ok == 1 ? "" : "s")}"); _ = _tab.ReloadAsync(); }
    }

    // ---- New text file (dialog or inline, per Settings) --------------------
    private async void NewTextFile_Click(object sender, RoutedEventArgs e)
    {
        if (_tab is null) return;

        if (SettingsStore.Instance.Settings.RenameMode == RenameMode.Inline)
        {
            var act = Activity.Begin("New file", FolderName(_tab.CurrentPath), "file-plus");
            var (err, created) = _ops.CreateFile(_tab.CurrentPath, "New text file.txt");
            Activity.Complete(act, err is null, err);
            if (err is not null) { SetStatus($"⚠️ {err}"); return; }
            if (created is not null)
            {
                act.Detail = Path.GetFileName(created);
                UndoService.Instance.Push(new RecycleAction(new[] { created }, "New file"));
            }
            await _tab.ReloadAsync();
            if (created is not null && _tab.Find(created) is { } item)
            {
                item.EditName = item.Name;
                item.IsEditing = true;
            }
        }
        else
        {
            var dlg = new InputDialog("New Text File", "File name:", "New text file.txt")
            { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            var act = Activity.Begin("New file", dlg.Value, "file-plus");
            var (err, created) = _ops.CreateFile(_tab.CurrentPath, dlg.Value);
            Activity.Complete(act, err is null, err);
            if (err is not null) { SetStatus($"⚠️ {err}"); return; }
            if (created is not null) UndoService.Instance.Push(new RecycleAction(new[] { created }, "New file"));
            await _tab.ReloadAsync();
        }
    }

    // ---- Inline-rename text box lifecycle ----
    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox tb)
            tb.Dispatcher.BeginInvoke(new Action(() => { tb.Focus(); tb.SelectAll(); }),
                DispatcherPriority.Input);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not FileItem item) return;
        if (e.Key == Key.Enter) { e.Handled = true; CommitInline(item); FileList.Focus(); }
        else if (e.Key == Key.Escape) { e.Handled = true; item.IsEditing = false; }
    }

    private void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is FileItem item) CommitInline(item);
    }

    private void CommitInline(FileItem item)
    {
        if (_committing || !item.IsEditing || _tab is null) return;
        _committing = true;
        item.IsEditing = false;
        string newName = item.EditName;
        if (!string.Equals(newName, item.Name, StringComparison.Ordinal))
        {
            if (!AllowRename(item.Name, newName, item.IsDirectory)) { _committing = false; return; }
            var act = Activity.Begin("Rename", $"{item.Name} → {newName}", "pencil");
            string? err = _ops.Rename(item.FullPath, newName);
            Activity.Complete(act, err is null, err);
            if (err is not null) SetStatus($"⚠️ {err}");
            else PushRenameUndo(item.FullPath, newName);
            _ = _tab.ReloadAsync();
        }
        _committing = false;
    }

    // Record a rename so Ctrl+Z restores the previous name.
    private static void PushRenameUndo(string oldPath, string newName)
    {
        string? dir = Path.GetDirectoryName(oldPath.TrimEnd(Path.DirectorySeparatorChar));
        if (dir is null) return;
        UndoService.Instance.Push(new RenameAction(Path.Combine(dir, newName), oldPath));
    }
}
