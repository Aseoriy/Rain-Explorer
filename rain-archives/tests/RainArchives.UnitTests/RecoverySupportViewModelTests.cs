using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class RecoverySupportViewModelTests
{
    [TestMethod]
    public async Task RefreshUsesOnlyFixedProtocolFields()
    {
        var client = new SupportClient { Response = CreateResponse() };
        await using var viewModel = new RecoverySupportViewModel(client);

        await viewModel.InitializeAsync();

        Assert.AreEqual(1, client.Calls);
        Assert.IsTrue(viewModel.Diagnostics.IsAvailable);
        StringAssert.Contains(viewModel.Diagnostics.ProtocolText, "1.22");
        StringAssert.Contains(viewModel.Diagnostics.AdmissionText, "32 admitted");
        StringAssert.Contains(viewModel.Diagnostics.ControlsText, "Cancel is the only");
        StringAssert.Contains(viewModel.Diagnostics.RecoveryText, "unavailable");
    }

    [TestMethod]
    public async Task DowngradeAndFailureNeverDeriveFallbackOrRetainExceptionText()
    {
        var downlevelClient = new SupportClient { SupportsDiagnostics = false };
        await using (var downlevel = new RecoverySupportViewModel(downlevelClient))
        {
            await downlevel.InitializeAsync();
            Assert.AreEqual(0, downlevelClient.Calls);
            Assert.IsFalse(downlevel.Diagnostics.IsAvailable);
            StringAssert.Contains(downlevel.StatusText, "requires protocol 1.22");
        }

        var failingClient = new SupportClient
        {
            Failure = new ArchiveClientException(new ArchiveError(
                "provider.failure",
                ArchiveErrorCategory.ProviderFailure,
                "C:\\temp\\secret archive provider failure")),
        };
        await using var failing = new RecoverySupportViewModel(failingClient);
        await failing.InitializeAsync();
        Assert.IsFalse(failing.Diagnostics.IsAvailable);
        Assert.IsFalse(failing.StatusText.Contains("temp", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(failing.StatusText.Contains("provider", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(failing.StatusText, "No exception details");
    }

    [TestMethod]
    public async Task CloseCancelsAndDrainsOutstandingRefresh()
    {
        var client = new SupportClient { BlockUntilCancellation = true };
        var viewModel = new RecoverySupportViewModel(client);

        var refresh = viewModel.InitializeAsync();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.DisposeAsync();
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsFalse(viewModel.IsBusy);
        StringAssert.Contains(viewModel.StatusText, "cancelled");
    }

    private static GetSupportDiagnosticsResponse CreateResponse() => new(
        ProtocolMajor: 1,
        ProtocolMinor: 22,
        EnabledFeatureCount: 24,
        WorkerContainmentModel.SameUserWorkerProcess,
        IsSameUserSecuritySandbox: false,
        MaximumActiveAndQueuedOperations: 32,
        MaximumConcurrentOperations: 1,
        OperationQueueOrdering.Unspecified,
        MaximumRetainedTerminalOperations: 64,
        ActiveAndQueuedOperationCount: 2,
        RetainedTerminalOperationCount: 3,
        OperationHistoryTruncated: false,
        SupportsQueueReordering: false,
        SupportsPause: false,
        SupportsRetry: false,
        SupportsAutomaticRecoveryDisposition: false,
        PersistsOperationActivityAcrossWorkerRestart: false);

    private sealed class SupportClient : ArchiveClientTestDouble
    {
        public bool SupportsDiagnostics { get; init; } = true;
        public bool BlockUntilCancellation { get; init; }
        public GetSupportDiagnosticsResponse? Response { get; init; }
        public Exception? Failure { get; init; }
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public override bool SupportsFeature(string feature) =>
            feature != ProtocolFeatures.SupportDiagnostics || SupportsDiagnostics;

        public override async Task<GetSupportDiagnosticsResponse> GetSupportDiagnosticsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Started.TrySetResult(true);
            if (BlockUntilCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return Response ?? CreateResponse();
        }
    }
}
