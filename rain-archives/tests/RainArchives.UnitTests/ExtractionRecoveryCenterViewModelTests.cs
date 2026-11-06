using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ExtractionRecoveryCenterViewModelTests
{
    private const string RecoveryId =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherRecoveryId =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset RecordedAtUtc =
        new(2026, 8, 31, 14, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task InventoryIsPathFreeAndInspectionIsAlwaysExplicit()
    {
        var client = new ExtractionRecoveryClient();
        client.InventoryResponses.Enqueue(CreateInventory(
            [CreateSummary(RecoveryId)],
            active: 2,
            suspicious: 3,
            incomplete: 4));
        await using var viewModel = new ExtractionRecoveryCenterViewModel(client);

        await viewModel.InitializeAsync();

        Assert.AreEqual(1, client.ListCalls);
        Assert.AreEqual(0, client.InspectCalls);
        Assert.AreEqual(1, viewModel.Recoveries.Count);
        Assert.IsFalse(viewModel.HasInspection);
        StringAssert.Contains(viewModel.InventorySummaryText, "2 active");
        StringAssert.Contains(viewModel.InventorySummaryText, "3 suspicious");
        StringAssert.Contains(viewModel.InventorySummaryText, "4 incomplete");
        Assert.IsFalse(viewModel.StatusText.Contains(@"C:\", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExplicitInspectionShowsBoundedEvidenceAndNoMutationAuthority()
    {
        var client = new ExtractionRecoveryClient
        {
            Inspection = CreateInspection(@"C:\Recovery\report‮txt"),
        };
        client.InventoryResponses.Enqueue(CreateInventory([CreateSummary(RecoveryId)]));
        await using var viewModel = new ExtractionRecoveryCenterViewModel(client);
        await viewModel.InitializeAsync();
        viewModel.SelectedRecovery = viewModel.Recoveries.Single();

        await viewModel.InspectSelectedAsync();

        Assert.AreEqual(1, client.InspectCalls);
        Assert.IsTrue(viewModel.HasInspection);
        Assert.IsFalse(viewModel.DestinationPathText.Contains('‮'));
        StringAssert.Contains(viewModel.DestinationPathText, "\\u202E");
        StringAssert.Contains(viewModel.StagingArtifactText, "owned identity match");
        StringAssert.Contains(viewModel.FinalArtifactText, "absent");
        StringAssert.Contains(viewModel.ActionGuidance, "read-only");
        StringAssert.Contains(viewModel.ActionGuidance, "Cleanup");
        StringAssert.Contains(viewModel.ActionGuidance, "retry");
    }

    [TestMethod]
    public async Task AdvertisedMutationAuthorityIsRejectedAndPriorEvidenceIsCleared()
    {
        var client = new ExtractionRecoveryClient
        {
            Inspection = CreateInspection(@"C:\Recovery\out", supportsMutation: true),
        };
        client.InventoryResponses.Enqueue(CreateInventory([CreateSummary(RecoveryId)]));
        await using var viewModel = new ExtractionRecoveryCenterViewModel(client);
        await viewModel.InitializeAsync();
        viewModel.SelectedRecovery = viewModel.Recoveries.Single();

        await viewModel.InspectSelectedAsync();

        Assert.IsFalse(viewModel.HasInspection);
        Assert.AreEqual(string.Empty, viewModel.DestinationPathText);
        StringAssert.Contains(viewModel.StatusText, "No recovery action");
    }

    [TestMethod]
    public async Task RefreshAndSelectionChangeInvalidateInspectedEvidence()
    {
        var client = new ExtractionRecoveryClient
        {
            Inspection = CreateInspection(@"C:\Recovery\out"),
        };
        client.InventoryResponses.Enqueue(CreateInventory(
            [CreateSummary(RecoveryId), CreateSummary(OtherRecoveryId)]));
        client.InventoryResponses.Enqueue(CreateInventory([CreateSummary(RecoveryId)]));
        await using var viewModel = new ExtractionRecoveryCenterViewModel(client);
        await viewModel.InitializeAsync();
        viewModel.SelectedRecovery = viewModel.Recoveries[0];
        await viewModel.InspectSelectedAsync();
        Assert.IsTrue(viewModel.HasInspection);

        viewModel.SelectedRecovery = viewModel.Recoveries[1];
        Assert.IsFalse(viewModel.HasInspection);
        await viewModel.RefreshAsync();

        Assert.IsNull(viewModel.SelectedRecovery);
        Assert.IsFalse(viewModel.HasInspection);
    }

    [TestMethod]
    public async Task FailureDoesNotRetainProviderOrExceptionText()
    {
        var client = new ExtractionRecoveryClient
        {
            InspectException = new ArchiveClientException(new ArchiveError(
                "provider.failure",
                ArchiveErrorCategory.ProviderFailure,
                "provider secret C:\\temp\\archive-name")),
        };
        client.InventoryResponses.Enqueue(CreateInventory([CreateSummary(RecoveryId)]));
        await using var viewModel = new ExtractionRecoveryCenterViewModel(client);
        await viewModel.InitializeAsync();
        viewModel.SelectedRecovery = viewModel.Recoveries.Single();

        await viewModel.InspectSelectedAsync();

        Assert.IsFalse(viewModel.HasInspection);
        Assert.IsFalse(viewModel.StatusText.Contains("provider", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(viewModel.StatusText.Contains("temp", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(viewModel.StatusText, "inspection failed", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task DowngradeAndCloseMakeNoUnsafeFallbackAndDrainOutstandingWork()
    {
        var downlevelClient = new ExtractionRecoveryClient { SupportsInspection = false };
        await using (var downlevel = new ExtractionRecoveryCenterViewModel(downlevelClient))
        {
            await downlevel.InitializeAsync();
            Assert.AreEqual(0, downlevelClient.ListCalls);
            Assert.IsFalse(downlevel.CanInspect);
            StringAssert.Contains(downlevel.StatusText, "does not support");
        }

        var blockedClient = new ExtractionRecoveryClient { BlockInventoryUntilCancellation = true };
        var blocked = new ExtractionRecoveryCenterViewModel(blockedClient);
        var initialization = blocked.InitializeAsync();
        await blockedClient.ListStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await blocked.DisposeAsync();
        await initialization.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(blocked.IsBusy);
        StringAssert.Contains(blocked.StatusText, "cancelled");
    }

    private static ExtractionRecoverySummary CreateSummary(string recoveryId) => new(
        recoveryId,
        ExtractionRecoveryInventoryState.Incomplete,
        ExtractionRecoveryPhase.Writing,
        Sequence: 1,
        RecordedAtUtc,
        HasIncompleteTail: false);

    private static InspectExtractionRecoveryResponse CreateInspection(
        string destinationPath,
        bool supportsMutation = false) => new(
        CreateSummary(RecoveryId),
        destinationPath,
        IsRemoteDestination: false,
        JournalFileCount: null,
        JournalDirectoryCount: null,
        JournalTotalBytes: null,
        JournalNamedStreamCount: null,
        JournalRevision: null,
        ExtractionRecoveryParentState.MatchesJournal,
        new ExtractionRecoveryArtifactInspection(
            ExtractionRecoveryArtifactState.IdentityMatchRevisionUnbound,
            ObservedFileCount: 1,
            ObservedDirectoryCount: 0,
            ObservedTotalBytes: 64,
            ObservedNamedStreamCount: 0),
        new ExtractionRecoveryArtifactInspection(ExtractionRecoveryArtifactState.Absent),
        ExtractionRecoveryAssessment.OwnedIncompleteStagingObserved,
        RecordedAtUtc.AddMinutes(1),
        SupportsMutation: supportsMutation,
        SupportsRetry: false,
        SupportsCleanup: false);

    private static ListExtractionRecoveriesResponse CreateInventory(
        IReadOnlyList<ExtractionRecoverySummary> recoveries,
        int active = 0,
        int suspicious = 0,
        int incomplete = 0) => new(
        recoveries,
        active,
        suspicious,
        incomplete,
        IsTruncated: false,
        InspectionIncomplete: false);

    private sealed class ExtractionRecoveryClient : ArchiveClientTestDouble
    {
        public Queue<ListExtractionRecoveriesResponse> InventoryResponses { get; } = new();
        public bool SupportsInspection { get; init; } = true;
        public bool BlockInventoryUntilCancellation { get; init; }
        public InspectExtractionRecoveryResponse? Inspection { get; init; }
        public Exception? InspectException { get; init; }
        public TaskCompletionSource<bool> ListStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ListCalls { get; private set; }
        public int InspectCalls { get; private set; }

        public override bool SupportsFeature(string feature) =>
            feature != ProtocolFeatures.ExtractionRecoveryInspection || SupportsInspection;

        public override async Task<ListExtractionRecoveriesResponse> ListExtractionRecoveriesAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls++;
            ListStarted.TrySetResult(true);
            if (BlockInventoryUntilCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return InventoryResponses.Count == 0 ? CreateInventory([]) : InventoryResponses.Dequeue();
        }

        public override Task<InspectExtractionRecoveryResponse> InspectExtractionRecoveryAsync(
            string recoveryId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCalls++;
            if (InspectException is not null)
            {
                return Task.FromException<InspectExtractionRecoveryResponse>(InspectException);
            }

            Assert.AreEqual(RecoveryId, recoveryId);
            Assert.IsNotNull(Inspection);
            return Task.FromResult(Inspection!);
        }
    }
}
