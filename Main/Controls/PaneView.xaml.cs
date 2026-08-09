using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using RainExplorer.Helpers;
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
    private sealed record PinMenuTarget(string Key, string Name, bool Dynamic);
    private PaneViewModel? _vm;
    private TabViewModel? _tab;
    private readonly FileOperationsService _ops = new();
    private bool _committing;
    private Point _dragStart;
    private bool _maybeDrag;
    private Point _marqueeStart;
    private bool _maybeMarquee;
    private bool _marqueeing;
    // When the user presses an already-selected row (part of a multi-selection) without
    // modifiers, we suppress WPF's default "collapse to this row" so a drag can carry the
    // whole selection. If no drag follows, the mouse-up collapses to just this row.
    private FileItem? _pendingSingleSelect;
    private FileItem? _dropTarget;
    private DragAdorner? _dragAdorner;
    private ViewBase? _detailsView;
    private ItemsPanelTemplate? _detailsPanel;
    private Style? _detailsRowStyle;
    private bool _clearSelectionAfterNav;
    private bool _fileContextMenuOpen;
    private bool _fileOperationInProgress;
    private CancellationTokenSource? _gitMenuCts;
    private GitRepositoryContext? _gitMenuRepository;
    private GitRepositoryStatus? _gitMenuStatus;
    private const string TabDragTokenFormat = "RainExplorer.TabDragToken";
    private sealed record TabDragPayload(PaneViewModel SourcePane, TabViewModel Tab);
    private sealed record TabDragSlot(TabViewModel Tab, ListBoxItem Container, double X, double Width);
    private static readonly Dictionary<string, TabDragPayload> ActiveTabDrags = new();
    private TabViewModel? _tabDragCandidate;
    private ListBoxItem? _tabDragCandidateContainer;
    private Point _tabDragStart;
    private Popup? _tabDragPopup;
    private HwndSource? _tabDragPopupSource;
    private bool _tabDragCancelled;
    private TabViewModel? _tabGroupTarget;
    private TabViewModel? _tabGroupSource;
    private TabViewModel? _tabGroupHoverCandidate;
    private TabViewModel? _tabGroupHoverSource;
    private DispatcherTimer? _tabGroupHoverTimer;
    private TabInsertionAdorner? _tabInsertionAdorner;
    private TabViewModel? _tabPreviewSource;
    private TabViewModel? _tabPreviewTarget;
    private bool _tabPreviewAfter;
    private int _tabPreviewIndex = -1;
    private readonly List<TabDragSlot> _tabDragSlots = new();
    private static readonly TimeSpan TabGroupHoverDelay = TimeSpan.FromSeconds(1);
    private const double TabGroupHoverStartRatio = 0.15;
    private const double TabGroupHoverEndRatio = 0.85;
    private Window? _ownerWindow;
    private ContextMenu? _openTabContextMenu;
    private int _projectsAnimationVersion;
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
        Loaded += PaneView_Loaded;
        Unloaded += PaneView_Unloaded;
        SettingsStore.Instance.Settings.PropertyChanged += OnSettingChanged;
    }

    private void PaneView_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyLayout();
        ApplyPreviewVisibility();
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is null) return;
        _ownerWindow.AddHandler(UIElement.PreviewMouseDownEvent,
            (MouseButtonEventHandler)DismissToolbarPopupsOnOutsideClick, true);
    }

    private void PaneView_Unloaded(object sender, RoutedEventArgs e)
    {
        ResetTabGroupHover();
        if (_ownerWindow is null) return;
        _ownerWindow.RemoveHandler(UIElement.PreviewMouseDownEvent,
            (MouseButtonEventHandler)DismissToolbarPopupsOnOutsideClick);
        _ownerWindow = null;
    }

    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ViewLayout)) ApplyLayout();
        else if (e.PropertyName == nameof(AppSettings.ShowPreviewPane)) ApplyPreviewVisibility();
        else if (e.PropertyName == nameof(AppSettings.TabProjects)) RefreshProjectsButton();
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
    // These are plain Popups (not ContextMenu — a ContextMenu keeps its own mouse capture
    // regardless of StaysOpen, which swallows the re-click on its own toggle button rather
    // than letting it fall through as a normal Click, so a naive toggle either instantly
    // reopens or gets stuck open no matter how the reopen is suppressed). A bare Popup has no
    // such capture, so we can safely own the whole open/close lifecycle ourselves: the button's
    // Click is the only way its own popup opens/closes, and any other click in the window
    // (handled below) closes it.
    private void OpenButtonMenu_Click(object sender, RoutedEventArgs e)
    {
        var flyout = sender == SortButton ? SortPopup : sender == LayoutButton ? LayoutPopup : null;
        if (flyout is null) return;
        if (flyout.Visibility == Visibility.Visible)
        {
            SetToolbarFlyoutOpen(flyout, false);
            return;
        }
        CloseToolbarPopups();
        SetToolbarFlyoutOpen(flyout, true);
    }

    private static void SetToolbarFlyoutOpen(Border flyout, bool open)
    {
        flyout.BeginAnimation(OpacityProperty, null);
        if (flyout.RenderTransform is not TranslateTransform transform) return;
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        if (!open)
        {
            flyout.Visibility = Visibility.Collapsed;
            flyout.Opacity = 0;
            transform.Y = -6;
            return;
        }

        flyout.Visibility = Visibility.Visible;
        flyout.Opacity = 0;
        transform.Y = -6;
        flyout.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
            TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0,
            TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    private void CloseToolbarPopups()
    {
        SetToolbarFlyoutOpen(SortPopup, false);
        SetToolbarFlyoutOpen(LayoutPopup, false);
        SetProjectsFlyoutOpen(false);
    }

    // A click anywhere else in the window closes whichever dropdown is open (popup content
    // itself renders in its own top-level window, so clicks inside it never reach here).
    private void DismissToolbarPopupsOnOutsideClick(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (SortPopup.Visibility == Visibility.Visible
            && !IsWithin(src, SortButton) && !IsWithin(src, SortPopup))
            SetToolbarFlyoutOpen(SortPopup, false);
        if (LayoutPopup.Visibility == Visibility.Visible
            && !IsWithin(src, LayoutButton) && !IsWithin(src, LayoutPopup))
            SetToolbarFlyoutOpen(LayoutPopup, false);
        if (ProjectsFlyout.Visibility == Visibility.Visible
            && !IsWithin(src, ProjectsButton) && !IsWithin(src, ProjectsFlyout))
            SetProjectsFlyoutOpen(false);
    }

    private static bool IsWithin(DependencyObject? d, DependencyObject ancestor)
    {
        while (d is not null)
        {
            if (ReferenceEquals(d, ancestor)) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string key }) _tab?.SortCommand.Execute(key);
        SetToolbarFlyoutOpen(SortPopup, false);
    }

    private void ReverseSort_Click(object sender, RoutedEventArgs e)
    {
        _tab?.SortCommand.Execute(_tab.SortKey);   // re-selecting the same key flips direction
        SetToolbarFlyoutOpen(SortPopup, false);
    }

    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name } &&
            Enum.TryParse<ViewLayout>(name, out var layout))
        {
            SettingsStore.Instance.Settings.ViewLayout = layout;
            // Apply directly as well as through the settings notification. This also
            // repairs the view when the selected setting already matches the clicked item.
            ApplyLayout();
        }
        SetToolbarFlyoutOpen(LayoutPopup, false);
    }

    // ---- Single-click to open (when enabled) -------------------------------
    private void FileList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeing) { EndMarquee(); e.Handled = true; return; }
        _maybeMarquee = false;

        // A plain click (no drag) on a row that was part of a multi-selection: now that we
        // know it wasn't a drag, collapse the selection down to just that row (normal click).
        if (_pendingSingleSelect is { } single)
        {
            _pendingSingleSelect = null;
            FileList.SelectedItems.Clear();
            FileList.SelectedItems.Add(single);
            return;
        }

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

    /// <summary>Open a folder in a new tab of this pane (used by the breadcrumb context menu).</summary>
    public void OpenInNewTab(string path) => _vm?.NewTab(path, activate: true);

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
        RefreshProjectsButton();
    }

    private void OnPanePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.SelectedTab)) OnSelectedTabChanged();
        else if (e.PropertyName == nameof(PaneViewModel.ActiveProjectId)) RefreshProjectsButton();
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
        // Ctrl+F: jump to the search box for the current folder. The recursive toggle
        // beside it chooses "this folder only" vs "include subfolders".
        else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
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

        TrySelectPendingItem();

        // After a double-click folder open, drop any selection the trailing click left
        // behind. Deferred below pending input so it runs after that stray mouse event.
        if (_clearSelectionAfterNav)
        {
            _clearSelectionAfterNav = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => FileList.UnselectAll());
        }
    }

    // When launched via "Show in folder" / "Reveal in File Explorer" the shell hands
    // us a file to highlight (App.SelectPath). Once its folder is loaded, select and
    // scroll to that row, then clear the request so it only fires once.
    private void TrySelectPendingItem()
    {
        if (App.SelectPath is not { } target || _tab is null) return;
        if (!string.Equals(_tab.CurrentPath, Path.GetDirectoryName(target),
                StringComparison.OrdinalIgnoreCase)) return;

        var match = FileList.Items.OfType<FileItem>().FirstOrDefault(
            i => string.Equals(i.FullPath, target, StringComparison.OrdinalIgnoreCase));
        if (match is null) return;

        App.SelectPath = null;   // one-shot

        // Defer until the rows are realized so ScrollIntoView/focus land on a container.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            FileList.SelectedItem = match;
            FileList.ScrollIntoView(match);
            (FileList.ItemContainerGenerator.ContainerFromItem(match) as ListViewItem)?.Focus();
        });
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
        {
            // In-place folder navigation: the second click's trailing mouse event can
            // land on the freshly-loaded list and select whatever row is now under the
            // cursor. Clear that stray selection once the new folder finishes loading.
            if (item.IsDirectory) _clearSelectionAfterNav = true;
            _vm?.SelectedTab?.Open(item);
        }
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
        if (e.ChangedButton == MouseButton.Right
            && TabUnder(e.OriginalSource) is { } contextTab)
        {
            OpenTabContextMenu(contextTab, e.OriginalSource);
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && !IsWithin<Button>(e.OriginalSource))
        {
            if (_vm?.SelectedTab is { } current
                && ItemFromPoint<TabViewModel>(e) is { } clicked
                && !ReferenceEquals(current, clicked))
                CaptureTabPreview(current);
            _tabDragCandidate = ItemFromPoint<TabViewModel>(e);
            _tabDragCandidateContainer = TabContainer(e.OriginalSource);
            _tabDragStart = e.GetPosition(sender as ListBox ?? TabBar);
        }

        if (e.ChangedButton != MouseButton.Middle) return;
        if (ItemFromPoint<TabViewModel>(e) is { } tab)
        {
            _vm?.CloseTab(tab);
            e.Handled = true;
        }
    }

    private void TabBar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;
        var sourceBar = sender as ListBox ?? TabBar;
        Point here = e.GetPosition(sourceBar);
        if (Math.Abs(here.X - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(here.Y - _tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var tab = _tabDragCandidate;
        _tabDragCandidate = null;
        _tabDragCancelled = false;
        tab.IsDragging = true;
        CaptureTabDragSlots(sourceBar);
        ShowTabDragGhost(tab);
        string? dragToken = null;
        try
        {
            if (_vm is null) return;
            dragToken = Guid.NewGuid().ToString("N");
            ActiveTabDrags[dragToken] = new TabDragPayload(_vm, tab);
            var data = new DataObject();
            data.SetData(TabDragTokenFormat, dragToken);
            var result = DragDrop.DoDragDrop(sourceBar, data, DragDropEffects.Move);
            if (result == DragDropEffects.None && !_tabDragCancelled
                && GetCursorPos(out var cursor) && CursorIsOutsideOwner(cursor))
                MainWindow.OpenDetachedTab(_vm, tab,
                    DevicePixelsToDips(new Point(cursor.X, cursor.Y)));
        }
        catch { /* a cancelled tab drag is harmless */ }
        finally
        {
            if (dragToken is not null) ActiveTabDrags.Remove(dragToken);
            HideTabDragGhost();
            ClearTabDragPreview();
            _tabDragSlots.Clear();
            tab.IsDragging = false;
            _tabDragCandidateContainer = null;
            ResetTabGroupHover();
            SetTabGroupTarget(null, null);
        }
    }

    private void TabItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TabViewModel tab
            || !ReferenceEquals(tab, _vm?.SelectedTab))
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () => CaptureTabPreview(tab));
    }

    private void CaptureTabPreview(TabViewModel? tab)
    {
        if (tab is null || !ReferenceEquals(tab, _vm?.SelectedTab)
            || TabContentHost.ActualWidth < 1 || TabContentHost.ActualHeight < 1)
            return;

        const int width = 960;
        const int height = 540;
        try
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle((Brush)FindResource("Bg"), null, new Rect(0, 0, width, height));
                var brush = new VisualBrush(TabContentHost)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Top,
                };
                dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            tab.PreviewImage = bitmap;
        }
        catch
        {
            // A layout can detach while the pointer crosses tabs; the next hover retries.
        }
    }

    private void ShowTabDragGhost(TabViewModel tab)
    {
        HideTabDragGhost();
        double width = Math.Clamp(_tabDragCandidateContainer?.ActualWidth ?? 154, 112, 220);
        var row = new DockPanel { LastChildFill = true };
        if (TryFindResource("Ic.folder") is Geometry folder)
            row.Children.Add(new System.Windows.Shapes.Path
            {
                Data = folder,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                Stroke = (Brush)FindResource("AccentBright"),
                StrokeThickness = 1.7,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        row.Children.Add(new TextBlock
        {
            Text = tab.Title,
            Foreground = (Brush)FindResource("Text"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        _tabDragPopup = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Focusable = false,
            Placement = PlacementMode.AbsolutePoint,
            Child = new Border
            {
                Width = width,
                Height = 34,
                Padding = new Thickness(11, 0, 11, 0),
                Background = (Brush)FindResource("Glass4"),
                BorderBrush = (Brush)FindResource("AccentLine"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8, 8, 3, 3),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    Opacity = 0.55,
                    BlurRadius = 18,
                    ShadowDepth = 4,
                },
                Child = row,
            },
        };
        _tabDragPopup.Opened += TabDragPopup_Opened;
        if (GetCursorPos(out var cursor))
        {
            Point screen = DevicePixelsToDips(new Point(cursor.X, cursor.Y));
            _tabDragPopup.HorizontalOffset = screen.X - 24;
            _tabDragPopup.VerticalOffset = screen.Y - 18;
        }
        _tabDragPopup.IsOpen = true;
    }

    private void TabDragPopup_Opened(object? sender, EventArgs e)
    {
        if (_tabDragPopup?.Child is not Visual child) return;
        _tabDragPopupSource = PresentationSource.FromVisual(child) as HwndSource;
        _tabDragPopupSource?.AddHook(TabDragPopupWndProc);
    }

    private static IntPtr TabDragPopupWndProc(
        IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmNcHitTest = 0x0084;
        const int HtTransparent = -1;
        if (message != WmNcHitTest) return IntPtr.Zero;
        handled = true;
        return new IntPtr(HtTransparent);
    }

    private void HideTabDragGhost()
    {
        if (_tabDragPopup is null) return;
        _tabDragPopupSource?.RemoveHook(TabDragPopupWndProc);
        _tabDragPopupSource = null;
        _tabDragPopup.Opened -= TabDragPopup_Opened;
        _tabDragPopup.IsOpen = false;
        _tabDragPopup.Child = null;
        _tabDragPopup = null;
    }

    private void TabBar_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (_tabDragPopup is null || !GetCursorPos(out var p)) return;
        Point screen = DevicePixelsToDips(new Point(p.X, p.Y));
        _tabDragPopup.HorizontalOffset = screen.X - 24;
        _tabDragPopup.VerticalOffset = screen.Y - 18;
        e.UseDefaultCursors = false;
        Mouse.SetCursor(Cursors.Hand);
        e.Handled = true;
    }

    private void TabBar_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed) _tabDragCancelled = true;
    }

    private void TabClose_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var bar = IsWithin(sender as DependencyObject, GroupTabBar) ? GroupTabBar : TabBar;
        FindVisualChild<AdaptiveTabPanel>(bar)?.LockCurrentWidths();
    }

    private void TabClose_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var bar = IsWithin(sender as DependencyObject, GroupTabBar) ? GroupTabBar : TabBar;
        // Let the Button click remove the tab, then release the temporary width lock
        // almost immediately. This keeps a second close click stable without making
        // an overflowing strip wait for MouseLeave before it expands again.
        Dispatcher.BeginInvoke(() => FindVisualChild<AdaptiveTabPanel>(bar)?.UnlockWidths(),
            DispatcherPriority.Input);
    }

    private void TabBar_MouseLeave(object sender, MouseEventArgs e) =>
        FindVisualChild<AdaptiveTabPanel>(sender as DependencyObject)?.UnlockWidths();

    private static T? FindVisualChild<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private Point DevicePixelsToDips(Point point)
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformFromDevice.Transform(point) ?? point;
    }

    private bool CursorIsOutsideOwner(POINT cursor)
    {
        var owner = _ownerWindow ?? Window.GetWindow(this);
        if (owner is null) return false;
        try
        {
            Point topLeft = owner.PointToScreen(new Point(0, 0));
            Point bottomRight = owner.PointToScreen(new Point(owner.ActualWidth, owner.ActualHeight));
            return cursor.X < topLeft.X || cursor.X > bottomRight.X
                || cursor.Y < topLeft.Y || cursor.Y > bottomRight.Y;
        }
        catch
        {
            return false;
        }
    }

    // Use an explicit menu open here rather than a Style setter. WPF can otherwise route
    // the ListBox's own header menu before a templated tab item's menu gets a chance.
    private void TabBar_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_vm is null || TabUnder(e.OriginalSource) is not { } tab) return;
        OpenTabContextMenu(tab, e.OriginalSource);
        e.Handled = true;
    }

    private void OpenTabContextMenu(TabViewModel tab, object? source)
    {
        if (_openTabContextMenu is not null)
            _openTabContextMenu.IsOpen = false;

        var menu = (ContextMenu)FindResource("TabContextMenu");
        menu.DataContext = tab;
        menu.PlacementTarget = (UIElement?)TabContainer(source) ?? TabBar;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 2;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openTabContextMenu, menu))
                _openTabContextMenu = null;
        };
        _openTabContextMenu = menu;
        menu.IsOpen = true;
    }

    // ---- Tab context menus -------------------------------------------------
    private void TabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not TabViewModel tab || _vm is null) return;

        var pin = menu.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, "pin"));
        if (pin is not null) pin.Header = tab.IsPinned ? "Unpin tab" : "Pin tab";

        int index = _vm.Tabs.IndexOf(tab);
        var closeOther = menu.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, "close-other"));
        if (closeOther is not null) closeOther.IsEnabled = _vm.Tabs.Any(t => t != tab && !t.IsPinned);
        var closeRight = menu.Items.OfType<MenuItem>().FirstOrDefault(i => Equals(i.Tag, "close-right"));
        if (closeRight is not null)
            closeRight.IsEnabled = index >= 0 && _vm.Tabs.Skip(index + 1).Any(t => !t.IsPinned);

        foreach (var element in menu.Items.OfType<FrameworkElement>()
                     .Where(item => item.Tag is string tag && tag.StartsWith("group-", StringComparison.Ordinal)))
            element.Visibility = tab.IsGrouped ? Visibility.Visible : Visibility.Collapsed;
    }

    private static TabViewModel? TabFromMenu(object sender) =>
        (sender as FrameworkElement)?.DataContext as TabViewModel;

    private void TabDuplicate_Click(object sender, RoutedEventArgs e) => _vm?.DuplicateTab(TabFromMenu(sender));

    private void TabReload_Click(object sender, RoutedEventArgs e)
    {
        if (TabFromMenu(sender) is { } tab) _ = tab.ReloadAsync();
    }

    private void TabPin_Click(object sender, RoutedEventArgs e) => _vm?.TogglePin(TabFromMenu(sender));
    private void TabGroupNew_Click(object sender, RoutedEventArgs e) => _vm?.NewTabInGroup(TabFromMenu(sender));

    private void TabGroupRename_Click(object sender, RoutedEventArgs e)
    {
        var tab = TabFromMenu(sender);
        if (_vm is null || tab is null || !tab.IsGrouped) return;
        var dialog = new InputDialog("Rename tab group", "Group name:", tab.GroupName)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true) _vm.RenameGroup(tab, dialog.Value);
    }

    private void TabGroupRemove_Click(object sender, RoutedEventArgs e) =>
        _vm?.RemoveFromGroup(TabFromMenu(sender));

    private void TabGroupClear_Click(object sender, RoutedEventArgs e) =>
        _vm?.ClearGroup(TabFromMenu(sender));

    private void TabGroupClose_Click(object sender, RoutedEventArgs e) =>
        _vm?.CloseGroup(TabFromMenu(sender));

    private void TabClose_Click(object sender, RoutedEventArgs e) => _vm?.CloseTab(TabFromMenu(sender));
    private void TabCloseOther_Click(object sender, RoutedEventArgs e) => _vm?.CloseOtherTabs(TabFromMenu(sender));
    private void TabCloseRight_Click(object sender, RoutedEventArgs e) => _vm?.CloseTabsToRight(TabFromMenu(sender));

    private void TabStripNewTab_Click(object sender, RoutedEventArgs e) => _vm?.NewTab(activate: true);
    private void TabStripNewGroupTab_Click(object sender, RoutedEventArgs e) => _vm?.NewTabInActiveGroup();
    private void NewActiveGroupTab_Click(object sender, RoutedEventArgs e) => _vm?.NewTabInActiveGroup();
    private void ReopenClosedTab_Click(object sender, RoutedEventArgs e) => _vm?.ReopenClosedTab();

    // ---- Tab projects ------------------------------------------------------
    private void ProjectsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (ProjectsFlyout.Visibility == Visibility.Visible)
        {
            SetProjectsFlyoutOpen(false);
            return;
        }
        CloseToolbarPopups();
        BuildProjectsMenu();
        SetProjectsFlyoutOpen(true);
    }

    private void SetProjectsFlyoutOpen(bool open)
    {
        int version = ++_projectsAnimationVersion;
        ProjectsFlyout.BeginAnimation(OpacityProperty, null);
        ProjectsFlyoutTransform.BeginAnimation(TranslateTransform.YProperty, null);

        if (open)
        {
            ProjectsFlyout.Visibility = Visibility.Visible;
            ProjectsFlyout.Opacity = 0;
            ProjectsFlyoutTransform.Y = -6;
            ProjectsFlyout.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            ProjectsFlyoutTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0,
                TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            return;
        }

        if (ProjectsFlyout.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(ProjectsFlyout.Opacity, 0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) =>
        {
            if (version != _projectsAnimationVersion) return;
            ProjectsFlyout.Visibility = Visibility.Collapsed;
            ProjectsFlyout.Opacity = 0;
            ProjectsFlyoutTransform.Y = -6;
        };
        ProjectsFlyout.BeginAnimation(OpacityProperty, fade);
        ProjectsFlyoutTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(ProjectsFlyoutTransform.Y, -6, TimeSpan.FromMilliseconds(110)));
    }

    private void BuildProjectsMenu()
    {
        ProjectsMenuItems.Children.Clear();
        var settings = SettingsStore.Instance.Settings;

        ProjectsMenuItems.Children.Add(new MenuItem { Header = "TAB PROJECTS", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        ProjectsMenuItems.Children.Add(new MenuItem { Header = "Save current tabs as new project…", Command = ProjectCommand(SaveCurrentTabsAsProject) });
        ProjectsMenuItems.Children.Add(new MenuItem { Header = "New empty project…", Command = ProjectCommand(CreateEmptyProject) });

        if (settings.TabProjects.Count > 0)
        {
            ProjectsMenuItems.Children.Add(new Separator());
            foreach (var project in settings.TabProjects)
            {
                var item = new MenuItem
                {
                    Header = $"{project.Name}  ·  {project.Tabs.Count} tab{(project.Tabs.Count == 1 ? "" : "s")}",
                    IsCheckable = true,
                    IsChecked = project.Id == _vm?.ActiveProjectId,
                    Tag = project.Id,
                };
                item.Click += (_, _) =>
                {
                    SetProjectsFlyoutOpen(false);
                    SwitchProject(project.Id);
                };
                ProjectsMenuItems.Children.Add(item);
            }

            var manage = new MenuItem { Header = "Manage projects…" };
            manage.Click += (_, _) => BuildProjectManager();
            ProjectsMenuItems.Children.Add(manage);
        }
        RefreshProjectsButton();
    }

    private void BuildProjectManager()
    {
        ProjectsMenuItems.Children.Clear();
        ProjectsMenuItems.Children.Add(new MenuItem
        {
            Header = "MANAGE TAB PROJECTS",
            IsEnabled = false,
            FontWeight = FontWeights.SemiBold
        });
        var back = new MenuItem { Header = "← Back to projects" };
        back.Click += (_, _) => BuildProjectsMenu();
        ProjectsMenuItems.Children.Add(back);
        ProjectsMenuItems.Children.Add(new Separator());

        foreach (var project in SettingsStore.Instance.Settings.TabProjects.ToList())
        {
            var row = new Grid { Margin = new Thickness(7, 5, 7, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = project.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 180,
                Margin = new Thickness(3, 0, 10, 0)
            };
            row.Children.Add(name);

            var rename = ProjectManagerButton("Rename");
            rename.Click += (_, _) =>
            {
                SetProjectsFlyoutOpen(false);
                RenameProject(project.Id);
            };
            Grid.SetColumn(rename, 1);
            row.Children.Add(rename);

            var delete = ProjectManagerButton("Delete");
            delete.Foreground = (Brush)FindResource("Danger");
            delete.Click += (_, _) =>
            {
                SetProjectsFlyoutOpen(false);
                DeleteProject(project.Id);
            };
            Grid.SetColumn(delete, 2);
            row.Children.Add(delete);

            ProjectsMenuItems.Children.Add(row);
        }
    }

    private Button ProjectManagerButton(string text) => new()
    {
        Content = text,
        Style = (Style)FindResource("GhostButton"),
        MinWidth = 0,
        Padding = new Thickness(8, 4, 8, 4),
        Margin = new Thickness(3, 0, 0, 0),
        FontSize = 11,
    };

    private ICommand ProjectCommand(Action action) => new RelayCommand(_ =>
    {
        SetProjectsFlyoutOpen(false);
        action();
    });

    private ICommand ProjectCommand(Func<bool> action) => new RelayCommand(_ =>
    {
        SetProjectsFlyoutOpen(false);
        action();
    });

    private void RefreshProjectsButton()
    {
        if (ProjectsButtonText is null) return;
        string? id = _vm?.ActiveProjectId;
        string? name = SettingsStore.Instance.Settings.TabProjects
            .FirstOrDefault(project => project.Id == id)?.Name;
        ProjectsButtonText.Text = name is null ? "Tab projects" : $"Project · {name}";
    }

    private bool SaveCurrentTabsAsProject()
    {
        if (_vm is null) return false;
        var dialog = new InputDialog("Save tab project", "Project name:", "Current tabs") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return false;

        var snapshot = _vm.CaptureProject();
        var project = new TabProject
        {
            Name = UniqueProjectName(dialog.Value),
            Tabs = snapshot.Tabs,
            SelectedIndex = snapshot.SelectedIndex,
        };
        SettingsStore.Instance.Settings.TabProjects.Add(project);
        SettingsStore.Instance.Settings.NotifyTabProjectsChanged();
        _vm.ActiveProjectId = project.Id;
        return true;
    }

    private void CreateEmptyProject()
    {
        if (_vm is null) return;
        if (_vm.ActiveProjectId is null && _vm.Tabs.Count > 0 && !SaveCurrentTabsAsProject()) return;
        if (_vm.ActiveProjectId is not null) UpdateCurrentProject();

        var dialog = new InputDialog("New tab project", "Project name:", "New project") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        var project = new TabProject { Name = UniqueProjectName(dialog.Value) };
        SettingsStore.Instance.Settings.TabProjects.Add(project);
        SettingsStore.Instance.Settings.NotifyTabProjectsChanged();
        _vm.LoadProject(project.Tabs, project.SelectedIndex);
        _vm.ActiveProjectId = project.Id;
    }

    private void SwitchProject(string id)
    {
        if (_vm is null || _vm.ActiveProjectId == id) return;
        if (_vm.ActiveProjectId is null && _vm.Tabs.Count > 0 && !SaveCurrentTabsAsProject()) return;
        if (_vm.ActiveProjectId is not null) UpdateCurrentProject();

        var project = SettingsStore.Instance.Settings.TabProjects.FirstOrDefault(p => p.Id == id);
        if (project is null) return;
        _vm.LoadProject(project.Tabs, project.SelectedIndex);
        _vm.ActiveProjectId = project.Id;
    }

    private void UpdateCurrentProject()
    {
        if (_vm?.ActiveProjectId is { Length: > 0 } id) UpdateProject(id);
    }

    private void UpdateProject(string id)
    {
        if (_vm is null) return;
        var project = SettingsStore.Instance.Settings.TabProjects.FirstOrDefault(p => p.Id == id);
        if (project is null) return;
        var snapshot = _vm.CaptureProject();
        project.Tabs = snapshot.Tabs;
        project.SelectedIndex = snapshot.SelectedIndex;
        SettingsStore.Instance.Settings.NotifyTabProjectsChanged();
    }

    private void RenameProject(string id)
    {
        var project = SettingsStore.Instance.Settings.TabProjects.FirstOrDefault(p => p.Id == id);
        if (project is null) return;
        var dialog = new InputDialog("Rename tab project", "Project name:", project.Name) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        project.Name = UniqueProjectName(dialog.Value, project.Id);
        SettingsStore.Instance.Settings.NotifyTabProjectsChanged();
    }

    private void DeleteProject(string id)
    {
        var settings = SettingsStore.Instance.Settings;
        var project = settings.TabProjects.FirstOrDefault(p => p.Id == id);
        if (project is null) return;
        if (!ConfirmDialog.Ask(Window.GetWindow(this), "Delete tab project",
            $"Delete the saved project “{project.Name}”? Its tabs will stay open if it is active.",
            "Delete", "Cancel", danger: true))
            return;
        settings.TabProjects.Remove(project);
        settings.NotifyTabProjectsChanged();
        if (_vm?.ActiveProjectId == id) _vm.ActiveProjectId = null;
    }

    private static string UniqueProjectName(string raw, string? currentId = null)
    {
        string baseName = string.IsNullOrWhiteSpace(raw) ? "New project" : raw.Trim();
        string name = baseName;
        int suffix = 2;
        var projects = SettingsStore.Instance.Settings.TabProjects;
        while (projects.Any(p => p.Id != currentId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} ({suffix++})";
        return name;
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
        if (IsWithin<TextBox>(e.OriginalSource) || IsWithin<CheckBox>(e.OriginalSource)) return;

        // The ListView's ScrollBar lives inside the ListView template, so its mouse
        // events also route through this handler. Never arm file dragging or the
        // empty-space selection marquee for scrollbar chrome; otherwise the marquee
        // captures the mouse after a few pixels and steals an in-progress thumb drag.
        _maybeDrag = false;
        _maybeMarquee = false;
        _pendingSingleSelect = null;
        if (IsWithin<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource)) return;

        _dragStart = e.GetPosition(null);
        var hit = ItemFromPoint<FileItem>(e);
        _maybeDrag = hit is not null;
        // Pressing empty space (not a row, not a column header) arms a rubber-band marquee.
        _marqueeStart = e.GetPosition(FileList);
        _maybeMarquee = hit is null && _tab is { IsFolderView: true }
                        && !IsWithin<GridViewColumnHeader>(e.OriginalSource);

        // WPF keeps an extended selection when the click falls on the list's blank area.
        // In a file manager that makes the previous selection feel stuck, so a plain blank
        // click always clears it before a possible rubber-band gesture starts.
        if (_maybeMarquee && Keyboard.Modifiers == ModifierKeys.None)
            FileList.UnselectAll();

        // Pressing a row that's already part of a multi-selection (no Ctrl/Shift) must NOT
        // collapse the selection — otherwise a drag would only carry this one row. Suppress
        // the default and remember to collapse on mouse-up if the press turns out to be a click.
        if (hit is not null && FileList.SelectedItems.Count > 1
            && FileList.SelectedItems.Contains(hit)
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            _pendingSingleSelect = hit;
            e.Handled = true;
        }
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
        _pendingSingleSelect = null;   // a drag happened — don't collapse the selection on mouse-up
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
            var result = DragDrop.DoDragDrop(FileList, data,
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            // If the drop target moved the files out, our folder is now stale.
            if (result == DragDropEffects.Move) _ = _tab?.ReloadAfterOperationAsync();
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
        _ = _tab.ReloadAfterOperationAsync();
    }

    // ===================== Drop onto a tab header =====================
    // Drops the dragged files into that tab's folder (move/copy by the same rules).
    private TabViewModel? _tabDropTarget;

    private static bool TryResolveTabDrag(
        IDataObject data, out PaneViewModel? sourcePane, out TabViewModel? tab)
    {
        sourcePane = null;
        tab = null;
        if (!data.GetDataPresent(TabDragTokenFormat)
            || data.GetData(TabDragTokenFormat) is not string token
            || !ActiveTabDrags.TryGetValue(token, out var payload))
            return false;

        sourcePane = payload.SourcePane;
        tab = payload.Tab;
        return true;
    }

    private void TabBar_DragOver(object sender, DragEventArgs e)
    {
        if (TryResolveTabDrag(e.Data, out var sourcePane, out var dragged))
        {
            var dragBar = sender as ListBox ?? TabBar;
            if (ReferenceEquals(dragBar, TabBar)) EnsureTopLevelTabDragSlots();
            Point pointer = e.GetPosition(dragBar);
            var stableSlot = ReferenceEquals(sourcePane, _vm) && ReferenceEquals(dragBar, TabBar)
                ? TabDragSlotAt(pointer.X)
                : null;
            var target = stableSlot?.Tab ?? TabUnder(e.OriginalSource);
            bool valid = dragged is not null && sourcePane is not null
                && (target is null || dragged.IsPinned == target.IsPinned);
            e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
            SetTabDropTarget(null);
            bool waitingToGroup = false;
            bool alreadyInSameGroup = dragged?.GroupId is { Length: > 0 } draggedGroupId
                && draggedGroupId == target?.GroupId;
            if (valid && dragged is not null && target is not null && !dragged.IsPinned
                && !ReferenceEquals(dragged, target) && !alreadyInSameGroup)
            {
                double? ratio = stableSlot is not null
                    ? (pointer.X - stableSlot.X) / stableSlot.Width
                    : TabContainer(e.OriginalSource) is { ActualWidth: > 0 } targetBox
                        ? e.GetPosition(targetBox).X / targetBox.ActualWidth
                        : null;
                if (ratio is >= TabGroupHoverStartRatio and <= TabGroupHoverEndRatio)
                {
                    waitingToGroup = true;
                    BeginTabGroupHover(dragged, target);
                }
                else
                {
                    ResetTabGroupHover();
                    SetTabGroupTarget(null, null);
                }
            }
            else
            {
                ResetTabGroupHover();
                SetTabGroupTarget(null, null);
            }

            // Preview ordinary same-pane reordering as a ghost. The live collection stays
            // untouched until drop, so the dragged tab can always return to its origin.
            if (valid && !waitingToGroup && ReferenceEquals(sourcePane, _vm)
                && dragged is not null && target is not null && stableSlot is not null
                && dragged.IsGrouped)
            {
                PreviewGroupedTabMove(dragged, stableSlot,
                    pointer.X - stableSlot.X >= stableSlot.Width / 2);
            }
            else if (valid && !waitingToGroup && ReferenceEquals(sourcePane, _vm)
                && dragged is not null && target is not null
                && !ReferenceEquals(dragged, target) && !target.IsGrouped && stableSlot is not null)
            {
                var display = _tabDragSlots.Select(slot => slot.Tab).ToList();
                int targetIndex = display.IndexOf(target);
                if (pointer.X - stableSlot.X >= stableSlot.Width / 2) targetIndex++;
                int sourceIndex = display.IndexOf(dragged);
                if (sourceIndex >= 0 && sourceIndex < targetIndex) targetIndex--;
                PreviewTabMove(dragged, targetIndex);
            }
            else
            {
                ClearTabDragPreview();
            }
            e.Handled = true;
            return;
        }

        var tab = TabUnder(e.OriginalSource);
        string? dest = tab is { IsFolderView: true } ? tab.CurrentPath : null;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        var eff = FileDropService.EffectFor(files, dest, e.KeyStates);
        e.Effects = eff;
        SetTabDropTarget(eff == DragDropEffects.None ? null : tab);
        e.Handled = true;
    }

    private void TabBar_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement bar)
        {
            Point pointer = e.GetPosition(bar);
            if (pointer.X >= 0 && pointer.X <= bar.ActualWidth
                && pointer.Y >= 0 && pointer.Y <= bar.ActualHeight)
                return;
        }
        SetTabDropTarget(null);
        if (e.Data.GetDataPresent(TabDragTokenFormat))
        {
            ClearTabDragPreview();
            ResetTabGroupHover();
            SetTabGroupTarget(null, null);
        }
    }

    private void TabBar_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (TryResolveTabDrag(e.Data, out var sourcePane, out var dragged))
        {
            var dropBar = sender as ListBox ?? TabBar;
            Point pointer = e.GetPosition(dropBar);
            var stableSlot = ReferenceEquals(sourcePane, _vm) && ReferenceEquals(dropBar, TabBar)
                ? TabDragSlotAt(pointer.X)
                : null;
            var target = stableSlot?.Tab ?? TabUnder(e.OriginalSource);
            int previewIndex = ReferenceEquals(_tabPreviewSource, dragged) ? _tabPreviewIndex : -1;
            var previewTarget = ReferenceEquals(_tabPreviewSource, dragged) ? _tabPreviewTarget : null;
            bool previewAfter = _tabPreviewAfter;
            double? stableRatio = stableSlot is null ? null : (pointer.X - stableSlot.X) / stableSlot.Width;
            ClearTabDragPreview();
            if (_vm is not null && sourcePane is not null && dragged is not null
                && (target is null || dragged.IsPinned == target.IsPinned))
            {
                bool groupDrop = ReferenceEquals(_tabGroupSource, dragged)
                    && ReferenceEquals(_tabGroupTarget, target);
                if (!ReferenceEquals(sourcePane, _vm))
                {
                    int targetIndex = target is null ? _vm.Tabs.Count : _vm.Tabs.IndexOf(target);
                    if (!groupDrop && target is not null
                        && TabContainer(e.OriginalSource) is { ActualWidth: > 0 } foreignBox
                        && e.GetPosition(foreignBox).X >= foreignBox.ActualWidth / 2)
                        targetIndex++;
                    if (_vm.TransferTabFrom(sourcePane, dragged, targetIndex,
                            activate: true, preserveGroup: false)
                        && groupDrop)
                        _vm.GroupTabs(dragged, target);
                }
                else
                {
                    if (groupDrop)
                    {
                        _vm.GroupTabs(dragged, target);
                    }
                    else if (dragged.IsGrouped)
                    {
                        var moveTarget = previewTarget ?? target;
                        bool dropsAfter = previewTarget is not null
                            ? previewAfter
                            : stableRatio is double ratio
                                ? ratio >= 0.5
                                : target is not null
                                  && TabContainer(e.OriginalSource) is { ActualWidth: > 0 } groupedBox
                                  && e.GetPosition(groupedBox).X >= groupedBox.ActualWidth / 2;
                        _vm.MoveTabOutOfGroup(dragged, moveTarget, dropsAfter);
                    }
                    else
                    {
                        int targetIndex = previewIndex;
                        if (targetIndex < 0)
                        {
                            targetIndex = target is null ? _vm.Tabs.Count - 1 : _vm.Tabs.IndexOf(target);
                            bool dropsAfter = stableRatio is double ratio
                                ? ratio >= 0.5
                                : target is not null
                                  && TabContainer(e.OriginalSource) is { ActualWidth: > 0 } box
                                  && e.GetPosition(box).X >= box.ActualWidth / 2;
                            if (target is not null && dropsAfter) targetIndex++;
                            int sourceIndex = _vm.Tabs.IndexOf(dragged);
                            if (sourceIndex >= 0 && sourceIndex < targetIndex) targetIndex--;
                        }
                        MoveTabWithAnimation(dragged, targetIndex);
                    }
                }
                e.Effects = DragDropEffects.Move;
            }
            SetTabGroupTarget(null, null);
            ResetTabGroupHover();
            SetTabDropTarget(null);
            return;
        }

        var tab = TabUnder(e.OriginalSource);
        SetTabDropTarget(null);
        if (tab is null || !tab.IsFolderView) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        bool move = FileDropService.EffectFor(files, tab.CurrentPath, e.KeyStates) == DragDropEffects.Move;
        string? err = FileDropService.Perform(files, tab.CurrentPath, move);
        if (err is not null) SetStatus($"⚠️ {err}");
        _ = tab.ReloadAfterOperationAsync();
        if (_tab is not null && !ReferenceEquals(_tab, tab))
            _ = _tab.ReloadAfterOperationAsync();   // source tab is now stale
    }

    private void SetTabGroupTarget(TabViewModel? source, TabViewModel? target)
    {
        if (_tabGroupSource is not null) _tabGroupSource.IsGroupDropTarget = false;
        if (_tabGroupTarget is not null) _tabGroupTarget.IsGroupDropTarget = false;
        _tabGroupSource = source;
        _tabGroupTarget = target;
        if (_tabGroupSource is not null) _tabGroupSource.IsGroupDropTarget = true;
        if (_tabGroupTarget is not null) _tabGroupTarget.IsGroupDropTarget = true;
    }

    private void ResetTabGroupHover()
    {
        _tabGroupHoverTimer?.Stop();
        _tabGroupHoverCandidate = null;
        _tabGroupHoverSource = null;
    }

    private void BeginTabGroupHover(TabViewModel source, TabViewModel target)
    {
        if (ReferenceEquals(_tabGroupHoverSource, source)
            && ReferenceEquals(_tabGroupHoverCandidate, target))
            return;

        ResetTabGroupHover();
        SetTabGroupTarget(null, null);
        _tabGroupHoverSource = source;
        _tabGroupHoverCandidate = target;
        _tabGroupHoverTimer ??= CreateTabGroupHoverTimer();
        _tabGroupHoverTimer.Interval = TabGroupHoverDelay;
        _tabGroupHoverTimer.Start();
    }

    private DispatcherTimer CreateTabGroupHoverTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) => CompleteTabGroupHover();
        return timer;
    }

    private void CompleteTabGroupHover()
    {
        _tabGroupHoverTimer?.Stop();
        var source = _tabGroupHoverSource;
        var target = _tabGroupHoverCandidate;
        var slot = target is null
            ? null
            : _tabDragSlots.FirstOrDefault(candidate => ReferenceEquals(candidate.Tab, target));
        if (source is null || target is null || slot is null || !GetCursorPos(out var cursor))
        {
            ResetTabGroupHover();
            SetTabGroupTarget(null, null);
            return;
        }

        try
        {
            Point pointer = TabBar.PointFromScreen(new Point(cursor.X, cursor.Y));
            double ratio = (pointer.X - slot.X) / slot.Width;
            bool stillCentered = pointer.Y >= 0 && pointer.Y <= TabBar.ActualHeight
                && ratio is >= TabGroupHoverStartRatio and <= TabGroupHoverEndRatio;
            if (stillCentered)
            {
                SetTabGroupTarget(source, target);
                return;
            }
        }
        catch
        {
            // The tab strip can detach while a drag is being cancelled.
        }

        ResetTabGroupHover();
        SetTabGroupTarget(null, null);
    }

    private void PreviewTabMove(TabViewModel dragged, int targetIndex)
    {
        if (_vm is null || _tabDragSlots.Count == 0) return;
        var display = _tabDragSlots.Select(slot => slot.Tab).ToList();
        int sourceIndex = display.IndexOf(dragged);
        if (sourceIndex < 0) return;
        targetIndex = Math.Clamp(targetIndex, 0, display.Count - 1);
        if (sourceIndex == targetIndex)
        {
            ClearTabDragPreview();
            return;
        }
        if (ReferenceEquals(_tabPreviewSource, dragged) && _tabPreviewIndex == targetIndex) return;

        var reordered = display.ToList();
        reordered.RemoveAt(sourceIndex);
        reordered.Insert(targetIndex, dragged);
        var slotsByTab = _tabDragSlots.ToDictionary(slot => slot.Tab);
        for (int index = 0; index < reordered.Count; index++)
        {
            var candidate = reordered[index];
            var original = slotsByTab[candidate];
            double destination = _tabDragSlots[index].X;
            original.Container.RenderTransform = new TranslateTransform(destination - original.X, 0);
        }

        _tabPreviewSource = dragged;
        _tabPreviewTarget = null;
        _tabPreviewAfter = false;
        _tabPreviewIndex = targetIndex;
        EnsureTabInsertionAdorner();
        if (_tabInsertionAdorner is not null)
            _tabInsertionAdorner.X = _tabDragSlots[targetIndex].X;
    }

    private void PreviewGroupedTabMove(TabViewModel dragged, TabDragSlot targetSlot, bool after)
    {
        if (ReferenceEquals(_tabPreviewSource, dragged)
            && ReferenceEquals(_tabPreviewTarget, targetSlot.Tab)
            && _tabPreviewAfter == after)
            return;

        ClearTabDragPreview();
        _tabPreviewSource = dragged;
        _tabPreviewTarget = targetSlot.Tab;
        _tabPreviewAfter = after;
        EnsureTabInsertionAdorner();
        if (_tabInsertionAdorner is not null)
            _tabInsertionAdorner.X = targetSlot.X + (after ? targetSlot.Width : 0);
    }

    private void CaptureTabDragSlots(ListBox sourceBar)
    {
        _tabDragSlots.Clear();
        if (!ReferenceEquals(sourceBar, TabBar) || _vm is null) return;
        foreach (var tab in _vm.TopLevelTabs)
        {
            if (TabBar.ItemContainerGenerator.ContainerFromItem(tab) is not ListBoxItem container)
            {
                _tabDragSlots.Clear();
                return;
            }
            _tabDragSlots.Add(new TabDragSlot(tab, container,
                container.TranslatePoint(new Point(), TabBar).X,
                Math.Max(1, container.ActualWidth)));
        }
    }

    private void EnsureTopLevelTabDragSlots()
    {
        if (_vm is null) return;
        var topLevelTabs = _vm.TopLevelTabs;
        if (_tabDragSlots.Count == topLevelTabs.Count
            && _tabDragSlots.Select(slot => slot.Tab).SequenceEqual(topLevelTabs))
            return;

        ClearTabDragPreview();
        _tabDragSlots.Clear();
        foreach (var tab in topLevelTabs)
        {
            if (TabBar.ItemContainerGenerator.ContainerFromItem(tab) is not ListBoxItem container)
            {
                _tabDragSlots.Clear();
                return;
            }
            _tabDragSlots.Add(new TabDragSlot(tab, container,
                container.TranslatePoint(new Point(), TabBar).X,
                Math.Max(1, container.ActualWidth)));
        }
    }

    private TabDragSlot? TabDragSlotAt(double x)
    {
        if (_tabDragSlots.Count == 0) return null;
        foreach (var slot in _tabDragSlots)
            if (x >= slot.X && x < slot.X + slot.Width) return slot;
        if (x < _tabDragSlots[0].X) return _tabDragSlots[0];
        return _tabDragSlots[^1];
    }

    private void EnsureTabInsertionAdorner()
    {
        if (_tabInsertionAdorner is not null) return;
        var layer = AdornerLayer.GetAdornerLayer(TabBar);
        if (layer is null) return;
        _tabInsertionAdorner = new TabInsertionAdorner(TabBar);
        layer.Add(_tabInsertionAdorner);
    }

    private void ClearTabDragPreview()
    {
        if (_tabPreviewSource is null && _tabInsertionAdorner is null) return;
        foreach (var slot in _tabDragSlots) slot.Container.RenderTransform = null;
        if (_tabInsertionAdorner is not null)
        {
            AdornerLayer.GetAdornerLayer(TabBar)?.Remove(_tabInsertionAdorner);
            _tabInsertionAdorner = null;
        }
        _tabPreviewSource = null;
        _tabPreviewTarget = null;
        _tabPreviewAfter = false;
        _tabPreviewIndex = -1;
    }

    private void MoveTabWithAnimation(TabViewModel tab, int targetIndex)
    {
        if (_vm is null) return;
        var before = _vm.Tabs
            .Select(candidate => (candidate,
                container: TabBar.ItemContainerGenerator.ContainerFromItem(candidate) as ListBoxItem))
            .Where(pair => pair.container is not null)
            .ToDictionary(pair => pair.candidate,
                pair => pair.container!.TranslatePoint(new Point(), TabBar).X);

        if (!_vm.MoveTab(tab, targetIndex)) return;
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var candidate in _vm.Tabs)
            {
                if (!before.TryGetValue(candidate, out double oldX)
                    || TabBar.ItemContainerGenerator.ContainerFromItem(candidate) is not ListBoxItem container)
                    continue;
                double newX = container.TranslatePoint(new Point(), TabBar).X;
                double delta = oldX - newX;
                if (Math.Abs(delta) < 0.5) continue;
                var transform = new TranslateTransform(delta, 0);
                container.RenderTransform = transform;
                var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(145))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                transform.BeginAnimation(TranslateTransform.XProperty, animation);
            }
        }, DispatcherPriority.Render);
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
        return TabContainer(source)?.DataContext as TabViewModel;
    }

    private static ListBoxItem? TabContainer(object? source)
    {
        DependencyObject? d = source as DependencyObject;
        while (d is not null and not ListBoxItem) d = VisualTreeHelper.GetParent(d);
        return d as ListBoxItem;
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
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2" };

    private void FileContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        _fileContextMenuOpen = true;
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

        BuildPinMenu(items.Where(i => i.IsDirectory).ToList());
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
        MenuGit.Visibility = SettingsStore.Instance.Settings.GitIntegrationEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Set up the native menu once the regular menu has painted. The submenu then opens
        // immediately in normal use, while an instant hover still falls back to its loader.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (_fileContextMenuOpen) PrepareShowMoreOptions();
        }));
    }

    private void BuildPinMenu(IReadOnlyList<FileItem> dirs)
    {
        var targets = MainViewModel.PinTargets();
        var owner = ItemsControl.ItemsControlFromItemContainer(MenuPin);
        if (owner is not null)
        {
            foreach (var old in owner.Items.OfType<MenuItem>()
                         .Where(m => m.Tag is PinMenuTarget { Dynamic: true }).ToList())
                owner.Items.Remove(old);
        }
        MenuPin.Items.Clear();
        MenuPin.Tag = null;

        if (targets.Count == 0)
        {
            MenuPin.Header = "No sidebar lists";
            MenuPin.IsEnabled = false;
            return;
        }

        bool useSubmenu = targets.Count(t => t.Key != "quick") > 2;
        if (useSubmenu)
        {
            MenuPin.Header = "Pin to sidebar";
            MenuPin.IsEnabled = dirs.Count > 0;
            foreach (var target in targets)
                MenuPin.Items.Add(CreatePinMenuItem(target, dirs, dynamic: false));
            return;
        }

        var primary = targets.FirstOrDefault(t => t.Key == "quick") ?? targets[0];
        ConfigurePinMenuItem(MenuPin, primary, dirs, dynamic: false);
        if (owner is null) return;
        int insert = owner.Items.IndexOf(MenuPin) + 1;
        foreach (var target in targets.Where(t => t.Key != primary.Key))
            owner.Items.Insert(insert++, CreatePinMenuItem(target, dirs, dynamic: true));
    }

    private MenuItem CreatePinMenuItem(MainViewModel.SidebarPinTarget target,
        IReadOnlyList<FileItem> dirs, bool dynamic)
    {
        var item = new MenuItem();
        ConfigurePinMenuItem(item, target, dirs, dynamic);
        item.Click += Pin_Click;
        return item;
    }

    private static void ConfigurePinMenuItem(MenuItem item, MainViewModel.SidebarPinTarget target,
        IReadOnlyList<FileItem> dirs, bool dynamic)
    {
        bool allPinned = dirs.Count > 0 && dirs.All(d => MainViewModel.IsPinnedTo(target.Key, d.FullPath));
        item.Header = allPinned ? $"Unpin from {target.Name}" : $"Pin to {target.Name}";
        item.IsEnabled = dirs.Count > 0;
        item.Tag = new PinMenuTarget(target.Key, target.Name, dynamic);
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
        _ = _tab.ReloadAfterOperationAsync();
    }

    // ---- Delete (Recycle Bin) ----
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private async void DeleteSelected()
    {
        if (_tab is null || _fileOperationInProgress) return;
        var tab = _tab;
        var paths = SelectedPaths();
        if (paths.Count == 0) return;

        string what = paths.Count == 1
            ? $"“{Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar))}”"
            : $"{paths.Count} items";

        // Decide recycle vs permanent based on the setting (prompt / always recycle / always permanent).
        bool permanent;
        switch (SettingsStore.Instance.Settings.DeleteBehavior)
        {
            case DeleteBehavior.Recycle: permanent = false; break;
            case DeleteBehavior.Permanent: permanent = true; break;
            default:
                var choice = DeleteDialog.Ask(Window.GetWindow(this),
                    $"What should happen to {what}?");
                if (choice == DeleteChoice.Cancel) return;
                permanent = choice == DeleteChoice.Permanent;
                break;
        }

        // Every permanent-delete path gets one Rain-styled confirmation. The underlying
        // shell operation suppresses Windows' per-file prompts so a multi-delete stays one action.
        if (permanent && !ConfirmDialog.Ask(Window.GetWindow(this), "Permanently delete",
            $"Permanently delete {what}? This can't be undone.",
            "Delete permanently", "Cancel", danger: true))
            return;

        var act = Activity.Begin(permanent ? "Permanently deleted" : "Recycled", Summarize(paths), "trash");
        tab.BeginKnownFileOperation();
        _fileOperationInProgress = true;
        OpResult res;
        try
        {
            res = await Task.Run(() => permanent ? _ops.DeletePermanent(paths) : _ops.Delete(paths));
        }
        finally
        {
            _fileOperationInProgress = false;
        }

        // Keep the current collection in sync locally. A full directory reload can
        // rebuild thousands of rows and made the window appear frozen after deletes.
        tab.ApplyLocalDelete(res.Completed);

        // The user backed out of the OS confirm dialog — nothing was deleted. Reflect that in
        // the activity center (instead of falsely logging a completed delete) and stop here.
        if (res.Canceled)
        {
            Activity.Cancel(act);
            if (!permanent) PushDeleteUndo(res.Completed);
            if (!permanent && res.Completed.Count > 0)
                SetStatus($"Delete canceled after {res.Completed.Count} item{(res.Completed.Count == 1 ? "" : "s")}; use Undo to restore them.");
            return;
        }

        Activity.Complete(act, res.Ok, res.Error);
        if (!res.Ok) SetStatus($"⚠️ {res.Error}");
        // Only a Recycle-Bin delete is undoable; a permanent delete can't be restored.
        if (!permanent) PushDeleteUndo(res.Completed);
    }

    private static void PushDeleteUndo(IReadOnlyList<string> completed)
    {
        if (completed.Count == 0) return;
        UndoService.Instance.Push(new RestoreFromBinAction(
            completed, completed.Count == 1 ? "Delete" : $"Delete ({completed.Count} items)"));
    }

    // ---- Keyboard shortcuts, scoped to the file list (so typing in text boxes is unaffected) ----
    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        // Editing keys belong to the rename box. In particular, Space must type a
        // space instead of bubbling here and toggling the preview pane.
        if (e.OriginalSource is TextBox || _tab?.Items.Any(item => item.IsEditing) == true) return;

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

    private async void BeginRename()
    {
        if (_tab is null || _fileOperationInProgress || FileList.SelectedItems.Count != 1) return;
        var tab = _tab;
        var item = (FileItem)FileList.SelectedItem;

        if (SettingsStore.Instance.Settings.RenameMode == RenameMode.Inline)
        {
            item.EditName = item.Name;
            tab.BeginInlineEdit();
            item.IsEditing = true;
        }
        else
        {
            var dlg = new InputDialog("Rename", "New name:", item.Name) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true)
            {
                if (!AllowRename(item.Name, dlg.Value, item.IsDirectory)) return;
                var act = Activity.Begin("Rename", $"{item.Name} → {dlg.Value}", "pencil");
                string oldPath = item.FullPath;
                tab.BeginKnownFileOperation();
                _fileOperationInProgress = true;
                string? err;
                try
                {
                    err = await Task.Run(() => _ops.Rename(item.FullPath, dlg.Value));
                }
                finally
                {
                    _fileOperationInProgress = false;
                }
                Activity.Complete(act, err is null, err);
                if (err is not null) SetStatus($"⚠️ {err}");
                else
                {
                    PushRenameUndo(oldPath, dlg.Value);
                    string dir = Path.GetDirectoryName(oldPath)!;
                    tab.ApplyLocalRename(item, Path.Combine(dir, dlg.Value));
                }
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
            await _tab.ReloadAfterOperationAsync();
            if (created is not null && _tab.Find(created) is { } item)
            {
                item.EditName = item.Name;
                _tab.BeginInlineEdit();
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
            await _tab.ReloadAfterOperationAsync();
        }
    }

    private void Properties_Click(object sender, RoutedEventArgs e) => OpenSelectedProperties();

    private void OpenSelectedProperties()
    {
        if (FileList.SelectedItem is not FileItem item) return;
        var dlg = new PropertiesDialog(item) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        if (dlg.Changed) _ = _tab?.ReloadAfterOperationAsync();
    }

    // ---- First-class Git actions -------------------------------------------
    private async void GitMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!SettingsStore.Instance.Settings.GitIntegrationEnabled) return;

        _gitMenuCts?.Cancel();
        _gitMenuCts?.Dispose();
        _gitMenuCts = new CancellationTokenSource();
        CancellationToken token = _gitMenuCts.Token;
        MenuGit.Items.Clear();
        MenuGit.Items.Add(new MenuItem { Header = "Checking repository…", IsEnabled = false });

        try
        {
            var runtime = GitIntegrationRuntime.Instance;
            GitInstallationInfo? installation = await runtime.ExecutableLocator.FindAsync(token);
            if (!_fileContextMenuOpen || token.IsCancellationRequested) return;
            if (installation is null || !installation.IsSupported)
            {
                BuildGitUnavailableMenu(installation);
                return;
            }

            string? candidate = GitMenuCandidate();
            if (candidate is null)
            {
                MenuGit.Items.Clear();
                MenuGit.Items.Add(new MenuItem { Header = "No folder is available", IsEnabled = false });
                return;
            }

            GitRepositoryContext? repository = await runtime.RepositoryLocator.FindAsync(candidate, token);
            if (repository is null)
            {
                BuildGitInitializeMenu(candidate);
                return;
            }

            GitRepositoryStatus status = await runtime.StatusReader.ReadAsync(repository, token);
            if (!_fileContextMenuOpen || token.IsCancellationRequested) return;
            _gitMenuRepository = repository;
            _gitMenuStatus = status;
            BuildGitRepositoryMenu(status);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_fileContextMenuOpen) return;
            MenuGit.Items.Clear();
            MenuGit.Items.Add(new MenuItem
            {
                Header = GitSecurity.Redact(ex.Message),
                IsEnabled = false,
            });
        }
    }

    private string? GitMenuCandidate()
    {
        if (SelectedItems() is [{ IsDirectory: true } directory] && Directory.Exists(directory.FullPath))
            return directory.FullPath;
        if (_tab is { IsFolderView: true } && Directory.Exists(_tab.CurrentPath))
            return _tab.CurrentPath;
        return null;
    }

    private void BuildGitUnavailableMenu(GitInstallationInfo? installation)
    {
        MenuGit.Items.Clear();
        MenuGit.Items.Add(new MenuItem
        {
            Header = installation is null
                ? "Git for Windows is required"
                : $"Git {installation.DisplayVersion} is too old (2.40+ required)",
            IsEnabled = false,
        });
        var choose = new MenuItem { Header = "Choose Git executable…" };
        choose.Click += ChooseGitExecutable_Click;
        MenuGit.Items.Add(choose);
        var download = new MenuItem { Header = "Get Git for Windows" };
        download.Click += (_, _) => OpenWebAddress("https://git-scm.com/download/win");
        MenuGit.Items.Add(download);
    }

    private void BuildGitInitializeMenu(string folder)
    {
        _gitMenuRepository = null;
        _gitMenuStatus = null;
        MenuGit.Items.Clear();
        MenuGit.Items.Add(new MenuItem { Header = "Not a Git repository", IsEnabled = false });
        var setup = new MenuItem { Header = "Open Git & GitHub…", Tag = folder };
        setup.Click += OpenGitSetup_Click;
        MenuGit.Items.Add(setup);
        var initialize = new MenuItem { Header = "Initialize Git repository…", Tag = folder };
        initialize.Click += InitializeGit_Click;
        MenuGit.Items.Add(initialize);
    }

    private void BuildGitRepositoryMenu(GitRepositoryStatus status)
    {
        MenuGit.Items.Clear();
        MenuGit.Items.Add(new MenuItem
        {
            Header = $"{status.Branch.DisplayName} · {status.Files.Count} changed · "
                     + $"{status.Files.Count(file => file.IsStaged)} staged",
            IsEnabled = false,
        });
        if (status.Repository.OperationState != GitRepositoryOperationState.Normal)
            MenuGit.Items.Add(new MenuItem
            {
                Header = $"{status.Repository.OperationState} in progress",
                IsEnabled = false,
            });

        MenuGit.Items.Add(new Separator());
        var changes = new MenuItem { Header = "Open repository changes…" };
        changes.Click += OpenGitChanges_Click;
        MenuGit.Items.Add(changes);

        HashSet<string> selected = SelectedRepositoryPaths(status.Repository);
        GitFileStatus[] selectedStatuses = status.Files.Where(file =>
            selected.Count == 0 || selected.Any(path => IsSameOrChild(file.Path, path))).ToArray();
        if (selected.Count > 0 && selectedStatuses.Any(file => file.IsUnstaged || file.IsConflict))
        {
            var stage = new MenuItem { Header = "Stage selected files", Tag = selectedStatuses };
            stage.Click += StageFromGitMenu_Click;
            MenuGit.Items.Add(stage);
        }
        if (selected.Count > 0 && selectedStatuses.Any(file => file.IsStaged))
        {
            var unstage = new MenuItem { Header = "Unstage selected files", Tag = selectedStatuses };
            unstage.Click += UnstageFromGitMenu_Click;
            MenuGit.Items.Add(unstage);
        }

        var commit = new MenuItem
        {
            Header = "Commit staged changes…",
            IsEnabled = status.HasStagedChanges && !status.HasConflicts
                && status.Repository.OperationState == GitRepositoryOperationState.Normal
                && !status.Branch.IsDetached,
        };
        commit.Click += OpenGitChanges_Click;
        MenuGit.Items.Add(commit);

        GitRemoteInfo? pushRemote = status.Remotes.FirstOrDefault(remote =>
                                        remote.Name == status.PreferredPushRemote)
                                    ?? (status.Remotes.Count == 1 ? status.Remotes[0] : null);
        bool hasPushableCommits = string.IsNullOrWhiteSpace(status.Branch.Upstream)
            || status.Branch.Ahead > 0;
        var push = new MenuItem
        {
            Header = pushRemote is null
                ? "Push (no push remote configured)"
                : $"Push {status.Branch.DisplayName} to {pushRemote.Name}…",
            IsEnabled = pushRemote is not null
                && !status.Branch.IsDetached
                && !status.Branch.IsUnborn
                && !status.HasConflicts
                && status.Repository.OperationState == GitRepositoryOperationState.Normal
                && hasPushableCommits,
        };
        push.Click += OpenGitChanges_Click;
        MenuGit.Items.Add(push);

        if (pushRemote is { IsGitHub: true, WebUrl.Length: > 0 })
        {
            var github = new MenuItem { Header = "Open repository on GitHub", Tag = pushRemote.WebUrl };
            github.Click += (_, _) => OpenWebAddress((string)github.Tag);
            MenuGit.Items.Add(github);
        }

        MenuGit.Items.Add(new Separator());
        var copyRoot = new MenuItem { Header = "Copy repository root" };
        copyRoot.Click += (_, _) =>
        {
            try { Clipboard.SetText(status.Repository.WorkTreeRoot); }
            catch (Exception ex) { SetStatus($"⚠️ {ex.Message}"); }
        };
        MenuGit.Items.Add(copyRoot);
    }

    private HashSet<string> SelectedRepositoryPaths(GitRepositoryContext repository)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string fullPath in SelectedPaths())
        {
            try
            {
                string relative = Path.GetRelativePath(repository.WorkTreeRoot, fullPath).Replace('\\', '/');
                if (relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal))
                    paths.Add(relative == "." ? string.Empty : relative);
            }
            catch { }
        }
        return paths;
    }

    private static bool IsSameOrChild(string filePath, string selectedPath) =>
        string.Equals(filePath, selectedPath, StringComparison.Ordinal)
        || filePath.StartsWith(selectedPath.TrimEnd('/') + "/", StringComparison.Ordinal);

    private async void StageFromGitMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_gitMenuRepository is null || sender is not MenuItem { Tag: GitFileStatus[] files }) return;
        string[] paths = files.Where(file => file.IsUnstaged || file.IsConflict)
            .Select(file => file.Path).Distinct(StringComparer.Ordinal).ToArray();
        GitOperationResult result = await GitIntegrationRuntime.Instance.Mutations.StageAsync(
            _gitMenuRepository, paths, CancellationToken.None);
        SetStatus(result.Message ?? (result.Succeeded ? "Selected files staged." : "Staging failed."));
        await (_tab?.ReloadAfterOperationAsync() ?? Task.CompletedTask);
    }

    private async void UnstageFromGitMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_gitMenuRepository is null || _gitMenuStatus is null
            || sender is not MenuItem { Tag: GitFileStatus[] files }) return;
        string[] paths = files.Where(file => file.IsStaged)
            .Select(file => file.Path).Distinct(StringComparer.Ordinal).ToArray();
        GitOperationResult result = await GitIntegrationRuntime.Instance.Mutations.UnstageAsync(
            _gitMenuRepository, paths, !_gitMenuStatus.Branch.IsUnborn, CancellationToken.None);
        SetStatus(result.Message ?? (result.Succeeded ? "Selected files unstaged." : "Unstaging failed."));
        await (_tab?.ReloadAfterOperationAsync() ?? Task.CompletedTask);
    }

    private async void InitializeGit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string folder }) return;
        if (!ConfirmDialog.Ask(Window.GetWindow(this), "Initialize Git repository",
                $"Create Git repository metadata in:\n{folder}\n\n"
                + "This does not stage, commit, or upload any files.",
                "Initialize", "Cancel", danger: false))
            return;

        GitOperationResult result = await GitIntegrationRuntime.Instance.Mutations.InitializeAsync(
            folder, CancellationToken.None);
        SetStatus(result.Message ?? (result.Succeeded
            ? "Git repository initialized. No files were staged."
            : "Repository initialization failed."));
        if (!result.Succeeded) return;
        GitIntegrationRuntime.Instance.RepositoryLocator.Invalidate(folder);
        GitRepositoryContext? repository = await GitIntegrationRuntime.Instance.RepositoryLocator.FindAsync(
            folder, CancellationToken.None, useCache: false);
        if (repository is not null) OpenGitChanges(repository);
    }

    private async void OpenGitSetup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string folder }) return;
        var window = new GitSetupWindow(folder)
        {
            Owner = Window.GetWindow(this),
        };
        if (window.ShowDialog() != true || window.RepositoryPath is not { } repositoryPath)
            return;

        _vm?.NewTab(repositoryPath, activate: true);
        GitRepositoryContext? repository = await GitIntegrationRuntime.Instance.RepositoryLocator
            .FindAsync(repositoryPath, CancellationToken.None, useCache: false);
        if (repository is not null) OpenGitChanges(repository);
    }

    private void OpenGitChanges_Click(object sender, RoutedEventArgs e)
    {
        if (_gitMenuRepository is not null) OpenGitChanges(_gitMenuRepository);
    }

    private void OpenGitChanges(GitRepositoryContext repository)
    {
        var window = new GitChangesWindow(repository, SelectedPaths())
        {
            Owner = Window.GetWindow(this),
        };
        window.ShowDialog();
        _ = _tab?.ReloadAfterOperationAsync();
    }

    private void ChooseGitExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose Git executable",
            Filter = "Git executable (git.exe)|git.exe|Applications (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        SettingsStore.Instance.Settings.GitExecutablePath = dialog.FileName;
        SettingsStore.Instance.Flush();
        SetStatus("Git executable updated.");
    }

    private static void OpenWebAddress(string address)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch { }
    }

    // ---- Native Windows shell items, rendered in our themed submenu --------
    private ShellContextMenu? _shellSession;

    private void ShowMore_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        PrepareShowMoreOptions(sender as MenuItem);
    }

    private void PrepareShowMoreOptions(MenuItem? more = null)
    {
        more ??= MenuShowMore;
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
        _fileContextMenuOpen = false;
        _shellSession?.Dispose();
        _shellSession = null;
        _gitMenuCts?.Cancel();
        _gitMenuCts?.Dispose();
        _gitMenuCts = null;
        _gitMenuRepository = null;
        _gitMenuStatus = null;
        MenuGit.Items.Clear();
        MenuGit.Items.Add(new MenuItem { Header = "Checking repository…", IsEnabled = false });
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

    // ---- Pin / unpin selected folders to a chosen sidebar list ------------
    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: PinMenuTarget target }) return;
        e.Handled = true;
        var dirs = SelectedItems().Where(i => i.IsDirectory).ToList();
        if (dirs.Count == 0) return;
        bool allPinned = dirs.All(i => MainViewModel.IsPinnedTo(target.Key, i.FullPath));
        foreach (var d in dirs)
        {
            if (allPinned) MainViewModel.UnpinFrom(target.Key, d.FullPath);
            else MainViewModel.PinTo(target.Key, d.FullPath, d.Name, d.IconKey);
        }
        SetStatus(allPinned ? $"Unpinned from {target.Name}" : $"Pinned to {target.Name}");
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
        if (!TerminalLauncher.TryOpen(dir, out var err)) SetStatus($"⚠️ {err}");
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
            _ = _tab.ReloadAfterOperationAsync();
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
            _ = _tab.ReloadAfterOperationAsync();
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
                ExtractArchive(z.FullPath, dest);
                extracted.Add(dest);
                ok++;
            }
            catch (Exception ex) { lastErr = ex.Message; SetStatus($"⚠️ {ex.Message}"); }
        }
        Activity.Complete(act, ok == zips.Count, lastErr);
        if (extracted.Count > 0) UndoService.Instance.Push(new RecycleAction(extracted, "Extract"));
        if (ok > 0)
        {
            SetStatus($"Extracted {ok} archive{(ok == 1 ? "" : "s")}");
            _ = _tab.ReloadAfterOperationAsync();
        }
    }

    // Extract any supported archive into destDir. ZIP uses the built-in extractor;
    // RAR / 7z / TAR / GZ / BZ2 go through SharpCompress (pure-managed, no external tools).
    private static void ExtractArchive(string src, string destDir)
    {
        if (string.Equals(Path.GetExtension(src), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(src, destDir);
            return;
        }

        Directory.CreateDirectory(destDir);
        var opts = new SharpCompress.Common.ExtractionOptions { ExtractFullPath = true, Overwrite = true };
        SharpCompress.Archives.ArchiveFactory.WriteToDirectory(src, destDir, opts);
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
            await _tab.ReloadAfterOperationAsync();
            if (created is not null && _tab.Find(created) is { } item)
            {
                item.EditName = item.Name;
                _tab.BeginInlineEdit();
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
            await _tab.ReloadAfterOperationAsync();
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
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            item.IsEditing = false;
            _tab?.EndInlineEdit();
        }
    }

    private void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is FileItem item) CommitInline(item);
    }

    private async void CommitInline(FileItem item)
    {
        if (_committing || _fileOperationInProgress || !item.IsEditing || _tab is null) return;
        var tab = _tab;
        _committing = true;
        try
        {
            item.IsEditing = false;
            string newName = item.EditName;
            if (!string.Equals(newName, item.Name, StringComparison.Ordinal))
            {
                if (!AllowRename(item.Name, newName, item.IsDirectory)) return;
                string oldPath = item.FullPath;
                var act = Activity.Begin("Rename", $"{item.Name} → {newName}", "pencil");
                tab.BeginKnownFileOperation();
                _fileOperationInProgress = true;
                string? err;
                try
                {
                    err = await Task.Run(() => _ops.Rename(oldPath, newName));
                }
                finally
                {
                    _fileOperationInProgress = false;
                }
                Activity.Complete(act, err is null, err);
                if (err is not null) SetStatus($"⚠️ {err}");
                else
                {
                    PushRenameUndo(oldPath, newName);
                    string dir = Path.GetDirectoryName(oldPath)!;
                    tab.ApplyLocalRename(item, Path.Combine(dir, newName));
                }
            }
        }
        finally
        {
            tab.EndInlineEdit();
            _committing = false;
        }
    }

    // Record a rename so Ctrl+Z restores the previous name.
    private static void PushRenameUndo(string oldPath, string newName)
    {
        string? dir = Path.GetDirectoryName(oldPath.TrimEnd(Path.DirectorySeparatorChar));
        if (dir is null) return;
        UndoService.Instance.Push(new RenameAction(Path.Combine(dir, newName), oldPath));
    }
}
