using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using RainArchives.App.ViewModels;

namespace RainArchives.App.Views;

public partial class SettingsCenterWindow : Window
{
    private readonly SettingsCenterViewModel _viewModel;
    private bool _loaded;

    public SettingsCenterWindow(SettingsCenterViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    public void RequestCancellation()
    {
        _viewModel.RequestCancellation();
        if (Dispatcher.CheckAccess())
        {
            Close();
            return;
        }

        _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.Send);
    }

    public Task DrainAsync() => _viewModel.DrainAsync();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await _viewModel.InitializeAsync().ConfigureAwait(true);
        if (IsLoaded)
        {
            _ = DefaultFormatInput.Focus();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e) =>
        _viewModel.RequestCancellation();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
