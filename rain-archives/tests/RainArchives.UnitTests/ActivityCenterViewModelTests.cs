using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.ViewModels;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ActivityCenterViewModelTests
{
    [TestMethod]
    public async Task SnapshotPresentsTruthfulBoundedPolicyProgressAndFixedDiagnostics()
    {
        var now = DateTimeOffset.UtcNow;
        var activeId = OperationId.New();
        var terminalId = OperationId.New();
        var client = new ActivityClientDouble
        {
            Activity = CreateActivity(
                now,
                new OperationActivitySnapshot(
                    activeId,
                    OperationKind.Extract,
                    OperationPhase.Running,
                    4,
                    now.AddSeconds(-4),
                    now,
                    null,
                    new OperationActivityProgress(
                        2 * 1024 * 1024,
                        4 * 1024 * 1024,
                        3,
                        6,
                        TimeSpan.FromSeconds(4),
                        512 * 1024,
                        0.5),
                    0,
                    true),
                new OperationActivitySnapshot(
                    terminalId,
                    OperationKind.CreateArchive,
                    OperationPhase.CompletedWithWarnings,
                    7,
                    now.AddSeconds(-8),
                    now.AddSeconds(-1),
                    now.AddSeconds(-1),
                    new OperationActivityProgress(
                        4096,
                        4096,
                        2,
                        2,
                        TimeSpan.FromSeconds(7),
                        585,
                        0.7),
                    1,
                    false)),
            Diagnostics = CreateDiagnostics(active: 1, terminal: 1),
        };

        await using var viewModel = new ActivityCenterViewModel(
            client,
            refreshInterval: TimeSpan.FromSeconds(30));
        viewModel.Start();
        await WaitUntilAsync(() => viewModel.IsReady && viewModel.Operations.Count == 2);

        Assert.AreEqual(1, viewModel.ActiveAndQueuedCount);
        Assert.AreEqual(1, viewModel.RetainedTerminalCount);
        StringAssert.Contains(viewModel.PolicySummary, "32 operations");
        StringAssert.Contains(viewModel.PolicySummary, "1 runs");
        StringAssert.Contains(viewModel.PolicySummary, "64 terminal");
        StringAssert.Contains(viewModel.UnsupportedControlsText, "pause, retry");

        var active = viewModel.Operations.Single(operation => operation.OperationId == activeId);
        Assert.AreEqual("Extraction", active.OperationName);
        Assert.AreEqual("Running", active.PhaseText);
        Assert.AreEqual("50%", active.ProgressText);
        Assert.AreEqual("2 MiB of 4 MiB", active.BytesText);
        Assert.AreEqual("3 of 6", active.EntriesText);
        Assert.AreEqual("512 KiB/s", active.ThroughputText);
        Assert.AreEqual("0.5", active.RatioText);
        Assert.IsTrue(active.CanCancel);

        var terminal = viewModel.Operations.Single(operation => operation.OperationId == terminalId);
        Assert.AreEqual("Completed with warnings", terminal.PhaseText);
        Assert.AreEqual("1 reported", terminal.WarningText);
        Assert.IsFalse(terminal.CanCancel);
        StringAssert.Contains(terminal.OutcomeText, "Completed with 1 warning");

        Assert.IsTrue(viewModel.SupportDiagnostics.IsAvailable);
        StringAssert.Contains(
            viewModel.SupportDiagnostics.ProtocolText,
            $"{ProtocolConstants.MajorVersion}.{ProtocolConstants.MinorVersion}");
        StringAssert.Contains(viewModel.SupportDiagnostics.ContainmentText, "Same-user");
        StringAssert.Contains(viewModel.SupportDiagnostics.SandboxText, "Not a same-user filesystem sandbox");
        StringAssert.Contains(viewModel.SupportDiagnostics.ControlsText, "Cancel is the only");
        StringAssert.Contains(viewModel.SupportDiagnostics.PersistenceText, "connection-lifetime only");
    }

    [TestMethod]
    public async Task LiveEventsEscapeCurrentItemAndNeverRetainWarningOrErrorMessages()
    {
        var now = DateTimeOffset.UtcNow;
        var operationId = OperationId.New();
        var client = new ActivityClientDouble
        {
            Activity = CreateActivity(
                now,
                new OperationActivitySnapshot(
                    operationId,
                    OperationKind.IntegrityTest,
                    OperationPhase.Running,
                    1,
                    now,
                    now,
                    null,
                    null,
                    0,
                    true)),
            Diagnostics = CreateDiagnostics(active: 1, terminal: 0),
        };
        await using var viewModel = new ActivityCenterViewModel(
            client,
            refreshInterval: TimeSpan.FromSeconds(30));
        viewModel.Start();
        await WaitUntilAsync(() => viewModel.Operations.Count == 1);

        const string controlledItem = "folder\\name\r\n\u202Eitem";
        client.Raise(new OperationEvent(
            operationId,
            2,
            OperationEventKind.Progress,
            OperationPhase.Running,
            new OperationProgress(
                12,
                20,
                1,
                2,
                null,
                controlledItem,
                TimeSpan.FromSeconds(1),
                12,
                null)));
        client.Raise(new OperationEvent(
            operationId,
            3,
            OperationEventKind.Warning,
            OperationPhase.Running,
            Warning: new ArchiveWarning("unsafe", "C:\\private\\secret.txt\r\nwarning")));
        client.Raise(new OperationEvent(
            operationId,
            4,
            OperationEventKind.Terminal,
            OperationPhase.Failed,
            Error: new ArchiveError(
                "provider.failed",
                ArchiveErrorCategory.ProviderFailure,
                "C:\\temporary\\worker-path and archive text")));

        await WaitUntilAsync(() => viewModel.Operations[0].Phase == OperationPhase.Failed);
        var row = viewModel.Operations[0];
        Assert.AreEqual("folder\\\\name\\u000D\\u000A\\u202Eitem", row.CurrentItem);
        Assert.AreEqual("1 reported", row.WarningText);
        Assert.AreEqual("Failed (provider failure)", row.OutcomeText);
        Assert.IsFalse(row.OutcomeText.Contains("temporary", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(row.OutcomeText.Contains("archive text", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(row.CanCancel);
    }

    [TestMethod]
    public async Task CancelIsExposedOnlyBySnapshotAndTargetsExactOperationOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var operationId = OperationId.New();
        var client = new ActivityClientDouble
        {
            Activity = CreateActivity(
                now,
                new OperationActivitySnapshot(
                    operationId,
                    OperationKind.RebuildArchive,
                    OperationPhase.Queued,
                    0,
                    now,
                    now,
                    null,
                    null,
                    0,
                    true)),
            Diagnostics = CreateDiagnostics(active: 1, terminal: 0),
        };
        await using var viewModel = new ActivityCenterViewModel(
            client,
            refreshInterval: TimeSpan.FromSeconds(30));
        viewModel.TrackStarted(operationId, OperationKind.RebuildArchive, "ZIP rebuild copy");
        await Task.Delay(30);
        Assert.IsTrue(viewModel.Operations.Count == 0 || !viewModel.Operations[0].CanCancel);

        viewModel.Start();
        await WaitUntilAsync(() => viewModel.Operations.Count == 1 && viewModel.Operations[0].CanCancel);
        var row = viewModel.Operations[0];
        row.CancelCommand.Execute(null);
        await WaitUntilAsync(() => client.CancelledOperationIds.Count == 1);

        Assert.AreEqual(operationId, client.CancelledOperationIds.Single());
        Assert.IsTrue(row.IsCancellationPending);
        Assert.IsFalse(row.CanCancel);

        client.Raise(new OperationEvent(
            operationId,
            1,
            OperationEventKind.Terminal,
            OperationPhase.Cancelled));
        await WaitUntilAsync(() => row.Phase == OperationPhase.Cancelled);
        row.CancelCommand.Execute(null);
        await Task.Delay(40);
        Assert.AreEqual(1, client.CancelledOperationIds.Count);
        Assert.AreEqual("Cancelled; no retry was started", row.OutcomeText);
    }

    [TestMethod]
    public async Task RefreshRequestsNeverOverlapAndDisposalRejectsLateResponse()
    {
        var client = new BlockingActivityClientDouble();
        var viewModel = new ActivityCenterViewModel(
            client,
            refreshInterval: TimeSpan.FromMilliseconds(250));
        viewModel.Start();
        await client.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var manualRefresh = viewModel.RefreshNowAsync();
        await Task.Delay(350);
        Assert.AreEqual(1, client.MaximumConcurrentRequests);

        var dispose = viewModel.DisposeAsync().AsTask();
        client.ReleaseResponse.TrySetResult();
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        await manualRefresh.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Assert.AreEqual(1, client.MaximumConcurrentRequests);
        Assert.AreEqual(0, viewModel.Operations.Count);
        Assert.IsFalse(viewModel.CanRefresh);
    }

    [TestMethod]
    public async Task DowngradeAndMalformedResponsesFailClosedWithoutDerivedDiagnostics()
    {
        var downgradeClient = new ActivityClientDouble { SupportsActivity = false };
        await using (var downgrade = new ActivityCenterViewModel(
                         downgradeClient,
                         refreshInterval: TimeSpan.FromSeconds(30)))
        {
            downgrade.Start();
            await WaitUntilAsync(() => downgrade.IsUnavailable);
            Assert.AreEqual(0, downgradeClient.ActivityCalls);
            Assert.IsFalse(downgrade.SupportDiagnostics.IsAvailable);
            StringAssert.Contains(downgrade.SupportDiagnostics.AvailabilityText, "protocol 1.22");
        }

        var malformedClient = new ActivityClientDouble
        {
            Activity = CreateActivity(DateTimeOffset.UtcNow) with
            {
                SupportsRetry = true,
            },
            Diagnostics = CreateDiagnostics(active: 0, terminal: 0),
        };
        await using var malformed = new ActivityCenterViewModel(
            malformedClient,
            refreshInterval: TimeSpan.FromSeconds(30));
        malformed.Start();
        await WaitUntilAsync(() => malformed.IsOffline);
        Assert.AreEqual(
            "Activity data was rejected because it was malformed or inconsistent.",
            malformed.StatusText);
        Assert.IsFalse(malformed.SupportDiagnostics.IsAvailable);
    }

    private static GetOperationActivityResponse CreateActivity(
        DateTimeOffset now,
        params OperationActivitySnapshot[] operations) =>
        new(
            now,
            ProtocolConstants.MaximumQueuedOperations,
            ProtocolConstants.MaximumConcurrentOperations,
            OperationQueueOrdering.Unspecified,
            ProtocolConstants.MaximumRetainedOperationActivity,
            false,
            false,
            false,
            false,
            false,
            operations);

    private static GetSupportDiagnosticsResponse CreateDiagnostics(int active, int terminal) =>
        new(
            ProtocolConstants.MajorVersion,
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedVersion1.Count,
            WorkerContainmentModel.SameUserWorkerProcess,
            false,
            ProtocolConstants.MaximumQueuedOperations,
            ProtocolConstants.MaximumConcurrentOperations,
            OperationQueueOrdering.Unspecified,
            ProtocolConstants.MaximumRetainedOperationActivity,
            active,
            terminal,
            false,
            false,
            false,
            false,
            false,
            false);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                Assert.Fail("The Activity Center condition was not reached before timeout.");
            }

            await Task.Delay(10);
        }
    }

    private class ActivityClientDouble : ArchiveClientTestDouble
    {
        private EventHandler<OperationEvent>? _operationEventReceived;

        public bool SupportsActivity { get; init; } = true;
        public GetOperationActivityResponse Activity { get; set; } = CreateActivity(DateTimeOffset.UtcNow);
        public GetSupportDiagnosticsResponse Diagnostics { get; set; } = CreateDiagnostics(0, 0);
        public int ActivityCalls { get; private set; }
        public List<OperationId> CancelledOperationIds { get; } = [];

        public override event EventHandler<OperationEvent>? OperationEventReceived
        {
            add => _operationEventReceived += value;
            remove => _operationEventReceived -= value;
        }

        public override bool SupportsFeature(string feature) => feature switch
        {
            ProtocolFeatures.OperationActivity => SupportsActivity,
            ProtocolFeatures.SupportDiagnostics => SupportsActivity,
            _ => true,
        };

        public override Task<GetOperationActivityResponse> GetOperationActivityAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActivityCalls++;
            return Task.FromResult(Activity);
        }

        public override Task<GetSupportDiagnosticsResponse> GetSupportDiagnosticsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Diagnostics);
        }

        public override Task<CancelOperationResponse> CancelOperationAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancelledOperationIds.Add(operationId);
            return Task.FromResult(new CancelOperationResponse(true));
        }

        public void Raise(OperationEvent operationEvent) =>
            _operationEventReceived?.Invoke(this, operationEvent);
    }

    private sealed class BlockingActivityClientDouble : ActivityClientDouble
    {
        private int _concurrent;

        public TaskCompletionSource FirstRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseResponse { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaximumConcurrentRequests { get; private set; }

        public override async Task<GetOperationActivityResponse> GetOperationActivityAsync(
            CancellationToken cancellationToken = default)
        {
            var concurrent = Interlocked.Increment(ref _concurrent);
            MaximumConcurrentRequests = Math.Max(MaximumConcurrentRequests, concurrent);
            FirstRequestStarted.TrySetResult();
            try
            {
                await ReleaseResponse.Task.ConfigureAwait(false);
                return CreateActivity(DateTimeOffset.UtcNow);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }
}
