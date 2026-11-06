using System.Windows;
using RainArchives.App.ViewModels;

namespace RainArchives.App.Views;

public partial class ActivityCenterWindow : Window
{
    private readonly ActivityCenterViewModel _viewModel;

    public ActivityCenterWindow(ActivityCenterViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        Activated += OnActivated;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.RequestRefresh();
        _ = RefreshButton.Focus();
    }

    private void OnActivated(object? sender, EventArgs e) => _viewModel.RequestRefresh();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
