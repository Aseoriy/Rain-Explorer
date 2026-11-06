using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class MainWindowSettingsTests
{
    [TestMethod]
    public async Task SettingsCommandPresentsAuthenticatedClientOrTruthfulOfflineState()
    {
        var connectedDialogs = new SettingsDialogs();
        var client = new SettingsClientDouble();
        await using (var viewModel = new MainWindowViewModel(
                         connectedDialogs,
                         _ => Task.FromResult<IArchiveClient>(client)))
        {
            viewModel.SettingsCommand.Execute(null);
            await connectedDialogs.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreSame(client, connectedDialogs.Client);
            connectedDialogs.Release.TrySetResult();
            await WaitUntilAsync(() => viewModel.StatusText == "Settings and presets closed.");
        }

        var offlineDialogs = new SettingsDialogs();
        await using (var viewModel = new MainWindowViewModel(
                         offlineDialogs,
                         _ => Task.FromException<IArchiveClient>(new IOException("private path"))))
        {
            viewModel.SettingsCommand.Execute(null);
            await offlineDialogs.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsNull(offlineDialogs.Client);
            offlineDialogs.Release.TrySetResult();
            await WaitUntilAsync(() => viewModel.StatusText.Contains("remained offline", StringComparison.Ordinal));
            Assert.IsNull(offlineDialogs.ErrorMessage);
            Assert.IsFalse(viewModel.StatusText.Contains("private", StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public async Task DisposalCancelsAndDrainsSettingsBeforeClientDisposal()
    {
        var dialogs = new SettingsDialogs { WaitForCancellation = true };
        var client = new SettingsClientDouble(() => dialogs.Finished.Task.IsCompleted);
        var viewModel = new MainWindowViewModel(
            dialogs,
            _ => Task.FromResult<IArchiveClient>(client));
        viewModel.SettingsCommand.Execute(null);
        await dialogs.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.IsTrue(dialogs.CancellationObserved);
        Assert.IsTrue(dialogs.Finished.Task.IsCompleted);
        Assert.IsTrue(client.Disposed);
        Assert.IsTrue(client.SettingsWereDrainedBeforeDispose);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), "The expected Settings command state was not reached.");
    }

    private sealed class SettingsClientDouble : ArchiveClientTestDouble
    {
        private readonly Func<bool>? _settingsDrained;

        public SettingsClientDouble(Func<bool>? settingsDrained = null)
        {
            _settingsDrained = settingsDrained;
        }

        public bool Disposed { get; private set; }
        public bool SettingsWereDrainedBeforeDispose { get; private set; }

        public override Task<GetOperationActivityResponse> GetOperationActivityAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetOperationActivityResponse(
                DateTimeOffset.UtcNow,
                32,
                1,
                OperationQueueOrdering.Unspecified,
                64,
                SupportsReordering: false,
                SupportsPause: false,
                SupportsRetry: false,
                PersistsAcrossWorkerRestart: false,
                HistoryTruncated: false,
                Operations: []));

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            SettingsWereDrainedBeforeDispose = _settingsDrained?.Invoke() ?? true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SettingsDialogs : IAppDialogService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IArchiveClient? Client { get; private set; }
        public bool WaitForCancellation { get; init; }
        public bool CancellationObserved { get; private set; }
        public string? ErrorMessage { get; private set; }

        public async Task ShowSettingsAsync(
            IArchiveClient? client,
            CancellationToken cancellationToken)
        {
            Client = client;
            Started.TrySetResult();
            try
            {
                if (WaitForCancellation)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                else
                {
                    await Release.Task.WaitAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
            }
            finally
            {
                Finished.TrySetResult();
            }
        }

        public string? ChooseArchive() => null;
        public ArchiveCreationSelection? ChooseArchiveCreation(bool supportsDirectoryCreation) => null;
        public string? ChooseNewExtractionDestination(string archivePath, bool selectedEntriesOnly) => null;
        public Task<char[]?> RequestPasswordAsync(string archiveName, CancellationToken cancellationToken) =>
            Task.FromResult<char[]?>(null);
        public Task ShowArchiveRecoveryAsync(IArchiveClient client, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public void ShowError(string title, string message) => ErrorMessage = message;
        public void ShowInformation(string title, string message)
        {
        }
    }
}
