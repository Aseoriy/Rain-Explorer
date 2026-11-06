using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ArchiveRecoveryCenterViewModelTests
{
    private const string RecoveryId =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset RecordedAtUtc =
        new(2026, 8, 23, 10, 15, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task InventoryRemainsPathFreeUntilTheUserExplicitlyInspectsASelection()
    {
        var summary = CreateSummary(ArchiveCreationRecoveryPhase.CommitIntent);
        var client = new RecoveryArchiveClient
        {
            Inspection = CreateInspection(
                summary,
                ArchiveCreationRecoveryArtifactState.ExactMatch,
                ArchiveCreationRecoveryArtifactState.Absent,
                ArchiveCreationRecoveryAssessment.OwnedStagingObserved),
        };
        client.InventoryResponses.Enqueue(CreateInventory(summary));
        await using var viewModel = CreateViewModel(client, confirmationResult: false);

        await viewModel.InitializeAsync();

        Assert.AreEqual(1, client.ListCalls);
        Assert.AreEqual(0, client.InspectCalls);
        Assert.IsFalse(viewModel.HasInspection);
        Assert.AreEqual(string.Empty, viewModel.DestinationPathText);
        Assert.AreEqual(RecoveryId, viewModel.Recoveries.Single().RecoveryId);

        viewModel.SelectedRecovery = viewModel.Recoveries.Single();
        Assert.AreEqual(0, client.InspectCalls);
        await viewModel.InspectSelectedAsync();

        Assert.AreEqual(1, client.InspectCalls);
        Assert.IsTrue(viewModel.HasInspection);
        Assert.AreEqual("C:\\\\Recovery\\\\output.zip", viewModel.DestinationPathText);
        Assert.IsTrue(viewModel.CanCommitOwnedStaging);
        Assert.IsTrue(viewModel.CanDiscardOwnedStaging);
    }

    [TestMethod]
    public async Task CommitRequiresConfirmationAndUsesTheExactInspectedRevision()
    {
        var summary = CreateSummary(ArchiveCreationRecoveryPhase.CommitIntent);
        var client = new RecoveryArchiveClient
        {
            Inspection = CreateInspection(
                summary,
                ArchiveCreationRecoveryArtifactState.ExactMatch,
                ArchiveCreationRecoveryArtifactState.Absent,
                ArchiveCreationRecoveryAssessment.OwnedStagingObserved),
        };
        client.InventoryResponses.Enqueue(CreateInventory(summary));
        client.InventoryResponses.Enqueue(CreateInventory());
        var confirmationResult = false;
        ArchiveRecoveryDispositionConfirmation? confirmation = null;
        await using var viewModel = new ArchiveRecoveryCenterViewModel(
            client,
            request =>
            {
                confirmation = request;
                return confirmationResult;
            });
        await viewModel.InitializeAsync();
        viewModel.SelectedRecovery = viewModel.Recoveries.Single();
        await viewModel.InspectSelectedAsync();

        await viewModel.ApplyDispositionAsync(
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);

        Assert.IsNotNull(confirmation);
        Assert.AreEqual(
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging,
            confirmation.Action);
        Assert.AreEqual(0, client.ApplyCalls);
        Assert.IsTrue(viewModel.HasInspection);

        confirmationResult = true;
        await viewModel.ApplyDispositionAsync(
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);

        Assert.AreEqual(1, client.ApplyCalls);
        Assert.IsNotNull(client.LastDisposition);
        var applied = client.LastDisposition!;
        Assert.AreEqual(summary.RecoveryId, applied.RecoveryId);
        Assert.AreEqual(summary.Phase, applied.Phase);
        Assert.AreEqual(summary.Sequence, applied.Sequence);
        Assert.AreEqual(summary.RecordedAtUtc, applied.RecordedAtUtc);
        Assert.AreEqual(
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging,
            applied.Action);
        Assert.AreEqual(2, client.ListCalls);
        Assert.IsFalse(viewModel.HasInspection);
        Assert.IsFalse(viewModel.HasRecoveries);
        StringAssert.Contains(viewModel.StatusText, "was published");
    }

    [TestMethod]
    public async Task FailedDispositionIsNeverRetriedAndForcesARefreshAndReinspection()
    {
        var summary = CreateSummary(ArchiveCreationRecoveryPhase.CommitIntent);
        var client = new RecoveryArchiveClient
        {
            Inspection = CreateInspection(
                summary,
                ArchiveCreationRecoveryArtifactState.ExactMatch,
                ArchiveCreationRecoveryArtifactState.Absent,
                ArchiveCreationRecoveryAssessment.OwnedStagingObserved),
            DispositionException = new ArchiveClientException(new ArchiveError(
                "recovery.disposition_stale_revision",
                ArchiveErrorCategory.SourceChanged,
                "The recovery record changed.")),
        };
        client.InventoryResponses.Enqueue(CreateInventory(summary));
        await using var viewModel = CreateViewModel(client, confirmationResult: true);
        await viewModel.InitializeAsync();
        viewModel.SelectedRecovery = viewModel.Recoveries.Single();
        await viewModel.InspectSelectedAsync();

        await viewModel.ApplyDispositionAsync(
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);

        Assert.AreEqual(1, client.ApplyCalls);
        Assert.AreEqual(1, client.ListCalls);
        Assert.IsFalse(viewModel.HasInspection);
        Assert.IsFalse(viewModel.CanCommitOwnedStaging);
        StringAssert.Contains(viewModel.StatusText, "did not retry automatically");
        StringAssert.Contains(viewModel.StatusText, "Refresh and inspect again");
    }

    [TestMethod]
    public async Task ActionAvailabilityMatchesTheFreshlyInspectedArtifactEvidence()
    {
        var cases = new[]
        {
            new ActionCase(
                "commit intent staging",
                ArchiveCreationRecoveryPhase.CommitIntent,
                ArchiveCreationRecoveryArtifactState.ExactMatch,
                ArchiveCreationRecoveryArtifactState.Absent,
                ArchiveCreationRecoveryAssessment.OwnedStagingObserved,
                Commit: true,
                Discard: true,
                Accept: false,
                Acknowledge: false),
            new ActionCase(
                "writing staging",
                ArchiveCreationRecoveryPhase.Writing,
                ArchiveCreationRecoveryArtifactState.IdentityMatchLengthUnbound,
                ArchiveCreationRecoveryArtifactState.Absent,
                ArchiveCreationRecoveryAssessment.OwnedStagingObserved,
                Commit: false,
                Discard: true,
                Accept: false,
                Acknowledge: false),
            new ActionCase(
                "published final",
                ArchiveCreationRecoveryPhase.CommitIntent,
                ArchiveCreationRecoveryArtifactState.Absent,
                ArchiveCreationRecoveryArtifactState.ExactMatch,
                ArchiveCreationRecoveryAssessment.OwnedFinalObserved,
                Commit: false,
                Discard: false,
                Accept: true,
                Acknowledge: false),
            new ActionCase(
                "no artifact",
                ArchiveCreationRecoveryPhase.Admitted,
                ArchiveCreationRecoveryArtifactState.Absent,
                ArchiveCreationRecoveryArtifactState.Absent,
                ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved,
                Commit: false,
                Discard: false,
                Accept: false,
                Acknowledge: true),
            new ActionCase(
                "conflicting artifact",
                ArchiveCreationRecoveryPhase.CommitIntent,
                ArchiveCreationRecoveryArtifactState.IdentityMismatch,
                ArchiveCreationRecoveryArtifactState.Absent,
                ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts,
                Commit: false,
                Discard: false,
                Accept: false,
                Acknowledge: false),
        };

        foreach (var testCase in cases)
        {
            var summary = CreateSummary(testCase.Phase);
            var client = new RecoveryArchiveClient
            {
                Inspection = CreateInspection(
                    summary,
                    testCase.Staging,
                    testCase.Final,
                    testCase.Assessment),
            };
            client.InventoryResponses.Enqueue(CreateInventory(summary));
            await using var viewModel = CreateViewModel(client, confirmationResult: false);
            await viewModel.InitializeAsync();
            viewModel.SelectedRecovery = viewModel.Recoveries.Single();
            await viewModel.InspectSelectedAsync();

            Assert.AreEqual(testCase.Commit, viewModel.CanCommitOwnedStaging, testCase.Name);
            Assert.AreEqual(testCase.Discard, viewModel.CanDiscardOwnedStaging, testCase.Name);
            Assert.AreEqual(testCase.Accept, viewModel.CanAcceptPublishedFinal, testCase.Name);
            Assert.AreEqual(testCase.Acknowledge, viewModel.CanAcknowledgeNoOwnedArtifact, testCase.Name);
        }
    }

    [TestMethod]
    public async Task InspectionOnlyWorkerCannotExposeMutationActions()
    {
        var summary = CreateSummary(ArchiveCreationRecoveryPhase.CommitIntent);
        var client = new RecoveryArchiveClient
        {
            SupportsDisposition = false,
            Inspection = CreateInspection(
                summary,
                ArchiveCreationRecoveryArtifactState.ExactMatch,
                ArchiveCreationRecoveryArtifactState.Absent,
                ArchiveCreationRecoveryAssessment.OwnedStagingObserved),
        };
        client.InventoryResponses.Enqueue(CreateInventory(summary));
        await using var viewModel = CreateViewModel(client, confirmationResult: true);
        await viewModel.InitializeAsync();
        viewModel.SelectedRecovery = viewModel.Recoveries.Single();
        await viewModel.InspectSelectedAsync();

        Assert.IsTrue(viewModel.HasInspection);
        Assert.IsFalse(viewModel.CanCommitOwnedStaging);
        Assert.IsFalse(viewModel.CanDiscardOwnedStaging);
        StringAssert.Contains(viewModel.ActionGuidance, "inspection only");

        await viewModel.ApplyDispositionAsync(
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);
        Assert.AreEqual(0, client.ApplyCalls);
    }

    [TestMethod]
    public async Task DestinationAndFailureTextEscapeBidirectionalControls()
    {
        var summary = CreateSummary(ArchiveCreationRecoveryPhase.CommitIntent);
        var client = new RecoveryArchiveClient
        {
            Inspection = CreateInspection(
                summary,
                ArchiveCreationRecoveryArtifactState.ExactMatch,
                ArchiveCreationRecoveryArtifactState.Absent,
                ArchiveCreationRecoveryAssessment.OwnedStagingObserved,
                @"C:\Recovery\report‮txt.exe.zip"),
            DispositionException = new ArchiveClientException(new ArchiveError(
                "recovery.disposition_stale_revision",
                ArchiveErrorCategory.SourceChanged,
                "Changed ‮ path")),
        };
        client.InventoryResponses.Enqueue(CreateInventory(summary));
        ArchiveRecoveryDispositionConfirmation? confirmation = null;
        await using var viewModel = new ArchiveRecoveryCenterViewModel(
            client,
            request =>
            {
                confirmation = request;
                return true;
            });
        await viewModel.InitializeAsync();
        viewModel.SelectedRecovery = viewModel.Recoveries.Single();
        await viewModel.InspectSelectedAsync();

        Assert.IsFalse(viewModel.DestinationPathText.Contains('‮'));
        StringAssert.Contains(viewModel.DestinationPathText, "\\u202E");
        await viewModel.ApplyDispositionAsync(
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);

        Assert.IsNotNull(confirmation);
        Assert.IsFalse(confirmation.Message.Contains('‮'));
        StringAssert.Contains(confirmation.Message, "\\u202E");
        Assert.IsFalse(viewModel.StatusText.Contains('‮'));
        Assert.IsFalse(viewModel.StatusText.Contains("failure", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ClosingTheRecoveryCenterCancelsAndDrainsItsOutstandingRequest()
    {
        var client = new RecoveryArchiveClient
        {
            BlockInventoryUntilCancellation = true,
        };
        var viewModel = CreateViewModel(client, confirmationResult: false);

        var initialization = viewModel.InitializeAsync();
        await client.ListStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(viewModel.IsBusy);

        await viewModel.DisposeAsync();
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsFalse(viewModel.IsBusy);
        Assert.AreEqual("Recovery review cancelled.", viewModel.StatusText);
    }

    private static ArchiveRecoveryCenterViewModel CreateViewModel(
        RecoveryArchiveClient client,
        bool confirmationResult) =>
        new(client, _ => confirmationResult);

    private static ArchiveCreationRecoverySummary CreateSummary(
        ArchiveCreationRecoveryPhase phase) =>
        new(
            RecoveryId,
            OperationId.New(),
            phase,
            (int)phase + 1,
            RecordedAtUtc,
            HasIncompleteTail: false);

    private static ListArchiveCreationRecoveriesResponse CreateInventory(
        params ArchiveCreationRecoverySummary[] recoveries) =>
        new(
            recoveries,
            ActiveOperationCount: 0,
            SuspiciousOperationCount: 0,
            IncompleteJournalCount: 0,
            IsTruncated: false,
            InspectionIncomplete: false);

    private static InspectArchiveCreationRecoveryResponse CreateInspection(
        ArchiveCreationRecoverySummary summary,
        ArchiveCreationRecoveryArtifactState staging,
        ArchiveCreationRecoveryArtifactState final,
        ArchiveCreationRecoveryAssessment assessment,
        string destinationPath = @"C:\Recovery\output.zip") =>
        new(
            summary,
            destinationPath,
            $".rainarchives-{summary.RecoveryId}.zip.staging",
            summary.Phase is ArchiveCreationRecoveryPhase.Admitted
                or ArchiveCreationRecoveryPhase.Writing
                ? null
                : 42,
            IsRemoteDestination: false,
            ArchiveCreationRecoveryParentState.MatchesJournal,
            CreateArtifact(staging),
            CreateArtifact(final),
            assessment,
            RecordedAtUtc.AddMinutes(1));

    private static ArchiveCreationRecoveryArtifactInspection CreateArtifact(
        ArchiveCreationRecoveryArtifactState state) =>
        new(
            state,
            state is ArchiveCreationRecoveryArtifactState.IdentityMatchLengthUnbound
                or ArchiveCreationRecoveryArtifactState.ExactMatch
                or ArchiveCreationRecoveryArtifactState.IdentityMismatch
                or ArchiveCreationRecoveryArtifactState.LengthMismatch
                ? 42
                : null,
            state == ArchiveCreationRecoveryArtifactState.UnexpectedHardLinks ? 2 : null);

    private sealed record ActionCase(
        string Name,
        ArchiveCreationRecoveryPhase Phase,
        ArchiveCreationRecoveryArtifactState Staging,
        ArchiveCreationRecoveryArtifactState Final,
        ArchiveCreationRecoveryAssessment Assessment,
        bool Commit,
        bool Discard,
        bool Accept,
        bool Acknowledge);

    private sealed record DispositionCall(
        string RecoveryId,
        ArchiveCreationRecoveryPhase Phase,
        int Sequence,
        DateTimeOffset RecordedAtUtc,
        ArchiveCreationRecoveryDispositionAction Action);

    private sealed class RecoveryArchiveClient : IArchiveClient
    {
        public event EventHandler<OperationEvent>? OperationEventReceived
        {
            add { }
            remove { }
        }

        public Queue<ListArchiveCreationRecoveriesResponse> InventoryResponses { get; } = new();
        public InspectArchiveCreationRecoveryResponse? Inspection { get; init; }
        public ArchiveClientException? DispositionException { get; init; }
        public bool SupportsInspection { get; init; } = true;
        public bool SupportsDisposition { get; init; } = true;
        public bool BlockInventoryUntilCancellation { get; init; }
        public TaskCompletionSource<bool> ListStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ListCalls { get; private set; }
        public int InspectCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public DispositionCall? LastDisposition { get; private set; }

        public bool SupportsFeature(string feature) => feature switch
        {
            ProtocolFeatures.ArchiveCreationRecoveryInspection => SupportsInspection,
            ProtocolFeatures.ArchiveCreationRecoveryDisposition => SupportsDisposition,
            _ => true,
        };

        public async Task<ListArchiveCreationRecoveriesResponse> ListArchiveCreationRecoveriesAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls++;
            ListStarted.TrySetResult(true);
            if (BlockInventoryUntilCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return InventoryResponses.Count > 0
                ? InventoryResponses.Dequeue()
                : CreateInventory();
        }

        public Task<InspectArchiveCreationRecoveryResponse> InspectArchiveCreationRecoveryAsync(
            string recoveryId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCalls++;
            Assert.AreEqual(RecoveryId, recoveryId);
            Assert.IsNotNull(Inspection);
            return Task.FromResult(Inspection!);
        }

        public Task<ApplyArchiveCreationRecoveryDispositionResponse>
            ApplyArchiveCreationRecoveryDispositionAsync(
                string recoveryId,
                ArchiveCreationRecoveryPhase expectedPhase,
                int expectedSequence,
                DateTimeOffset expectedRecordedAtUtc,
                ArchiveCreationRecoveryDispositionAction action,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCalls++;
            LastDisposition = new DispositionCall(
                recoveryId,
                expectedPhase,
                expectedSequence,
                expectedRecordedAtUtc,
                action);
            if (DispositionException is not null)
            {
                return Task.FromException<ApplyArchiveCreationRecoveryDispositionResponse>(
                    DispositionException);
            }

            var finalAssessment = action is
                ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging
                or ArchiveCreationRecoveryDispositionAction.AcceptPublishedFinal
                    ? ArchiveCreationRecoveryAssessment.OwnedFinalObserved
                    : ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved;
            return Task.FromResult(new ApplyArchiveCreationRecoveryDispositionResponse(
                recoveryId,
                action,
                ArchiveCreationRecoveryDispositionResult.Completed,
                finalAssessment,
                RecordedAtUtc.AddMinutes(2)));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<Guid> CreateSecretAsync(
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DiscardSecretAsync(
            Guid slotId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<OpenArchiveResponse> OpenArchiveAsync(
            string sourcePath,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<OpenNestedArchiveResponse> OpenNestedArchiveAsync(
            ArchiveSessionId parentSessionId,
            EntryId entryId,
            long maximumBytes = ProtocolConstants.DefaultNestedArchiveBytes,
            Guid? parentPasswordSlotId = null,
            Guid? nestedPasswordSlotId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ListEntriesResponse> ListEntriesAsync(
            ArchiveSessionId sessionId,
            int offset = 0,
            int limit = 512,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ArchiveEntryStreamLease> OpenEntryStreamAsync(
            ArchiveSessionId sessionId,
            EntryId entryId,
            long maximumBytes = ProtocolConstants.DefaultEntryStreamBytes,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CloseArchiveResponse> CloseArchiveAsync(
            ArchiveSessionId sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StartOperationResponse> StartExtractionAsync(
            ArchiveSessionId sessionId,
            IReadOnlyList<EntryId> entryIds,
            string destinationPath,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StartOperationResponse> StartIntegrityTestAsync(
            ArchiveSessionId sessionId,
            IReadOnlyList<EntryId> entryIds,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StartOperationResponse> StartZipCreationAsync(
            string destinationPath,
            IReadOnlyList<ArchiveCreationFile> files,
            ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StartOperationResponse> StartZipTreeCreationAsync(
            string destinationPath,
            IReadOnlyList<ArchiveCreationFile> files,
            IReadOnlyList<ArchiveCreationDirectory> directories,
            ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CancelOperationResponse> CancelOperationAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<OperationEvent> WaitForTerminalAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
