using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class NestedWorkspaceRecoveryCenterViewModelTests
{
    private const string RecoveryId =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherRecoveryId =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Revision =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string SourceId =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string ContentSha256 =
        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static readonly DateTimeOffset InspectedAtUtc =
        new(2026, 8, 24, 12, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task InitialInventoryIsPathFreeAndRequiresExplicitInspection()
    {
        var client = new NestedRecoveryArchiveClient();
        client.InventoryResponses.Enqueue(CreateInventory(
            [new NestedWorkspaceRecoverySummary(RecoveryId)],
            active: 2,
            suspicious: 3));
        await using var viewModel = CreateViewModel(client, confirmationResult: false);

        await viewModel.InitializeAsync();

        Assert.AreEqual(1, client.ListCalls);
        Assert.AreEqual(0, client.InspectCalls);
        Assert.AreEqual(0, client.DiscardCalls);
        Assert.AreEqual(1, viewModel.Recoveries.Count);
        Assert.AreEqual(RecoveryId, viewModel.Recoveries.Single().RecoveryId);
        Assert.AreEqual("aaaaaaaaaaaa…aaaaaaaa", viewModel.Recoveries.Single().DisplayId);
        Assert.IsFalse(viewModel.HasInspection);
        Assert.IsFalse(viewModel.CanDiscard);
        StringAssert.Contains(viewModel.InventorySummaryText, "2 active");
        StringAssert.Contains(viewModel.InventorySummaryText, "3 suspicious");
        StringAssert.Contains(viewModel.StatusText, "Active workspaces were left untouched");
        StringAssert.Contains(viewModel.StatusText, "Suspicious workspaces were preserved");
    }

    [TestMethod]
    public async Task VerifiedInspectionForwardsExactEvidenceAfterExplicitConfirmation()
    {
        var client = new NestedRecoveryArchiveClient
        {
            Inspection = CreateVerifiedInspection(sourceCount: 2, totalBytes: 4_096),
        };
        client.InventoryResponses.Enqueue(CreateInventory(
            [new NestedWorkspaceRecoverySummary(RecoveryId)]));
        client.InventoryResponses.Enqueue(CreateInventory([]));
        NestedWorkspaceRecoveryDiscardConfirmation? confirmation = null;
        await using var viewModel = new NestedWorkspaceRecoveryCenterViewModel(
            client,
            request =>
            {
                confirmation = request;
                return true;
            });
        await viewModel.InitializeAsync();
        viewModel.SelectedRecovery = viewModel.Recoveries.Single();

        await viewModel.InspectSelectedAsync();

        Assert.IsTrue(viewModel.HasInspection);
        Assert.IsTrue(viewModel.CanDiscard);
        Assert.AreEqual(RecoveryId, viewModel.RecoveryIdText);
        Assert.AreEqual(Revision, viewModel.RevisionText);
        Assert.AreEqual("2", viewModel.SourceCountText);
        StringAssert.Contains(viewModel.TotalSourceBytesText, "4.00 KiB");

        await viewModel.DiscardInspectedAsync();

        Assert.IsNotNull(confirmation);
        Assert.AreEqual(RecoveryId, confirmation.RecoveryId);
        Assert.AreEqual(Revision, confirmation.Revision);
        Assert.AreEqual(2, confirmation.SourceCount);
        Assert.AreEqual(4_096, confirmation.TotalSourceBytes);
        StringAssert.Contains(confirmation.Message, RecoveryId);
        StringAssert.Contains(confirmation.Message, Revision);
        StringAssert.Contains(confirmation.Message, "No archive destination");
        Assert.AreEqual(
            new DiscardCall(RecoveryId, Revision, 2, 4_096),
            client.LastDiscard);
        Assert.AreEqual(1, client.DiscardCalls);
        Assert.AreEqual(2, client.ListCalls);
        Assert.IsFalse(viewModel.HasInspection);
        Assert.IsFalse(viewModel.HasRecoveries);
        StringAssert.Contains(viewModel.StatusText, "verified temporary workspace was discarded");
    }

    [TestMethod]
    public async Task IndependentReopenForwardsFreshExactEvidenceAndReturnsDepthZeroSession()
    {
        var archive = CreateRecoveredArchive();
        var client = new NestedRecoveryArchiveClient
        {
            SupportsReopen = true,
            Inspection = CreateVerifiedInspection(sourceCount: 1, totalBytes: 64),
            ReopenInspection = CreateVerifiedReopenInspection(),
            ReopenResponse = new ReopenNestedWorkspaceRecoveryResponse(
                RecoveryId,
                SourceId,
                NestedWorkspaceRecoveryReopenMode.IndependentRoot,
                archive,
                MaterializedBytes: 64,
                Depth: 0,
                InspectedAtUtc.AddMinutes(1)),
        };
        client.InventoryResponses.Enqueue(CreateInventory(
            [new NestedWorkspaceRecoverySummary(RecoveryId)]));
        NestedWorkspaceRecoveryReopenConfirmation? confirmation = null;
        ReopenNestedWorkspaceRecoveryResponse? callbackResult = null;
        await using var viewModel = new NestedWorkspaceRecoveryCenterViewModel(
            client,
            _ => false,
            request =>
            {
                confirmation = request;
                return true;
            },
            requestPasswordSlot: null,
            reopened => callbackResult = reopened);
        await viewModel.InitializeAsync();
        viewModel.SelectedRecovery = viewModel.Recoveries.Single();

        await viewModel.InspectSelectedAsync();

        Assert.IsTrue(viewModel.CanReopen);
        Assert.AreEqual(1, viewModel.Sources.Count);
        Assert.AreEqual("Recovered source 1", viewModel.Sources.Single().DisplayName);
        Assert.IsFalse(viewModel.Sources.Single().DisplayName.Contains(SourceId, StringComparison.Ordinal));
        await viewModel.ReopenInspectedAsync();

        Assert.IsNotNull(confirmation);
        Assert.AreEqual(RecoveryId, confirmation.RecoveryId);
        Assert.AreEqual(Revision, confirmation.Revision);
        Assert.AreEqual(SourceId, confirmation.SourceId);
        Assert.AreEqual(ContentSha256, confirmation.ContentSha256);
        Assert.AreEqual(
            new ReopenCall(RecoveryId, Revision, 1, 64, SourceId, 64, ContentSha256, null),
            client.LastReopen);
        Assert.AreSame(archive, viewModel.ReopenedArchive!.Archive);
        Assert.AreSame(viewModel.ReopenedArchive, callbackResult);
        Assert.AreEqual(0, viewModel.ReopenedArchive.Depth);
        Assert.IsFalse(viewModel.ReopenedWithPassword);
        Assert.IsFalse(viewModel.HasInspection);
        StringAssert.Contains(viewModel.StatusText, "recovery workspace remains preserved");
    }

    [TestMethod]
    public async Task PasswordRequiredUsesOneExplicitSecretSlotAndNeverRetainsFailureText()
    {
        var passwordSlot = Guid.NewGuid();
        var passwordRequests = 0;
        var client = new NestedRecoveryArchiveClient
        {
            SupportsReopen = true,
            RequirePassword = true,
            Inspection = CreateVerifiedInspection(sourceCount: 1, totalBytes: 64),
            ReopenInspection = CreateVerifiedReopenInspection(),
            ReopenResponse = new ReopenNestedWorkspaceRecoveryResponse(
                RecoveryId,
                SourceId,
                NestedWorkspaceRecoveryReopenMode.IndependentRoot,
                CreateRecoveredArchive(),
                64,
                0,
                InspectedAtUtc.AddMinutes(1)),
        };
        client.InventoryResponses.Enqueue(CreateInventory(
            [new NestedWorkspaceRecoverySummary(RecoveryId)]));
        await using var viewModel = new NestedWorkspaceRecoveryCenterViewModel(
            client,
            _ => false,
            _ => true,
            _ =>
            {
                passwordRequests++;
                return Task.FromResult<Guid?>(passwordSlot);
            });
        await viewModel.InitializeAsync();
        viewModel.SelectedRecovery = viewModel.Recoveries.Single();
        await viewModel.InspectSelectedAsync();

        await viewModel.ReopenInspectedAsync();

        Assert.AreEqual(2, client.ReopenCalls);
        Assert.AreEqual(1, passwordRequests);
        Assert.AreEqual(passwordSlot, client.LastReopen!.PasswordSlotId);
        Assert.IsTrue(viewModel.ReopenedWithPassword);
    }

    [TestMethod]
    public async Task DeclinedDefaultNoConfirmationSendsNoDiscardAndKeepsFreshEvidence()
    {
        var client = new NestedRecoveryArchiveClient
        {
            Inspection = CreateVerifiedInspection(sourceCount: 1, totalBytes: 64),
        };
        client.InventoryResponses.Enqueue(CreateInventory(
            [new NestedWorkspaceRecoverySummary(RecoveryId)]));
        await using var viewModel = CreateViewModel(client, confirmationResult: false);
        await viewModel.InitializeAsync();
        viewModel.SelectedRecovery = viewModel.Recoveries.Single();
        await viewModel.InspectSelectedAsync();

        await viewModel.DiscardInspectedAsync();

        Assert.AreEqual(0, client.DiscardCalls);
        Assert.AreEqual(1, client.ListCalls);
        Assert.IsTrue(viewModel.HasInspection);
        Assert.IsTrue(viewModel.CanDiscard);
        Assert.AreEqual("No nested-workspace recovery changes were made.", viewModel.StatusText);
    }

    [TestMethod]
    public async Task UnverifiedAssessmentsNeverEnableDiscardOrForwardARequest()
    {
        var assessments = new[]
        {
            NestedWorkspaceRecoveryAssessment.Active,
            NestedWorkspaceRecoveryAssessment.IdentityUnavailable,
            NestedWorkspaceRecoveryAssessment.UnexpectedContents,
            NestedWorkspaceRecoveryAssessment.InspectionIncomplete,
        };

        foreach (var assessment in assessments)
        {
            var client = new NestedRecoveryArchiveClient
            {
                Inspection = new InspectNestedWorkspaceRecoveryResponse(
                    RecoveryId,
                    assessment,
                    SourceCount: null,
                    TotalSourceBytes: null,
                    Revision: null,
                    InspectedAtUtc),
            };
            client.InventoryResponses.Enqueue(CreateInventory(
                [new NestedWorkspaceRecoverySummary(RecoveryId)]));
            await using var viewModel = CreateViewModel(client, confirmationResult: true);
            await viewModel.InitializeAsync();
            viewModel.SelectedRecovery = viewModel.Recoveries.Single();

            await viewModel.InspectSelectedAsync();
            await viewModel.DiscardInspectedAsync();

            Assert.IsFalse(viewModel.CanDiscard, assessment.ToString());
            Assert.AreEqual(0, client.DiscardCalls, assessment.ToString());
            StringAssert.Contains(viewModel.StatusText, "not available");
        }
    }

    [TestMethod]
    public async Task RefreshAndSelectionChangeClearPreviouslyInspectedAuthority()
    {
        var client = new NestedRecoveryArchiveClient
        {
            Inspection = CreateVerifiedInspection(sourceCount: 1, totalBytes: 64),
        };
        client.InventoryResponses.Enqueue(CreateInventory(
        [
            new NestedWorkspaceRecoverySummary(RecoveryId),
            new NestedWorkspaceRecoverySummary(OtherRecoveryId),
        ]));
        client.InventoryResponses.Enqueue(CreateInventory(
            [new NestedWorkspaceRecoverySummary(RecoveryId)]));
        await using var viewModel = CreateViewModel(client, confirmationResult: true);
        await viewModel.InitializeAsync();
        viewModel.SelectedRecovery = viewModel.Recoveries[0];
        await viewModel.InspectSelectedAsync();
        Assert.IsTrue(viewModel.CanDiscard);

        viewModel.SelectedRecovery = viewModel.Recoveries[1];
        Assert.IsFalse(viewModel.HasInspection);
        Assert.IsFalse(viewModel.CanDiscard);

        await viewModel.RefreshAsync();
        Assert.IsNull(viewModel.SelectedRecovery);
        Assert.IsFalse(viewModel.HasInspection);
        Assert.IsFalse(viewModel.CanDiscard);
    }

    [TestMethod]
    public async Task FailedDiscardIsNeverRetriedAndDoesNotRetainUntrustedFailureText()
    {
        var client = new NestedRecoveryArchiveClient
        {
            Inspection = CreateVerifiedInspection(sourceCount: 1, totalBytes: 64),
            DiscardException = new ArchiveClientException(new ArchiveError(
                "nested_recovery.changed",
                ArchiveErrorCategory.SourceChanged,
                "Changed ‮ path")),
        };
        client.InventoryResponses.Enqueue(CreateInventory(
            [new NestedWorkspaceRecoverySummary(RecoveryId)]));
        await using var viewModel = CreateViewModel(client, confirmationResult: true);
        await viewModel.InitializeAsync();
        viewModel.SelectedRecovery = viewModel.Recoveries.Single();
        await viewModel.InspectSelectedAsync();

        await viewModel.DiscardInspectedAsync();

        Assert.AreEqual(1, client.DiscardCalls);
        Assert.AreEqual(1, client.ListCalls);
        Assert.IsFalse(viewModel.HasInspection);
        Assert.IsFalse(viewModel.CanDiscard);
        Assert.IsFalse(viewModel.StatusText.Contains('‮'));
        Assert.IsFalse(viewModel.StatusText.Contains("path", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(viewModel.StatusText, "did not retry automatically");
        StringAssert.Contains(viewModel.StatusText, "Refresh and inspect again");
    }

    [TestMethod]
    public async Task DowngradedWorkerMakesNoRecoveryRequest()
    {
        var client = new NestedRecoveryArchiveClient
        {
            SupportsRecovery = false,
        };
        await using var viewModel = CreateViewModel(client, confirmationResult: true);

        await viewModel.InitializeAsync();

        Assert.AreEqual(0, client.ListCalls);
        Assert.AreEqual(0, client.InspectCalls);
        Assert.AreEqual(0, client.DiscardCalls);
        Assert.IsFalse(viewModel.HasRecoveries);
        Assert.IsFalse(viewModel.CanInspect);
        Assert.IsFalse(viewModel.CanDiscard);
        StringAssert.Contains(viewModel.StatusText, "does not support");
    }

    [TestMethod]
    public async Task ClosingTheViewModelCancelsAndDrainsItsOutstandingRequest()
    {
        var client = new NestedRecoveryArchiveClient
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
        Assert.AreEqual(
            "Nested-workspace recovery review cancelled.",
            viewModel.StatusText);
    }

    private static NestedWorkspaceRecoveryCenterViewModel CreateViewModel(
        NestedRecoveryArchiveClient client,
        bool confirmationResult) =>
        new(client, _ => confirmationResult);

    private static InspectNestedWorkspaceRecoveryResponse CreateVerifiedInspection(
        int sourceCount,
        long totalBytes) =>
        new(
            RecoveryId,
            NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
            sourceCount,
            totalBytes,
            Revision,
            InspectedAtUtc);

    private static ListNestedWorkspaceRecoveriesResponse CreateInventory(
        IReadOnlyList<NestedWorkspaceRecoverySummary> recoveries,
        int active = 0,
        int suspicious = 0,
        bool isTruncated = false,
        bool inspectionIncomplete = false) =>
        new(
            recoveries,
            active,
            suspicious,
            isTruncated,
            inspectionIncomplete);

    private static InspectNestedWorkspaceReopenResponse CreateVerifiedReopenInspection() => new(
        RecoveryId,
        NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
        SourceCount: 1,
        TotalSourceBytes: 64,
        Revision,
        Sources: [new NestedWorkspaceRecoverySourceEvidence(SourceId, 64, ContentSha256)],
        InspectedAtUtc,
        SupportsIndependentReopen: true);

    private static ArchiveDescriptor CreateRecoveredArchive() => new(
        ArchiveSessionId.New(),
        "zip",
        "provider-controlled",
        ArchiveCapability.List | ArchiveCapability.Extract | ArchiveCapability.Test,
        new ArchiveFingerprint(
            64,
            InspectedAtUtc,
            "provider-volume",
            new string('A', 64)),
        EntryCount: 0,
        IsSolid: false,
        HasEncryptedEntries: false,
        IsMultipart: false,
        IsReadOnly: true,
        Warnings: []);

    private sealed record DiscardCall(
        string RecoveryId,
        string Revision,
        int SourceCount,
        long TotalSourceBytes);

    private sealed record ReopenCall(
        string RecoveryId,
        string Revision,
        int SourceCount,
        long TotalSourceBytes,
        string SourceId,
        long SourceLength,
        string ContentSha256,
        Guid? PasswordSlotId);

    private sealed class NestedRecoveryArchiveClient : ArchiveClientTestDouble
    {
        public Queue<ListNestedWorkspaceRecoveriesResponse> InventoryResponses { get; } = new();

        public InspectNestedWorkspaceRecoveryResponse? Inspection { get; init; }

        public ArchiveClientException? DiscardException { get; init; }

        public InspectNestedWorkspaceReopenResponse? ReopenInspection { get; init; }

        public ReopenNestedWorkspaceRecoveryResponse? ReopenResponse { get; init; }

        public bool RequirePassword { get; init; }

        public bool SupportsRecovery { get; init; } = true;

        public bool SupportsReopen { get; init; }

        public bool BlockInventoryUntilCancellation { get; init; }

        public TaskCompletionSource<bool> ListStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ListCalls { get; private set; }

        public int InspectCalls { get; private set; }

        public int DiscardCalls { get; private set; }

        public int ReopenInspectCalls { get; private set; }

        public int ReopenCalls { get; private set; }

        public DiscardCall? LastDiscard { get; private set; }

        public ReopenCall? LastReopen { get; private set; }

        public override bool SupportsFeature(string feature) => feature switch
        {
            ProtocolFeatures.NestedWorkspaceRecovery => SupportsRecovery,
            ProtocolFeatures.NestedWorkspaceIndependentReopen => SupportsReopen,
            _ => base.SupportsFeature(feature),
        };

        public override async Task<ListNestedWorkspaceRecoveriesResponse>
            ListNestedWorkspaceRecoveriesAsync(
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
                : CreateInventory([]);
        }

        public override Task<InspectNestedWorkspaceRecoveryResponse>
            InspectNestedWorkspaceRecoveryAsync(
                string recoveryId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCalls++;
            Assert.AreEqual(RecoveryId, recoveryId);
            Assert.IsNotNull(Inspection);
            return Task.FromResult(Inspection!);
        }

        public override Task<DiscardNestedWorkspaceRecoveryResponse>
            DiscardNestedWorkspaceRecoveryAsync(
                string recoveryId,
                string expectedRevision,
                int expectedSourceCount,
                long expectedTotalSourceBytes,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscardCalls++;
            LastDiscard = new DiscardCall(
                recoveryId,
                expectedRevision,
                expectedSourceCount,
                expectedTotalSourceBytes);
            if (DiscardException is not null)
            {
                return Task.FromException<DiscardNestedWorkspaceRecoveryResponse>(
                    DiscardException);
            }

            return Task.FromResult(new DiscardNestedWorkspaceRecoveryResponse(
                recoveryId,
                NestedWorkspaceRecoveryDiscardResult.Discarded,
                InspectedAtUtc.AddMinutes(1)));
        }

        public override Task<InspectNestedWorkspaceReopenResponse>
            InspectNestedWorkspaceReopenAsync(
                string recoveryId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReopenInspectCalls++;
            Assert.AreEqual(RecoveryId, recoveryId);
            Assert.IsNotNull(ReopenInspection);
            return Task.FromResult(ReopenInspection!);
        }

        public override Task<ReopenNestedWorkspaceRecoveryResponse>
            ReopenNestedWorkspaceRecoveryAsync(
                string recoveryId,
                string expectedRevision,
                int expectedSourceCount,
                long expectedTotalSourceBytes,
                string sourceId,
                long expectedSourceLength,
                string expectedContentSha256,
                Guid? passwordSlotId = null,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReopenCalls++;
            LastReopen = new ReopenCall(
                recoveryId,
                expectedRevision,
                expectedSourceCount,
                expectedTotalSourceBytes,
                sourceId,
                expectedSourceLength,
                expectedContentSha256,
                passwordSlotId);
            if (RequirePassword && passwordSlotId is null)
            {
                return Task.FromException<ReopenNestedWorkspaceRecoveryResponse>(
                    new ArchiveClientException(new ArchiveError(
                        "archive.password_required",
                        ArchiveErrorCategory.PasswordRequired,
                        "A password is required.")));
            }

            Assert.IsNotNull(ReopenResponse);
            return Task.FromResult(ReopenResponse!);
        }
    }
}
