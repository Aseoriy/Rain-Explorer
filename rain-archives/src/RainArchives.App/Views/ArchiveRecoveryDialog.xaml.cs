using System.Windows;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.App.Views;

public partial class ArchiveRecoveryDialog : Window
{
    private readonly ArchiveRecoveryCenterViewModel _creationViewModel;
    private readonly NestedWorkspaceRecoveryCenterViewModel _nestedViewModel;
    private readonly ExtractionRecoveryCenterViewModel _extractionViewModel;
    private readonly RecoverySupportViewModel _supportViewModel;
    private readonly IArchiveClient _client;
    private Task _initialization = Task.CompletedTask;

    public ArchiveRecoveryDialog(IArchiveClient client)
    {
        InitializeComponent();
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _creationViewModel = new ArchiveRecoveryCenterViewModel(client, ConfirmDisposition);
        _nestedViewModel = new NestedWorkspaceRecoveryCenterViewModel(
            client,
            ConfirmNestedDiscard,
            ConfirmNestedReopen,
            RequestRecoveredPasswordSlotAsync,
            OnRecoveredArchiveOpened);
        _extractionViewModel = new ExtractionRecoveryCenterViewModel(client);
        _supportViewModel = new RecoverySupportViewModel(client);
        CreationRecoveryTab.DataContext = _creationViewModel;
        NestedRecoveryTab.DataContext = _nestedViewModel;
        ExtractionRecoveryTab.DataContext = _extractionViewModel;
        SupportRecoveryTab.DataContext = _supportViewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public ReopenNestedWorkspaceRecoveryResponse? ReopenedArchive { get; private set; }

    public bool ReopenedWithPassword { get; private set; }

    public void RequestCancellation()
    {
        RequestViewModelCancellation();
        if (Dispatcher.CheckAccess())
        {
            Close();
            return;
        }

        _ = Dispatcher.BeginInvoke(Close);
    }

    public async ValueTask DrainAsync()
    {
        RequestViewModelCancellation();
        var creationDrain = _creationViewModel.DisposeAsync().AsTask();
        var nestedDrain = _nestedViewModel.DisposeAsync().AsTask();
        var extractionDrain = _extractionViewModel.DisposeAsync().AsTask();
        var supportDrain = _supportViewModel.DisposeAsync().AsTask();
        await Task.WhenAll(
                _initialization,
                creationDrain,
                nestedDrain,
                extractionDrain,
                supportDrain)
            .ConfigureAwait(true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _initialization = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await _creationViewModel.InitializeAsync().ConfigureAwait(true);
        await _nestedViewModel.InitializeAsync().ConfigureAwait(true);
        await _extractionViewModel.InitializeAsync().ConfigureAwait(true);
        await _supportViewModel.InitializeAsync().ConfigureAwait(true);
        if (!IsLoaded)
        {
            return;
        }

        if (_creationViewModel.HasRecoveries)
        {
            RecoveryGrid.SelectedIndex = 0;
            RecoveryGrid.Focus();
        }
        else
        {
            RefreshRecoveriesButton.Focus();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        RequestViewModelCancellation();
    }

    private void RequestViewModelCancellation()
    {
        _creationViewModel.RequestCancellation();
        _nestedViewModel.RequestCancellation();
        _extractionViewModel.RequestCancellation();
        _supportViewModel.RequestCancellation();
    }

    private bool ConfirmDisposition(ArchiveRecoveryDispositionConfirmation confirmation)
    {
        var result = MessageBox.Show(
            this,
            confirmation.Message,
            confirmation.Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private bool ConfirmNestedDiscard(NestedWorkspaceRecoveryDiscardConfirmation confirmation)
    {
        var result = MessageBox.Show(
            this,
            confirmation.Message,
            confirmation.Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private bool ConfirmNestedReopen(NestedWorkspaceRecoveryReopenConfirmation confirmation)
    {
        var result = MessageBox.Show(
            this,
            confirmation.Message,
            confirmation.Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private async Task<Guid?> RequestRecoveredPasswordSlotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new PasswordDialog("Recovered archive") { Owner = this };
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((PasswordDialog)state!).RequestCancellation(),
            dialog);
        try
        {
            if (dialog.ShowDialog() != true)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var password = dialog.TakePassword();
            try
            {
                return await _client.CreateSecretAsync(password, cancellationToken)
                    .ConfigureAwait(true);
            }
            finally
            {
                Array.Clear(password);
            }
        }
        finally
        {
            dialog.ClearCapturedPassword();
        }
    }

    private void OnRecoveredArchiveOpened(ReopenNestedWorkspaceRecoveryResponse reopened)
    {
        ReopenedArchive = reopened;
        ReopenedWithPassword = _nestedViewModel.ReopenedWithPassword;
        DialogResult = true;
    }
}
