using System.Collections.Specialized;
using System.ComponentModel;
// Avalonia 11.3 marks the IDataObject drag/clipboard API obsolete in favour of
// DataTransfer; it is still the documented 11.x path and works on X11.
#pragma warning disable CS0618
using System.Text;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RainExplorer.Helpers;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using RainExplorer.Views;

namespace RainExplorer.Controls;

/// <summary>
/// One browsing pane: tab strip, navigation bar, breadcrumb/address bar, search, the file
/// list (details / list / tiles / grid) and the optional preview pane. A port of the Windows
/// build's PaneView — same layout, same single context menu with enabled/disabled items,
/// same tab dragging, grouping and tab projects.
/// </summary>
public partial class PaneView : UserControl
{
    private const string InternalDragFormat = "application/x-rain-explorer-paths";
    private const double DragThreshold = 6;

    private readonly FileOperationsService _ops = new();
    private PaneViewModel? _vm;
    private TabViewModel? _tab;
    private bool _fileOperationInProgress;
    private string[]? _pendingSelectPaths;
    private TabViewModel? _pendingSelectionTab;
    private bool? _compactNavigation;
    private sealed record TabLayoutSnapshot(Dictionary<TabViewModel, double> TopRow,
        Dictionary<TabViewModel, double> GroupRow);
    private TabLayoutSnapshot? _pendingTabLayoutBefore;
    private TabLayoutSnapshot? _lastTabLayout;
    private bool _tabLayoutAnimationScheduled;
    private double _newTabButtonTargetX = double.NaN;
    private bool _newTabButtonPositionReady;
    private Popup? _tabDragGhost;
    private Vector _tabDragGrabOffset;
    private sealed class TabPreviewMotion(TranslateTransform transform, double target)
    {
        public TranslateTransform Transform { get; } = transform;
        public double Target { get; set; } = target;
    }
    private readonly Dictionary<Control, TabPreviewMotion> _tabPreviewMotions = new();
    private Control? _tabPreviewHiddenContainer;
    private TabViewModel? _tabPreviewTarget;
    private bool _tabPreviewAfter;
    private bool _tabPreviewFrameScheduled;
    private bool _tabPreviewReturning;
    private TimeSpan? _tabPreviewLastFrame;
    private double _tabPreviewLastReorderX = double.NaN;

    // File drag / marquee state
    private Point? _dragStart;
    private FileItem? _dragCandidate;
    private FileItem? _pendingSingleSelect;
    private ListBoxItem? _dropContainer;
    private bool _marqueeing;
    private Point _marqueeStart;
    private HashSet<FileItem>? _marqueeBaseSelection;

    // Tab drag state
    private TabViewModel? _tabDragCandidate;
    private ListBox? _tabDragSourceBar;
    private Point _tabDragStart;
    private bool _tabDragging;
    private IPointer? _tabDragPointer;
    private TabViewModel? _groupHoverTarget;
    private DispatcherTimer? _groupHoverTimer;
    private bool _groupArmed;

    // File context menu (one static menu; items are enabled/disabled per selection)
    private ContextMenu _fileMenu = null!;
    private MenuItem _menuRestore = null!, _menuOpen = null!, _menuOpenNewTab = null!, _menuOpenWith = null!,
        _menuOpenTerminal = null!, _menuCut = null!, _menuCopy = null!, _menuCopyPath = null!, _menuPaste = null!,
        _menuShortcut = null!, _menuPin = null!, _menuRename = null!, _menuDelete = null!, _menuNewFolder = null!,
        _menuNewFile = null!, _menuProperties = null!, _menuShowMore = null!;
    private Separator _restoreSeparator = null!;
    private readonly List<MenuItem> _dynamicPinItems = new();

    private static ActivityService Activity => ActivityService.Instance;
    private static AppSettings Settings => SettingsStore.Instance.Settings;

    public PaneView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateNavigationLayout();
        DataContextChanged += (_, _) => OnDataContextChanged();
        AddHandler(PointerPressedEvent, Root_PointerPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || !_tabDragging) return;
            EndTabDrag(cancelled: true);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        FileList.AddHandler(PointerPressedEvent, FileList_PointerPressed, RoutingStrategies.Tunnel);
        FileList.AddHandler(PointerMovedEvent, FileList_PointerMoved, RoutingStrategies.Tunnel);
        FileList.AddHandler(PointerReleasedEvent, FileList_PointerReleased, RoutingStrategies.Tunnel);
        FileList.AddHandler(PointerCaptureLostEvent, (_, _) => EndMarquee());
        FileList.AddHandler(KeyDownEvent, FileList_KeyDown, RoutingStrategies.Tunnel);
        FileList.AddHandler(DragDrop.DragOverEvent, FileList_DragOver);
        FileList.AddHandler(DragDrop.DragLeaveEvent, FileList_DragLeave);
        FileList.AddHandler(DragDrop.DropEvent, FileList_Drop);

        foreach (ListBox bar in new[] { TabBar, GroupTabBar })
        {
            bar.AddHandler(PointerPressedEvent, TabBar_PointerPressed, RoutingStrategies.Tunnel);
            bar.AddHandler(PointerMovedEvent, TabBar_PointerMoved, RoutingStrategies.Tunnel);
            bar.AddHandler(PointerReleasedEvent, TabBar_PointerReleased, RoutingStrategies.Tunnel);
            bar.AddHandler(PointerCaptureLostEvent, (_, _) =>
            {
                // The list can release its implicit press capture while selection
                // and focus change. Only a started drag owns capture on this bar.
                if (_tabDragging && !ReferenceEquals(_tabDragPointer?.Captured, _tabDragSourceBar))
                    EndTabDrag(cancelled: true);
            });
            bar.AddHandler(Button.ClickEvent, TabBar_ButtonClick);
            bar.AddHandler(DragDrop.DragOverEvent, TabBar_DragOver);
            bar.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetTabDropTarget(null));
            bar.AddHandler(DragDrop.DropEvent, TabBar_Drop);
            bar.ContainerPrepared += TabBar_ContainerPrepared;
            bar.ContextRequested += TabBar_ContextRequested;
        }
        TabBar.PointerExited += (_, _) => (TabBar.ItemsPanelRoot as AdaptiveTabPanel)?.UnlockWidths();
        TabBar.SizeChanged += (_, _) => UpdateNewTabButtonPosition();
        TabBar.LayoutUpdated += TabBar_LayoutUpdated;
        TabStrip.ContextRequested += TabStrip_ContextRequested;

        AddressBar.OpenInNewTabRequested += path => _vm?.NewTab(path, activate: true);
        AddressBar.PropertiesRequested += path => _ = ShowProperties(path);
        AddressBar.FilesDropped += (files, dest, move) => _ = PerformDrop(files, dest, move);

        BuildFileContextMenu();
        Settings.PropertyChanged += OnSettingChanged;
        ClipboardService.Changed += OnClipboardChanged;
        ApplyLayout();
        ApplyPreviewVisibility();
        StatusBar.IsVisible = Settings.ShowStatusBar;
    }

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;
    private MainViewModel? MainVm => OwnerWindow?.DataContext as MainViewModel;

    private void UpdateNavigationLayout()
    {
        bool compact = Bounds.Width < 640;
        if (_compactNavigation == compact) return;
        _compactNavigation = compact;

        // Keep the path usable in split panes and at the minimum window width.
        Grid.SetRow(SearchActions, compact ? 1 : 0);
        Grid.SetColumn(SearchActions, compact ? 0 : 2);
        Grid.SetColumnSpan(SearchActions, compact ? 2 : 1);
        SearchActions.Margin = compact ? new Thickness(0, 8, 0, 0) : default;
        SearchActions.ColumnDefinitions[0].Width = compact ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        SearchPill.Width = compact ? double.NaN : 210;
        SearchPill.Margin = compact ? default : new Thickness(8, 0, 0, 0);
    }

    // ===================== DataContext / tab tracking =====================

    private void OnDataContextChanged()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnPanePropertyChanged;
            _vm.Tabs.CollectionChanged -= OnTabsCollectionChanged;
        }
        _vm = DataContext as PaneViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnPanePropertyChanged;
            _vm.Tabs.CollectionChanged += OnTabsCollectionChanged;
        }
        OnSelectedTabChanged();
        RefreshProjectsButton();
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.DisplayedTab)) OnSelectedTabChanged();
        if (e.PropertyName == nameof(PaneViewModel.ActiveProjectId)) RefreshProjectsButton();
        if (e.PropertyName is nameof(PaneViewModel.TopLevelTabs) or nameof(PaneViewModel.ActiveGroupTabs))
        {
            _pendingTabLayoutBefore ??= _lastTabLayout;
            ScheduleTabLayoutAnimation();
        }
    }

    private void OnSelectedTabChanged()
    {
        Motion.Cancel(FileList);
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
            _tab.ContentsChanged -= OnTabContentsChanged;
        }
        _tab = _vm?.DisplayedTab;
        TabContentHost.IsVisible = _tab is not null;
        NavigationGrid.IsEnabled = _tab is not null;
        if (!ReferenceEquals(_pendingSelectionTab, _tab))
        {
            _pendingSelectPaths = null;
            _pendingSelectionTab = null;
        }
        if (_tab is not null)
        {
            _tab.PropertyChanged += OnTabPropertyChanged;
            _tab.ContentsChanged += OnTabContentsChanged;
        }
        FileList.ItemsSource = _tab?.Items;
        RefreshSortIndicators();
        ApplyLayout();
        UpdatePreview();
        var tab = _tab;
        Dispatcher.UIThread.Post(() => CaptureTabPreview(tab), DispatcherPriority.Render);
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TabViewModel.SortKey):
            case nameof(TabViewModel.SortDir):
                RefreshSortIndicators();
                break;
            case nameof(TabViewModel.IsSearchView):
                ApplyLayout();
                break;
        }
    }

    // ---- Navigation animation (subtle fade + slide up) ---------------------
    private void OnTabContentsChanged()
    {
        Preview.Clear();
        ApplyCutMarks();
        PlayListAnimation();
        var tab = _tab;
        QueuePreviewAfterNavigation(tab);
        var selection = _pendingSelectPaths?.Select(path => _tab?.Find(path)).OfType<FileItem>().ToArray();
        if (ReferenceEquals(_pendingSelectionTab, _tab) && selection is not null
            && (selection.Length > 0 || _pendingSelectPaths!.Length == 0))
        {
            _pendingSelectPaths = null;
            _pendingSelectionTab = null;
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_tab, tab) || selection.Any(item => !ReferenceEquals(tab?.Find(item.FullPath), item))) return;
                FileList.SelectedItems?.Clear();
                foreach (var item in selection) FileList.SelectedItems?.Add(item);
                if (selection.Length > 0) FileList.ScrollIntoView(selection[0]);
            }, DispatcherPriority.Loaded);
        }
        else if (_tab?.Items.Count > 0)
        {
            FileList.ScrollIntoView(0);
        }
    }

    private void PlayListAnimation()
    {
        _ = Motion.Play(FileList, 220, new CubicEaseOut(),
        [
            Motion.Frame(0, (OpacityProperty, 0), (TranslateTransform.YProperty, 10)),
            Motion.Frame(0.82, (OpacityProperty, 1)),
            Motion.Frame(1, (OpacityProperty, 1), (TranslateTransform.YProperty, 0)),
        ]);
    }

    private void QueuePreviewAfterNavigation(TabViewModel? tab)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(240);
            CaptureTabPreview(tab);
        }, DispatcherPriority.Render);
    }

    /// <summary>Select a file once the folder that contains it has loaded.</summary>
    public void SelectPathWhenLoaded(string path)
    {
        SelectPathsWhenLoaded([path]);
    }

    public void SelectPathsWhenLoaded(IReadOnlyList<string> paths)
    {
        _pendingSelectPaths = paths.ToArray();
        _pendingSelectionTab = _tab;
        if (paths.Count == 0 || paths.Any(path => _tab?.Find(path) is not null)) OnTabContentsChanged();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ViewLayout): ApplyLayout(); break;
            case nameof(AppSettings.ShowPreviewPane): ApplyPreviewVisibility(); break;
            case nameof(AppSettings.ShowStatusBar): StatusBar.IsVisible = Settings.ShowStatusBar; break;
            case nameof(AppSettings.TabProjects): RefreshProjectsButton(); break;
        }
    }

    // ---- Activate this pane on any click -----------------------------------
    private void Root_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        _vm?.ActivateCommand.Execute(null);

    private Window? _selectionWindow;

    private void FileSelection_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source
            || source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null
            || source.FindAncestorOfType<TextBox>(includeSelf: true) is not null) return;
        var clickedItem = FileList.IsVisualAncestorOf(source) ? ItemUnder(source) : null;
        if (_tab?.Items.FirstOrDefault(item => item.IsEditing) is { } editing
            && !ReferenceEquals(editing, clickedItem)) _ = FinishInlineEdit(editing);
        // Leave Ctrl/Shift marquee selection and row selection to the list.
        if (clickedItem is null && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) == 0)
            FileList.SelectedItems?.Clear();
    }

    // ===================== Layout (Details / List / Tiles / Grid) =====================

    private void ApplyLayout()
    {
        bool search = _tab?.IsSearchView == true;
        var layout = search ? ViewLayout.Details : Settings.ViewLayout;
        bool details = layout == ViewLayout.Details;
        string template = search ? "SearchTemplate" : layout switch
        {
            ViewLayout.List => "ListItemTemplate",
            ViewLayout.Tiles => "TileTemplateMedium",
            ViewLayout.Grid => "TileTemplateLarge",
            _ => "DetailsTemplate",
        };

        var items = FileList.ItemsSource;
        FileList.ItemsSource = null;   // regenerate containers with the new theme/template
        FileList.ItemContainerTheme = (ControlTheme?)this.FindResourceOrDefault(details ? "FileRow" : "TileItem");
        if (Resources[template] is IDataTemplate dt) FileList.ItemTemplate = dt;
        if (Resources[details ? "RowsPanel" : "IconWrapPanel"] is ITemplate<Panel?> panel) FileList.ItemsPanel = panel;
        FileList.ItemsSource = items;

        HeaderRow.IsVisible = details;
        ColDate.Content = search ? "Date modified" : "Date modified";
        RefreshSortIndicators();
    }

    // Show a ▲/▼ arrow on the active sort column.
    private void RefreshSortIndicators()
    {
        if (_tab is null) return;
        bool search = _tab.IsSearchView;
        string arrow = _tab.SortDir >= 0 ? "  ▲" : "  ▼";
        ColName.Content = "Name" + (_tab.SortKey == "Name" ? arrow : "");
        ColDate.Content = "Date modified" + (_tab.SortKey == "Modified" ? arrow : "");
        ColCreated.Content = (search ? "Folder" : "Date created") + (_tab.SortKey == "Created" && !search ? arrow : "");
        ColType.Content = search ? "" : "Type" + (_tab.SortKey == "Type" ? arrow : "");
        ColSize.Content = "Size" + (_tab.SortKey == "Size" ? arrow : "");
    }

    private void Header_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key }) _tab?.SortCommand.Execute(key);
    }

    private void ColumnGrip_DragDelta(object? sender, VectorEventArgs e)
    {
        if (sender is Thumb { Tag: string column }) FileColumns.Shared.Resize(column, e.Vector.X);
    }

    private void ApplyPreviewVisibility()
    {
        bool show = Settings.ShowPreviewPane;
        var columns = FolderGrid.ColumnDefinitions;
        columns[1].Width = new GridLength(show ? 6 : 0);
        columns[2].Width = new GridLength(show ? Math.Clamp(Settings.PreviewPaneWidth, 240, 900) : 0);
        Preview.IsVisible = show;
        PreviewSplitter.IsVisible = show;
        PreviewToggle.IsChecked = show;
        if (show) UpdatePreview();
        else Preview.Clear();
    }

    private void PreviewToggle_Click(object? sender, RoutedEventArgs e) =>
        Settings.ShowPreviewPane = PreviewToggle.IsChecked == true;

    private void PreviewSplitter_DragCompleted(object? sender, VectorEventArgs e)
    {
        double width = FolderGrid.ColumnDefinitions[2].ActualWidth;
        if (width > 0) Settings.PreviewPaneWidth = Math.Round(width);
    }

    private void UpdatePreview()
    {
        if (!Settings.ShowPreviewPane) return;
        var selected = SelectedItems();
        Preview.ShowItem(selected.Count == 1 ? selected[0] : null, selected.Count);
    }

    private void SplitButton_Click(object? sender, RoutedEventArgs e) => MainVm?.ToggleSplitCommand.Execute(null);

    // ===================== Toolbar dropdowns (Sort / Layout / Projects) =====================

    private static MenuItem MenuHeader(string text) => new()
    {
        Header = text,
        IsEnabled = false,
        FontWeight = FontWeight.SemiBold,
    };

    private void SortButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_tab is null) return;
        var tab = _tab;
        var items = new List<Control> { MenuHeader("Sort by") };
        foreach (var (key, label) in new[] { ("Name", "Name"), ("Modified", "Date modified"),
                     ("Created", "Date created"), ("Type", "Type"), ("Size", "Size") })
            items.Add(MenuFactory.Item(label, null, () => tab.SortCommand.Execute(key)));
        items.Add(new Separator());
        // Re-selecting the same key flips direction.
        items.Add(MenuFactory.Item("Reverse order", null, () => tab.SortCommand.Execute(tab.SortKey)));
        OpenToolbarMenu(SortButton, items, 180);
    }

    private void LayoutButton_Click(object? sender, RoutedEventArgs e)
    {
        var items = new List<Control> { MenuHeader("Layout") };
        foreach (var layout in new[] { ViewLayout.Details, ViewLayout.List, ViewLayout.Tiles, ViewLayout.Grid })
            items.Add(MenuFactory.Item(layout.ToString(), null, () =>
            {
                Settings.ViewLayout = layout;
                ApplyLayout();
            }));
        OpenToolbarMenu(LayoutButton, items, 140);
    }

    private static void OpenToolbarMenu(Control anchor, List<Control> items, double minWidth)
    {
        var menu = new ContextMenu
        {
            ItemsSource = items,
            MinWidth = minWidth,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            PlacementTarget = anchor,
        };
        menu.Open(anchor);
    }

    // ---- Tab projects ---------------------------------------------------------
    private void ProjectsButton_Click(object? sender, RoutedEventArgs e) => OpenProjectsMenu(manage: false);

    private void OpenProjectsMenu(bool manage)
    {
        if (_vm is null) return;
        var settings = Settings;
        var items = new List<Control>();
        var menu = new ContextMenu
        {
            MinWidth = 240,
            MaxHeight = 520,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            PlacementTarget = ProjectsButton,
        };
        Motion.AnimateProjectMenu(menu);

        if (!manage)
        {
            items.Add(MenuHeader("TAB PROJECTS"));
            items.Add(MenuFactory.Item("Save current tabs as new project…", null, () => _ = SaveCurrentTabsAsProject()));
            items.Add(MenuFactory.Item("New empty project…", null, () => _ = CreateEmptyProject()));
            if (settings.TabProjects.Count > 0)
            {
                items.Add(new Separator());
                foreach (var project in settings.TabProjects)
                {
                    var item = MenuFactory.Check(
                        $"{project.Name}  ·  {project.Tabs.Count} tab{(project.Tabs.Count == 1 ? "" : "s")}",
                        project.Id == _vm.ActiveProjectId,
                        () => _ = SwitchProject(project.Id));
                    items.Add(item);
                }
                var manageItem = MenuFactory.Item("Manage projects…", null, () =>
                    Dispatcher.UIThread.Post(() => OpenProjectsMenu(manage: true)));
                items.Add(manageItem);
            }
        }
        else
        {
            items.Add(MenuHeader("MANAGE TAB PROJECTS"));
            items.Add(MenuFactory.Item("← Back to projects", null, () =>
                Dispatcher.UIThread.Post(() => OpenProjectsMenu(manage: false))));
            items.Add(new Separator());
            foreach (var project in settings.TabProjects.ToList())
            {
                var row = new Grid { Margin = new Thickness(7, 5), ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                row.Children.Add(new TextBlock
                {
                    Text = project.Name, VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180, Margin = new Thickness(3, 0, 10, 0),
                });
                var rename = ProjectManagerButton("Rename");
                rename.Click += (_, _) => { menu.Close(); _ = RenameProject(project.Id); };
                Grid.SetColumn(rename, 1);
                row.Children.Add(rename);
                var delete = ProjectManagerButton("Delete");
                delete.Bind(ForegroundProperty, delete.GetResourceObservable("Danger"));
                delete.Click += (_, _) => { menu.Close(); _ = DeleteProject(project.Id); };
                Grid.SetColumn(delete, 2);
                row.Children.Add(delete);
                items.Add(row);
            }
        }

        menu.ItemsSource = items;
        menu.Open(ProjectsButton);
        RefreshProjectsButton();
    }

    private Button ProjectManagerButton(string text) => new()
    {
        Content = text,
        Theme = (ControlTheme?)this.FindResourceOrDefault("GhostButton"),
        MinWidth = 0,
        Padding = new Thickness(8, 4),
        Margin = new Thickness(3, 0, 0, 0),
        FontSize = 11,
    };

    private void RefreshProjectsButton()
    {
        string? id = _vm?.ActiveProjectId;
        string? name = Settings.TabProjects.FirstOrDefault(project => project.Id == id)?.Name;
        ProjectsButtonText.Text = name is null ? "Tab projects" : $"Project · {name}";
    }

    private async Task<bool> SaveCurrentTabsAsProject()
    {
        if (_vm is null) return false;
        string? name = await InputDialog.Ask(OwnerWindow, "Save tab project", "Project name:", "Current tabs");
        if (name is null) return false;
        var snapshot = _vm.CaptureProject();
        var project = new TabProject
        {
            Name = UniqueProjectName(name),
            Tabs = snapshot.Tabs,
            SelectedIndex = snapshot.SelectedIndex,
        };
        Settings.TabProjects.Add(project);
        Settings.NotifyTabProjectsChanged();
        _vm.ActiveProjectId = project.Id;
        return true;
    }

    private async Task CreateEmptyProject()
    {
        if (_vm is null) return;
        if (_vm.ActiveProjectId is null && _vm.Tabs.Count > 0 && !await SaveCurrentTabsAsProject()) return;
        if (_vm.ActiveProjectId is not null) UpdateCurrentProject();
        string? name = await InputDialog.Ask(OwnerWindow, "New tab project", "Project name:", "New project");
        if (name is null) return;
        var project = new TabProject { Name = UniqueProjectName(name) };
        Settings.TabProjects.Add(project);
        Settings.NotifyTabProjectsChanged();
        _vm.LoadProject(project.Tabs, project.SelectedIndex);
        _vm.ActiveProjectId = project.Id;
    }

    private async Task SwitchProject(string id)
    {
        if (_vm is null || _vm.ActiveProjectId == id) return;
        if (_vm.ActiveProjectId is null && _vm.Tabs.Count > 0 && !await SaveCurrentTabsAsProject()) return;
        if (_vm.ActiveProjectId is not null) UpdateCurrentProject();
        var project = Settings.TabProjects.FirstOrDefault(p => p.Id == id);
        if (project is null) return;
        _vm.LoadProject(project.Tabs, project.SelectedIndex);
        _vm.ActiveProjectId = project.Id;
    }

    private void UpdateCurrentProject()
    {
        if (_vm?.ActiveProjectId is not { Length: > 0 } id) return;
        var project = Settings.TabProjects.FirstOrDefault(p => p.Id == id);
        if (project is null) return;
        var snapshot = _vm.CaptureProject();
        project.Tabs = snapshot.Tabs;
        project.SelectedIndex = snapshot.SelectedIndex;
        Settings.NotifyTabProjectsChanged();
    }

    private async Task RenameProject(string id)
    {
        var project = Settings.TabProjects.FirstOrDefault(p => p.Id == id);
        if (project is null) return;
        string? name = await InputDialog.Ask(OwnerWindow, "Rename tab project", "Project name:", project.Name);
        if (name is null) return;
        project.Name = UniqueProjectName(name, project.Id);
        Settings.NotifyTabProjectsChanged();
    }

    private async Task DeleteProject(string id)
    {
        var project = Settings.TabProjects.FirstOrDefault(p => p.Id == id);
        if (project is null) return;
        if (!await ConfirmDialog.Ask(OwnerWindow, "Delete tab project",
                $"Delete the saved project “{project.Name}”? Its tabs will stay open if it is active.",
                "Delete", "Cancel", danger: true))
            return;
        Settings.TabProjects.Remove(project);
        Settings.NotifyTabProjectsChanged();
        if (_vm?.ActiveProjectId == id) _vm.ActiveProjectId = null;
    }

    private static string UniqueProjectName(string raw, string? currentId = null)
    {
        string baseName = string.IsNullOrWhiteSpace(raw) ? "New project" : raw.Trim();
        string name = baseName;
        int suffix = 2;
        while (Settings.TabProjects.Any(p => p.Id != currentId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} ({suffix++})";
        return name;
    }

    // ===================== Tabs =====================

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (this.GetVisualRoot() is null) return;
        _pendingTabLayoutBefore ??= CaptureTabLayout();
        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
            foreach (var tab in e.OldItems.OfType<TabViewModel>())
                if (!tab.IsDragging)
                {
                    AnimateRemovedTab(TabBar, tab);
                    AnimateRemovedTab(GroupTabBar, tab);
                }
        ScheduleTabLayoutAnimation();
    }

    private TabLayoutSnapshot CaptureTabLayout() => new(CaptureTabRow(TabBar), CaptureTabRow(GroupTabBar));

    private static Dictionary<TabViewModel, double> CaptureTabRow(ListBox bar)
    {
        var result = new Dictionary<TabViewModel, double>();
        foreach (var tab in bar.Items.OfType<TabViewModel>())
            if (bar.ContainerFromItem(tab) is Control container)
                result[tab] = container.TranslatePoint(default, bar)?.X ?? container.Bounds.X;
        return result;
    }

    private void ScheduleTabLayoutAnimation()
    {
        if (_tabLayoutAnimationScheduled) return;
        _tabLayoutAnimationScheduled = true;
        Dispatcher.UIThread.Post(AnimatePendingTabLayoutChange, DispatcherPriority.Render);
    }

    private void AnimatePendingTabLayoutChange()
    {
        var before = _pendingTabLayoutBefore;
        _pendingTabLayoutBefore = null;
        TabBar.UpdateLayout();
        GroupTabBar.UpdateLayout();
        if (this.GetVisualRoot() is not null && before is not null)
        {
            AnimateTabRow(TabBar, before.TopRow);
            AnimateTabRow(GroupTabBar, before.GroupRow);
        }
        _lastTabLayout = CaptureTabLayout();
        _tabLayoutAnimationScheduled = false;
        UpdateNewTabButtonPosition();
    }

    private void AnimateTabRow(ListBox bar, IReadOnlyDictionary<TabViewModel, double> before)
    {
        foreach (var tab in bar.Items.OfType<TabViewModel>())
        {
            if (bar.ContainerFromItem(tab) is not Control container) continue;
            double newX = TabLayoutX(container, bar);
            if (before.TryGetValue(tab, out double oldX))
            {
                if (Math.Abs(oldX - newX) >= 0.5) _ = Motion.Slide(container, oldX - newX);
            }
            else _ = Motion.Arrive(container);
        }
    }

    private static double TabLayoutX(Control container, ListBox bar) => container.Bounds.X
        + (container.GetVisualParent()?.TranslatePoint(default, bar)?.X ?? 0);

    private void TabBar_LayoutUpdated(object? sender, EventArgs e)
    {
        if (!_tabLayoutAnimationScheduled) _lastTabLayout = CaptureTabLayout();
        UpdateNewTabButtonPosition();
    }

    private void UpdateNewTabButtonPosition()
    {
        if (TopTabDropSurface.Bounds.Width < 1) return;
        double used = TabBar.Items.OfType<TabViewModel>()
            .Select(tab => TabBar.ContainerFromItem(tab) as Control)
            .OfType<Control>()
            .Select(c => TabLayoutX(c, TabBar) + c.Bounds.Width)
            .DefaultIfEmpty(0).Max();
        double target = Math.Clamp(used + 4, 0, Math.Max(0, TopTabDropSurface.Bounds.Width - NewTabButton.Width));
        if (!double.IsNaN(_newTabButtonTargetX) && Math.Abs(target - _newTabButtonTargetX) < 0.5) return;
        _newTabButtonTargetX = target;
        var transform = (TranslateTransform)NewTabButton.RenderTransform!;
        double current = transform.X;
        Motion.Cancel(NewTabButton);
        transform.X = target;
        if (_newTabButtonPositionReady)
            _ = Motion.Play(NewTabButton, 145, new CubicEaseOut(),
                [Motion.Frame(0, (TranslateTransform.XProperty, current)), Motion.Frame(1, (TranslateTransform.XProperty, target))]);
        _newTabButtonPositionReady = true;
    }

    private void AnimateRemovedTab(ListBox bar, TabViewModel tab)
    {
        if (bar.ContainerFromItem(tab) is not Control container || !container.IsEffectivelyVisible
            || container.Bounds.Width < 1 || container.Bounds.Height < 1) return;
        var origin = container.TranslatePoint(default, TabAnimationLayer);
        var end = container.TranslatePoint(new Point(container.Bounds.Width, container.Bounds.Height), TabAnimationLayer);
        if (origin is null || end is null) return;
        // Preserve the current appearance when closing interrupts an arrival or slide.
        var renderedSize = new Size(end.Value.X - origin.Value.X, end.Value.Y - origin.Value.Y);
        double opacity = container.Opacity;
        Motion.Cancel(container);
        container.RenderTransform = null;
        double renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(container.Bounds.Width * renderScaling),
            (int)Math.Ceiling(container.Bounds.Height * renderScaling)), new Vector(96, 96));
        try
        {
            // Match native pixel density so the snapshot keeps the live text's antialiasing.
            // Keep DPI at 96: Image's source coordinates are treated as pixels.
            using var context = bitmap.CreateDrawingContext();
            context.DrawRectangle(new VisualBrush(container) { Stretch = Stretch.Fill }, null,
                new Rect(bitmap.Size));
        }
        catch { bitmap.Dispose(); return; }
        var ghost = new Image { Source = bitmap, Width = renderedSize.Width, Height = renderedSize.Height,
            Stretch = Stretch.Fill, Opacity = opacity };
        Canvas.SetLeft(ghost, origin.Value.X);
        Canvas.SetTop(ghost, origin.Value.Y);
        TabAnimationLayer.Children.Add(ghost);
        // Keep the final opacity until removal; releasing it first flashes the snapshot.
        _ = Motion.Play(ghost, 115, new QuadraticEaseIn(),
        [
            Motion.Frame(0, (OpacityProperty, opacity)),
            Motion.Frame(1, (OpacityProperty, 0)),
        ], () =>
        {
            TabAnimationLayer.Children.Remove(ghost);
            bitmap.Dispose();
        }, fillMode: Avalonia.Animation.FillMode.Forward);
    }

    private void NewActiveGroupTab_Click(object? sender, RoutedEventArgs e) => _vm?.NewTabInActiveGroup();

    private void TabBar_ButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { DataContext: TabViewModel tab } button) return;
        if (button.Classes.Contains("tabClose"))
        {
            // Keep tab widths steady while several close buttons are clicked in a row.
            (TabBar.ItemsPanelRoot as AdaptiveTabPanel)?.LockCurrentWidths();
            _vm?.CloseTab(tab);
            e.Handled = true;
        }
        else if (button.Classes.Contains("groupClose"))
        {
            _vm?.CloseGroup(tab);
            e.Handled = true;
        }
    }

    private void TabBar_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is not ListBoxItem container || e.Container.DataContext is not TabViewModel tab) return;
        ToolTip.SetTip(container, new ToolTip { Content = BuildTabTooltip(tab), MaxWidth = double.PositiveInfinity });
        ActiveTabDragOwner?.SuppressTooltip(container);
        container.PointerEntered -= TabContainer_PointerEntered;
        container.PointerEntered += TabContainer_PointerEntered;
    }

    /// <summary>480×270 snapshot of the tab's contents, its title and its path.</summary>
    private static Control BuildTabTooltip(TabViewModel tab)
    {
        var image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch, Name = "TabPreviewImage" };
        image.Bind(Image.SourceProperty, new Avalonia.Data.Binding(nameof(TabViewModel.PreviewImage)) { Source = tab });
        image.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(TabViewModel.HasPreviewImage)) { Source = tab });

        var placeholder = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var folder = new Icon { Kind = "folder", Width = 28, Height = 28 };
        folder.Bind(Icon.ForegroundProperty, folder.GetResourceObservable("TextMuted"));
        placeholder.Children.Add(folder);
        var hint = new TextBlock { Text = "Preview appears after visiting this tab", Margin = new Thickness(0, 10, 0, 0), FontSize = 11 };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("TextFaint"));
        placeholder.Children.Add(hint);
        placeholder.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(TabViewModel.HasPreviewImage)) { Source = tab, Converter = Avalonia.Data.Converters.BoolConverters.Not });

        var frame = new Border
        {
            Height = 270, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true,
            Child = new Panel { Children = { image, placeholder } },
        };
        frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("Glass2"));
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Line2"));

        var title = new TextBlock { Margin = new Thickness(2, 9, 2, 0), FontWeight = FontWeight.SemiBold };
        title.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(TabViewModel.TopLevelTitle)) { Source = tab });
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("Text"));
        var path = new TextBlock { Margin = new Thickness(2, 3, 2, 0), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        path.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(TabViewModel.CurrentPath)) { Source = tab });
        path.Bind(TextBlock.ForegroundProperty, path.GetResourceObservable("TextMuted"));

        return new StackPanel { Width = 480, Margin = new Thickness(2), Children = { frame, title, path } };
    }

    private void TabContainer_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (ActiveTabDragOwner is not null) return;
        if ((sender as Control)?.DataContext is not TabViewModel tab || !ReferenceEquals(tab, _vm?.SelectedTab)) return;
        Dispatcher.UIThread.Post(() => CaptureTabPreview(tab), DispatcherPriority.Render);
    }

    private void CaptureTabPreview(TabViewModel? tab)
    {
        if (tab is null || ActiveTabDragOwner is not null || this.GetVisualRoot() is null
            || !ReferenceEquals(tab, _vm?.DisplayedTab)
            || TabContentHost.Bounds.Width < 1 || TabContentHost.Bounds.Height < 1)
            return;
        RenderTargetBitmap? bitmap = null;
        try
        {
            const double width = 960;
            double scale = width / TabContentHost.Bounds.Width;
            var size = new PixelSize((int)width, (int)Math.Ceiling(TabContentHost.Bounds.Height * scale));
            bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(TabContentHost);
            tab.PreviewImage = bitmap;
            bitmap = null;
        }
        catch
        {
            bitmap?.Dispose();
            // A layout can detach while the pointer crosses tabs; the next hover retries.
        }
    }

    private static TabViewModel? TabFromSource(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as TabViewModel;

    private void TabStrip_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is null || TabFromSource(e.Source) is not null) return;
        var items = new List<Control>
        {
            MenuFactory.Item("New tab", null, () => _vm.NewTab(activate: true), "Ctrl+T"),
        };
        if (_vm.HasActiveGroup) items.Add(MenuFactory.Item("New tab in active group", null, () => _vm.NewTabInActiveGroup()));
        items.Add(new Separator());
        items.Add(MenuFactory.Item("Reopen last closed tab", null, () => _vm.ReopenClosedTab(), "Ctrl+Shift+T",
            enabled: _vm.HasClosedTabs));
        new ContextMenu { ItemsSource = items }.Open(TabStrip);
        e.Handled = true;
    }

    private void TabBar_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is null || TabFromSource(e.Source) is not { } tab) return;
        var items = new List<Control>
        {
            MenuFactory.Item("Duplicate tab", null, () => _vm.DuplicateTab(tab), enabled: tab.CanDuplicate),
            MenuFactory.Item("Reload", null, () => _ = tab.ReloadAsync(), "F5"),
            MenuFactory.Item(tab.IsPinned ? "Unpin tab" : "Pin tab", null, () => _vm.TogglePin(tab)),
        };
        if (tab.IsGrouped)
        {
            items.Add(new Separator());
            items.Add(MenuFactory.Item("New tab in group", null, () => _vm.NewTabInGroup(tab)));
            items.Add(MenuFactory.Item("Rename tab group…", null, async () =>
            {
                string? name = await InputDialog.Ask(OwnerWindow, "Rename tab group", "Group name:", tab.GroupName);
                if (!string.IsNullOrWhiteSpace(name)) _vm.RenameGroup(tab, name);
            }));
            items.Add(MenuFactory.Item("Remove tab from group", null, () => _vm.RemoveFromGroup(tab)));
            items.Add(MenuFactory.Item("Ungroup tabs", null, () => _vm.ClearGroup(tab)));
            items.Add(MenuFactory.Item("Close tab group", null, () => _vm.CloseGroup(tab)));
        }
        items.Add(new Separator());
        items.Add(MenuFactory.Item("Close tab", null, () => _vm.CloseTab(tab), "Ctrl+W"));
        items.Add(MenuFactory.Item("Close other tabs", null, () => _vm.CloseOtherTabs(tab),
            enabled: _vm.Tabs.Any(t => t != tab && !t.IsPinned)));
        int index = _vm.Tabs.IndexOf(tab);
        items.Add(MenuFactory.Item("Close tabs to the right", null, () => _vm.CloseTabsToRight(tab),
            enabled: _vm.Tabs.Skip(index + 1).Any(t => !t.IsPinned)));
        new ContextMenu { ItemsSource = items }.Open(sender as Control ?? TabBar);
        e.Handled = true;
    }

    // ---- Tab drag: reorder, group by hovering, move to the other pane, or tear off ----

    // ---- Files dragged onto a tab drop into that tab's folder ----------------
    private TabViewModel? _tabDropTarget;

    private void SetTabDropTarget(TabViewModel? tab)
    {
        if (ReferenceEquals(_tabDropTarget, tab)) return;
        if (_tabDropTarget is not null) _tabDropTarget.IsDropTarget = false;
        _tabDropTarget = tab;
        if (tab is not null) tab.IsDropTarget = true;
    }

    private static TabViewModel? TabUnder(DragEventArgs e, InputElement relativeTo) =>
        TabFromSource(relativeTo.InputHitTest(e.GetPosition(relativeTo)));

    private void TabBar_DragOver(object? sender, DragEventArgs e)
    {
        if (sender is not ListBox bar) return;
        var tab = TabUnder(e, bar);
        string? dest = tab is { IsFolderView: true, IsTrashView: false } ? tab.CurrentPath : null;
        e.DragEffects = FileDropService.EffectFor(DraggedPaths(e), dest, e.KeyModifiers);
        SetTabDropTarget(e.DragEffects == DragDropEffects.None ? null : tab);
        e.Handled = true;
    }

    private async void TabBar_Drop(object? sender, DragEventArgs e)
    {
        if (sender is not ListBox bar) return;
        var tab = TabUnder(e, bar);
        SetTabDropTarget(null);
        var files = DraggedPaths(e);
        string? dest = tab is { IsFolderView: true, IsTrashView: false } ? tab.CurrentPath : null;
        var effect = FileDropService.EffectFor(files, dest, e.KeyModifiers);
        if (files is null || dest is null || effect == DragDropEffects.None) return;
        e.Handled = true;
        await PerformDrop(files, dest, effect == DragDropEffects.Move);
    }

    // ===================== Selection / opening =====================

    private List<FileItem> SelectedItems() =>
        FileList.SelectedItems?.OfType<FileItem>().ToList() ?? new List<FileItem>();

    private List<string> SelectedPaths() => SelectedItems()
        .Where(i => i.HasPhysicalPath)
        .Select(i => i.FullPath)
        .ToList();

    private void FileList_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void FileList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Settings.SingleClickToOpen) return;
        if (ItemUnder(e.Source) is { } item && !item.IsEditing)
        {
            OpenItem(item);
            // After a double-click folder open, drop any selection the trailing click left behind.
            if (item.IsDirectory) Dispatcher.UIThread.Post(() => FileList.SelectedItems?.Clear(), DispatcherPriority.Background);
        }
    }

    private static FileItem? ItemUnder(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as FileItem;

    private void OpenItem(FileItem item, bool newTab = false)
    {
        if (_tab is null) return;
        if (item.IsDirectory && (newTab || Settings.OpenFoldersInNewTab))
        {
            _vm?.NewTab(item.FullPath, activate: true);
            return;
        }
        _tab.Open(item);
    }

    /// <summary>Open a folder in a new tab of this pane (used by the breadcrumb context menu).</summary>
    public void OpenInNewTab(string path) => _vm?.NewTab(path, activate: true);

    // ===================== Keyboard =====================

    /// <summary>Breadcrumb edit mode (Ctrl+L).</summary>
    public void BeginAddressEdit() => AddressBar.BeginEdit();

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Pane-level shortcuts, routed here by the window for the active pane.</summary>
    public bool HandleGlobalKey(KeyEventArgs e)
    {
        if (_tab is null) return false;
        var mods = e.KeyModifiers;
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        bool shift = mods.HasFlag(KeyModifiers.Shift);
        bool alt = mods.HasFlag(KeyModifiers.Alt);
        bool inText = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

        if (alt && !ctrl)
        {
            switch (e.Key)
            {
                case Key.Left: _tab.BackCommand.Execute(null); return true;
                case Key.Right: _tab.ForwardCommand.Execute(null); return true;
                case Key.Up: _tab.UpCommand.Execute(null); return true;
                case Key.Home: _ = _tab.NavigateAsync(XdgDirs.Home, true); return true;
                case Key.Enter when !inText: OpenSelectedProperties(); return true;
                case Key.D: BeginAddressEdit(); return true;
            }
        }
        switch (e.Key)
        {
            case Key.F5:
            case Key.R when ctrl && !shift:
                _tab.RefreshCommand.Execute(null);
                return true;
            case Key.L when ctrl:
            case Key.F6:
                BeginAddressEdit();
                return true;
            case Key.F when ctrl:
                FocusSearch();
                return true;
            case Key.F3:
                MainVm?.ToggleSplitCommand.Execute(null);
                return true;
            case Key.N when ctrl && shift:
                _ = CreateNew(folder: true);
                return true;
            case Key.F4 when shift:
                OpenTerminalHere();
                return true;
            case Key.F10 when shift && !inText:
            case Key.Apps when !inText:
                FileList.Focus();
                _fileMenu.Open(FileList);
                return true;
        }

        if (inText)
        {
            if (e.Key == Key.Escape && ReferenceEquals(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement(), SearchBox))
            {
                _tab.Filter = string.Empty;
                FileList.Focus();
                return true;
            }
            return false;
        }

        if (ctrl && !alt)
        {
            switch (e.Key)
            {
                case Key.C when shift: CopyPaths(); return true;
                case Key.C: _ = ClipboardPut(cut: false); return true;
                case Key.X: _ = ClipboardPut(cut: true); return true;
                case Key.V: _ = PasteFromClipboard(); return true;
                case Key.A: FileList.SelectAll(); return true;
            }
            return false;
        }

        if (alt) return false;
        switch (e.Key)
        {
            case Key.Enter:
                var selected = SelectedItems();
                if (selected.Count == 1) OpenItem(selected[0]);
                else foreach (var item in selected.Where(i => i.IsDirectory)) OpenItem(item, newTab: true);
                return selected.Count > 0;
            case Key.Delete:
                _ = DeleteSelected(forcePermanent: shift);
                return true;
            case Key.F2:
                _ = BeginRename();
                return true;
            case Key.Back:
                _tab.UpCommand.Execute(null);
                return true;
            case Key.Space when !shift:
                Settings.ShowPreviewPane = !Settings.ShowPreviewPane;
                return true;
            case Key.Escape when FileList.SelectedItems?.Count > 0:
                FileList.SelectedItems?.Clear();
                return true;
        }
        return false;
    }

    private void FileList_KeyDown(object? sender, KeyEventArgs e)
    {
        // Arrow keys in the inline rename box must not move the list selection.
        if (e.Source is TextBox && e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            e.Handled = e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown;
    }

    // ===================== File context menu (one menu, enabled per selection) =====================

    private void BuildFileContextMenu()
    {
        BuildArchiveMenu();
        _menuRestore = MenuFactory.Item("Restore", "undo", () => _ = RunTrashViewOperation(SelectedPaths(), restore: true),
            iconBrush: "AccentBright");
        _restoreSeparator = new Separator();
        _menuOpen = MenuFactory.Item("Open", "arrow-right", () =>
        {
            if (FileList.SelectedItem is FileItem item) OpenItem(item);
        }, iconBrush: "AccentBright");
        _menuOpen.FontWeight = FontWeight.SemiBold;
        _menuOpenNewTab = MenuFactory.Item("Open in new tab", "plus", () =>
        {
            foreach (var d in SelectedItems().Where(i => i.IsDirectory)) OpenItem(d, newTab: true);
        });
        _menuOpenWith = MenuFactory.Item("Open with…", "external-link", () => _ = OpenWith());
        _menuOpenTerminal = MenuFactory.Item("Open in Terminal", "terminal", () => OpenTerminalHere());
        _menuCut = MenuFactory.Item("Cut", "scissors", () => _ = ClipboardPut(cut: true), "Ctrl+X");
        _menuCopy = MenuFactory.Item("Copy", "copy", () => _ = ClipboardPut(cut: false), "Ctrl+C");
        _menuCopyPath = MenuFactory.Item("Copy as path", "hash", CopyPaths);
        _menuPaste = MenuFactory.Item("Paste", "clipboard", () =>
        {
            var only = SelectedItems() is [{ IsDirectory: true } dir] ? dir.FullPath : null;
            _ = PasteFromClipboard(only);
        }, "Ctrl+V");
        _menuShortcut = MenuFactory.Item("Create link", "link", () => _ = CreateLinks());
        _menuPin = MenuFactory.Item("Pin to Quick Access", "star", () => { }, iconBrush: "AccentBright");
        _menuRename = MenuFactory.Item("Rename", "pencil", () => _ = BeginRename(), "F2");
        _menuDelete = MenuFactory.Item("Delete", "trash", () => _ = DeleteSelected(), "Delete", iconBrush: "Danger");
        _menuNewFolder = MenuFactory.Item("New folder", "folder-plus", () => _ = CreateNew(folder: true), iconBrush: "AccentBright");
        _menuNewFile = MenuFactory.Item("New text file", "file-plus", () => _ = CreateNew(folder: false));
        _menuProperties = MenuFactory.Item("Properties", "info", OpenSelectedProperties, "Alt+Enter");
        _menuShowMore = MenuFactory.Submenu("Show more options", "list", Array.Empty<Control>());

        _fileMenu = new ContextMenu();
        foreach (Control item in new Control[]
                 {
                     _menuRestore, _restoreSeparator,
                     _menuOpen, _menuOpenNewTab, _menuOpenWith, _menuOpenTerminal,
                     _archiveSeparator, _menuArchiveOpen, _menuExtract, _menuCompress,
                     new Separator(),
                     _menuCut, _menuCopy, _menuCopyPath, _menuPaste,
                     new Separator(),
                     _menuShortcut, _menuPin,
                     new Separator(),
                     _menuRename, _menuDelete,
                     new Separator(),
                     _menuNewFolder, _menuNewFile,
                     new Separator(),
                     _menuProperties,
                     new Separator(),
                     _menuShowMore,
                 })
            _fileMenu.Items.Add(item);
        _fileMenu.Opening += FileContextMenu_Opening;
        FileList.ContextMenu = _fileMenu;
    }

    private async void FileContextMenu_Opening(object? sender, CancelEventArgs e)
    {
        var items = SelectedItems();
        int n = items.Count;
        bool has = n > 0;
        bool trash = _tab?.IsTrashView == true;
        bool physical = _tab?.CanUsePhysicalFileCommands == true && items.All(item => item.HasPhysicalPath);
        bool singleDir = n == 1 && items[0].IsDirectory;
        bool singleFile = n == 1 && !items[0].IsDirectory;

        _menuRestore.IsVisible = trash;
        _restoreSeparator.IsVisible = trash;
        _menuRestore.IsEnabled = trash && has;

        _menuOpen.IsEnabled = n == 1;
        _menuOpenNewTab.IsEnabled = singleDir;
        _menuOpenWith.IsEnabled = physical && singleFile && !trash;
        _menuOpenTerminal.IsEnabled = physical && !trash;
        UpdateArchiveMenu(items, physical, trash);

        BuildPinMenu(items.Where(i => i.IsDirectory && i.HasPhysicalPath).ToList());
        _menuCut.IsEnabled = physical && has && !trash;
        _menuCopy.IsEnabled = physical && has;
        _menuCopyPath.IsEnabled = has && items.All(item => item.HasPhysicalPath);
        _menuPaste.IsEnabled = physical && !trash && await ClipboardService.HasFilesAsync(TopLevel.GetTopLevel(this));
        _menuShortcut.IsEnabled = physical && has && !trash;
        _menuRename.IsEnabled = physical && n == 1 && !trash;
        _menuDelete.IsEnabled = physical && has;
        _menuDelete.Header = trash ? "Delete permanently" : "Delete";
        _menuNewFolder.IsEnabled = physical && !trash;
        _menuNewFile.IsEnabled = physical && !trash;
        _menuProperties.IsEnabled = physical && n <= 1;
        _menuShowMore.IsEnabled = physical;
        _menuShowMore.ItemsSource = BuildShowMoreItems(items, trash);
    }

    /// <summary>Linux extras that the Windows build reaches through the native shell menu.</summary>
    private List<Control> BuildShowMoreItems(List<FileItem> items, bool trash)
    {
        var list = new List<Control>();
        if (items.Count > 0)
        {
            list.Add(MenuFactory.Item(trash ? "Delete permanently" : "Delete permanently", "trash",
                () => _ = DeleteSelected(forcePermanent: true), "Shift+Delete", iconBrush: "Danger"));
            list.Add(MenuFactory.Item(items.Count == 1 ? "Copy name" : "Copy names", "copy", () =>
                _ = ClipboardService.SetTextAsync(TopLevel.GetTopLevel(this), string.Join('\n', items.Select(i => i.Name)))));
            if (items.All(i => !i.IsDirectory) && !trash)
            {
                bool allExecutable = items.All(i => IsExecutable(i.FullPath));
                list.Add(MenuFactory.Item(allExecutable ? "Remove execute permission" : "Allow executing as program",
                    "terminal", () => ToggleExecutable(items, !allExecutable)));
            }
        }
        if (_tab?.IsFolderView == true)
        {
            if (list.Count > 0) list.Add(new Separator());
            list.Add(MenuFactory.Check("Show hidden files", Settings.ShowHiddenFiles,
                () => Settings.ShowHiddenFiles = !Settings.ShowHiddenFiles));
            list.Add(MenuFactory.Item("Refresh", "refresh", () => _tab.RefreshCommand.Execute(null), "F5"));
            if (trash) list.Add(MenuFactory.Item("Empty trash…", "trash", EmptyTrash, iconBrush: "Danger"));
        }
        return list;
    }

    private static bool IsExecutable(string path)
    {
        try { return (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0; }
        catch { return false; }
    }

    private void ToggleExecutable(List<FileItem> items, bool executable)
    {
        const UnixFileMode exec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        foreach (var item in items)
        {
            try
            {
                var mode = File.GetUnixFileMode(item.FullPath);
                // Only grant execute where read is already granted, like chmod +x respecting umask.
                var grant = exec & (UnixFileMode)((int)(mode & (UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead)) >> 2);
                File.SetUnixFileMode(item.FullPath, executable ? mode | grant : mode & ~exec);
            }
            catch (Exception ex) { SetStatus($"⚠️ {ex.Message}"); }
        }
        _ = _tab?.ReloadAfterOperationAsync();
    }

    private void BuildPinMenu(IReadOnlyList<FileItem> dirs)
    {
        foreach (var old in _dynamicPinItems) _fileMenu.Items.Remove(old);
        _dynamicPinItems.Clear();
        _menuPin.ItemsSource = null;

        var targets = MainViewModel.PinTargets();
        if (targets.Count == 0)
        {
            _menuPin.Header = "No sidebar lists";
            _menuPin.IsEnabled = false;
            return;
        }

        bool useSubmenu = targets.Count(t => t.Key != "quick") > 2;
        if (useSubmenu)
        {
            _menuPin.Header = "Pin to sidebar";
            _menuPin.IsEnabled = dirs.Count > 0;
            _menuPin.ItemsSource = targets.Select(t => (Control)CreatePinMenuItem(t, dirs)).ToList();
            return;
        }

        var primary = targets.FirstOrDefault(t => t.Key == "quick") ?? targets[0];
        ConfigurePinMenuItem(_menuPin, primary, dirs);
        int insert = _fileMenu.Items.IndexOf(_menuPin) + 1;
        foreach (var target in targets.Where(t => t.Key != primary.Key))
        {
            var item = CreatePinMenuItem(target, dirs);
            _dynamicPinItems.Add(item);
            _fileMenu.Items.Insert(insert++, item);
        }
    }

    private MenuItem CreatePinMenuItem(MainViewModel.SidebarPinTarget target, IReadOnlyList<FileItem> dirs)
    {
        var item = MenuFactory.Item("", "star", () => { }, iconBrush: "AccentBright");
        ConfigurePinMenuItem(item, target, dirs);
        return item;
    }

    private Dictionary<MenuItem, EventHandler<RoutedEventArgs>> _pinHandlers = new();

    private void ConfigurePinMenuItem(MenuItem item, MainViewModel.SidebarPinTarget target, IReadOnlyList<FileItem> dirs)
    {
        bool allPinned = dirs.Count > 0 && dirs.All(d => MainViewModel.IsPinnedTo(target.Key, d.FullPath));
        item.Header = allPinned ? $"Unpin from {target.Name}" : $"Pin to {target.Name}";
        item.IsEnabled = dirs.Count > 0;
        if (_pinHandlers.TryGetValue(item, out var old)) item.Click -= old;
        EventHandler<RoutedEventArgs> handler = (_, _) =>
        {
            foreach (var dir in dirs)
            {
                if (allPinned) MainViewModel.UnpinFrom(target.Key, dir.FullPath);
                else MainViewModel.PinTo(target.Key, dir.FullPath);
            }
        };
        _pinHandlers[item] = handler;
        item.Click += handler;
    }

    // ===================== Clipboard =====================

    private async Task ClipboardPut(bool cut)
    {
        if (_tab?.CanUsePhysicalFileCommands != true) return;
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        await ClipboardService.SetFilesAsync(TopLevel.GetTopLevel(this), paths, cut);
    }

    private void OnClipboardChanged() => Dispatcher.UIThread.Post(ApplyCutMarks);

    private void ApplyCutMarks()
    {
        if (_tab is null) return;
        var cut = ClipboardService.CutPaths.ToHashSet(PathUtil.Comparer);
        foreach (var item in _tab.Items) item.IsCut = cut.Contains(item.FullPath);
    }

    private async Task PasteFromClipboard(string? targetDir = null)
    {
        if (_tab is null || !_tab.CanUsePhysicalFileCommands || _tab.IsTrashView) return;
        var tab = _tab;
        var (files, cut) = await ClipboardService.GetFilesAsync(TopLevel.GetTopLevel(this));
        if (files.Count == 0) return;
        string destination = targetDir ?? tab.CurrentPath;
        tab.BeginKnownFileOperation();
        string? err = await FileDropService.Perform(files.ToArray(), destination, cut, OwnerWindow);
        if (err is not null) SetStatus($"⚠️ {err}");
        else if (cut) await ClipboardService.ClearAfterCutAsync(TopLevel.GetTopLevel(this));
        _ = tab.ReloadAfterOperationAsync();
    }

    private void CopyPaths()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0 && _tab is { IsFolderView: true }) paths.Add(_tab.CurrentPath);
        if (paths.Count == 0) return;
        _ = ClipboardService.SetTextAsync(TopLevel.GetTopLevel(this), string.Join('\n', paths.Select(p => $"\"{p}\"")));
    }

    // ===================== Open with / links / terminal / properties =====================

    private async Task OpenWith()
    {
        if (FileList.SelectedItem is not FileItem { IsDirectory: false } item) return;
        string? error = await OpenWithDialog.ShowFor(OwnerWindow, item.FullPath);
        if (error is not null) SetStatus($"⚠️ {error}");
        else RecentsStore.Instance.Add(item.FullPath, isDirectory: false);
    }

    /// <summary>Linux counterpart of "Create shortcut": a symbolic link beside each item.</summary>
    private async Task CreateLinks()
    {
        if (_tab is null) return;
        var tab = _tab;
        var created = new List<string>();
        string? error = null;
        foreach (string path in SelectedPaths())
        {
            try
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                string ext = Path.GetExtension(path);
                if (Directory.Exists(path)) { stem = PathUtil.DisplayName(path); ext = string.Empty; }
                string link = FileOperationsService.UniquePath(Path.Combine(tab.CurrentPath, $"{stem} - Link{ext}"));
                File.CreateSymbolicLink(link, path);
                created.Add(link);
            }
            catch (Exception ex) { error ??= ex.Message; }
        }
        if (created.Count > 0)
            UndoService.Instance.Push(new TrashAction(created, created.Count == 1 ? "Create link" : $"Create link ({created.Count} items)"));
        if (error is not null) SetStatus($"⚠️ {error}");
        await tab.ReloadAfterOperationAsync();
    }

    private void OpenSelectedProperties()
    {
        if (_tab is null) return;
        var selected = SelectedItems();
        string? path = selected.Count == 1 ? selected[0].FullPath : _tab.IsFolderView ? _tab.CurrentPath : null;
        if (path is not null) _ = ShowProperties(path);
    }

    private async Task ShowProperties(string path)
    {
        var tab = _tab;
        bool changed = await PropertiesDialog.ShowFor(OwnerWindow, path);
        if (changed && tab is not null) await tab.ReloadAfterOperationAsync();
    }

    private void OpenTerminalHere(string? dir = null)
    {
        dir ??= SelectedItems() is [{ IsDirectory: true } only] ? only.FullPath : _tab?.CurrentPath;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = XdgDirs.Home;
        if (!TerminalLauncher.TryOpen(dir, out string? error)) SetStatus($"⚠️ {error}");
    }

    // ===================== Delete / trash =====================

    private async Task DeleteSelected(bool forcePermanent = false)
    {
        if (_tab is null || !_tab.CanUsePhysicalFileCommands || _fileOperationInProgress) return;
        var tab = _tab;
        var paths = SelectedPaths();
        if (paths.Count == 0) return;

        string what = paths.Count == 1 ? $"“{PathUtil.DisplayName(paths[0])}”" : $"{paths.Count} items";

        // Inside the trash, "delete" can only mean permanent removal.
        if (tab.IsTrashView)
        {
            if (!await ConfirmDialog.Ask(OwnerWindow, "Permanently delete",
                    $"Permanently delete {what}? This can't be undone.",
                    "Delete permanently", "Cancel", danger: true))
                return;
            await RunTrashViewOperation(paths, restore: false);
            return;
        }

        bool permanent;
        if (forcePermanent) permanent = true;
        else
        {
            switch (Settings.DeleteBehavior)
            {
                case DeleteBehavior.Recycle: permanent = false; break;
                case DeleteBehavior.Permanent: permanent = true; break;
                default:
                    var choice = await DeleteDialog.Ask(OwnerWindow, $"What should happen to {what}?");
                    if (choice == DeleteChoice.Cancel) return;
                    permanent = choice == DeleteChoice.Permanent;
                    break;
            }
        }

        // Every permanent-delete path gets one Rain-styled confirmation.
        if (permanent && !await ConfirmDialog.Ask(OwnerWindow, "Permanently delete",
                $"Permanently delete {what}? This can't be undone.",
                "Delete permanently", "Cancel", danger: true))
            return;

        var act = Activity.Begin(permanent ? "Permanently deleted" : "Moved to Trash", Summarize(paths), "trash");
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

        // Keep the current collection in sync locally instead of a full reload.
        tab.ApplyLocalDelete(res.Completed);
        Activity.Complete(act, res.Ok, res.Error);
        if (!res.Ok)
        {
            SetStatus($"⚠️ {res.Error}");
            if (!permanent && res.Completed.Count == 0
                && await ConfirmDialog.Ask(OwnerWindow, "Can't move to Trash",
                    $"{res.Error}\n\nDelete {what} permanently instead?", "Delete permanently", "Cancel", danger: true))
            {
                var retry = await Task.Run(() => _ops.DeletePermanent(paths));
                tab.ApplyLocalDelete(retry.Completed);
                if (!retry.Ok) SetStatus($"⚠️ {retry.Error}");
            }
        }
        // Only a trash delete is undoable; a permanent delete can't be restored.
        if (!permanent && res.Completed.Count > 0)
            UndoService.Instance.Push(new RestoreFromTrashAction(
                res.Completed, res.Completed.Count == 1 ? "Delete" : $"Delete ({res.Completed.Count} items)"));
    }

    private async Task RunTrashViewOperation(IReadOnlyList<string> payloads, bool restore)
    {
        if (_tab is null) return;
        var tab = _tab;
        var act = Activity.Begin(restore ? "Restored from Trash" : "Permanently deleted", Summarize(payloads),
            restore ? "undo" : "trash");
        tab.BeginKnownFileOperation();
        var (done, error) = await Task.Run(() =>
        {
            var completed = new List<string>();
            string? firstError = null;
            foreach (string payload in payloads)
            {
                string? err = restore ? TrashService.RestoreEntry(payload).error : TrashService.DeleteEntry(payload);
                if (err is null) completed.Add(payload);
                else firstError ??= err;
            }
            return (completed, firstError);
        });
        tab.ApplyLocalDelete(done);
        Activity.Complete(act, error is null, error);
        if (error is not null) SetStatus($"⚠️ {error}");
        MainVm?.RefreshAll(afterKnownOperation: true);
    }

    /// <summary>Permanently delete everything in the trash after a confirmation.</summary>
    public async void EmptyTrash()
    {
        if (TrashService.HomeTrashIsEmpty())
        {
            SetStatus("The Trash is already empty.");
            return;
        }
        if (!await ConfirmDialog.Ask(OwnerWindow, "Empty Trash",
                "Permanently delete everything in the Trash? This can't be undone.",
                "Empty Trash", "Cancel", danger: true))
            return;
        var act = Activity.Begin("Emptied Trash", "All items", "trash");
        string? error = await Task.Run(TrashService.EmptyHomeTrash);
        Activity.Complete(act, error is null, error);
        if (error is not null) SetStatus($"⚠️ {error}");
        MainVm?.RefreshAll(afterKnownOperation: true);
    }

    // ===================== Rename =====================

    private async Task<bool> AllowRename(string oldName, string newName, bool isDir)
    {
        if (isDir || !Settings.WarnOnExtensionChange) return true;
        string oldExt = Path.GetExtension(oldName);
        string newExt = Path.GetExtension(newName);
        if (string.Equals(oldExt, newExt, StringComparison.Ordinal)) return true;
        string from = string.IsNullOrEmpty(oldExt) ? "(none)" : oldExt;
        string to = string.IsNullOrEmpty(newExt) ? "(none)" : newExt;
        return await ConfirmDialog.Ask(OwnerWindow, "Change file extension",
            $"Changing the extension from {from} to {to} might make the file unusable. Continue?",
            "Change", "Cancel", danger: true);
    }

    private async Task BeginRename()
    {
        if (_tab is null || !_tab.CanUsePhysicalFileCommands || _fileOperationInProgress || _tab.IsTrashView) return;
        var selected = SelectedItems();
        if (selected.Count != 1) return;
        var tab = _tab;
        var item = selected[0];

        if (Settings.RenameMode == RenameMode.Inline)
        {
            StartInlineEdit(item);
            return;
        }

        int selectionLength = RenameSelection.GetSelectionLength(item.Name, item.IsDirectory,
            Settings.SelectFileNameWithoutExtensionOnRename);
        string? newName = await InputDialog.Ask(OwnerWindow, "Rename", "New name:", item.Name, selectionLength);
        if (newName is null || newName == item.Name) return;
        await CommitRename(tab, item, newName);
    }

    private void StartInlineEdit(FileItem item)
    {
        if (_tab is null) return;
        item.EditName = item.Name;
        _tab.BeginInlineEdit();
        item.IsEditing = true;
        FileList.ScrollIntoView(item);
        Dispatcher.UIThread.Post(() =>
        {
            if (FileList.ContainerFromItem(item) is not { } container) return;
            var box = container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsVisible);
            if (box is null) return;
            box.Focus();
            box.SelectionStart = 0;
            box.SelectionEnd = RenameSelection.GetSelectionLength(item.Name, item.IsDirectory,
                Settings.SelectFileNameWithoutExtensionOnRename);
        }, DispatcherPriority.Loaded);
    }

    private async void InlineBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FileItem item }) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndInlineEdit(item, commit: false);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await FinishInlineEdit(item);
        }
    }

    private async void InlineBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: FileItem { IsEditing: true } item }) await FinishInlineEdit(item);
    }

    private async Task FinishInlineEdit(FileItem item)
    {
        if (!item.IsEditing || _tab is null) return;
        string newName = item.EditName.Trim();
        var tab = _tab;
        EndInlineEdit(item, commit: true);
        if (newName.Length == 0 || newName == item.Name) return;
        await CommitRename(tab, item, newName);
    }

    private void EndInlineEdit(FileItem item, bool commit)
    {
        item.IsEditing = false;
        if (!commit) item.EditName = item.Name;
        _tab?.EndInlineEdit();
        FileList.Focus();
    }

    private async Task CommitRename(TabViewModel tab, FileItem item, string newName)
    {
        if (!await AllowRename(item.Name, newName, item.IsDirectory)) return;
        var act = Activity.Begin("Rename", $"{item.Name} → {newName}", "pencil");
        string oldPath = item.FullPath;
        tab.BeginKnownFileOperation();
        _fileOperationInProgress = true;
        string? err;
        try { err = await Task.Run(() => _ops.Rename(oldPath, newName)); }
        finally { _fileOperationInProgress = false; }
        Activity.Complete(act, err is null, err);
        if (err is not null)
        {
            SetStatus($"⚠️ {err}");
            return;
        }
        string newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, newName);
        UndoService.Instance.Push(new RenameAction(newPath, oldPath));
        tab.ApplyLocalRename(item, newPath);
        FileList.SelectedItem = item;
    }

    // ===================== New folder / file =====================

    private async Task CreateNew(bool folder)
    {
        if (_tab is null || !_tab.CanUsePhysicalFileCommands || _tab.IsTrashView) return;
        var tab = _tab;
        string defaultName = folder ? "New folder" : "New text file.txt";

        string? name = defaultName;
        if (Settings.RenameMode == RenameMode.Dialog)
        {
            int select = folder ? defaultName.Length : RenameSelection.GetSelectionLength(defaultName, false, true);
            name = await InputDialog.Ask(OwnerWindow, folder ? "New Folder" : "New Text File",
                folder ? "Folder name:" : "File name:", defaultName, select);
            if (name is null) return;
        }

        var act = Activity.Begin(folder ? "New folder" : "New file", name, folder ? "folder-plus" : "file-plus");
        tab.BeginKnownFileOperation();
        var (err, created) = folder ? _ops.CreateFolder(tab.CurrentPath, name) : _ops.CreateFile(tab.CurrentPath, name);
        Activity.Complete(act, err is null, err);
        if (err is not null)
        {
            SetStatus($"⚠️ {err}");
            return;
        }
        if (created is null) return;
        act.Detail = PathUtil.DisplayName(created);
        UndoService.Instance.Push(new TrashAction(new[] { created }, folder ? "New folder" : "New file"));
        await tab.ReloadAfterOperationAsync();
        if (tab.Find(created) is { } item)
        {
            FileList.SelectedItems?.Clear();
            FileList.SelectedItem = item;
            FileList.ScrollIntoView(item);
            if (Settings.RenameMode == RenameMode.Inline) StartInlineEdit(item);
        }
    }

    // ===================== Pointer: drag out, marquee, right-click selection =====================

    private void FileList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(FileList);
        var item = ItemUnder(e.Source);
        _pendingSingleSelect = null;

        // Right-click selects the row under the pointer first (or clears on empty space).
        if (point.Properties.IsRightButtonPressed)
        {
            if (item is null) FileList.SelectedItems?.Clear();
            else if (FileList.SelectedItems?.Contains(item) != true)
            {
                FileList.SelectedItems?.Clear();
                FileList.SelectedItem = item;
            }
            return;
        }
        if (point.Properties.IsMiddleButtonPressed && item is { IsDirectory: true } folder)
        {
            OpenItem(folder, newTab: true);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed || e.Source is TextBox) return;

        if (item is null)
        {
            // Empty space: start a rubber-band selection.
            if (e.Source is Visual v && v.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null) return;
            BeginMarquee(point.Position, e);
            e.Handled = true;
            return;
        }

        _dragCandidate = item;
        _dragStart = point.Position;
        // A plain press on a row that is part of a multi-selection keeps the selection so the
        // whole set can be dragged; it collapses to this row on release if no drag happened.
        if (e.KeyModifiers == KeyModifiers.None && FileList.SelectedItems?.Count > 1
            && FileList.SelectedItems.Contains(item))
        {
            _pendingSingleSelect = item;
            e.Handled = true;
        }
    }

    private void FileList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_marqueeing)
        {
            EndMarquee();
            e.Handled = true;
            return;
        }
        _dragStart = null;
        _dragCandidate = null;

        if (_pendingSingleSelect is { } single)
        {
            _pendingSingleSelect = null;
            FileList.SelectedItems?.Clear();
            FileList.SelectedItem = single;
            return;
        }

        if (!Settings.SingleClickToOpen || e.InitialPressMouseButton != MouseButton.Left) return;
        if (e.KeyModifiers != KeyModifiers.None) return;   // let Ctrl/Shift extend selection
        if (ItemUnder(e.Source) is not { IsEditing: false } item) return;
        if (FileList.SelectedItems?.Count > 1) return;      // don't hijack multi-select
        OpenItem(item);
    }

    private async void FileList_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_marqueeing)
        {
            UpdateMarquee(e.GetPosition(FileList));
            return;
        }
        if (_dragStart is not { } start || _dragCandidate is null || _tab?.CanUsePhysicalFileCommands != true) return;
        var point = e.GetCurrentPoint(FileList);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _dragStart = null;
            return;
        }
        Vector delta = point.Position - start;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;

        var candidate = _dragCandidate;
        _dragStart = null;
        _dragCandidate = null;
        _pendingSingleSelect = null;
        var paths = SelectedItems().Contains(candidate) ? SelectedPaths() : new List<string> { candidate.FullPath };
        if (paths.Count == 0) return;

        var data = new DataObject();
        data.Set(InternalDragFormat, string.Join('\n', paths));
        data.Set("text/uri-list", Encoding.UTF8.GetBytes(string.Concat(paths.Select(p => new Uri(p).AbsoluteUri + "\r\n"))));
        data.Set(DataFormats.Text, string.Join('\n', paths));
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is { } storage)
            {
                var storageItems = new List<IStorageItem>();
                foreach (string path in paths)
                {
                    IStorageItem? storageItem = Directory.Exists(path)
                        ? await storage.TryGetFolderFromPathAsync(path)
                        : await storage.TryGetFileFromPathAsync(path);
                    if (storageItem is not null) storageItems.Add(storageItem);
                }
                if (storageItems.Count > 0) data.Set(DataFormats.Files, storageItems);
            }
        }
        catch { /* external drag targets just won't get the storage-item format */ }

        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            SetStatus($"⚠️ {ex.Message}");
        }
        finally
        {
            SetDropContainer(null);
        }
    }

    private void BeginMarquee(Point start, PointerPressedEventArgs e)
    {
        _marqueeing = true;
        _marqueeStart = start;
        bool extend = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (!extend) FileList.SelectedItems?.Clear();
        _marqueeBaseSelection = extend ? SelectedItems().ToHashSet() : new HashSet<FileItem>();
        FileList.Focus();
        e.Pointer.Capture(FileList);
        Canvas.SetLeft(Marquee, start.X);
        Canvas.SetTop(Marquee, start.Y);
        Marquee.Width = 0;
        Marquee.Height = 0;
    }

    private void UpdateMarquee(Point current)
    {
        double x = Math.Min(_marqueeStart.X, current.X), y = Math.Min(_marqueeStart.Y, current.Y);
        double w = Math.Abs(current.X - _marqueeStart.X), h = Math.Abs(current.Y - _marqueeStart.Y);
        if (w < 3 && h < 3) return;
        Marquee.IsVisible = true;
        Canvas.SetLeft(Marquee, x);
        Canvas.SetTop(Marquee, y);
        Marquee.Width = w;
        Marquee.Height = h;

        var rect = new Rect(x, y, w, h);
        var selection = FileList.SelectedItems;
        if (selection is null) return;
        foreach (var container in FileList.GetRealizedContainers())
        {
            if (container.DataContext is not FileItem item) continue;
            var topLeft = container.TranslatePoint(new Point(0, 0), FileList);
            if (topLeft is null) continue;
            var bounds = new Rect(topLeft.Value, container.Bounds.Size);
            bool hit = bounds.Intersects(rect) || (_marqueeBaseSelection?.Contains(item) ?? false);
            bool selected = selection.Contains(item);
            if (hit && !selected) selection.Add(item);
            else if (!hit && selected) selection.Remove(item);
        }
    }

    private void EndMarquee()
    {
        if (!_marqueeing) return;
        _marqueeing = false;
        _marqueeBaseSelection = null;
        Marquee.IsVisible = false;
    }

    // ===================== Drop into the list =====================

    /// <summary>Local paths carried by a drag from Rain, Dolphin, Nautilus or a browser.</summary>
    internal static string[]? DraggedPaths(DragEventArgs e)
    {
        if (e.Data.Get(InternalDragFormat) is string own)
            return own.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var files = e.Data.GetFiles()?
            .Select(f => f.TryGetLocalPath())
            .OfType<string>()
            .ToArray();
        if (files is { Length: > 0 }) return files;
        if (e.Data.Get("text/uri-list") is { } raw)
        {
            string? text = raw switch { byte[] b => Encoding.UTF8.GetString(b), string s => s, _ => null };
            var paths = text?.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => !l.StartsWith('#'))
                .Select(l => Uri.TryCreate(l.Trim(), UriKind.Absolute, out Uri? u) && u.IsFile ? u.LocalPath : null)
                .OfType<string>()
                .ToArray();
            if (paths is { Length: > 0 }) return paths;
        }
        return null;
    }

    private string? DropTargetDir(DragEventArgs e, out ListBoxItem? container)
    {
        container = null;
        if (_tab?.CanUsePhysicalFileCommands != true || _tab.IsTrashView) return null;
        if (FileList.InputHitTest(e.GetPosition(FileList)) is Visual hit
            && hit.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: FileItem { IsDirectory: true } folder } item)
        {
            container = item;
            return folder.FullPath;
        }
        return _tab.CurrentPath;
    }

    private void FileList_DragOver(object? sender, DragEventArgs e)
    {
        string? dest = DropTargetDir(e, out var container);
        e.DragEffects = FileDropService.EffectFor(DraggedPaths(e), dest, e.KeyModifiers);
        SetDropContainer(e.DragEffects == DragDropEffects.None ? null : container);
        e.Handled = true;
    }

    private void FileList_DragLeave(object? sender, DragEventArgs e) => SetDropContainer(null);

    private async void FileList_Drop(object? sender, DragEventArgs e)
    {
        string? dest = DropTargetDir(e, out _);
        SetDropContainer(null);
        var files = DraggedPaths(e);
        var effect = FileDropService.EffectFor(files, dest, e.KeyModifiers);
        if (files is null || dest is null || effect == DragDropEffects.None) return;
        e.Handled = true;
        await PerformDrop(files, dest, effect == DragDropEffects.Move);
    }

    private async Task PerformDrop(string[] files, string dest, bool move)
    {
        _tab?.BeginKnownFileOperation();
        string? err = await FileDropService.Perform(files, dest, move, OwnerWindow);
        if (err is not null) SetStatus($"⚠️ {err}");
        MainVm?.RefreshAll(afterKnownOperation: true);
    }

    private void SetDropContainer(ListBoxItem? container)
    {
        if (ReferenceEquals(_dropContainer, container)) return;
        _dropContainer?.Classes.Set("drop", false);
        _dropContainer = container;
        _dropContainer?.Classes.Set("drop", true);
    }

    // ===================== Helpers =====================

    private void SetStatus(string msg)
    {
        if (_tab is not null) _tab.Status = msg;
    }

    private static string Summarize(IReadOnlyList<string> paths) =>
        paths.Count == 1 ? PathUtil.DisplayName(paths[0]) : $"{paths.Count} items";

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LoadedPaneViews.Add(this);
        _selectionWindow = OwnerWindow;
        _selectionWindow?.AddHandler(PointerPressedEvent, FileSelection_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Settings.PropertyChanged -= OnSettingChanged;
        Settings.PropertyChanged += OnSettingChanged;
        ClipboardService.Changed -= OnClipboardChanged;
        ClipboardService.Changed += OnClipboardChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LoadedPaneViews.Remove(this);
        _selectionWindow?.RemoveHandler(PointerPressedEvent, FileSelection_PointerPressed);
        _selectionWindow = null;
        if (ActiveTabDragOwner?._tabDragReceiver == this) ActiveTabDragOwner.EndTabDrag(cancelled: true);
        Settings.PropertyChanged -= OnSettingChanged;
        ClipboardService.Changed -= OnClipboardChanged;
        EndTabDrag(cancelled: true);
        ClearTabPreviewMotions();
        Motion.Cancel(FileList);
        Motion.Cancel(NewTabButton);
        foreach (var ghost in TabAnimationLayer.Children.OfType<Image>())
        {
            Motion.Cancel(ghost);
            (ghost.Source as IDisposable)?.Dispose();
        }
        TabAnimationLayer.Children.Clear();
        foreach (var bar in new[] { TabBar, GroupTabBar })
            foreach (var container in bar.GetRealizedContainers()) Motion.Cancel(container);
    }
}
