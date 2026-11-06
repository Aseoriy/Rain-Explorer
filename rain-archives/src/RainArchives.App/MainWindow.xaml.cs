using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using RainArchives.App.Infrastructure;
using RainArchives.App.Services;
using RainArchives.App.ViewModels;
using RainArchives.Contracts;

namespace RainArchives.App;

public partial class MainWindow : Window
{
    private const double MinimumInspectorWidth = 320;
    private const double MaximumInspectorWidth = 380;
    private const double InspectorWidthRatio = 0.36;
    private readonly MainWindowViewModel _viewModel;
    private readonly AppThemeManager? _themeManager;

    public MainWindow()
    {
        _themeManager = (Application.Current as App)?.ThemeManager;
        _themeManager?.Initialize();
        InitializeComponent();
        _viewModel = new MainWindowViewModel(new AppDialogService(this));
        _viewModel.Browser.FocusRequested += Browser_FocusRequested;
        _viewModel.PreviewFocusRequested += ViewModel_PreviewFocusRequested;
        _viewModel.PropertiesFocusRequested += ViewModel_PropertiesFocusRequested;
        DataContext = _viewModel;
        PreviewPanel.Width = CalculateInspectorWidth(Width);
        UpdateThemeMenuState();
        if (_themeManager is not null)
        {
            _themeManager.ThemeChanged += ThemeManager_ThemeChanged;
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _viewModel.Browser.FocusRequested -= Browser_FocusRequested;
        _viewModel.PreviewFocusRequested -= ViewModel_PreviewFocusRequested;
        _viewModel.PropertiesFocusRequested -= ViewModel_PropertiesFocusRequested;
        if (_themeManager is not null)
        {
            _themeManager.ThemeChanged -= ThemeManager_ThemeChanged;
        }
        try
        {
            await _viewModel.DisposeAsync().ConfigureAwait(true);
        }
        catch
        {
            // The window is already closed; worker cleanup is also enforced by its Job Object.
        }
    }

    internal static double CalculateInspectorWidth(double windowWidth)
    {
        if (!double.IsFinite(windowWidth) || windowWidth <= 0)
        {
            return MinimumInspectorWidth;
        }

        return Math.Clamp(
            windowWidth * InspectorWidthRatio,
            MinimumInspectorWidth,
            MaximumInspectorWidth);
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e) =>
        _themeManager?.ApplyWindowChrome(this);

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) =>
        PreviewPanel.Width = CalculateInspectorWidth(e.NewSize.Width);

    private void ThemeManager_ThemeChanged(object? sender, EventArgs e)
    {
        UpdateThemeMenuState();
        _themeManager?.ApplyWindowChrome(this);
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void UseSystemTheme_Click(object sender, RoutedEventArgs e) =>
        _themeManager?.SetPreference(AppThemePreference.System);

    private void UseLightTheme_Click(object sender, RoutedEventArgs e) =>
        _themeManager?.SetPreference(AppThemePreference.Light);

    private void UseDarkTheme_Click(object sender, RoutedEventArgs e) =>
        _themeManager?.SetPreference(AppThemePreference.Dark);

    private void UpdateThemeMenuState()
    {
        var preference = _themeManager?.Preference ?? AppThemePreference.System;
        SystemThemeMenuItem.IsChecked = preference == AppThemePreference.System;
        LightThemeMenuItem.IsChecked = preference == AppThemePreference.Light;
        DarkThemeMenuItem.IsChecked = preference == AppThemePreference.Dark;
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedEntries();
        _viewModel.UpdateContextSelection(selected);
        await _viewModel.ExtractAsync(selected).ConfigureAwait(true);
    }

    private void EntriesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: ArchiveEntryViewModel entry })
        {
            ExecutePrimaryCommand(entry);
        }
    }

    private void EntriesGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter
            && sender is DataGrid { SelectedItem: ArchiveEntryViewModel entry })
        {
            e.Handled = true;
            ExecutePrimaryCommand(entry);
        }
    }

    private void EntriesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SynchronizeContextSelection();

    private void EntriesGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        SynchronizeContextSelection();

    private void EntriesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid
            || e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(grid, source) is not DataGridRow row)
        {
            return;
        }

        if (!row.IsSelected)
        {
            grid.UnselectAll();
            row.IsSelected = true;
            grid.CurrentItem = row.Item;
        }

        _ = row.Focus();
        SynchronizeContextSelection();
    }

    private void ExecutePrimaryCommand(ArchiveEntryViewModel entry)
    {
        SynchronizeContextSelection();
        var command = entry.IsDirectory
            ? _viewModel.NavigateSelectionCommand
            : _viewModel.PreviewSelectionCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private ArchiveEntryViewModel[] GetSelectedEntries() => EntriesGrid.SelectedItems
        .OfType<ArchiveEntryViewModel>()
        .ToArray();

    private void SynchronizeContextSelection()
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Browser.SelectedEntry = EntriesGrid.SelectedItem as ArchiveEntryViewModel;
            viewModel.UpdateContextSelection(GetSelectedEntries());
        }
    }

    private void Breadcrumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            _viewModel.NavigateTo(button.Tag is EntryId entryId ? entryId : null);
        }
    }

    private void Browser_FocusRequested(
        object? sender,
        ArchiveBrowserFocusRequestedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (e.Target == ArchiveBrowserFocusTarget.Search)
                {
                    if (SearchInput.IsEnabled)
                    {
                        _ = SearchInput.Focus();
                        SearchInput.SelectAll();
                    }

                    return;
                }

                if (!EntriesGrid.IsVisible || !EntriesGrid.IsEnabled)
                {
                    return;
                }

                _ = EntriesGrid.Focus();
                if (_viewModel.Browser.SelectedEntry is { } selected)
                {
                    EntriesGrid.ScrollIntoView(selected);
                }
            });
    }

    private void ViewModel_PreviewFocusRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (_viewModel.Preview.IsTextVisible && PreviewTextBox.IsVisible)
                {
                    _ = PreviewTextBox.Focus();
                    return;
                }

                if (PreviewStatePanel.IsVisible)
                {
                    _ = PreviewStatePanel.Focus();
                }
            });
    }

    private void ViewModel_PropertiesFocusRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (InspectorTabs.IsVisible)
                {
                    InspectorTabs.SelectedIndex = 0;
                    _ = InspectorTabs.Focus();
                }
            });
    }
}
