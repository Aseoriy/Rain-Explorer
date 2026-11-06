using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class ArchiveClientProtocolTests
{
    private static readonly IReadOnlySet<string> EnabledFeatures =
        ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion)
            .ToHashSet(StringComparer.Ordinal);

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_RejectsRebuildCapabilityOnGlobalRequest()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var capabilityTask = harness.Client.GetZipWriteCapabilitiesAsync();
        var requestFrame = await harness.ReadRequestAsync();
        var request = requestFrame.ReadPayload<GetZipWriteCapabilitiesRequest>();

        Assert.IsNull(request.SessionId);
        await harness.RespondAsync(requestFrame, CreateCapabilitiesResponse(canRebuild: true));

        await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await capabilityTask);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_AcceptsRebuildCapabilityForSessionBoundRequest()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var sessionId = new ArchiveSessionId(
            new Guid("00112233-4455-6677-8899-aabbccddeeff"));
        var capabilityTask = harness.Client.GetZipWriteCapabilitiesAsync(sessionId);
        var requestFrame = await harness.ReadRequestAsync();
        var request = requestFrame.ReadPayload<GetZipWriteCapabilitiesRequest>();

        Assert.AreEqual(sessionId, request.SessionId);
        await harness.RespondAsync(requestFrame, CreateCapabilitiesResponse(canRebuild: true));

        var response = await capabilityTask;
        Assert.IsTrue(response.CanRebuild);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_RebuildCopyDispatchesTypedRequestAndAuthorizesOperationEvents()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var sessionId = new ArchiveSessionId(
            new Guid("00112233-4455-6677-8899-aabbccddeeff"));
        var operationId = OperationId.New();
        var startTask = harness.Client.StartZipRebuildCopyAsync(
            sessionId,
            @"C:\rebuilt.zip",
            [new ArchiveEntryKey("docs/readme.txt", 0)],
            [new ArchiveRebuildCopyFile(@"C:\replacement.txt", "docs/readme.txt")],
            ArchiveCompressionPreset.Fast);

        var requestFrame = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.StartZipRebuildCopy, requestFrame.Kind);
        var request = requestFrame.ReadPayload<StartZipRebuildCopyRequest>();
        Assert.AreEqual(sessionId, request.SessionId);
        Assert.AreEqual(@"C:\rebuilt.zip", request.DestinationPath);
        CollectionAssert.AreEqual(
            new[] { new ArchiveEntryKey("docs/readme.txt", 0) },
            request.EntriesToDelete.ToArray());
        CollectionAssert.AreEqual(
            new[] { new ArchiveRebuildCopyFile(@"C:\replacement.txt", "docs/readme.txt") },
            request.Files.ToArray());
        Assert.AreEqual(ArchiveCompressionPreset.Fast, request.CompressionPreset);

        await harness.RespondAsync(requestFrame, new StartOperationResponse(operationId));
        Assert.AreEqual(operationId, (await startTask).OperationId);

        await harness.WriteOperationEventAsync(new OperationEvent(
            operationId,
            1,
            OperationEventKind.StateChanged,
            OperationPhase.Queued));
        await harness.WriteOperationEventAsync(new OperationEvent(
            operationId,
            2,
            OperationEventKind.StateChanged,
            OperationPhase.Validating));
        await harness.WriteOperationEventAsync(new OperationEvent(
            operationId,
            3,
            OperationEventKind.StateChanged,
            OperationPhase.Running));

        var terminal = new OperationEvent(
            operationId,
            4,
            OperationEventKind.Terminal,
            OperationPhase.Completed);
        await harness.WriteOperationEventAsync(terminal);

        Assert.AreEqual(terminal, await harness.Client.WaitForTerminalAsync(operationId));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_RebuildRenameDispatchesTypedRequestAndAuthorizesOperationEvents()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var sessionId = new ArchiveSessionId(
            new Guid("00112233-4455-6677-8899-aabbccddeeff"));
        var operationId = OperationId.New();
        var startTask = harness.Client.StartZipRebuildRenameAsync(
            sessionId,
            @"C:\rebuilt.zip",
            [new ArchiveEntryKey("docs/readme.txt", 0)],
            [new ArchiveRebuildCopyFile(@"C:\replacement.txt", "docs/readme.txt")],
            [new ArchiveRebuildRename(
                new ArchiveEntryKey("docs/guide.txt", 0),
                "manual/guide.txt")],
            ArchiveCompressionPreset.Fast);

        var requestFrame = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.StartZipRebuildRename, requestFrame.Kind);
        var request = requestFrame.ReadPayload<StartZipRebuildRenameRequest>();
        Assert.AreEqual(sessionId, request.SessionId);
        Assert.AreEqual(@"C:\rebuilt.zip", request.DestinationPath);
        CollectionAssert.AreEqual(
            new[] { new ArchiveEntryKey("docs/readme.txt", 0) },
            request.EntriesToDelete.ToArray());
        CollectionAssert.AreEqual(
            new[] { new ArchiveRebuildCopyFile(@"C:\replacement.txt", "docs/readme.txt") },
            request.Files.ToArray());
        CollectionAssert.AreEqual(
            new[] { new ArchiveRebuildRename(
                new ArchiveEntryKey("docs/guide.txt", 0),
                "manual/guide.txt") },
            request.Renames.ToArray());
        Assert.AreEqual(ArchiveCompressionPreset.Fast, request.CompressionPreset);

        await harness.RespondAsync(requestFrame, new StartOperationResponse(operationId));
        Assert.AreEqual(operationId, (await startTask).OperationId);

        await harness.WriteOperationEventAsync(new OperationEvent(
            operationId, 1, OperationEventKind.StateChanged, OperationPhase.Queued));
        await harness.WriteOperationEventAsync(new OperationEvent(
            operationId, 2, OperationEventKind.StateChanged, OperationPhase.Validating));
        await harness.WriteOperationEventAsync(new OperationEvent(
            operationId, 3, OperationEventKind.StateChanged, OperationPhase.Running));
        var terminal = new OperationEvent(
            operationId, 4, OperationEventKind.Terminal, OperationPhase.Completed);
        await harness.WriteOperationEventAsync(terminal);

        Assert.AreEqual(terminal, await harness.Client.WaitForTerminalAsync(operationId));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_TreePlanQueryDispatchesTypedRequestAndValidatesResponse()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var sessionId = ArchiveSessionId.New();
        var queryTask = harness.Client.GetZipRebuildTreePlanAsync(sessionId);
        var request = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.GetZipRebuildTreePlan, request.Kind);
        Assert.AreEqual(sessionId, request.ReadPayload<GetZipRebuildTreePlanRequest>().SessionId);

        var response = new GetZipRebuildTreePlanResponse(
            [new ZipRebuildTreePlanNode("docs", true, 2, 2, new string('a', 64))]);
        await harness.RespondAsync(request, response);
        var result = await queryTask;
        Assert.AreEqual("docs", result.Directories.Single().NormalizedPath);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_SourceFolderReviewDispatchesTypedRequestAndValidatesResponse()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var sessionId = ArchiveSessionId.New();
        var queryTask = harness.Client.GetZipRebuildSourceFolderReviewAsync(
            sessionId,
            [new ArchiveCreationDirectory(@"C:\folder", "Imported/")]);
        var request = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.GetZipRebuildSourceFolderReview, request.Kind);
        var payload = request.ReadPayload<GetZipRebuildSourceFolderReviewRequest>();
        Assert.AreEqual(sessionId, payload.SessionId);
        CollectionAssert.AreEqual(
            new[] { new ArchiveCreationDirectory(@"C:\folder", "Imported/") },
            payload.Directories.ToArray());

        await harness.RespondAsync(
            request,
            new GetZipRebuildSourceFolderReviewResponse(
                [new ZipRebuildSourceFolderReview(@"C:\folder", "Imported", 4, 2)]));
        var result = await queryTask;
        Assert.AreEqual("Imported", result.Directories.Single().ArchiveRoot);
        Assert.AreEqual(4, result.Directories.Single().DescendantCount);
        Assert.AreEqual(2, result.Directories.Single().FileCount);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_SourceFolderReviewRejectsMisleadingSourceOrRoot()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var queryTask = harness.Client.GetZipRebuildSourceFolderReviewAsync(
            ArchiveSessionId.New(),
            [new ArchiveCreationDirectory(@"C:\folder", "Imported")]);
        var request = await harness.ReadRequestAsync();
        await harness.RespondAsync(
            request,
            new GetZipRebuildSourceFolderReviewResponse(
                [new ZipRebuildSourceFolderReview(@"C:\other", "Imported", 1, 1)]));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await queryTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_SourceFolderReviewRejectsImpossibleAndAggregateCounts()
    {
        await using var impossibleHarness = await ArchiveClientProtocolHarness.StartAsync();
        var impossibleTask = impossibleHarness.Client.GetZipRebuildSourceFolderReviewAsync(
            ArchiveSessionId.New(),
            [new ArchiveCreationDirectory(@"C:\folder", "Imported")]);
        var impossibleRequest = await impossibleHarness.ReadRequestAsync();
        await impossibleHarness.RespondAsync(
            impossibleRequest,
            new GetZipRebuildSourceFolderReviewResponse(
                [new ZipRebuildSourceFolderReview(@"C:\folder", "Imported", 1, 2)]));
        var impossibleFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(
            async () => await impossibleTask);
        Assert.AreEqual("protocol.invalid_worker_frame", impossibleFailure.Error.Code);

        await using var aggregateHarness = await ArchiveClientProtocolHarness.StartAsync();
        var aggregateTask = aggregateHarness.Client.GetZipRebuildSourceFolderReviewAsync(
            ArchiveSessionId.New(),
            [
                new ArchiveCreationDirectory(@"C:\first", "first"),
                new ArchiveCreationDirectory(@"C:\second", "second"),
            ]);
        var aggregateRequest = await aggregateHarness.ReadRequestAsync();
        await aggregateHarness.RespondAsync(
            aggregateRequest,
            new GetZipRebuildSourceFolderReviewResponse(
            [
                new ZipRebuildSourceFolderReview(@"C:\first", "first", 3_000, 1),
                new ZipRebuildSourceFolderReview(@"C:\second", "second", 2_000, 1),
            ]));
        var aggregateFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(
            async () => await aggregateTask);
        Assert.AreEqual("protocol.invalid_worker_frame", aggregateFailure.Error.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_TreeEditDispatchesTypedRequestAndAuthorizesOperationEvents()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var operationId = OperationId.New();
        var startTask = harness.Client.StartZipRebuildTreeEditAsync(
            ArchiveSessionId.New(),
            @"C:\rebuilt.zip",
            [new ArchiveEntryKey("docs/old.txt", 0)],
            [new ArchiveRebuildCopyFile(@"C:\replacement.txt", "docs/new.txt")],
            [],
            [new ArchiveCreationDirectory(@"C:\folder", "added")],
            [new ArchiveRebuildTreeAction("docs", new string('b', 64), ArchiveRebuildTreeActionKind.Move, "manual")],
            ArchiveCompressionPreset.Fast);

        var request = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.StartZipRebuildTreeEdit, request.Kind);
        var payload = request.ReadPayload<StartZipRebuildTreeEditRequest>();
        Assert.AreEqual(1, payload.Directories.Count);
        Assert.AreEqual(1, payload.TreeActions.Count);
        await harness.RespondAsync(request, new StartOperationResponse(operationId));
        Assert.AreEqual(operationId, (await startTask).OperationId);

        await harness.WriteOperationEventAsync(new OperationEvent(operationId, 1, OperationEventKind.StateChanged, OperationPhase.Queued));
        await harness.WriteOperationEventAsync(new OperationEvent(operationId, 2, OperationEventKind.StateChanged, OperationPhase.Validating));
        await harness.WriteOperationEventAsync(new OperationEvent(operationId, 3, OperationEventKind.StateChanged, OperationPhase.Running));
        var terminal = new OperationEvent(operationId, 4, OperationEventKind.Terminal, OperationPhase.Completed);
        await harness.WriteOperationEventAsync(terminal);
        Assert.AreEqual(terminal, await harness.Client.WaitForTerminalAsync(operationId));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorThirteenRefusesTreeEditBeforeSendingAFrame()
    {
        var minorThirteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipRebuildRenameMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorThirteen);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.GetZipRebuildTreePlanAsync(ArchiveSessionId.New()));

        Assert.AreEqual("zip.rebuild_tree_edit_feature_not_negotiated", failure.Error.Code);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorThirteenRefusesSourceFolderReviewBeforeSendingAFrame()
    {
        var minorThirteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipRebuildRenameMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorThirteen);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.GetZipRebuildSourceFolderReviewAsync(
                ArchiveSessionId.New(),
                [new ArchiveCreationDirectory(@"C:\folder", "Imported")]))
            .ConfigureAwait(false);

        Assert.AreEqual(
            "zip.rebuild_source_folder_review_feature_not_negotiated",
            failure.Error.Code);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_TreePlanRejectsInvalidRevisionBeforeReturning()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var queryTask = harness.Client.GetZipRebuildTreePlanAsync(ArchiveSessionId.New());
        var request = await harness.ReadRequestAsync();
        await harness.RespondAsync(
            request,
            new GetZipRebuildTreePlanResponse(
                [new ZipRebuildTreePlanNode("docs", false, 0, 0, new string('A', 64))]));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await queryTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorTwelveRefusesRebuildRenameBeforeSendingAFrame()
    {
        var minorTwelveFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipRebuildCopyMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorTwelveFeatures);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.StartZipRebuildRenameAsync(
                ArchiveSessionId.New(),
                @"C:\rebuilt.zip",
                [],
                [],
                []));

        Assert.AreEqual(
            "zip.rebuild_rename_feature_not_negotiated",
            failure.Error.Code);
        Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, failure.Error.Category);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_InvalidRebuildRenameShapeIsRefusedBeforeSendingAFrame()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var duplicateKeys = new[]
        {
            new ArchiveRebuildRename(new ArchiveEntryKey("docs/a.txt", 0), "docs/a-new.txt"),
            new ArchiveRebuildRename(new ArchiveEntryKey("docs/a.txt", 0), "docs/a-other.txt"),
        };

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await harness.Client.StartZipRebuildRenameAsync(
                ArchiveSessionId.New(),
                @"C:\rebuilt.zip",
                [],
                [],
                duplicateKeys));

        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_OversizedTypedRebuildRenameFailsLocallyWithoutPoisoningTransport()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var longRelativePath = string.Join(
            '/',
            Enumerable.Range(0, 100).Select(index =>
                $"{index:D3}-{new string('a', 246)}"));
        var oversizedRenames = Enumerable.Range(0, 64)
            .Select(index => new ArchiveRebuildRename(
                new ArchiveEntryKey($"source-{index:D2}.txt", 0),
                $"{longRelativePath}/target-{index:D2}.txt"))
            .ToArray();

        var failure = await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await harness.Client.StartZipRebuildRenameAsync(
                ArchiveSessionId.New(),
                @"C:\rebuilt.zip",
                [],
                [],
                oversizedRenames));

        StringAssert.Contains(failure.Message, "bounded protocol frame size");
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));

        var healthTask = harness.Client.ListNestedWorkspaceRecoveriesAsync();
        var healthRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.ListNestedWorkspaceRecoveries, healthRequest.Kind);
        await harness.RespondAsync(
            healthRequest,
            new ListNestedWorkspaceRecoveriesResponse(
                [], 0, 0, false, false));
        Assert.HasCount(0, (await healthTask).Recoveries);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_OldWorkerFeatureSetRefusesRebuildCopyBeforeSendingAFrame()
    {
        var minorElevenFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.NestedWorkspaceRecoveryMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorElevenFeatures);

        Assert.IsFalse(harness.Client.SupportsFeature(ProtocolFeatures.ZipRebuildCopy));
        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.StartZipRebuildCopyAsync(
                ArchiveSessionId.New(),
                @"C:\rebuilt.zip",
                [],
                []));

        Assert.AreEqual(
            "zip.rebuild_copy_feature_not_negotiated",
            failure.Error.Code);
        Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, failure.Error.Category);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_InvalidRebuildCopyShapeIsRefusedBeforeSendingAFrame()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var tooManyDeletes = Enumerable.Range(0, ProtocolConstants.MaximumZipCreationEntries + 1)
            .Select(index => new ArchiveEntryKey($"entry-{index}.txt", 0))
            .ToArray();

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await harness.Client.StartZipRebuildCopyAsync(
                ArchiveSessionId.New(),
                @"C:\rebuilt.zip",
                tooManyDeletes,
                []));

        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_OversizedTypedRebuildFailsLocallyWithoutPoisoningTransport()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var longRelativePath = string.Join(
            '/',
            Enumerable.Range(0, 100).Select(index =>
                $"{index:D3}-{new string('a', 246)}"));
        var oversizedMappings = Enumerable.Range(0, 32)
            .Select(index => new ArchiveRebuildCopyFile(
                $@"C:\{longRelativePath.Replace('/', '\\')}\source-{index:D2}.txt",
                $"{longRelativePath}/target-{index:D2}.txt"))
            .ToArray();

        var failure = await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await harness.Client.StartZipRebuildCopyAsync(
                ArchiveSessionId.New(),
                @"C:\rebuilt.zip",
                [],
                oversizedMappings));

        StringAssert.Contains(failure.Message, "bounded protocol frame size");
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));

        var healthTask = harness.Client.ListNestedWorkspaceRecoveriesAsync();
        var healthRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.ListNestedWorkspaceRecoveries, healthRequest.Kind);
        await harness.RespondAsync(
            healthRequest,
            new ListNestedWorkspaceRecoveriesResponse(
                [],
                ActiveWorkspaceCount: 0,
                SuspiciousWorkspaceCount: 0,
                IsTruncated: false,
                InspectionIncomplete: false));
        Assert.HasCount(0, (await healthTask).Recoveries);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_InvalidOpenPathDiscardsSuppliedSlotExactlyOnce()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var slotId = Guid.NewGuid();
        var createTask = harness.Client.CreateSecretAsync("invalid-open-path".AsMemory());
        var createRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.CreateSecretSlot, createRequest.Kind);
        await harness.RespondAsync(
            createRequest,
            new CreateSecretSlotResponse(slotId, DateTimeOffset.UtcNow.AddMinutes(5)));

        var secret = await harness.ReadSecretAsync();
        Assert.AreEqual(slotId, secret.SlotId);
        Assert.IsTrue(secret.SecretBytes.Length > 0);
        CryptographicOperations.ZeroMemory(secret.SecretBytes);
        await createTask;

        var invalidOpen = harness.Client.OpenArchiveAsync(" ", slotId);
        var discardRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.DiscardSecretSlot, discardRequest.Kind);
        var discard = discardRequest.ReadPayload<DiscardSecretSlotRequest>();
        Assert.AreEqual(slotId, discard.SlotId);
        await harness.RespondAsync(discardRequest, new DiscardSecretSlotResponse(true));

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await invalidOpen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_PreCancelledRecoveryReopenSettlesSlotWithoutReopenFrame()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var slotId = await CreateSecretSlotAsync(harness, "cancelled-recovery-reopen");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var reopenTask = harness.Client.ReopenNestedWorkspaceRecoveryAsync(
            new string('a', 64),
            new string('b', 64),
            expectedSourceCount: 1,
            expectedTotalSourceBytes: 1,
            new string('c', 64),
            expectedSourceLength: 1,
            new string('d', 64),
            slotId,
            cancellation.Token);
        var discardRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.DiscardSecretSlot, discardRequest.Kind);
        Assert.AreEqual(slotId, discardRequest.ReadPayload<DiscardSecretSlotRequest>().SlotId);
        await harness.RespondAsync(discardRequest, new DiscardSecretSlotResponse(true));

        try
        {
            await reopenTask;
            Assert.Fail("The pre-cancelled recovery reopen unexpectedly completed.");
        }
        catch (OperationCanceledException exception)
        {
            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
        }
        await AssertNoPendingRecoveryRequestAsync(harness);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_InvalidRecoveryReopenSettlesTransferredSlotExactlyOnce()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var slotId = await CreateSecretSlotAsync(harness, "invalid-recovery-reopen");

        var reopenTask = harness.Client.ReopenNestedWorkspaceRecoveryAsync(
            "invalid",
            new string('b', 64),
            expectedSourceCount: 1,
            expectedTotalSourceBytes: 1,
            new string('c', 64),
            expectedSourceLength: 1,
            new string('d', 64),
            slotId);
        var discardRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.DiscardSecretSlot, discardRequest.Kind);
        Assert.AreEqual(slotId, discardRequest.ReadPayload<DiscardSecretSlotRequest>().SlotId);
        await harness.RespondAsync(discardRequest, new DiscardSecretSlotResponse(true));

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await reopenTask);
        await AssertNoPendingRecoveryRequestAsync(harness);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_GateCancelledNestedOpenSettlesBothSlotsWithoutOpenFrame()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var parentSlotId = await CreateSecretSlotAsync(harness, "cancelled-parent-open");
        var nestedSlotId = await CreateSecretSlotAsync(harness, "cancelled-nested-open");
        var sessionGate = GetPrivateSemaphore(harness.Client, "_sessionLifecycleGate");
        await sessionGate.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var openTask = harness.Client.OpenNestedArchiveAsync(
                ArchiveSessionId.New(),
                EntryId.New(),
                maximumBytes: 1,
                parentSlotId,
                nestedSlotId,
                cancellation.Token);
            cancellation.Cancel();

            var parentDiscard = await harness.ReadRequestAsync();
            Assert.AreEqual(ProtocolMessageKinds.DiscardSecretSlot, parentDiscard.Kind);
            Assert.AreEqual(
                parentSlotId,
                parentDiscard.ReadPayload<DiscardSecretSlotRequest>().SlotId);
            await harness.RespondAsync(parentDiscard, new DiscardSecretSlotResponse(true));

            var nestedDiscard = await harness.ReadRequestAsync();
            Assert.AreEqual(ProtocolMessageKinds.DiscardSecretSlot, nestedDiscard.Kind);
            Assert.AreEqual(
                nestedSlotId,
                nestedDiscard.ReadPayload<DiscardSecretSlotRequest>().SlotId);
            await harness.RespondAsync(nestedDiscard, new DiscardSecretSlotResponse(true));

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await openTask);
        }
        finally
        {
            sessionGate.Release();
        }

        await AssertNoPendingRecoveryRequestAsync(harness);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_ExplicitSecretDiscardIsAcknowledged()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var slotId = Guid.NewGuid();

        var discardTask = harness.Client.DiscardSecretAsync(slotId);
        var discardRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.DiscardSecretSlot, discardRequest.Kind);
        Assert.AreEqual(slotId, discardRequest.ReadPayload<DiscardSecretSlotRequest>().SlotId);
        await harness.RespondAsync(discardRequest, new DiscardSecretSlotResponse(true));

        Assert.IsTrue(await discardTask);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorSixRefusesZipCreationWithoutSendingAFrame()
    {
        var minorSixFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.NestedArchiveOpenMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorSixFeatures);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.StartZipCreationAsync(
                @"C:\created.zip",
                [new ArchiveCreationFile(@"C:\source.txt", "source.txt")]));

        Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, exception.Error.Category);
        Assert.AreEqual("zip.creation_feature_not_negotiated", exception.Error.Code);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorSevenRefusesZipTreeCreationWithoutSendingAFrame()
    {
        var minorSevenFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipCreationMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorSevenFeatures);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.StartZipTreeCreationAsync(
                @"C:\created.zip",
                [],
                [new ArchiveCreationDirectory(@"C:\source", "source")]));

        Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, exception.Error.Category);
        Assert.AreEqual(
            "zip.directory_creation_feature_not_negotiated",
            exception.Error.Code);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_CurrentMinorDispatchesTarCreationAndCapabilityMessages()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var startTask = harness.Client.StartTarCreationAsync(
            @"C:\created.tar",
            [new ArchiveCreationFile(@"C:\source.txt", "source.txt")]);

        var startRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.StartTarCreation, startRequest.Kind);
        var startPayload = startRequest.ReadPayload<StartTarCreationRequest>();
        Assert.AreEqual(@"C:\created.tar", startPayload.DestinationPath);
        Assert.AreEqual(ArchiveCompressionPreset.Store, startPayload.CompressionPreset);
        CollectionAssert.AreEqual(
            new[] { new ArchiveCreationFile(@"C:\source.txt", "source.txt") },
            startPayload.Files.ToArray());
        var operationId = OperationId.New();
        await harness.RespondAsync(startRequest, new StartOperationResponse(operationId));
        Assert.AreEqual(operationId, (await startTask).OperationId);

        var treeTask = harness.Client.StartTarTreeCreationAsync(
            @"C:\created-tree.tar",
            [],
            [new ArchiveCreationDirectory(@"C:\source", "source")]);
        var treeRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.StartTarTreeCreation, treeRequest.Kind);
        var treePayload = treeRequest.ReadPayload<StartTarTreeCreationRequest>();
        Assert.AreEqual(@"C:\created-tree.tar", treePayload.DestinationPath);
        Assert.AreEqual(ArchiveCompressionPreset.Store, treePayload.CompressionPreset);
        Assert.IsEmpty(treePayload.Files);
        CollectionAssert.AreEqual(
            new[] { new ArchiveCreationDirectory(@"C:\source", "source") },
            treePayload.Directories.ToArray());
        var treeOperationId = OperationId.New();
        await harness.RespondAsync(treeRequest, new StartOperationResponse(treeOperationId));
        Assert.AreEqual(treeOperationId, (await treeTask).OperationId);

        var capabilityTask = harness.Client.GetTarWriteCapabilitiesAsync();
        var capabilityRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.GetTarWriteCapabilities, capabilityRequest.Kind);
        Assert.AreEqual(
            new GetTarWriteCapabilitiesRequest(),
            capabilityRequest.ReadPayload<GetTarWriteCapabilitiesRequest>());
        await harness.RespondAsync(
            capabilityRequest,
            new GetTarWriteCapabilitiesResponse(
                CanCreate: true,
                CanRebuild: false,
                SupportsTreeCreation: true,
                CompressionPresets: [ArchiveCompressionPreset.Store],
                MaximumEntries: ProtocolConstants.MaximumTarCreationEntries,
                MaximumSourceBytes: ProtocolConstants.MaximumTarCreationSourceBytes,
                SupportsEncryption: false,
                SupportsFilenameEncryption: false,
                SupportsSplitOutput: false,
                SupportsExistingDestinationReplacement: false,
                SupportsInPlaceUpdate: false));
        var capabilities = await capabilityTask;
        Assert.IsTrue(capabilities.CanCreate);
        Assert.IsFalse(capabilities.CanRebuild);
        Assert.IsTrue(capabilities.SupportsTreeCreation);
        CollectionAssert.AreEqual(
            new[] { ArchiveCompressionPreset.Store },
            capabilities.CompressionPresets.ToArray());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorSixteenRefusesTarRoutesBeforeSendingAFrame()
    {
        var minorSixteenFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipWriteCapabilitiesMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorSixteenFeatures);

        var creationFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.StartTarCreationAsync(
                @"C:\created.tar",
                [new ArchiveCreationFile(@"C:\source.txt", "source.txt")]));
        Assert.AreEqual(
            "tar.creation_feature_not_negotiated",
            creationFailure.Error.Code);

        var treeFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.StartTarTreeCreationAsync(
                @"C:\created.tar",
                [],
                [new ArchiveCreationDirectory(@"C:\source", "source")]));
        Assert.AreEqual(
            "tar.directory_creation_feature_not_negotiated",
            treeFailure.Error.Code);

        var capabilityFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.GetTarWriteCapabilitiesAsync());
        Assert.AreEqual(
            "tar.write_capabilities_feature_not_negotiated",
            capabilityFailure.Error.Code);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_CurrentMinorDispatchesAllManagedCreationRoutesAndCapabilities()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();

        var tarGZipTask = harness.Client.StartTarGZipCreationAsync(
            @"C:\created.tar.gz",
            [new ArchiveCreationFile(@"C:\source.txt", "source.txt")],
            ArchiveCompressionPreset.Maximum);
        var tarGZipRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.StartTarGZipCreation, tarGZipRequest.Kind);
        var tarGZip = tarGZipRequest.ReadPayload<StartTarGZipCreationRequest>();
        Assert.AreEqual(@"C:\created.tar.gz", tarGZip.DestinationPath);
        Assert.AreEqual(ArchiveCompressionPreset.Maximum, tarGZip.CompressionPreset);
        CollectionAssert.AreEqual(
            new[] { new ArchiveCreationFile(@"C:\source.txt", "source.txt") },
            tarGZip.Files.ToArray());
        var tarGZipOperation = OperationId.New();
        await harness.RespondAsync(tarGZipRequest, new StartOperationResponse(tarGZipOperation));
        Assert.AreEqual(tarGZipOperation, (await tarGZipTask).OperationId);

        var tarGZipTreeTask = harness.Client.StartTarGZipTreeCreationAsync(
            @"C:\tree.tar.gz",
            [],
            [new ArchiveCreationDirectory(@"C:\source", "source")],
            ArchiveCompressionPreset.Store);
        var tarGZipTreeRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.StartTarGZipTreeCreation, tarGZipTreeRequest.Kind);
        var tarGZipTree = tarGZipTreeRequest.ReadPayload<StartTarGZipTreeCreationRequest>();
        Assert.AreEqual(ArchiveCompressionPreset.Store, tarGZipTree.CompressionPreset);
        Assert.IsEmpty(tarGZipTree.Files);
        CollectionAssert.AreEqual(
            new[] { new ArchiveCreationDirectory(@"C:\source", "source") },
            tarGZipTree.Directories.ToArray());
        var tarGZipTreeOperation = OperationId.New();
        await harness.RespondAsync(tarGZipTreeRequest, new StartOperationResponse(tarGZipTreeOperation));
        Assert.AreEqual(tarGZipTreeOperation, (await tarGZipTreeTask).OperationId);

        var tarBZip2Task = harness.Client.StartTarBZip2CreationAsync(
            @"C:\created.tar.bz2",
            [],
            ArchiveCompressionPreset.Balanced);
        var tarBZip2Request = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.StartTarBZip2Creation, tarBZip2Request.Kind);
        var tarBZip2 = tarBZip2Request.ReadPayload<StartTarBZip2CreationRequest>();
        Assert.AreEqual(ArchiveCompressionPreset.Balanced, tarBZip2.CompressionPreset);
        Assert.IsEmpty(tarBZip2.Files);
        var tarBZip2Operation = OperationId.New();
        await harness.RespondAsync(tarBZip2Request, new StartOperationResponse(tarBZip2Operation));
        Assert.AreEqual(tarBZip2Operation, (await tarBZip2Task).OperationId);

        var tarBZip2TreeTask = harness.Client.StartTarBZip2TreeCreationAsync(
            @"C:\tree.tar.bz2",
            [],
            [new ArchiveCreationDirectory(@"C:\source", "source")]);
        var tarBZip2TreeRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.StartTarBZip2TreeCreation, tarBZip2TreeRequest.Kind);
        var tarBZip2Tree = tarBZip2TreeRequest.ReadPayload<StartTarBZip2TreeCreationRequest>();
        Assert.AreEqual(ArchiveCompressionPreset.Balanced, tarBZip2Tree.CompressionPreset);
        CollectionAssert.AreEqual(
            new[] { new ArchiveCreationDirectory(@"C:\source", "source") },
            tarBZip2Tree.Directories.ToArray());
        var tarBZip2TreeOperation = OperationId.New();
        await harness.RespondAsync(tarBZip2TreeRequest, new StartOperationResponse(tarBZip2TreeOperation));
        Assert.AreEqual(tarBZip2TreeOperation, (await tarBZip2TreeTask).OperationId);

        var gzipTask = harness.Client.StartGZipCreationAsync(
            @"C:\payload.gz",
            @"C:\payload.txt",
            ArchiveCompressionPreset.Fast);
        var gzipRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.StartGZipCreation, gzipRequest.Kind);
        var gzip = gzipRequest.ReadPayload<StartGZipCreationRequest>();
        Assert.AreEqual(@"C:\payload.gz", gzip.DestinationPath);
        Assert.AreEqual(@"C:\payload.txt", gzip.SourcePath);
        Assert.AreEqual(ArchiveCompressionPreset.Fast, gzip.CompressionPreset);
        var gzipOperation = OperationId.New();
        await harness.RespondAsync(gzipRequest, new StartOperationResponse(gzipOperation));
        Assert.AreEqual(gzipOperation, (await gzipTask).OperationId);

        var bzip2Task = harness.Client.StartBZip2CreationAsync(
            @"C:\payload.bz2",
            @"C:\payload.txt");
        var bzip2Request = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.StartBZip2Creation, bzip2Request.Kind);
        var bzip2 = bzip2Request.ReadPayload<StartBZip2CreationRequest>();
        Assert.AreEqual(@"C:\payload.bz2", bzip2.DestinationPath);
        Assert.AreEqual(@"C:\payload.txt", bzip2.SourcePath);
        Assert.AreEqual(ArchiveCompressionPreset.Balanced, bzip2.CompressionPreset);
        var bzip2Operation = OperationId.New();
        await harness.RespondAsync(bzip2Request, new StartOperationResponse(bzip2Operation));
        Assert.AreEqual(bzip2Operation, (await bzip2Task).OperationId);

        var capabilityTask = harness.Client.GetManagedWriteCapabilitiesAsync();
        var capabilityRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.GetManagedWriteCapabilities, capabilityRequest.Kind);
        Assert.AreEqual(
            new GetManagedWriteCapabilitiesRequest(),
            capabilityRequest.ReadPayload<GetManagedWriteCapabilitiesRequest>());
        await harness.RespondAsync(capabilityRequest, CreateManagedCapabilitiesResponse());
        var capabilities = await capabilityTask;
        Assert.HasCount(4, capabilities.Capabilities);
        Assert.AreEqual(ManagedArchiveCreationFormat.TarGZip, capabilities.Capabilities[0].Format);
        Assert.AreEqual(ManagedArchiveCreationFormat.TarBZip2, capabilities.Capabilities[1].Format);
        Assert.AreEqual(ManagedArchiveCreationFormat.GZip, capabilities.Capabilities[2].Format);
        Assert.AreEqual(ManagedArchiveCreationFormat.BZip2, capabilities.Capabilities[3].Format);
        Assert.IsTrue(capabilities.Capabilities[0].SupportsTreeCreation);
        Assert.IsTrue(capabilities.Capabilities[1].SupportsTreeCreation);
        Assert.IsFalse(capabilities.Capabilities[2].SupportsTreeCreation);
        Assert.IsFalse(capabilities.Capabilities[3].SupportsTreeCreation);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorSeventeenRefusesAllManagedCreationRoutesBeforeSendingAFrame()
    {
        var minorSeventeenFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.TarWriteCapabilitiesMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorSeventeenFeatures);

        var failures = new Func<Task>[]
        {
            () => harness.Client.StartTarGZipCreationAsync(
                @"C:\created.tar.gz",
                [new ArchiveCreationFile(@"C:\source.txt", "source.txt")]),
            () => harness.Client.StartTarGZipTreeCreationAsync(
                @"C:\created.tar.gz",
                [],
                [new ArchiveCreationDirectory(@"C:\source", "source")]),
            () => harness.Client.StartTarBZip2CreationAsync(@"C:\created.tar.bz2", []),
            () => harness.Client.StartTarBZip2TreeCreationAsync(
                @"C:\created.tar.bz2",
                [],
                [new ArchiveCreationDirectory(@"C:\source", "source")]),
            () => harness.Client.StartGZipCreationAsync(@"C:\payload.gz", @"C:\payload.txt"),
            () => harness.Client.StartBZip2CreationAsync(@"C:\payload.bz2", @"C:\payload.txt"),
            () => harness.Client.GetManagedWriteCapabilitiesAsync(),
        };

        foreach (var failureTask in failures)
        {
            var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(failureTask);
            Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, failure.Error.Category);
        }

        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_CurrentMinorDispatchesAndValidatesOperationActivity()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var queryTask = harness.Client.GetOperationActivityAsync();
        var request = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.GetOperationActivity, request.Kind);
        Assert.AreEqual(
            new GetOperationActivityRequest(),
            request.ReadPayload<GetOperationActivityRequest>());

        var createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var activeId = OperationId.New();
        var terminalId = OperationId.New();
        var response = CreateOperationActivityResponse(
        [
            new OperationActivitySnapshot(
                activeId,
                OperationKind.CreateArchive,
                OperationPhase.Running,
                LastEventSequence: 3,
                createdAtUtc,
                createdAtUtc.AddSeconds(2),
                CompletedAtUtc: null,
                new OperationActivityProgress(
                    BytesProcessed: 13,
                    TotalBytes: 26,
                    EntriesProcessed: 1,
                    TotalEntries: 2,
                    Elapsed: TimeSpan.FromSeconds(2),
                    BytesPerSecond: 6.5,
                    CompressionRatio: 0.5),
                WarningCount: 0,
                CanCancel: true),
            new OperationActivitySnapshot(
                terminalId,
                OperationKind.RebuildArchive,
                OperationPhase.CompletedWithWarnings,
                LastEventSequence: 4,
                createdAtUtc.AddSeconds(-10),
                createdAtUtc.AddSeconds(1),
                createdAtUtc.AddSeconds(1),
                Progress: null,
                WarningCount: 1,
                CanCancel: false),
        ], createdAtUtc.AddMinutes(1));
        await harness.RespondAsync(request, response);

        var result = await queryTask;
        Assert.HasCount(2, result.Operations);
        Assert.AreEqual(activeId, result.Operations[0].OperationId);
        Assert.AreEqual(terminalId, result.Operations[1].OperationId);
        Assert.IsFalse(result.SupportsReordering);
        Assert.IsFalse(result.SupportsPause);
        Assert.IsFalse(result.SupportsRetry);
        Assert.IsFalse(result.PersistsAcrossWorkerRestart);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorEighteenRefusesOperationActivityBeforeSendingAFrame()
    {
        var minorEighteenFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ManagedWriteCapabilitiesMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorEighteenFeatures);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.GetOperationActivityAsync());

        Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, failure.Error.Category);
        Assert.AreEqual("operation.activity_feature_not_negotiated", failure.Error.Code);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_UnsafeOperationActivityResponseAbortsTransport()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var queryTask = harness.Client.GetOperationActivityAsync();
        var request = await harness.ReadRequestAsync();
        var response = CreateOperationActivityResponse([], DateTimeOffset.UtcNow) with
        {
            SupportsRetry = true,
        };
        await harness.RespondAsync(request, response);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await queryTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_DuplicateOrOutOfOrderActivitySnapshotsAbortTransport()
    {
        var duplicateId = OperationId.New();
        var now = DateTimeOffset.UtcNow;
        await using (var duplicateHarness = await ArchiveClientProtocolHarness.StartAsync())
        {
            var queryTask = duplicateHarness.Client.GetOperationActivityAsync();
            var request = await duplicateHarness.ReadRequestAsync();
            await duplicateHarness.RespondAsync(
                request,
                CreateOperationActivityResponse(
                [
                    ActiveActivity(duplicateId, now.AddSeconds(-2)),
                    ActiveActivity(duplicateId, now.AddSeconds(-1)),
                ],
                now));
            var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await queryTask);
            Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        }

        await using var orderHarness = await ArchiveClientProtocolHarness.StartAsync();
        var orderTask = orderHarness.Client.GetOperationActivityAsync();
        var orderRequest = await orderHarness.ReadRequestAsync();
        await orderHarness.RespondAsync(
            orderRequest,
            CreateOperationActivityResponse(
            [
                ActiveActivity(OperationId.New(), now.AddSeconds(-1)),
                ActiveActivity(OperationId.New(), now.AddSeconds(-2)),
            ],
            now));
        var orderFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(
            async () => await orderTask);
        Assert.AreEqual("protocol.invalid_worker_frame", orderFailure.Error.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_ExcessTerminalActivityAbortsTransport()
    {
        var snapshotAtUtc = DateTimeOffset.UtcNow;
        var operations = Enumerable.Range(
                0,
                ProtocolConstants.MaximumRetainedOperationActivity + 1)
            .Select(index => TerminalActivity(
                OperationId.New(),
                snapshotAtUtc.AddSeconds(-index - 1)))
            .ToArray();
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var queryTask = harness.Client.GetOperationActivityAsync();
        var request = await harness.ReadRequestAsync();
        await harness.RespondAsync(
            request,
            CreateOperationActivityResponse(operations, snapshotAtUtc));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await queryTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_CurrentMinorDispatchesAndValidatesSupportDiagnostics()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var queryTask = harness.Client.GetSupportDiagnosticsAsync();
        var request = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.GetSupportDiagnostics, request.Kind);
        Assert.AreEqual(
            new GetSupportDiagnosticsRequest(),
            request.ReadPayload<GetSupportDiagnosticsRequest>());
        await harness.RespondAsync(
            request,
            CreateSupportDiagnosticsResponse(
                activeAndQueuedOperationCount: 2,
                retainedTerminalOperationCount: 3));

        var response = await queryTask;
        Assert.AreEqual(ProtocolConstants.MajorVersion, response.ProtocolMajor);
        Assert.AreEqual(ProtocolConstants.MinorVersion, response.ProtocolMinor);
        Assert.AreEqual(EnabledFeatures.Count, response.EnabledFeatureCount);
        Assert.AreEqual(
            WorkerContainmentModel.SameUserWorkerProcess,
            response.ContainmentModel);
        Assert.IsFalse(response.IsSameUserSecuritySandbox);
        Assert.AreEqual(2, response.ActiveAndQueuedOperationCount);
        Assert.AreEqual(3, response.RetainedTerminalOperationCount);
        Assert.IsFalse(response.SupportsQueueReordering);
        Assert.IsFalse(response.SupportsPause);
        Assert.IsFalse(response.SupportsRetry);
        Assert.IsFalse(response.SupportsAutomaticRecoveryDisposition);
        Assert.IsFalse(response.PersistsOperationActivityAcrossWorkerRestart);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorTwentyOneRefusesSupportDiagnosticsBeforeSendingAFrame()
    {
        var minorTwentyOneFeatures = ProtocolFeatures
            .SupportedForMinorVersion(
                ProtocolConstants.NestedWorkspaceIndependentReopenMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(
            minorTwentyOneFeatures,
            ProtocolConstants.NestedWorkspaceIndependentReopenMinorVersion);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.GetSupportDiagnosticsAsync());

        Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, failure.Error.Category);
        Assert.AreEqual("worker.support_diagnostics_feature_not_negotiated", failure.Error.Code);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_UnsafeSupportDiagnosticsAbortsTransport()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var queryTask = harness.Client.GetSupportDiagnosticsAsync();
        var request = await harness.ReadRequestAsync();
        await harness.RespondAsync(
            request,
            CreateSupportDiagnosticsResponse(
                activeAndQueuedOperationCount: 0,
                retainedTerminalOperationCount: 0) with
            {
                IsSameUserSecuritySandbox = true,
            });

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await queryTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_CurrentMinorDispatchesExtractionListAndInspection()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var recoveryId = new string('a', 64);
        var recordedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var summary = new ExtractionRecoverySummary(
            recoveryId,
            ExtractionRecoveryInventoryState.Finalized,
            ExtractionRecoveryPhase.Finalized,
            Sequence: 3,
            recordedAt,
            HasIncompleteTail: false);

        var listTask = harness.Client.ListExtractionRecoveriesAsync();
        var listRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.ListExtractionRecoveries, listRequest.Kind);
        Assert.AreEqual(
            new ListExtractionRecoveriesRequest(),
            listRequest.ReadPayload<ListExtractionRecoveriesRequest>());
        await harness.RespondAsync(
            listRequest,
            new ListExtractionRecoveriesResponse(
                [summary],
                0,
                0,
                0,
                false,
                false));
        Assert.AreEqual(recoveryId, (await listTask).Recoveries.Single().RecoveryId);

        var inspectTask = harness.Client.InspectExtractionRecoveryAsync(recoveryId);
        var inspectRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.InspectExtractionRecovery, inspectRequest.Kind);
        Assert.AreEqual(
            new InspectExtractionRecoveryRequest(recoveryId),
            inspectRequest.ReadPayload<InspectExtractionRecoveryRequest>());
        var revision = new string('b', 64);
        await harness.RespondAsync(
            inspectRequest,
            new InspectExtractionRecoveryResponse(
                summary,
                @"C:\Rain\output",
                false,
                1,
                1,
                13,
                0,
                revision,
                ExtractionRecoveryParentState.MatchesJournal,
                new ExtractionRecoveryArtifactInspection(
                    ExtractionRecoveryArtifactState.ExactMatch,
                    1,
                    1,
                    13,
                    0,
                    revision),
                new ExtractionRecoveryArtifactInspection(
                    ExtractionRecoveryArtifactState.Absent),
                ExtractionRecoveryAssessment.OwnedFinalizedStagingObserved,
                DateTimeOffset.UtcNow,
                SupportsMutation: false,
                SupportsRetry: false,
                SupportsCleanup: false));
        var inspection = await inspectTask;
        Assert.AreEqual(@"C:\Rain\output", inspection.DestinationPath);
        Assert.AreEqual(ExtractionRecoveryAssessment.OwnedFinalizedStagingObserved, inspection.Assessment);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorNineteenRefusesExtractionRecoveryBeforeSend()
    {
        var minorNineteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.OperationActivityMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorNineteen);

        var listFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.ListExtractionRecoveriesAsync());
        var inspectFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.InspectExtractionRecoveryAsync(new string('a', 64)));

        Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, listFailure.Error.Category);
        Assert.AreEqual(
            "extraction_recovery.inspection_feature_not_negotiated",
            listFailure.Error.Code);
        Assert.AreEqual(listFailure.Error.Code, inspectFailure.Error.Code);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_InvalidExtractionRecoveryIdRefusesBeforeSend()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await harness.Client.InspectExtractionRecoveryAsync("NOT-AN-OPAQUE-ID"));

        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_CancelledExtractionRecoveryInspectionKillsAStuckOwnedWorker()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var inspection = harness.Client.InspectExtractionRecoveryAsync(
            new string('c', 64),
            cancellation.Token);
        var request = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.InspectExtractionRecovery, request.Kind);

        cancellation.Cancel();
        var cancelRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.CancelRequest, cancelRequest.Kind);
        Assert.AreEqual(
            request.RequestId,
            cancelRequest.ReadPayload<CancelRequestRequest>().TargetRequestId);
        await harness.RespondAsync(cancelRequest, new CancelRequestResponse(true));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await inspection.WaitAsync(TimeSpan.FromSeconds(6)));
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_UnsafeExtractionRecoveryAuthorityAbortsTransport()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var recoveryId = new string('a', 64);
        var queryTask = harness.Client.InspectExtractionRecoveryAsync(recoveryId);
        var request = await harness.ReadRequestAsync();
        var summary = new ExtractionRecoverySummary(
            recoveryId,
            ExtractionRecoveryInventoryState.Incomplete,
            ExtractionRecoveryPhase.Writing,
            2,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            false);
        await harness.RespondAsync(
            request,
            new InspectExtractionRecoveryResponse(
                summary,
                @"C:\Rain\output",
                false,
                null,
                null,
                null,
                null,
                null,
                ExtractionRecoveryParentState.MatchesJournal,
                new ExtractionRecoveryArtifactInspection(
                    ExtractionRecoveryArtifactState.Absent),
                new ExtractionRecoveryArtifactInspection(
                    ExtractionRecoveryArtifactState.Absent),
                ExtractionRecoveryAssessment.NoOwnedArtifactObserved,
                DateTimeOffset.UtcNow,
                SupportsMutation: true,
                SupportsRetry: false,
                SupportsCleanup: false));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await queryTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_TornExtractionRecoveryCannotClaimOwnedAssessment()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var recoveryId = new string('a', 64);
        var revision = new string('b', 64);
        var queryTask = harness.Client.InspectExtractionRecoveryAsync(recoveryId);
        var request = await harness.ReadRequestAsync();
        var summary = new ExtractionRecoverySummary(
            recoveryId,
            ExtractionRecoveryInventoryState.Finalized,
            ExtractionRecoveryPhase.Finalized,
            3,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            HasIncompleteTail: true);
        await harness.RespondAsync(
            request,
            new InspectExtractionRecoveryResponse(
                summary,
                @"C:\Rain\output",
                false,
                1,
                0,
                13,
                0,
                revision,
                ExtractionRecoveryParentState.MatchesJournal,
                new ExtractionRecoveryArtifactInspection(
                    ExtractionRecoveryArtifactState.ExactMatch,
                    1,
                    0,
                    13,
                    0,
                    revision),
                new ExtractionRecoveryArtifactInspection(
                    ExtractionRecoveryArtifactState.Absent),
                ExtractionRecoveryAssessment.OwnedFinalizedStagingObserved,
                DateTimeOffset.UtcNow,
                SupportsMutation: false,
                SupportsRetry: false,
                SupportsCleanup: false));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await queryTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_OversizedManagedTarRequestsFailLocallyWithoutPoisoningTransport()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var files = CreateOversizedManagedTarFiles();

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await harness.Client.StartTarGZipCreationAsync(@"C:\created.tar.gz", files));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await harness.Client.StartTarBZip2CreationAsync(@"C:\created.tar.bz2", files));
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));

        var healthTask = harness.Client.ListNestedWorkspaceRecoveriesAsync();
        var healthRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.ListNestedWorkspaceRecoveries, healthRequest.Kind);
        await harness.RespondAsync(
            healthRequest,
            new ListNestedWorkspaceRecoveriesResponse(
                [],
                ActiveWorkspaceCount: 0,
                SuspiciousWorkspaceCount: 0,
                IsTruncated: false,
                InspectionIncomplete: false));
        Assert.HasCount(0, (await healthTask).Recoveries);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_CancelledManagedStartAbortsWhenWorkerStaysStuck()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var startTask = harness.Client.StartTarGZipCreationAsync(
            @"C:\created.tar.gz",
            [],
            cancellationToken: cancellation.Token);
        var startRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.StartTarGZipCreation, startRequest.Kind);

        cancellation.Cancel();
        var cancelRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.CancelRequest, cancelRequest.Kind);
        Assert.AreEqual(
            startRequest.RequestId,
            cancelRequest.ReadPayload<CancelRequestRequest>().TargetRequestId);
        await harness.RespondAsync(cancelRequest, new CancelRequestResponse(true));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await startTask);
        Assert.AreEqual(ArchiveErrorCategory.WorkerFailure, failure.Error.Category);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorEightRefusesRecoveryRequestsWithoutSendingAFrame()
    {
        var minorEightFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipDirectoryCreationMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorEightFeatures);

        var listFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.ListArchiveCreationRecoveriesAsync());
        var inspectFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.InspectArchiveCreationRecoveryAsync(new string('a', 64)));

        Assert.AreEqual(
            "recovery.inspection_feature_not_negotiated",
            listFailure.Error.Code);
        Assert.AreEqual(
            "recovery.inspection_feature_not_negotiated",
            inspectFailure.Error.Code);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_RecoveryInspectionRequiresCancellationWithoutSendingAFrame()
    {
        var inconsistentFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.MinorVersion)
            .Where(feature => feature != ProtocolFeatures.RequestCancellation)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(
            inconsistentFeatures);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.ListArchiveCreationRecoveriesAsync());

        Assert.AreEqual(
            "recovery.inspection_feature_not_negotiated",
            failure.Error.Code);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorNineRefusesRecoveryDispositionWithoutSendingAFrame()
    {
        var minorNineFeatures = ProtocolFeatures
            .SupportedForMinorVersion(
                ProtocolConstants.ArchiveCreationRecoveryInspectionMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorNineFeatures);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.ApplyArchiveCreationRecoveryDispositionAsync(
                new string('a', 64),
                ArchiveCreationRecoveryPhase.CommitIntent,
                4,
                DateTimeOffset.UtcNow,
                ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging));

        Assert.AreEqual(
            "recovery.disposition_feature_not_negotiated",
            failure.Error.Code);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorTenRefusesNestedRecoveryWithoutSendingAFrame()
    {
        var minorTenFeatures = ProtocolFeatures
            .SupportedForMinorVersion(
                ProtocolConstants.ArchiveCreationRecoveryDispositionMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorTenFeatures);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.ListNestedWorkspaceRecoveriesAsync());

        Assert.AreEqual("nested_recovery.feature_not_negotiated", failure.Error.Code);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_NestedRecoveryRoundTripsExactDispositionEvidence()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var recoveryId = new string('7', 64);
        var revision = new string('8', 64);

        var listTask = harness.Client.ListNestedWorkspaceRecoveriesAsync();
        var listRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.ListNestedWorkspaceRecoveries, listRequest.Kind);
        await harness.RespondAsync(
            listRequest,
            new ListNestedWorkspaceRecoveriesResponse(
                [new NestedWorkspaceRecoverySummary(recoveryId)],
                ActiveWorkspaceCount: 1,
                SuspiciousWorkspaceCount: 0,
                IsTruncated: false,
                InspectionIncomplete: false));
        var listed = await listTask;
        Assert.AreEqual(recoveryId, listed.Recoveries.Single().RecoveryId);

        var inspectTask = harness.Client.InspectNestedWorkspaceRecoveryAsync(recoveryId);
        var inspectRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.InspectNestedWorkspaceRecovery, inspectRequest.Kind);
        Assert.AreEqual(
            recoveryId,
            inspectRequest.ReadPayload<InspectNestedWorkspaceRecoveryRequest>().RecoveryId);
        await harness.RespondAsync(
            inspectRequest,
            new InspectNestedWorkspaceRecoveryResponse(
                recoveryId,
                NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
                SourceCount: 2,
                TotalSourceBytes: 123,
                revision,
                DateTimeOffset.UtcNow));
        var inspected = await inspectTask;
        Assert.AreEqual(revision, inspected.Revision);

        var discardTask = harness.Client.DiscardNestedWorkspaceRecoveryAsync(
            recoveryId,
            revision,
            expectedSourceCount: 2,
            expectedTotalSourceBytes: 123);
        var discardRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.DiscardNestedWorkspaceRecovery, discardRequest.Kind);
        var discardPayload = discardRequest.ReadPayload<DiscardNestedWorkspaceRecoveryRequest>();
        Assert.AreEqual(recoveryId, discardPayload.RecoveryId);
        Assert.IsTrue(string.Equals(
            revision,
            discardPayload.ExpectedRevision,
            StringComparison.Ordinal));
        Assert.AreEqual(2, discardPayload.ExpectedSourceCount);
        Assert.AreEqual(123L, discardPayload.ExpectedTotalSourceBytes);
        await harness.RespondAsync(
            discardRequest,
            new DiscardNestedWorkspaceRecoveryResponse(
                recoveryId,
                NestedWorkspaceRecoveryDiscardResult.Discarded,
                DateTimeOffset.UtcNow));

        Assert.AreEqual(
            NestedWorkspaceRecoveryDiscardResult.Discarded,
            (await discardTask).Result);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_UnverifiedNestedRecoveryCannotCarryDispositionEvidence()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var recoveryId = new string('9', 64);
        var inspectTask = harness.Client.InspectNestedWorkspaceRecoveryAsync(recoveryId);
        var request = await harness.ReadRequestAsync();
        await harness.RespondAsync(
            request,
            new InspectNestedWorkspaceRecoveryResponse(
                recoveryId,
                NestedWorkspaceRecoveryAssessment.UnexpectedContents,
                SourceCount: 1,
                TotalSourceBytes: 5,
                Revision: new string('a', 64),
                DateTimeOffset.UtcNow));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await inspectTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorTwentyRefusesIndependentReopenWithoutSendingAFrame()
    {
        var minorTwentyFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ExtractionRecoveryInspectionMinorVersion)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorTwentyFeatures);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.InspectNestedWorkspaceReopenAsync(new string('a', 64)));

        Assert.AreEqual("nested_recovery.reopen_feature_not_negotiated", failure.Error.Code);
        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_IndependentReopenRoundTripsExactFullHashEvidence()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var recoveryId = new string('a', 64);
        var revision = new string('b', 64);
        var sourceId = new string('c', 64);
        var contentHash = new string('d', 64);

        var inspectTask = harness.Client.InspectNestedWorkspaceReopenAsync(recoveryId);
        var inspectRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.InspectNestedWorkspaceReopen, inspectRequest.Kind);
        Assert.AreEqual(
            recoveryId,
            inspectRequest.ReadPayload<InspectNestedWorkspaceReopenRequest>().RecoveryId);
        await harness.RespondAsync(
            inspectRequest,
            new InspectNestedWorkspaceReopenResponse(
                recoveryId,
                NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
                SourceCount: 1,
                TotalSourceBytes: 1,
                revision,
                [new NestedWorkspaceRecoverySourceEvidence(sourceId, 1, contentHash)],
                DateTimeOffset.UtcNow,
                SupportsIndependentReopen: true));
        var inspected = await inspectTask;
        Assert.AreEqual(contentHash, inspected.Sources!.Single().ContentSha256);

        var passwordSlot = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var reopenTask = harness.Client.ReopenNestedWorkspaceRecoveryAsync(
            recoveryId,
            revision,
            expectedSourceCount: 1,
            expectedTotalSourceBytes: 1,
            sourceId,
            expectedSourceLength: 1,
            contentHash,
            passwordSlot);
        var reopenRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.ReopenNestedWorkspaceRecovery, reopenRequest.Kind);
        var payload = reopenRequest.ReadPayload<ReopenNestedWorkspaceRecoveryRequest>();
        Assert.AreEqual(recoveryId, payload.RecoveryId);
        Assert.IsTrue(string.Equals(revision, payload.ExpectedRevision, StringComparison.Ordinal));
        Assert.AreEqual(sourceId, payload.SourceId);
        Assert.IsTrue(string.Equals(
            contentHash,
            payload.ExpectedContentSha256,
            StringComparison.Ordinal));
        Assert.AreEqual(NestedWorkspaceRecoveryReopenMode.IndependentRoot, payload.Mode);
        Assert.AreEqual(passwordSlot, payload.PasswordSlotId);
        var descriptor = CreateArchiveDescriptor(
            ArchiveCapability.List,
            isMultipart: false,
            ArchiveVolumeSetKind.Single);
        await harness.RespondAsync(
            reopenRequest,
            new ReopenNestedWorkspaceRecoveryResponse(
                recoveryId,
                sourceId,
                NestedWorkspaceRecoveryReopenMode.IndependentRoot,
                descriptor,
                MaterializedBytes: 1,
                Depth: 0,
                DateTimeOffset.UtcNow));

        var reopened = await reopenTask;
        Assert.AreEqual(descriptor.SessionId, reopened.Archive.SessionId);
        Assert.AreEqual(0, reopened.Depth);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_UnverifiedReopenInspectionCannotCarrySources()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var recoveryId = new string('e', 64);
        var inspectTask = harness.Client.InspectNestedWorkspaceReopenAsync(recoveryId);
        var request = await harness.ReadRequestAsync();
        await harness.RespondAsync(
            request,
            new InspectNestedWorkspaceReopenResponse(
                recoveryId,
                NestedWorkspaceRecoveryAssessment.UnexpectedContents,
                SourceCount: 1,
                TotalSourceBytes: 1,
                Revision: new string('f', 64),
                [new NestedWorkspaceRecoverySourceEvidence(
                    new string('1', 64),
                    1,
                    new string('2', 64))],
                DateTimeOffset.UtcNow,
                SupportsIndependentReopen: true));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await inspectTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_InvalidDispositionRevisionIsRejectedLocally()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await harness.Client.ApplyArchiveCreationRecoveryDispositionAsync(
                new string('a', 64),
                ArchiveCreationRecoveryPhase.CommitIntent,
                3,
                DateTimeOffset.UtcNow,
                ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging));

        Assert.AreEqual(0L, GetPrivateLong(harness.Client, "_nextRequestSequence"));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_RecoveryDispositionRoundTripsExactRevision()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var recoveryId = new string('d', 64);
        var recordedAtUtc = new DateTimeOffset(2026, 8, 23, 12, 34, 56, TimeSpan.Zero);
        var dispositionTask = harness.Client.ApplyArchiveCreationRecoveryDispositionAsync(
            recoveryId,
            ArchiveCreationRecoveryPhase.CommitIntent,
            4,
            recordedAtUtc,
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);

        var request = await harness.ReadRequestAsync();
        Assert.AreEqual(
            ProtocolMessageKinds.ApplyArchiveCreationRecoveryDisposition,
            request.Kind);
        var payload = request.ReadPayload<ApplyArchiveCreationRecoveryDispositionRequest>();
        Assert.AreEqual(recoveryId, payload.RecoveryId);
        Assert.AreEqual(ArchiveCreationRecoveryPhase.CommitIntent, payload.ExpectedPhase);
        Assert.AreEqual(4, payload.ExpectedSequence);
        Assert.IsTrue(recordedAtUtc.Equals(payload.ExpectedRecordedAtUtc));
        Assert.AreEqual(
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging,
            payload.Action);
        var resolvedAtUtc = DateTimeOffset.UtcNow;
        await harness.RespondAsync(
            request,
            new ApplyArchiveCreationRecoveryDispositionResponse(
                recoveryId,
                ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging,
                ArchiveCreationRecoveryDispositionResult.Completed,
                ArchiveCreationRecoveryAssessment.OwnedFinalObserved,
                resolvedAtUtc));

        var response = await dispositionTask;
        Assert.AreEqual(ArchiveCreationRecoveryDispositionResult.Completed, response.Result);
        Assert.AreEqual(resolvedAtUtc, response.ResolvedAtUtc);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MisleadingDispositionResponseAbortsTransport()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var recoveryId = new string('e', 64);
        var dispositionTask = harness.Client.ApplyArchiveCreationRecoveryDispositionAsync(
            recoveryId,
            ArchiveCreationRecoveryPhase.Writing,
            2,
            DateTimeOffset.UtcNow,
            ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging);
        var request = await harness.ReadRequestAsync();

        await harness.RespondAsync(
            request,
            new ApplyArchiveCreationRecoveryDispositionResponse(
                recoveryId,
                ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging,
                ArchiveCreationRecoveryDispositionResult.Completed,
                ArchiveCreationRecoveryAssessment.OwnedFinalObserved,
                DateTimeOffset.UtcNow));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await dispositionTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_CancelledRecoveryDispositionKillsAStuckOwnedWorker()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var disposition = harness.Client.ApplyArchiveCreationRecoveryDispositionAsync(
            new string('f', 64),
            ArchiveCreationRecoveryPhase.Writing,
            2,
            DateTimeOffset.UtcNow,
            ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging,
            cancellation.Token);
        var request = await harness.ReadRequestAsync();
        Assert.AreEqual(
            ProtocolMessageKinds.ApplyArchiveCreationRecoveryDisposition,
            request.Kind);

        cancellation.Cancel();
        var cancelRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.CancelRequest, cancelRequest.Kind);
        Assert.AreEqual(
            request.RequestId,
            cancelRequest.ReadPayload<CancelRequestRequest>().TargetRequestId);
        await harness.RespondAsync(cancelRequest, new CancelRequestResponse(true));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await disposition.WaitAsync(TimeSpan.FromSeconds(6)));
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_ValidRecoveryInventoryAndInspectionRoundTrip()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var recoveryId = new string('a', 64);
        var summary = new ArchiveCreationRecoverySummary(
            recoveryId,
            OperationId.New(),
            ArchiveCreationRecoveryPhase.CommitIntent,
            4,
            DateTimeOffset.UtcNow,
            HasIncompleteTail: false);

        var listTask = harness.Client.ListArchiveCreationRecoveriesAsync();
        var listRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.ListArchiveCreationRecoveries, listRequest.Kind);
        await harness.RespondAsync(
            listRequest,
            new ListArchiveCreationRecoveriesResponse(
                [summary],
                ActiveOperationCount: 0,
                SuspiciousOperationCount: 0,
                IncompleteJournalCount: 0,
                IsTruncated: false,
                InspectionIncomplete: false));
        var listed = await listTask;
        Assert.AreEqual(recoveryId, listed.Recoveries.Single().RecoveryId);

        var inspectTask = harness.Client.InspectArchiveCreationRecoveryAsync(recoveryId);
        var inspectRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.InspectArchiveCreationRecovery, inspectRequest.Kind);
        await harness.RespondAsync(
            inspectRequest,
            CreateValidRecoveryInspection(summary));

        var inspected = await inspectTask;
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.OwnedFinalObserved,
            inspected.Assessment);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MalformedRecoveryEvidenceAbortsTransport()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var recoveryId = new string('b', 64);
        var summary = new ArchiveCreationRecoverySummary(
            recoveryId,
            OperationId.New(),
            ArchiveCreationRecoveryPhase.CommitIntent,
            4,
            DateTimeOffset.UtcNow,
            HasIncompleteTail: false);
        var inspectTask = harness.Client.InspectArchiveCreationRecoveryAsync(recoveryId);
        var request = await harness.ReadRequestAsync();
        var malformed = CreateValidRecoveryInspection(summary) with
        {
            DestinationPath = @"\\.\PhysicalDrive0",
        };

        await harness.RespondAsync(request, malformed);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await inspectTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MisleadingRecoveryAssessmentAbortsTransport()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var recoveryId = new string('e', 64);
        var summary = new ArchiveCreationRecoverySummary(
            recoveryId,
            OperationId.New(),
            ArchiveCreationRecoveryPhase.CommitIntent,
            4,
            DateTimeOffset.UtcNow,
            HasIncompleteTail: false);
        var inspectTask = harness.Client.InspectArchiveCreationRecoveryAsync(recoveryId);
        var request = await harness.ReadRequestAsync();
        var misleading = CreateValidRecoveryInspection(summary) with
        {
            Assessment = ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved,
        };

        await harness.RespondAsync(request, misleading);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await inspectTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_RecoveryAlternateStreamPathAbortsTransport()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var recoveryId = new string('f', 64);
        var summary = new ArchiveCreationRecoverySummary(
            recoveryId,
            OperationId.New(),
            ArchiveCreationRecoveryPhase.CommitIntent,
            4,
            DateTimeOffset.UtcNow,
            HasIncompleteTail: false);
        var inspectTask = harness.Client.InspectArchiveCreationRecoveryAsync(recoveryId);
        var request = await harness.ReadRequestAsync();
        var alternateStream = CreateValidRecoveryInspection(summary) with
        {
            DestinationPath = @"C:\created.zip:Zone.Identifier",
        };

        await harness.RespondAsync(request, alternateStream);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await inspectTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_NullRecoveryArtifactAbortsTransport()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var recoveryId = new string('9', 64);
        var summary = new ArchiveCreationRecoverySummary(
            recoveryId,
            OperationId.New(),
            ArchiveCreationRecoveryPhase.CommitIntent,
            4,
            DateTimeOffset.UtcNow,
            HasIncompleteTail: false);
        var inspectTask = harness.Client.InspectArchiveCreationRecoveryAsync(recoveryId);
        var request = await harness.ReadRequestAsync();
        var missingEvidence = CreateValidRecoveryInspection(summary) with
        {
            StagingArtifact = null!,
        };

        await harness.RespondAsync(request, missingEvidence);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await inspectTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_CancelledRecoveryInspectionKillsAStuckOwnedWorker()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var inspection = harness.Client.InspectArchiveCreationRecoveryAsync(
            new string('c', 64),
            cancellation.Token);
        var request = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.InspectArchiveCreationRecovery, request.Kind);

        cancellation.Cancel();
        var cancelRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.CancelRequest, cancelRequest.Kind);
        var cancellationPayload = cancelRequest.ReadPayload<CancelRequestRequest>();
        Assert.AreEqual(request.RequestId, cancellationPayload.TargetRequestId);
        await harness.RespondAsync(cancelRequest, new CancelRequestResponse(true));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await inspection.WaitAsync(TimeSpan.FromSeconds(6)));
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_PreAdmissionControlCancellationAbortsOwnedWorker()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var blockedRequests = new List<Task<CancelOperationResponse>>();
        for (var index = 0; index < ProtocolConstants.MaximumControlRequests; index++)
        {
            var blocked = harness.Client.CancelOperationAsync(OperationId.New());
            var request = await harness.ReadRequestAsync();
            Assert.AreEqual(ProtocolMessageKinds.CancelOperation, request.Kind);
            blockedRequests.Add(blocked);
        }

        using var cancellation = new CancellationTokenSource();
        var admission = harness.Client.CancelOperationAsync(OperationId.New(), cancellation.Token);
        await Task.Delay(50);
        cancellation.Cancel();

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await admission);
        Assert.AreEqual(ArchiveErrorCategory.WorkerFailure, failure.Error.Category);
        await harness.WaitForOwnedWorkerExitAsync();

        foreach (var blocked in blockedRequests)
        {
            await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await blocked);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_StartCancellationReturnsStartWhenWorkerRejectsCancel()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var operationId = OperationId.New();
        var startTask = harness.Client.StartIntegrityTestAsync(
            ArchiveSessionId.New(),
            [],
            cancellationToken: cancellation.Token);
        var startRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.StartIntegrityTest, startRequest.Kind);

        cancellation.Cancel();
        var cancelRequestBeforeStart = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.CancelRequest, cancelRequestBeforeStart.Kind);
        Assert.AreEqual(
            startRequest.RequestId,
            cancelRequestBeforeStart.ReadPayload<CancelRequestRequest>().TargetRequestId);
        await harness.RespondAsync(cancelRequestBeforeStart, new CancelRequestResponse(false));
        await harness.RespondAsync(startRequest, new StartOperationResponse(operationId));

        var cancelRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.CancelOperation, cancelRequest.Kind);
        var cancel = cancelRequest.ReadPayload<CancelOperationRequest>();
        Assert.AreEqual(operationId, cancel.OperationId);
        await harness.RespondAsync(cancelRequest, new CancelOperationResponse(false));

        var started = await startTask;
        Assert.AreEqual(operationId, started.OperationId);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_AcknowledgedStartCancellationAbortsStuckRequest()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var startTask = harness.Client.StartIntegrityTestAsync(
            ArchiveSessionId.New(),
            [],
            cancellationToken: cancellation.Token);
        var startRequest = await harness.ReadRequestAsync();

        cancellation.Cancel();
        var cancelRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.CancelRequest, cancelRequest.Kind);
        Assert.AreEqual(
            startRequest.RequestId,
            cancelRequest.ReadPayload<CancelRequestRequest>().TargetRequestId);
        await harness.RespondAsync(cancelRequest, new CancelRequestResponse(true));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await startTask);
        Assert.AreEqual(ArchiveErrorCategory.WorkerFailure, failure.Error.Category);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_CancelledStartSettlesWhenWorkerReturnsAnOperation()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var operationId = OperationId.New();
        var startTask = harness.Client.StartIntegrityTestAsync(
            ArchiveSessionId.New(),
            [],
            cancellationToken: cancellation.Token);
        var startRequest = await harness.ReadRequestAsync();

        cancellation.Cancel();
        var requestCancellation = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.CancelRequest, requestCancellation.Kind);
        await harness.RespondAsync(requestCancellation, new CancelRequestResponse(true));
        await harness.RespondAsync(startRequest, new StartOperationResponse(operationId));

        var operationCancellation = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.CancelOperation, operationCancellation.Kind);
        Assert.AreEqual(
            operationId,
            operationCancellation.ReadPayload<CancelOperationRequest>().OperationId);
        await harness.RespondAsync(operationCancellation, new CancelOperationResponse(true));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await startTask);
        await harness.WriteOperationEventAsync(new OperationEvent(
            operationId,
            1,
            OperationEventKind.Terminal,
            OperationPhase.Cancelled,
            Error: new ArchiveError(
                "operation.cancelled",
                ArchiveErrorCategory.Cancelled,
                "The operation was cancelled.")));
        await WaitUntilAsync(
            () => GetPrivateCollectionCount(harness.Client, "_terminals") == 0,
            TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_NullListEntryAbortsTransport()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var listTask = harness.Client.ListEntriesAsync(ArchiveSessionId.New());
        var listRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.ListEntries, listRequest.Kind);

        var entries = new List<ArchiveEntryDescriptor> { null! };
        await harness.RespondAsync(
            listRequest,
            new ListEntriesResponse(entries, 0, 1, null));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await listTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_CurrentMinorRequiresVolumeSetIdentity()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var openTask = harness.Client.OpenArchiveAsync(@"C:\fixture.zip");
        var request = await harness.ReadRequestAsync();
        await harness.RespondAsync(
            request,
            new OpenArchiveResponse(CreateArchiveDescriptor(
                ArchiveCapability.List | ArchiveCapability.OpenEntryStream,
                isMultipart: false,
                volumeSetKind: null)));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await openTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_UnknownVolumeSetCannotAdvertiseDataOperations()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var openTask = harness.Client.OpenArchiveAsync(@"C:\fixture.zip");
        var request = await harness.ReadRequestAsync();
        await harness.RespondAsync(
            request,
            new OpenArchiveResponse(CreateArchiveDescriptor(
                ArchiveCapability.List | ArchiveCapability.Extract,
                isMultipart: false,
                ArchiveVolumeSetKind.Unknown)));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await openTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_UnknownVolumeSetIsAcceptedWhenListOnly()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var openTask = harness.Client.OpenArchiveAsync(@"C:\fixture.zip");
        var request = await harness.ReadRequestAsync();
        var descriptor = CreateArchiveDescriptor(
            ArchiveCapability.List,
            isMultipart: false,
            ArchiveVolumeSetKind.Unknown);
        await harness.RespondAsync(request, new OpenArchiveResponse(descriptor));

        var opened = await openTask;
        Assert.AreEqual(ArchiveVolumeSetKind.Unknown, opened.Archive.VolumeSetKind);
        Assert.IsFalse(opened.Archive.IsMultipart);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorThreeAcceptsOnlyOmittedVolumeSetIdentity()
    {
        var minorThreeFeatures = ProtocolFeatures
            .SupportedForMinorVersion(3)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorThreeFeatures);
        Assert.IsFalse(harness.Client.SupportsFeature(ProtocolFeatures.VolumeSetIdentity));

        var acceptedTask = harness.Client.OpenArchiveAsync(@"C:\legacy.zip");
        var acceptedRequest = await harness.ReadRequestAsync();
        await harness.RespondAsync(
            acceptedRequest,
            new OpenArchiveResponse(CreateArchiveDescriptor(
                ArchiveCapability.List | ArchiveCapability.OpenEntryStream,
                isMultipart: false,
                volumeSetKind: null)));
        Assert.IsNull((await acceptedTask).Archive.VolumeSetKind);

        var rejectedTask = harness.Client.OpenArchiveAsync(@"C:\unexpected.zip");
        var rejectedRequest = await harness.ReadRequestAsync();
        await harness.RespondAsync(
            rejectedRequest,
            new OpenArchiveResponse(CreateArchiveDescriptor(
                ArchiveCapability.List | ArchiveCapability.OpenEntryStream,
                isMultipart: false,
                ArchiveVolumeSetKind.Single)));
        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await rejectedTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorFourRefusesEntryStreamsLocally()
    {
        var minorFourFeatures = ProtocolFeatures
            .SupportedForMinorVersion(4)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorFourFeatures);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.OpenEntryStreamAsync(
                ArchiveSessionId.New(),
                EntryId.New()));

        Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, failure.Error.Category);
        Assert.AreEqual("entry.stream_feature_not_negotiated", failure.Error.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MinorFiveRefusesNestedOpenLocally()
    {
        var minorFiveFeatures = ProtocolFeatures
            .SupportedForMinorVersion(5)
            .ToHashSet(StringComparer.Ordinal);
        await using var harness = await ArchiveClientProtocolHarness.StartAsync(minorFiveFeatures);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.OpenNestedArchiveAsync(
                ArchiveSessionId.New(),
                EntryId.New()));

        Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, failure.Error.Category);
        Assert.AreEqual("nested.feature_not_negotiated", failure.Error.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_CloseParentClosesNestedDescendantsDeepestFirst()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var rootSessionId = ArchiveSessionId.New();
        var childSessionId = ArchiveSessionId.New();
        var grandchildSessionId = ArchiveSessionId.New();
        var childEntryId = EntryId.New();
        var grandchildEntryId = EntryId.New();

        var childTask = harness.Client.OpenNestedArchiveAsync(
            rootSessionId,
            childEntryId,
            maximumBytes: 1024);
        var childRequestFrame = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.OpenNestedArchive, childRequestFrame.Kind);
        var childRequest = childRequestFrame.ReadPayload<OpenNestedArchiveRequest>();
        Assert.AreEqual(rootSessionId, childRequest.ParentSessionId);
        Assert.AreEqual(childEntryId, childRequest.EntryId);
        await harness.RespondAsync(
            childRequestFrame,
            new OpenNestedArchiveResponse(
                CreateArchiveDescriptor(
                    ArchiveCapability.List | ArchiveCapability.Extract,
                    isMultipart: false,
                    ArchiveVolumeSetKind.Single) with
                {
                    SessionId = childSessionId,
                },
                rootSessionId,
                childEntryId,
                Depth: 1,
                MaterializedBytes: 1,
                ChainMaterializedBytes: 1));
        Assert.AreEqual(childSessionId, (await childTask).Archive.SessionId);

        var grandchildTask = harness.Client.OpenNestedArchiveAsync(
            childSessionId,
            grandchildEntryId,
            maximumBytes: 1024);
        var grandchildRequestFrame = await harness.ReadRequestAsync();
        await harness.RespondAsync(
            grandchildRequestFrame,
            new OpenNestedArchiveResponse(
                CreateArchiveDescriptor(
                    ArchiveCapability.List | ArchiveCapability.Extract,
                    isMultipart: false,
                    ArchiveVolumeSetKind.Single) with
                {
                    SessionId = grandchildSessionId,
                },
                childSessionId,
                grandchildEntryId,
                Depth: 2,
                MaterializedBytes: 1,
                ChainMaterializedBytes: 2));
        Assert.AreEqual(grandchildSessionId, (await grandchildTask).Archive.SessionId);

        var closeTask = harness.Client.CloseArchiveAsync(rootSessionId);
        var closeGrandchild = await harness.ReadRequestAsync();
        Assert.AreEqual(
            grandchildSessionId,
            closeGrandchild.ReadPayload<CloseArchiveRequest>().SessionId);
        await harness.RespondAsync(closeGrandchild, new CloseArchiveResponse(true));
        var closeChild = await harness.ReadRequestAsync();
        Assert.AreEqual(childSessionId, closeChild.ReadPayload<CloseArchiveRequest>().SessionId);
        await harness.RespondAsync(closeChild, new CloseArchiveResponse(true));
        var closeRoot = await harness.ReadRequestAsync();
        Assert.AreEqual(rootSessionId, closeRoot.ReadPayload<CloseArchiveRequest>().SessionId);
        await harness.RespondAsync(closeRoot, new CloseArchiveResponse(true));

        Assert.IsTrue((await closeTask).Closed);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_InvalidEntryStreamLeaseResponseAbortsOwnedWorker()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var entryId = EntryId.New();
        var openTask = harness.Client.OpenEntryStreamAsync(
            ArchiveSessionId.New(),
            entryId,
            maximumBytes: 1024);
        var request = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.OpenEntryStream, request.Kind);
        await harness.RespondAsync(
            request,
            new OpenEntryStreamResponse(default, entryId, DeclaredLength: 1));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await openTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_RejectsDataPipePeerThatIsNotOwnedWorker()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var entryId = EntryId.New();
        var openTask = harness.Client.OpenEntryStreamAsync(
            ArchiveSessionId.New(),
            entryId,
            maximumBytes: 1024);
        var requestFrame = await harness.ReadRequestAsync();
        var request = requestFrame.ReadPayload<OpenEntryStreamRequest>();
        await using var impostor = new NamedPipeClientStream(
            ".",
            request.PipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await impostor.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await harness.RespondAsync(
            requestFrame,
            new OpenEntryStreamResponse(EntryStreamId.New(), entryId, DeclaredLength: 1));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await openTask);
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        await harness.WaitForOwnedWorkerExitAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_UnknownOperationEventAbortsTransport()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var received = 0;
        harness.Client.OperationEventReceived += (_, _) => Interlocked.Increment(ref received);

        await harness.WriteOperationEventAsync(new OperationEvent(
            OperationId.New(),
            1,
            OperationEventKind.StateChanged,
            OperationPhase.Queued));
        await harness.WaitForOwnedWorkerExitAsync();

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await harness.Client.WaitForTerminalAsync(OperationId.New()));
        Assert.AreEqual("protocol.invalid_worker_frame", failure.Error.Code);
        Assert.AreEqual(0, received);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_SlowPresentationConsumerReceivesEveryOrderedEvent()
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var operationId = OperationId.New();
        var presented = new List<OperationEvent>();
        var presentedTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Client.OperationEventReceived += (_, operationEvent) =>
        {
            Thread.Sleep(2);
            lock (presented)
            {
                presented.Add(operationEvent);
            }

            if (operationEvent.Kind == OperationEventKind.Terminal)
            {
                presentedTerminal.TrySetResult();
            }
        };

        var startTask = harness.Client.StartIntegrityTestAsync(ArchiveSessionId.New(), []);
        var startRequest = await harness.ReadRequestAsync();
        await harness.RespondAsync(startRequest, new StartOperationResponse(operationId));
        Assert.AreEqual(operationId, (await startTask).OperationId);

        long sequence = 0;
        await harness.WriteOperationEventAsync(new OperationEvent(
            operationId,
            ++sequence,
            OperationEventKind.StateChanged,
            OperationPhase.Queued));
        await harness.WriteOperationEventAsync(new OperationEvent(
            operationId,
            ++sequence,
            OperationEventKind.StateChanged,
            OperationPhase.Validating));
        await harness.WriteOperationEventAsync(new OperationEvent(
            operationId,
            ++sequence,
            OperationEventKind.StateChanged,
            OperationPhase.Running));

        const int progressEventCount = 300;
        for (var index = 1; index <= progressEventCount; index++)
        {
            await harness.WriteOperationEventAsync(new OperationEvent(
                operationId,
                ++sequence,
                OperationEventKind.Progress,
                OperationPhase.Running,
                new OperationProgress(
                    index,
                    progressEventCount,
                    index,
                    progressEventCount,
                    null,
                    $"entry-{index:D3}.txt",
                    TimeSpan.FromMilliseconds(index),
                    index * 1000d,
                    null)));
        }

        await harness.WriteOperationEventAsync(new OperationEvent(
            operationId,
            ++sequence,
            OperationEventKind.Warning,
            OperationPhase.Running,
            Warning: new ArchiveWarning("fixture.warning", "Checked presentation warning.")));
        await harness.WriteOperationEventAsync(new OperationEvent(
            operationId,
            ++sequence,
            OperationEventKind.StateChanged,
            OperationPhase.Finalizing));
        await harness.WriteOperationEventAsync(new OperationEvent(
            operationId,
            ++sequence,
            OperationEventKind.Terminal,
            OperationPhase.CompletedWithWarnings));

        var terminal = await harness.Client.WaitForTerminalAsync(operationId);
        Assert.AreEqual(OperationPhase.CompletedWithWarnings, terminal.Phase);
        await presentedTerminal.Task.WaitAsync(TimeSpan.FromSeconds(10));

        OperationEvent[] snapshot;
        lock (presented)
        {
            snapshot = presented.ToArray();
        }

        Assert.AreEqual(sequence, snapshot.Length);
        CollectionAssert.AreEqual(
            Enumerable.Range(1, checked((int)sequence)).Select(value => (long)value).ToArray(),
            snapshot.Select(operationEvent => operationEvent.Sequence).ToArray());
        Assert.AreEqual(progressEventCount, snapshot.Count(item => item.Kind == OperationEventKind.Progress));
        Assert.AreEqual(1, snapshot.Count(item => item.Kind == OperationEventKind.Warning));
        Assert.AreEqual(OperationEventKind.Terminal, snapshot[^1].Kind);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_FirstCancelledEventWinsResponseEventRace()
    {
        await VerifyFirstTerminalEventRaceAsync(
            OperationPhase.Cancelled,
            new ArchiveError(
                "operation.cancelled",
                ArchiveErrorCategory.Cancelled,
                "The operation was cancelled."));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_FirstFailedEventWinsResponseEventRace()
    {
        await VerifyFirstTerminalEventRaceAsync(
            OperationPhase.Failed,
            new ArchiveError(
                "operation.failed",
                ArchiveErrorCategory.ProviderFailure,
                "The operation failed."));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ArchiveClientProtocol_MalformedWorkerAbortsOnlyOwnedWorker()
    {
        using var unrelatedWorker = StartOwnedWorkerProcess();
        try
        {
            await using var harness = await ArchiveClientProtocolHarness.StartAsync();
            var listTask = harness.Client.ListEntriesAsync(ArchiveSessionId.New());
            var listRequest = await harness.ReadRequestAsync();
            await harness.RespondAsync(
                listRequest,
                new ListEntriesResponse(new List<ArchiveEntryDescriptor> { null! }, 0, 1, null));

            await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () => await listTask);
            await harness.WaitForOwnedWorkerExitAsync();
            Assert.IsFalse(unrelatedWorker.HasExited, "AbortTransport closed a process it did not own.");
        }
        finally
        {
            await StopWorkerAsync(unrelatedWorker);
        }
    }

    private static async Task VerifyFirstTerminalEventRaceAsync(
        OperationPhase phase,
        ArchiveError error)
    {
        await using var harness = await ArchiveClientProtocolHarness.StartAsync();
        var presented = new TaskCompletionSource<OperationEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Client.OperationEventReceived += (_, operationEvent) => presented.TrySetResult(operationEvent);

        var operationId = OperationId.New();
        var terminalEvent = new OperationEvent(
            operationId,
            1,
            OperationEventKind.Terminal,
            phase,
            Error: error);
        var startTask = harness.Client.StartIntegrityTestAsync(ArchiveSessionId.New(), []);
        var startRequest = await harness.ReadRequestAsync();
        await harness.RespondAsync(startRequest, new StartOperationResponse(operationId));
        await harness.WriteOperationEventAsync(terminalEvent);

        var started = await startTask;
        Assert.AreEqual(operationId, started.OperationId);
        var terminal = await harness.Client.WaitForTerminalAsync(operationId);
        Assert.AreEqual(terminalEvent, terminal);
        Assert.AreEqual(terminalEvent, await presented.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<Guid> CreateSecretSlotAsync(
        ArchiveClientProtocolHarness harness,
        string secretText)
    {
        var slotId = Guid.NewGuid();
        var createTask = harness.Client.CreateSecretAsync(secretText.AsMemory());
        var createRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.CreateSecretSlot, createRequest.Kind);
        await harness.RespondAsync(
            createRequest,
            new CreateSecretSlotResponse(slotId, DateTimeOffset.UtcNow.AddMinutes(5)));

        var secret = await harness.ReadSecretAsync();
        Assert.AreEqual(slotId, secret.SlotId);
        try
        {
            Assert.IsTrue(secret.SecretBytes.Length > 0);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret.SecretBytes);
        }

        Assert.AreEqual(slotId, await createTask);
        return slotId;
    }

    private static async Task AssertNoPendingRecoveryRequestAsync(
        ArchiveClientProtocolHarness harness)
    {
        var healthTask = harness.Client.ListNestedWorkspaceRecoveriesAsync();
        var healthRequest = await harness.ReadRequestAsync();
        Assert.AreEqual(ProtocolMessageKinds.ListNestedWorkspaceRecoveries, healthRequest.Kind);
        await harness.RespondAsync(
            healthRequest,
            new ListNestedWorkspaceRecoveriesResponse(
                [],
                ActiveWorkspaceCount: 0,
                SuspiciousWorkspaceCount: 0,
                IsTruncated: false,
                InspectionIncomplete: false));
        Assert.HasCount(0, (await healthTask).Recoveries);
    }

    private static ArchiveDescriptor CreateArchiveDescriptor(
        ArchiveCapability capabilities,
        bool isMultipart,
        ArchiveVolumeSetKind? volumeSetKind) =>
        new(
            ArchiveSessionId.New(),
            "zip",
            "protocol-test",
            capabilities,
            new ArchiveFingerprint(
                1,
                DateTimeOffset.UtcNow,
                FileIdentity: null,
                PrefixSha256: new string('A', 64)),
            EntryCount: 0,
            IsSolid: false,
            HasEncryptedEntries: false,
            IsMultipart: isMultipart,
            IsReadOnly: true,
            Warnings: [],
            VolumeSetKind: volumeSetKind);

    private static GetZipWriteCapabilitiesResponse CreateCapabilitiesResponse(bool canRebuild) =>
        new(
            CanCreate: true,
            CanRebuild: canRebuild,
            SupportsAdd: true,
            SupportsReplace: true,
            SupportsDelete: true,
            SupportsFileRename: true,
            SupportsTreeEdit: true,
            SupportsMetadata: true,
            CompressionPresets: Enum.GetValues<ArchiveCompressionPreset>(),
            MaximumEntries: ProtocolConstants.MaximumZipCreationEntries,
            MaximumSourceBytes: ProtocolConstants.MaximumZipCreationSourceBytes,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);

    private static GetManagedWriteCapabilitiesResponse CreateManagedCapabilitiesResponse() =>
        new(
        [
            new ManagedWriteCapability(
                ManagedArchiveCreationFormat.TarGZip,
                CanCreate: true,
                SupportsTreeCreation: true,
                CanRebuild: false,
                CompressionPresets: Enum.GetValues<ArchiveCompressionPreset>(),
                MaximumEntries: ProtocolConstants.MaximumTarCreationEntries,
                MaximumSourceBytes: ProtocolConstants.MaximumTarCreationSourceBytes,
                SupportsEncryption: false,
                SupportsFilenameEncryption: false,
                SupportsSplitOutput: false,
                SupportsExistingDestinationReplacement: false,
                SupportsInPlaceUpdate: false),
            new ManagedWriteCapability(
                ManagedArchiveCreationFormat.TarBZip2,
                CanCreate: true,
                SupportsTreeCreation: true,
                CanRebuild: false,
                CompressionPresets: [ArchiveCompressionPreset.Balanced],
                MaximumEntries: ProtocolConstants.MaximumTarCreationEntries,
                MaximumSourceBytes: ProtocolConstants.MaximumTarCreationSourceBytes,
                SupportsEncryption: false,
                SupportsFilenameEncryption: false,
                SupportsSplitOutput: false,
                SupportsExistingDestinationReplacement: false,
                SupportsInPlaceUpdate: false),
            new ManagedWriteCapability(
                ManagedArchiveCreationFormat.GZip,
                CanCreate: true,
                SupportsTreeCreation: false,
                CanRebuild: false,
                CompressionPresets: Enum.GetValues<ArchiveCompressionPreset>(),
                MaximumEntries: ProtocolConstants.MaximumRawCreationEntries,
                MaximumSourceBytes: ProtocolConstants.MaximumRawCreationSourceBytes,
                SupportsEncryption: false,
                SupportsFilenameEncryption: false,
                SupportsSplitOutput: false,
                SupportsExistingDestinationReplacement: false,
                SupportsInPlaceUpdate: false),
            new ManagedWriteCapability(
                ManagedArchiveCreationFormat.BZip2,
                CanCreate: true,
                SupportsTreeCreation: false,
                CanRebuild: false,
                CompressionPresets: [ArchiveCompressionPreset.Balanced],
                MaximumEntries: ProtocolConstants.MaximumRawCreationEntries,
                MaximumSourceBytes: ProtocolConstants.MaximumRawCreationSourceBytes,
                SupportsEncryption: false,
                SupportsFilenameEncryption: false,
                SupportsSplitOutput: false,
                SupportsExistingDestinationReplacement: false,
                SupportsInPlaceUpdate: false),
        ]);

    private static GetOperationActivityResponse CreateOperationActivityResponse(
        IReadOnlyList<OperationActivitySnapshot> operations,
        DateTimeOffset snapshotAtUtc) =>
        new(
            snapshotAtUtc.ToUniversalTime(),
            MaximumActiveAndQueuedOperations: ProtocolConstants.MaximumQueuedOperations,
            MaximumConcurrentOperations: ProtocolConstants.MaximumConcurrentOperations,
            QueueOrdering: OperationQueueOrdering.Unspecified,
            MaximumRetainedTerminalOperations:
                ProtocolConstants.MaximumRetainedOperationActivity,
            SupportsReordering: false,
            SupportsPause: false,
            SupportsRetry: false,
            PersistsAcrossWorkerRestart: false,
            HistoryTruncated: false,
            Operations: operations);

    private static GetSupportDiagnosticsResponse CreateSupportDiagnosticsResponse(
        int activeAndQueuedOperationCount,
        int retainedTerminalOperationCount) =>
        new(
            ProtocolConstants.MajorVersion,
            ProtocolConstants.MinorVersion,
            EnabledFeatures.Count,
            WorkerContainmentModel.SameUserWorkerProcess,
            IsSameUserSecuritySandbox: false,
            MaximumActiveAndQueuedOperations: ProtocolConstants.MaximumQueuedOperations,
            MaximumConcurrentOperations: ProtocolConstants.MaximumConcurrentOperations,
            OperationQueueOrdering.Unspecified,
            MaximumRetainedTerminalOperations:
                ProtocolConstants.MaximumRetainedOperationActivity,
            activeAndQueuedOperationCount,
            retainedTerminalOperationCount,
            OperationHistoryTruncated: false,
            SupportsQueueReordering: false,
            SupportsPause: false,
            SupportsRetry: false,
            SupportsAutomaticRecoveryDisposition: false,
            PersistsOperationActivityAcrossWorkerRestart: false);

    private static OperationActivitySnapshot ActiveActivity(
        OperationId operationId,
        DateTimeOffset createdAtUtc) =>
        new(
            operationId,
            OperationKind.CreateArchive,
            OperationPhase.Queued,
            LastEventSequence: 0,
            createdAtUtc.ToUniversalTime(),
            createdAtUtc.ToUniversalTime(),
            CompletedAtUtc: null,
            Progress: null,
            WarningCount: 0,
            CanCancel: true);

    private static OperationActivitySnapshot TerminalActivity(
        OperationId operationId,
        DateTimeOffset completedAtUtc) =>
        new(
            operationId,
            OperationKind.CreateArchive,
            OperationPhase.Completed,
            LastEventSequence: 1,
            completedAtUtc.AddSeconds(-1).ToUniversalTime(),
            completedAtUtc.ToUniversalTime(),
            completedAtUtc.ToUniversalTime(),
            Progress: null,
            WarningCount: 0,
            CanCancel: false);

    private static IReadOnlyList<ArchiveCreationFile> CreateOversizedManagedTarFiles() =>
        Enumerable.Range(0, ProtocolConstants.MaximumTarCreationFiles)
            .Select(index => new ArchiveCreationFile(
                $@"C:\{new string('s', 500)}-{index:D4}",
                $"{new string('a', 500)}-{index:D4}"))
            .ToArray();

    private static InspectArchiveCreationRecoveryResponse CreateValidRecoveryInspection(
        ArchiveCreationRecoverySummary summary) =>
        new(
            summary,
            @"C:\created.zip",
            $".rainarchives-{summary.RecoveryId}.zip.staging",
            JournalBoundLength: 128,
            IsRemoteDestination: false,
            ArchiveCreationRecoveryParentState.MatchesJournal,
            new ArchiveCreationRecoveryArtifactInspection(
                ArchiveCreationRecoveryArtifactState.Absent),
            new ArchiveCreationRecoveryArtifactInspection(
                ArchiveCreationRecoveryArtifactState.ExactMatch,
                ObservedLength: 128,
                LinkCount: 1),
            ArchiveCreationRecoveryAssessment.OwnedFinalObserved,
            DateTimeOffset.UtcNow);

    private static int GetPrivateCollectionCount(object owner, string fieldName)
    {
        var field = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");
        var collection = field.GetValue(owner)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was null.");
        return (int)(collection.GetType().GetProperty("Count")?.GetValue(collection)
            ?? throw new InvalidOperationException($"Field '{fieldName}' had no Count property."));
    }

    private static long GetPrivateLong(object owner, string fieldName)
    {
        var field = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");
        return (long)(field.GetValue(owner)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was null."));
    }

    private static SemaphoreSlim GetPrivateSemaphore(object owner, string fieldName)
    {
        var field = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");
        return (SemaphoreSlim)(field.GetValue(owner)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was null."));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("The expected client state was not reached before timeout.");
            }

            await Task.Delay(20);
        }
    }

    private static Process StartOwnedWorkerProcess()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "Start-Sleep -Seconds 600",
            },
        });
        return process ?? throw new InvalidOperationException("Could not start the protocol test worker process.");
    }

    private static async Task StopWorkerAsync(Process process)
    {
        try
        {
            await TerminateWorkerAsync(process);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task TerminateWorkerAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class ArchiveClientProtocolHarness : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _clientPipe;
        private readonly NamedPipeClientStream _workerPipe;
        private readonly Process _ownedWorker;
        private int _disposed;

        private ArchiveClientProtocolHarness(
            NamedPipeServerStream clientPipe,
            NamedPipeClientStream workerPipe,
            Process ownedWorker,
            ArchiveClient client)
        {
            _clientPipe = clientPipe;
            _workerPipe = workerPipe;
            _ownedWorker = ownedWorker;
            Client = client;
        }

        public ArchiveClient Client { get; }

        public static async Task<ArchiveClientProtocolHarness> StartAsync(
            IReadOnlySet<string>? enabledFeatures = null,
            int negotiatedProtocolMinor = ProtocolConstants.MinorVersion)
        {
            var pipeName = $"rainarchives-client-protocol-{Guid.NewGuid():N}";
            var clientPipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            var workerPipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            Process? ownedWorker = null;
            WorkerJob? job = null;
            try
            {
                ownedWorker = StartOwnedWorkerProcess();
                var connected = clientPipe.WaitForConnectionAsync();
                await workerPipe.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await connected.WaitAsync(TimeSpan.FromSeconds(5));
                job = new WorkerJob(ownedWorker, (nuint)(2UL * 1024 * 1024 * 1024));
                var client = (ArchiveClient)Activator.CreateInstance(
                    typeof(ArchiveClient),
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    args:
                    [
                        clientPipe,
                        ownedWorker,
                        job,
                        enabledFeatures ?? EnabledFeatures,
                        negotiatedProtocolMinor,
                    ],
                    culture: CultureInfo.InvariantCulture)!;
                return new ArchiveClientProtocolHarness(clientPipe, workerPipe, ownedWorker, client);
            }
            catch
            {
                job?.Dispose();
                workerPipe.Dispose();
                clientPipe.Dispose();
                if (ownedWorker is not null)
                {
                    await StopWorkerAsync(ownedWorker);
                }

                throw;
            }
        }

        public async Task<ProtocolFrame> ReadRequestAsync(CancellationToken cancellationToken = default)
        {
            var incoming = await ProtocolStream.ReadAsync(_workerPipe, cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (incoming.Type != ProtocolFrameType.Json || incoming.Json is null)
            {
                throw new InvalidDataException("The client sent a non-JSON frame in the protocol test.");
            }

            return incoming.Json;
        }

        public async Task<SecretFrame> ReadSecretAsync(CancellationToken cancellationToken = default)
        {
            var incoming = await ProtocolStream.ReadAsync(_workerPipe, cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (incoming.Type != ProtocolFrameType.Secret || incoming.Secret is null)
            {
                throw new InvalidDataException("The client did not send the expected secret frame.");
            }

            return incoming.Secret;
        }

        public Task RespondAsync<TResponse>(ProtocolFrame request, TResponse response) =>
            ProtocolStream.WriteJsonAsync(
                    _workerPipe,
                    $"{request.Kind}.response",
                    request.RequestId,
                    response,
                    request.Sequence)
                .AsTask();

        public Task WriteOperationEventAsync(OperationEvent operationEvent) =>
            ProtocolStream.WriteJsonAsync(
                    _workerPipe,
                    ProtocolMessageKinds.OperationEvent,
                    Guid.Empty,
                    operationEvent,
                    operationEvent.Sequence)
                .AsTask();

        public Task WaitForOwnedWorkerExitAsync() =>
            _ownedWorker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            using var readCancellation = new CancellationTokenSource();
            try
            {
                var disposeTask = Client.DisposeAsync().AsTask();
                var closeRequestTask = ReadRequestAsync(readCancellation.Token);
                if (ReferenceEquals(await Task.WhenAny(disposeTask, closeRequestTask), closeRequestTask))
                {
                    try
                    {
                        var closeRequest = await closeRequestTask;
                        if (closeRequest.Kind == ProtocolMessageKinds.CloseWorker)
                        {
                            await RespondAsync(closeRequest, new CloseResponse(true));
                            await TerminateWorkerAsync(_ownedWorker);
                        }
                    }
                    catch (Exception exception) when (
                        exception is OperationCanceledException
                            or EndOfStreamException
                            or IOException
                            or ObjectDisposedException
                            or TimeoutException)
                    {
                    }
                }
                else
                {
                    readCancellation.Cancel();
                }

                try
                {
                    await disposeTask.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception exception) when (
                    exception is ArchiveClientException
                        or OperationCanceledException
                        or IOException
                        or ObjectDisposedException
                        or TimeoutException)
                {
                }
            }
            finally
            {
                readCancellation.Cancel();
                _workerPipe.Dispose();
                _clientPipe.Dispose();
                await StopWorkerAsync(_ownedWorker);
            }
        }
    }
}
