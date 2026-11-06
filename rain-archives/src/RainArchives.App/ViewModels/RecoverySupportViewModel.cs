using System.IO;
using RainArchives.App.Infrastructure;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public sealed class RecoverySupportViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IArchiveClient _client;
    private readonly object _operationGate = new();
    private CancellationTokenSource? _lifetime = new();
    private Task _activeOperation = Task.CompletedTask;
    private bool _isBusy;
    private string _statusText = "Loading fixed-field support evidence…";

    public RecoverySupportViewModel(IArchiveClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => CanRefresh);
    }

    public SupportDiagnosticsViewModel Diagnostics { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public bool SupportsDiagnostics => _client.SupportsFeature(ProtocolFeatures.SupportDiagnostics);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanRefresh));
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanRefresh => !IsBusy;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public Task InitializeAsync() => RefreshAsync();

    public Task RefreshAsync()
    {
        lock (_operationGate)
        {
            if (_lifetime is null || !_activeOperation.IsCompleted)
            {
                return Task.CompletedTask;
            }

            _activeOperation = RefreshCoreAsync(_lifetime.Token);
            return _activeOperation;
        }
    }

    public void RequestCancellation() => _lifetime?.Cancel();

    public async ValueTask DisposeAsync()
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is null)
        {
            return;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        Task activeOperation;
        lock (_operationGate)
        {
            activeOperation = _activeOperation;
        }

        try
        {
            await activeOperation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is represented by a fixed status below.
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            if (!SupportsDiagnostics)
            {
                Diagnostics.MarkUnavailable(requiresProtocolUpgrade: true);
                StatusText = "Support evidence requires protocol 1.22. No fallback data was inferred.";
                return;
            }

            var response = await _client.GetSupportDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(true);
            Diagnostics.Apply(response);
            StatusText = "Fixed-field support evidence refreshed.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Diagnostics.MarkUnavailable(requiresProtocolUpgrade: false);
            StatusText = "Support evidence refresh cancelled.";
        }
        catch (Exception exception) when (
            exception is ArchiveClientException or IOException or InvalidDataException)
        {
            Diagnostics.MarkUnavailable(requiresProtocolUpgrade: false);
            StatusText = "Fixed-field support evidence is unavailable. No exception details were retained.";
        }
        catch (Exception)
        {
            Diagnostics.MarkUnavailable(requiresProtocolUpgrade: false);
            StatusText = "Fixed-field support evidence is unavailable. No exception details were retained.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
