using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.WinRar;
using RainArchives.Security;
using RainArchives.Worker;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class WorkerProtocolIntegrationTests
{
    [TestMethod]
    public void NativePermissionFailuresMapWithoutOverclassifyingOtherNativeErrors()
    {
        foreach (var nativeErrorCode in new[] { 5, 1314 })
        {
            var error = WorkerHost.ToError(
                new System.ComponentModel.Win32Exception(nativeErrorCode));

            Assert.AreEqual("filesystem.permission_denied", error.Code);
            Assert.AreEqual(ArchiveErrorCategory.PermissionDenied, error.Category);
            Assert.IsTrue(error.IsRetryable);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error.DiagnosticId));
        }

        var unrelated = WorkerHost.ToError(new System.ComponentModel.Win32Exception(87));
        Assert.AreEqual("filesystem.native_failure", unrelated.Code);
        Assert.AreEqual(ArchiveErrorCategory.ProviderFailure, unrelated.Category);
    }

    [TestMethod]
    public void AggregateOperationFailuresPreserveTheAuthoritativePrimaryError()
    {
        var diskFull = WorkerHost.ToError(new AggregateException(
            "Creation and cleanup both failed.",
            new IOException("The physical volume is full.", unchecked((int)0x80070070)),
            new InvalidOperationException("Cleanup also failed.")));

        Assert.AreEqual("resource.insufficient_space", diskFull.Code);
        Assert.AreEqual(ArchiveErrorCategory.InsufficientSpace, diskFull.Category);
        Assert.IsTrue(diskFull.IsRetryable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(diskFull.DiagnosticId));

        var primaryProviderFailure = WorkerHost.ToError(new AggregateException(
            "Creation and cleanup both failed.",
            new ArchiveProviderException(
                ArchiveErrorCategory.SourceChanged,
                "source.changed_during_creation",
                "The source changed."),
            new IOException("The cleanup volume is full.", unchecked((int)0x80070070))));

        Assert.AreEqual("source.changed_during_creation", primaryProviderFailure.Code);
        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, primaryProviderFailure.Category);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task QueuedOpenCancellationReturnsAuthoritativeResponseAndSettlesSecret()
    {
        await using var harness = await RawWorkerHarness.StartAsync();
        var slot = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        await harness.StoreSecretAsync(slot.SlotId, "queued-open-secret");

        var missingPath = Path.Combine(
            Path.GetTempPath(),
            "RainArchives.WorkerProtocolIntegrationTests",
            Guid.NewGuid().ToString("N"),
            "queued.zip");
        using (harness.HoldNormalDispatchSlots())
        {
            var openRequest = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.OpenArchive,
                new OpenArchiveRequest(missingPath, slot.SlotId));
            var cancelRequest = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.CancelRequest,
                new CancelRequestRequest(openRequest.RequestId));

            var first = await harness.ReadFrameAsync();
            var second = await harness.ReadFrameAsync();
            var byRequest = new[] { first, second }.ToDictionary(frame => frame.RequestId);

            var cancelResponse = byRequest[cancelRequest.RequestId]
                .ReadPayload<CancelRequestResponse>();
            Assert.IsTrue(cancelResponse.CancellationRequested);
            var openFrame = byRequest[openRequest.RequestId];
            Assert.AreEqual(ProtocolMessageKinds.Error, openFrame.Kind);
            var openError = openFrame.ReadPayload<ErrorResponse>().Error;
            Assert.AreEqual(ArchiveErrorCategory.Cancelled, openError.Category);
            Assert.AreEqual("operation.cancelled", openError.Code);
        }

        var reuse = await harness.SendForErrorAsync(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "fixtures",
                    "corpus",
                    "archives",
                    "zip",
                    "plain.zip"),
                slot.SlotId));
        Assert.AreEqual("secret.invalid_slot", reuse.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task EarlyOpenFailuresSettleSecretsWithoutClientDiscardFallback()
    {
        await using var harness = await RawWorkerHarness.StartAsync();
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            "RainArchives.WorkerProtocolIntegrationTests",
            Guid.NewGuid().ToString("N"),
            "missing.zip");

        for (var index = 0; index < ProtocolConstants.MaximumSecretSlots; index++)
        {
            var slot = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
                ProtocolMessageKinds.CreateSecretSlot,
                new CreateSecretSlotRequest());
            await harness.StoreSecretAsync(slot.SlotId, $"raw-failed-open-{index}");

            var error = await harness.SendForErrorAsync(
                ProtocolMessageKinds.OpenArchive,
                new OpenArchiveRequest(missingPath, slot.SlotId));
            Assert.AreEqual("source.not_found", error.Code);
        }

        var capacityProof = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        var discarded = await harness.SendAsync<DiscardSecretSlotRequest, DiscardSecretSlotResponse>(
            ProtocolMessageKinds.DiscardSecretSlot,
            new DiscardSecretSlotRequest(capacityProof.SlotId));
        Assert.IsTrue(discarded.Discarded);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PreHandoffOperationFailuresSettleSecretsWithoutClientDiscardFallback()
    {
        await using var harness = await RawWorkerHarness.StartAsync();
        for (var index = 0; index < ProtocolConstants.MaximumSecretSlots; index++)
        {
            var slot = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
                ProtocolMessageKinds.CreateSecretSlot,
                new CreateSecretSlotRequest());
            await harness.StoreSecretAsync(slot.SlotId, $"raw-failed-operation-{index}");

            var error = await harness.SendForErrorAsync(
                ProtocolMessageKinds.StartIntegrityTest,
                new StartIntegrityTestRequest(ArchiveSessionId.New(), [], slot.SlotId));
            Assert.AreEqual("session.not_found", error.Code);
        }

        var capacityProof = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        var discarded = await harness.SendAsync<DiscardSecretSlotRequest, DiscardSecretSlotResponse>(
            ProtocolMessageKinds.DiscardSecretSlot,
            new DiscardSecretSlotRequest(capacityProof.SlotId));
        Assert.IsTrue(discarded.Discarded);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task WorkerRejectsMinorOneWelcomeThatEnablesMinorTwoFeature()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            protocolMinor: 1,
            ProtocolFeatures.SupportedVersion1);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await harness.WorkerRunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CurrentWorkerAcceptsMinorElevenClientAndRefusesTheNewRequestKind()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            ProtocolConstants.NestedWorkspaceRecoveryMinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(
                ProtocolConstants.NestedWorkspaceRecoveryMinorVersion));

        var secret = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        Assert.AreNotEqual(Guid.Empty, secret.SlotId);
        _ = await harness.SendAsync<DiscardSecretSlotRequest, DiscardSecretSlotResponse>(
            ProtocolMessageKinds.DiscardSecretSlot,
            new DiscardSecretSlotRequest(secret.SlotId));

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.StartZipRebuildCopy,
            new StartZipRebuildCopyRequest(
                ArchiveSessionId.New(),
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
                [],
                []));

        Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, error.Category);
        Assert.AreEqual("protocol.feature_not_negotiated", error.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CurrentWorkerAcceptsMinorTwelveClientAndRefusesRebuildRenameBeforeSessionAccess()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            ProtocolConstants.ZipRebuildCopyMinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(
                ProtocolConstants.ZipRebuildCopyMinorVersion));

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.StartZipRebuildRename,
            new StartZipRebuildRenameRequest(
                ArchiveSessionId.New(),
                new string('?', ProtocolConstants.MaximumPathCharacters + 1),
                [],
                [],
                []));

        Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, error.Category);
        Assert.AreEqual("protocol.feature_not_negotiated", error.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WorkerRebuildRenameValidatesRequestBeforeSessionOrFilesystemAccess()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.StartZipRebuildRename,
            new StartZipRebuildRenameRequest(
                ArchiveSessionId.New(),
                new string('x', ProtocolConstants.MaximumPathCharacters + 1),
                [],
                [],
                []));

        Assert.AreEqual(ArchiveErrorCategory.InvalidRequest, error.Category);
        Assert.AreEqual("zip.rebuild_rename_invalid_request", error.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CurrentWorkerAcceptsMinorThirteenClientAndRefusesTreeRequestsBeforePayloadAccess()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            ProtocolConstants.ZipRebuildRenameMinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.ZipRebuildRenameMinorVersion));

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.GetZipRebuildTreePlan,
            new GetZipRebuildTreePlanRequest(ArchiveSessionId.New()));

        Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, error.Category);
        Assert.AreEqual("protocol.feature_not_negotiated", error.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WorkerTreeEditValidatesBoundedRequestBeforeSessionOrFilesystemAccess()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.StartZipRebuildTreeEdit,
            new StartZipRebuildTreeEditRequest(
                ArchiveSessionId.New(),
                new string('x', ProtocolConstants.MaximumPathCharacters + 1),
                [],
                [],
                [],
                [],
                []));

        Assert.AreEqual(ArchiveErrorCategory.InvalidRequest, error.Category);
        Assert.AreEqual("zip.rebuild_tree_edit_invalid_request", error.Code);
    }

    [TestMethod]
    [DataRow(ProtocolFeatures.ZipRebuildRename)]
    [DataRow(ProtocolFeatures.ZipRebuildCopy)]
    [DataRow(ProtocolFeatures.ZipDirectoryCreation)]
    [DataRow(ProtocolFeatures.RequestCancellation)]
    [DataRow(ProtocolFeatures.PersistentEntryKeys)]
    [DataRow(ProtocolFeatures.VolumeSetIdentity)]
    [DataRow(ProtocolFeatures.ZipCreation)]
    [Timeout(15_000)]
    public async Task WorkerRejectsTreeEditWithoutEveryRequiredFeatureDependency(string omittedFeature)
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        var inconsistentFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.MinorVersion)
            .Where(feature => feature != omittedFeature)
            .ToArray();
        await harness.SendWelcomeAsync(ProtocolConstants.MinorVersion, inconsistentFeatures);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await harness.WorkerRunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [DataRow(ProtocolFeatures.ZipRebuildCopy)]
    [DataRow(ProtocolFeatures.RequestCancellation)]
    [DataRow(ProtocolFeatures.PersistentEntryKeys)]
    [DataRow(ProtocolFeatures.VolumeSetIdentity)]
    [DataRow(ProtocolFeatures.ZipCreation)]
    [Timeout(15_000)]
    public async Task WorkerRejectsRebuildRenameWithoutEveryRequiredFeatureDependency(
        string omittedFeature)
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        var inconsistentFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.MinorVersion)
            .Where(feature => feature != omittedFeature)
            .ToArray();
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            inconsistentFeatures);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await harness.WorkerRunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [DataRow(ProtocolFeatures.RequestCancellation)]
    [DataRow(ProtocolFeatures.PersistentEntryKeys)]
    [DataRow(ProtocolFeatures.VolumeSetIdentity)]
    [DataRow(ProtocolFeatures.ZipCreation)]
    [Timeout(15_000)]
    public async Task WorkerRejectsZipRebuildWithoutEveryRequiredFeatureDependency(
        string omittedFeature)
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        var inconsistentFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.MinorVersion)
            .Where(feature => feature != omittedFeature)
            .ToArray();
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            inconsistentFeatures);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await harness.WorkerRunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task WorkerRejectsRecoveryInspectionWithoutRequestCancellation()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        var inconsistentFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.MinorVersion)
            .Where(feature => feature != ProtocolFeatures.RequestCancellation)
            .ToArray();
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            inconsistentFeatures);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await harness.WorkerRunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task WorkerRejectsRecoveryDispositionWithoutInspectionDependency()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        var inconsistentFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.MinorVersion)
            .Where(feature => feature != ProtocolFeatures.ArchiveCreationRecoveryInspection)
            .ToArray();
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            inconsistentFeatures);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await harness.WorkerRunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task WorkerRejectsNestedRecoveryWithoutRequestCancellation()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        var inconsistentFeatures = ProtocolFeatures.RequiredVersion1
            .Append(ProtocolFeatures.NestedWorkspaceRecovery)
            .ToArray();
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            inconsistentFeatures);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await harness.WorkerRunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [DataRow(ProtocolFeatures.OperationEvents)]
    [DataRow(ProtocolFeatures.RequestCancellation)]
    [Timeout(15_000)]
    public async Task WorkerRejectsOperationActivityWithoutEveryRequiredDependency(
        string omittedFeature)
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        var inconsistentFeatures = ProtocolFeatures.RequiredVersion1
            .Append(ProtocolFeatures.RequestCancellation)
            .Append(ProtocolFeatures.OperationActivity)
            .Where(feature => feature != omittedFeature)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            inconsistentFeatures);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await harness.WorkerRunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [DataRow(ProtocolFeatures.OperationActivity)]
    [DataRow(ProtocolFeatures.RequestCancellation)]
    [Timeout(15_000)]
    public async Task WorkerRejectsSupportDiagnosticsWithoutEveryRequiredDependency(
        string omittedFeature)
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        var inconsistentFeatures = ProtocolFeatures.RequiredVersion1
            .Append(ProtocolFeatures.RequestCancellation)
            .Append(ProtocolFeatures.OperationActivity)
            .Append(ProtocolFeatures.SupportDiagnostics)
            .Where(feature => feature != omittedFeature)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await harness.SendWelcomeAsync(
            ProtocolConstants.SupportDiagnosticsMinorVersion,
            inconsistentFeatures);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await harness.WorkerRunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [DataRow(ProtocolFeatures.NewDestinationExtraction)]
    [DataRow(ProtocolFeatures.SourceFingerprintValidation)]
    [DataRow(ProtocolFeatures.RequestCancellation)]
    [Timeout(15_000)]
    public async Task WorkerRejectsExtractionRecoveryInspectionWithoutEveryRequiredDependency(
        string omittedFeature)
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        var inconsistentFeatures = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.MinorVersion)
            .Where(feature => feature != omittedFeature)
            .ToArray();
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            inconsistentFeatures);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await harness.WorkerRunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ExtractionRecoveryListIsPathFreeAndInspectionIsExplicitlyReadOnly()
    {
        using var root = new TemporaryDirectory();
        var recoveryRoot = Path.Combine(root.Path, "extraction-recovery");
        var destination = Path.Combine(root.Path, "private-destination-name");
        var operationId = OperationId.New();
        var store = new ExtractionRecoveryStore(recoveryRoot);
        var transaction = await store.BeginAsync(
            operationId,
            destination,
            FileSystemIdentityService.CaptureExistingDirectoryIdentity(root.Path),
            CancellationToken.None);
        var recoveryId = transaction.Token;
        await transaction.DisposeAsync();

        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            extractionRecoveryRoot: recoveryRoot);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var inventory = await harness.SendAsync<
            ListExtractionRecoveriesRequest,
            ListExtractionRecoveriesResponse>(
            ProtocolMessageKinds.ListExtractionRecoveries,
            new ListExtractionRecoveriesRequest());
        var summary = inventory.Recoveries.Single();
        Assert.AreEqual(recoveryId, summary.RecoveryId);
        Assert.AreEqual(ExtractionRecoveryPhase.Admitted, summary.Phase);
        Assert.AreEqual(ExtractionRecoveryInventoryState.Incomplete, summary.State);
        var inventoryJson = JsonSerializer.Serialize(inventory, ProtocolJson.Options);
        Assert.IsFalse(inventoryJson.Contains(destination, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(inventoryJson.Contains(operationId.Value.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(inventoryJson.Contains("private-destination-name", StringComparison.Ordinal));

        var inspection = await harness.SendAsync<
            InspectExtractionRecoveryRequest,
            InspectExtractionRecoveryResponse>(
            ProtocolMessageKinds.InspectExtractionRecovery,
            new InspectExtractionRecoveryRequest(recoveryId));
        Assert.AreEqual(destination, inspection.DestinationPath);
        Assert.AreEqual(ExtractionRecoveryParentState.MatchesJournal, inspection.DestinationParent);
        Assert.AreEqual(ExtractionRecoveryArtifactState.Absent, inspection.StagingArtifact.State);
        Assert.AreEqual(ExtractionRecoveryArtifactState.Absent, inspection.FinalArtifact.State);
        Assert.AreEqual(
            ExtractionRecoveryAssessment.NoOwnedArtifactObserved,
            inspection.Assessment);
        Assert.IsFalse(inspection.SupportsMutation);
        Assert.IsFalse(inspection.SupportsRetry);
        Assert.IsFalse(inspection.SupportsCleanup);
        Assert.IsTrue(Directory.Exists(transaction.OperationDirectoryPath));
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task MinorNineteenExtractionRecoveryRequestsRefuseBeforePayloadDeserialization()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            ProtocolConstants.OperationActivityMinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(
                ProtocolConstants.OperationActivityMinorVersion));
        using var malformedDocument = JsonDocument.Parse("{\"unexpected\":true}");

        foreach (var kind in new[]
                 {
                     ProtocolMessageKinds.ListExtractionRecoveries,
                     ProtocolMessageKinds.InspectExtractionRecovery,
                 })
        {
            var error = await harness.SendForErrorAsync(kind, malformedDocument.RootElement.Clone());
            Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, error.Category);
            Assert.AreEqual("protocol.feature_not_negotiated", error.Code);
        }

        var proof = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        Assert.AreNotEqual(Guid.Empty, proof.SlotId);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MinorSixConnectionRejectsZipCreationBeforeOperationAdmission()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            ProtocolConstants.NestedArchiveOpenMinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.NestedArchiveOpenMinorVersion));

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.StartZipCreation,
            new StartZipCreationRequest(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
                [],
                ArchiveCompressionPreset.Balanced));

        Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, error.Category);
        Assert.AreEqual("protocol.feature_not_negotiated", error.Code);

        var proof = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        Assert.AreNotEqual(Guid.Empty, proof.SlotId);
        _ = await harness.SendAsync<DiscardSecretSlotRequest, DiscardSecretSlotResponse>(
            ProtocolMessageKinds.DiscardSecretSlot,
            new DiscardSecretSlotRequest(proof.SlotId));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MinorSevenConnectionRejectsZipTreeCreationBeforeOperationAdmission()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            ProtocolConstants.ZipCreationMinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.ZipCreationMinorVersion));

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.StartZipTreeCreation,
            new StartZipTreeCreationRequest(
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
                [],
                [new ArchiveCreationDirectory(Path.GetTempPath(), "source")],
                ArchiveCompressionPreset.Balanced));

        Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, error.Category);
        Assert.AreEqual("protocol.feature_not_negotiated", error.Code);

        var proof = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        Assert.AreNotEqual(Guid.Empty, proof.SlotId);
        _ = await harness.SendAsync<DiscardSecretSlotRequest, DiscardSecretSlotResponse>(
            ProtocolMessageKinds.DiscardSecretSlot,
            new DiscardSecretSlotRequest(proof.SlotId));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MinorEightConnectionRejectsRecoveryInspectionBeforeDestinationAccess()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            ProtocolConstants.ZipDirectoryCreationMinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(
                ProtocolConstants.ZipDirectoryCreationMinorVersion));

        var listError = await harness.SendForErrorAsync(
            ProtocolMessageKinds.ListArchiveCreationRecoveries,
            new ListArchiveCreationRecoveriesRequest());
        var inspectError = await harness.SendForErrorAsync(
            ProtocolMessageKinds.InspectArchiveCreationRecovery,
            new InspectArchiveCreationRecoveryRequest(new string('a', 64)));

        Assert.AreEqual("protocol.feature_not_negotiated", listError.Code);
        Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, listError.Category);
        Assert.AreEqual("protocol.feature_not_negotiated", inspectError.Code);
        Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, inspectError.Category);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MinorNineConnectionRejectsRecoveryDispositionBeforeDestinationAccess()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            ProtocolConstants.ArchiveCreationRecoveryInspectionMinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(
                ProtocolConstants.ArchiveCreationRecoveryInspectionMinorVersion));

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.ApplyArchiveCreationRecoveryDisposition,
            new ApplyArchiveCreationRecoveryDispositionRequest(
                new string('a', 64),
                ArchiveCreationRecoveryPhase.CommitIntent,
                4,
                DateTimeOffset.UtcNow,
                ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging));

        Assert.AreEqual("protocol.feature_not_negotiated", error.Code);
        Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, error.Category);
        var healthy = await harness.SendAsync<
            ListArchiveCreationRecoveriesRequest,
            ListArchiveCreationRecoveriesResponse>(
            ProtocolMessageKinds.ListArchiveCreationRecoveries,
            new ListArchiveCreationRecoveriesRequest());
        Assert.IsNotNull(healthy.Recoveries);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MinorTenConnectionRejectsNestedRecoveryBeforeWorkspaceAccess()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            ProtocolConstants.ArchiveCreationRecoveryDispositionMinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(
                ProtocolConstants.ArchiveCreationRecoveryDispositionMinorVersion));

        var recoveryId = new string('a', 64);
        var listError = await harness.SendForErrorAsync(
            ProtocolMessageKinds.ListNestedWorkspaceRecoveries,
            new ListNestedWorkspaceRecoveriesRequest());
        var inspectError = await harness.SendForErrorAsync(
            ProtocolMessageKinds.InspectNestedWorkspaceRecovery,
            new InspectNestedWorkspaceRecoveryRequest(recoveryId));
        var discardError = await harness.SendForErrorAsync(
            ProtocolMessageKinds.DiscardNestedWorkspaceRecovery,
            new DiscardNestedWorkspaceRecoveryRequest(recoveryId, recoveryId, 0, 0));

        foreach (var error in new[] { listError, inspectError, discardError })
        {
            Assert.AreEqual("protocol.feature_not_negotiated", error.Code);
            Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, error.Category);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RecoveryListingIsFreshPathFreeAndInspectionIsReadOnly()
    {
        using var root = new TemporaryDirectory();
        var recoveryRoot = Path.Combine(root.Path, "creation-recovery");
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            creationRecoveryRoot: recoveryRoot);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var store = new ArchiveCreationRecoveryStore(recoveryRoot);
        var destination = Path.Combine(root.Path, "created.zip");
        var parentIdentity = FileSystemIdentityService.CaptureExistingDirectoryIdentity(root.Path);
        var transaction = await store.BeginAsync(
            OperationId.New(),
            destination,
            parentIdentity,
            CancellationToken.None);
        var stagingPath = Path.Combine(root.Path, transaction.StagingFileName);
        var payload = Enumerable.Range(0, 513).Select(value => (byte)value).ToArray();
        string stagingIdentity;
        await using (var stream = new FileStream(
                         stagingPath,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.Read | FileShare.Write | FileShare.Delete,
                         4096,
                         FileOptions.Asynchronous))
        {
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
            stagingIdentity = FileSystemIdentityService.CaptureOpenFile(stream, stagingPath).Identity
                ?? throw new InvalidOperationException("Windows did not return a staging identity.");
        }

        await transaction.RecordWritingAsync(stagingIdentity, CancellationToken.None);
        await transaction.RecordFinalizedAsync(payload.Length, CancellationToken.None);
        await transaction.RecordCommitIntentAsync(CancellationToken.None);
        File.Move(stagingPath, destination);
        await transaction.DisposeAsync();
        var localRecord = store.Inspect().RecoveryRecords.Single();

        var listed = await harness.SendAsync<
            ListArchiveCreationRecoveriesRequest,
            ListArchiveCreationRecoveriesResponse>(
            ProtocolMessageKinds.ListArchiveCreationRecoveries,
            new ListArchiveCreationRecoveriesRequest());
        var summary = listed.Recoveries.Single();
        Assert.AreEqual(localRecord.RecoveryId, summary.RecoveryId);
        Assert.AreEqual(localRecord.OperationId, summary.OperationId);
        var listedJson = System.Text.Json.JsonSerializer.Serialize(listed, ProtocolJson.Options);
        Assert.IsFalse(listedJson.Contains(destination, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(listedJson.Contains(transaction.StagingFileName, StringComparison.Ordinal));

        var inspected = await harness.SendAsync<
            InspectArchiveCreationRecoveryRequest,
            InspectArchiveCreationRecoveryResponse>(
            ProtocolMessageKinds.InspectArchiveCreationRecovery,
            new InspectArchiveCreationRecoveryRequest(localRecord.RecoveryId));
        Assert.AreEqual(destination, inspected.DestinationPath);
        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.ExactMatch,
            inspected.FinalArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.OwnedFinalObserved,
            inspected.Assessment);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(destination));
        Assert.AreEqual(1, store.Inspect().PotentialRecoveryCount);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RecoveryDispositionRoundTripsThroughWorkerAndReplaysDurably()
    {
        using var root = new TemporaryDirectory();
        var recoveryRoot = Path.Combine(root.Path, "creation-recovery");
        var destinationParent = Path.Combine(root.Path, "destination");
        Directory.CreateDirectory(destinationParent);
        var destination = Path.Combine(destinationParent, "created.zip");
        var store = new ArchiveCreationRecoveryStore(recoveryRoot);
        var transaction = await store.BeginAsync(
            OperationId.New(),
            destination,
            FileSystemIdentityService.CaptureExistingDirectoryIdentity(destinationParent),
            CancellationToken.None);
        var stagingPath = Path.Combine(destinationParent, transaction.StagingFileName);
        string stagingIdentity;
        await using (var stream = new FileStream(
                         stagingPath,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.Read | FileShare.Write | FileShare.Delete,
                         4096,
                         FileOptions.Asynchronous))
        {
            await stream.WriteAsync("recovery-disposition"u8.ToArray());
            await stream.FlushAsync();
            stream.Flush(flushToDisk: true);
            stagingIdentity = FileSystemIdentityService.CaptureOpenFile(stream, stagingPath).Identity
                ?? throw new InvalidOperationException("Windows did not return a staging identity.");
        }

        await transaction.RecordWritingAsync(stagingIdentity, CancellationToken.None);
        await transaction.DisposeAsync();
        var record = store.Inspect().RecoveryRecords.Single();
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            creationRecoveryRoot: recoveryRoot);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var inspection = await harness.SendAsync<
            InspectArchiveCreationRecoveryRequest,
            InspectArchiveCreationRecoveryResponse>(
            ProtocolMessageKinds.InspectArchiveCreationRecovery,
            new InspectArchiveCreationRecoveryRequest(record.RecoveryId));
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.OwnedStagingObserved,
            inspection.Assessment);
        var request = new ApplyArchiveCreationRecoveryDispositionRequest(
            inspection.Recovery.RecoveryId,
            inspection.Recovery.Phase,
            inspection.Recovery.Sequence,
            inspection.Recovery.RecordedAtUtc,
            ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging);

        var completed = await harness.SendAsync<
            ApplyArchiveCreationRecoveryDispositionRequest,
            ApplyArchiveCreationRecoveryDispositionResponse>(
            ProtocolMessageKinds.ApplyArchiveCreationRecoveryDisposition,
            request);

        Assert.AreEqual(ArchiveCreationRecoveryDispositionResult.Completed, completed.Result);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved,
            completed.FinalAssessment);
        Assert.IsFalse(File.Exists(stagingPath));
        Assert.IsFalse(File.Exists(destination));
        Assert.AreEqual(0, store.Inspect().PotentialRecoveryCount);

        var movedParent = destinationParent + "-moved";
        Directory.Move(destinationParent, movedParent);
        var replay = await harness.SendAsync<
            ApplyArchiveCreationRecoveryDispositionRequest,
            ApplyArchiveCreationRecoveryDispositionResponse>(
            ProtocolMessageKinds.ApplyArchiveCreationRecoveryDisposition,
            request);
        Assert.AreEqual(
            ArchiveCreationRecoveryDispositionResult.AlreadyCompleted,
            replay.Result);
        Assert.AreEqual(completed.ResolvedAtUtc, replay.ResolvedAtUtc);

        var listed = await harness.SendAsync<
            ListArchiveCreationRecoveriesRequest,
            ListArchiveCreationRecoveriesResponse>(
            ProtocolMessageKinds.ListArchiveCreationRecoveries,
            new ListArchiveCreationRecoveriesRequest());
        Assert.HasCount(0, listed.Recoveries);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WorkerStartupPrunesOnlyAnExpiredCompletedRecoveryReceipt()
    {
        using var root = new TemporaryDirectory();
        var recoveryRoot = Path.Combine(root.Path, "creation-recovery");
        var destinationParent = Path.Combine(root.Path, "destination");
        Directory.CreateDirectory(destinationParent);
        var store = new ArchiveCreationRecoveryStore(recoveryRoot);
        var transaction = await store.BeginAsync(
            OperationId.New(),
            Path.Combine(destinationParent, "created.zip"),
            FileSystemIdentityService.CaptureExistingDirectoryIdentity(destinationParent),
            CancellationToken.None);
        var operationDirectoryPath = transaction.OperationDirectoryPath;
        await transaction.DisposeAsync();
        var record = store.Inspect().RecoveryRecords.Single();
        var request = new ApplyArchiveCreationRecoveryDispositionRequest(
            record.RecoveryId,
            record.Phase,
            record.Sequence,
            record.RecordedAtUtc,
            ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);
        var completed = await store.ApplyDispositionAsync(
            request.RecoveryId,
            request.ExpectedPhase,
            request.ExpectedSequence,
            request.ExpectedRecordedAtUtc,
            request.Action);
        Directory.Delete(destinationParent);

        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            creationRecoveryRoot: recoveryRoot,
            recoveryMaintenanceNowUtc:
                completed.ResolvedAtUtc
                + ArchiveCreationRecoveryStore.CompletedDispositionReceiptRetention
                + TimeSpan.FromTicks(1));
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        Assert.IsFalse(Directory.Exists(operationDirectoryPath));
        var replay = await harness.SendForErrorAsync(
            ProtocolMessageKinds.ApplyArchiveCreationRecoveryDisposition,
            request);
        Assert.AreEqual(ArchiveErrorCategory.InvalidRequest, replay.Category);
        Assert.AreEqual("recovery.not_found", replay.Code);
        Assert.IsFalse(replay.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
        var listed = await harness.SendAsync<
            ListArchiveCreationRecoveriesRequest,
            ListArchiveCreationRecoveriesResponse>(
            ProtocolMessageKinds.ListArchiveCreationRecoveries,
            new ListArchiveCreationRecoveriesRequest());
        Assert.HasCount(0, listed.Recoveries);
        Assert.AreEqual(0, listed.SuspiciousOperationCount);
        Assert.IsFalse(listed.InspectionIncomplete);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RecoveryInspectionErrorsAreStructuredAndPathFree()
    {
        using var root = new TemporaryDirectory();
        var recoveryRoot = Path.Combine(root.Path, "creation-recovery");
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            creationRecoveryRoot: recoveryRoot);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var invalid = await harness.SendForErrorAsync(
            ProtocolMessageKinds.InspectArchiveCreationRecovery,
            new InspectArchiveCreationRecoveryRequest("../invalid"));
        var missing = await harness.SendForErrorAsync(
            ProtocolMessageKinds.InspectArchiveCreationRecovery,
            new InspectArchiveCreationRecoveryRequest(new string('f', 64)));

        Assert.AreEqual("recovery.invalid_id", invalid.Code);
        Assert.AreEqual("recovery.not_found", missing.Code);
        Assert.IsFalse(invalid.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(missing.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ActiveRecoveryRecordIsReportedWithoutDestinationAccess()
    {
        using var root = new TemporaryDirectory();
        var recoveryRoot = Path.Combine(root.Path, "creation-recovery");
        var store = new ArchiveCreationRecoveryStore(recoveryRoot);
        var transaction = await store.BeginAsync(
            OperationId.New(),
            Path.Combine(root.Path, "created.zip"),
            FileSystemIdentityService.CaptureExistingDirectoryIdentity(root.Path),
            CancellationToken.None);
        try
        {
            await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
                creationRecoveryRoot: recoveryRoot);
            await harness.SendWelcomeAsync(
                ProtocolConstants.MinorVersion,
                ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

            var error = await harness.SendForErrorAsync(
                ProtocolMessageKinds.InspectArchiveCreationRecovery,
                new InspectArchiveCreationRecoveryRequest(transaction.Token));

            Assert.AreEqual("recovery.active", error.Code);
            Assert.AreEqual(ArchiveErrorCategory.SourceChanged, error.Category);
            Assert.IsFalse(error.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task QueuedRecoveryInspectionCancellationLeavesWorkerHealthy()
    {
        await using var harness = await RawWorkerHarness.StartAsync();
        using (harness.HoldNormalDispatchSlots())
        {
            var inspectionRequest = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.InspectArchiveCreationRecovery,
                new InspectArchiveCreationRecoveryRequest(new string('d', 64)));
            var cancelRequest = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.CancelRequest,
                new CancelRequestRequest(inspectionRequest.RequestId));

            var first = await harness.ReadFrameAsync();
            var second = await harness.ReadFrameAsync();
            var byRequest = new[] { first, second }.ToDictionary(frame => frame.RequestId);
            Assert.IsTrue(byRequest[cancelRequest.RequestId]
                .ReadPayload<CancelRequestResponse>()
                .CancellationRequested);
            var inspectionFrame = byRequest[inspectionRequest.RequestId];
            Assert.AreEqual(ProtocolMessageKinds.Error, inspectionFrame.Kind);
            var inspectionError = inspectionFrame.ReadPayload<ErrorResponse>().Error;
            Assert.AreEqual("operation.cancelled", inspectionError.Code);
            Assert.AreEqual(ArchiveErrorCategory.Cancelled, inspectionError.Category);
        }

        var healthy = await harness.SendAsync<
            ListArchiveCreationRecoveriesRequest,
            ListArchiveCreationRecoveriesResponse>(
            ProtocolMessageKinds.ListArchiveCreationRecoveries,
            new ListArchiveCreationRecoveriesRequest());
        Assert.IsNotNull(healthy.Recoveries);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task QueuedRecoveryDispositionCancellationLeavesWorkerHealthy()
    {
        await using var harness = await RawWorkerHarness.StartAsync();
        using (harness.HoldNormalDispatchSlots())
        {
            var dispositionRequest = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.ApplyArchiveCreationRecoveryDisposition,
                new ApplyArchiveCreationRecoveryDispositionRequest(
                    new string('e', 64),
                    ArchiveCreationRecoveryPhase.Writing,
                    2,
                    DateTimeOffset.UtcNow,
                    ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging));
            var cancelRequest = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.CancelRequest,
                new CancelRequestRequest(dispositionRequest.RequestId));

            var first = await harness.ReadFrameAsync();
            var second = await harness.ReadFrameAsync();
            var byRequest = new[] { first, second }.ToDictionary(frame => frame.RequestId);
            Assert.IsTrue(byRequest[cancelRequest.RequestId]
                .ReadPayload<CancelRequestResponse>()
                .CancellationRequested);
            var dispositionFrame = byRequest[dispositionRequest.RequestId];
            Assert.AreEqual(ProtocolMessageKinds.Error, dispositionFrame.Kind);
            var dispositionError = dispositionFrame.ReadPayload<ErrorResponse>().Error;
            Assert.AreEqual("operation.cancelled", dispositionError.Code);
            Assert.AreEqual(ArchiveErrorCategory.Cancelled, dispositionError.Category);
        }

        var healthy = await harness.SendAsync<
            ListArchiveCreationRecoveriesRequest,
            ListArchiveCreationRecoveriesResponse>(
            ProtocolMessageKinds.ListArchiveCreationRecoveries,
            new ListArchiveCreationRecoveriesRequest());
        Assert.IsNotNull(healthy.Recoveries);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30_000)]
    public async Task ZipCreationCancellationRemovesStagingAndWorkerContinuesServingRequests(
        bool useDirectoryTree)
    {
        using var root = new TemporaryDirectory();
        var sourceTree = Path.Combine(root.Path, "source-tree");
        Directory.CreateDirectory(sourceTree);
        var source = Path.Combine(sourceTree, "source.bin");
        var destination = Path.Combine(root.Path, "cancelled.zip");
        await File.WriteAllBytesAsync(source, [0x52]);
        var writerProvider = new BlockingZipWriterProvider();
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            providers: [new NoMatchArchiveProvider()],
            writerProviders: [writerProvider]);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var started = useDirectoryTree
            ? await harness.SendAsync<StartZipTreeCreationRequest, StartOperationResponse>(
                ProtocolMessageKinds.StartZipTreeCreation,
                new StartZipTreeCreationRequest(
                    destination,
                    [],
                    [new ArchiveCreationDirectory(sourceTree, "source-tree")],
                    ArchiveCompressionPreset.Balanced))
            : await harness.SendAsync<StartZipCreationRequest, StartOperationResponse>(
                ProtocolMessageKinds.StartZipCreation,
                new StartZipCreationRequest(
                    destination,
                    [new ArchiveCreationFile(source, "source.bin")],
                    ArchiveCompressionPreset.Balanced));
        var reachedRunning = false;
        for (var count = 0; count < 8 && !reachedRunning; count++)
        {
            var frame = await harness.ReadFrameAsync();
            if (frame.Kind == ProtocolMessageKinds.OperationEvent)
            {
                var operationEvent = frame.ReadPayload<OperationEvent>();
                reachedRunning = operationEvent.OperationId == started.OperationId
                    && operationEvent.Kind == OperationEventKind.StateChanged
                    && operationEvent.Phase == OperationPhase.Running;
                if (operationEvent.OperationId == started.OperationId
                    && operationEvent.Kind == OperationEventKind.Terminal)
                {
                    Assert.Fail(
                        $"ZIP creation failed before reaching the injected writer: "
                        + $"{operationEvent.Error?.Code} ({operationEvent.Error?.Category}) {operationEvent.Error?.Message}");
                }
            }
        }

        Assert.IsTrue(reachedRunning, "ZIP creation did not reach its running phase.");

        var cancelEnvelope = await harness.SendWithoutWaitingAsync(
            ProtocolMessageKinds.CancelOperation,
            new CancelOperationRequest(started.OperationId));
        CancelOperationResponse? cancellationResponse = null;
        OperationEvent? terminal = null;
        for (var count = 0; count < 32 && (cancellationResponse is null || terminal is null); count++)
        {
            var frame = await harness.ReadFrameAsync();
            if (frame.RequestId == cancelEnvelope.RequestId)
            {
                Assert.AreEqual($"{ProtocolMessageKinds.CancelOperation}.response", frame.Kind);
                cancellationResponse = frame.ReadPayload<CancelOperationResponse>();
            }
            else if (frame.Kind == ProtocolMessageKinds.OperationEvent)
            {
                var operationEvent = frame.ReadPayload<OperationEvent>();
                if (operationEvent.OperationId == started.OperationId
                    && operationEvent.Kind == OperationEventKind.Terminal)
                {
                    terminal = operationEvent;
                }
            }
        }

        Assert.IsNotNull(cancellationResponse);
        Assert.IsTrue(cancellationResponse.CancellationRequested);
        Assert.IsNotNull(terminal);
        Assert.AreEqual(OperationPhase.Cancelled, terminal.Phase);
        Assert.AreEqual("operation.cancelled", terminal.Error?.Code);
        Assert.IsFalse(File.Exists(destination));
        Assert.HasCount(
            0,
            Directory.GetFiles(
                root.Path,
                ".rainarchives-*.zip.staging",
                SearchOption.TopDirectoryOnly));

        var proof = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        Assert.AreNotEqual(Guid.Empty, proof.SlotId);
        _ = await harness.SendAsync<DiscardSecretSlotRequest, DiscardSecretSlotResponse>(
            ProtocolMessageKinds.DiscardSecretSlot,
            new DiscardSecretSlotRequest(proof.SlotId));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task OperationActivityReportsPathFreeActiveAndRetainedTerminalState()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "private-source-name.bin");
        var destination = Path.Combine(root.Path, "private-destination-name.zip");
        await File.WriteAllBytesAsync(source, "rain activity"u8.ToArray());
        var writerProvider = new GateableZipWriterProvider();
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            providers: [new NoMatchArchiveProvider()],
            writerProviders: [writerProvider]);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var started = await harness.SendAsync<StartZipCreationRequest, StartOperationResponse>(
            ProtocolMessageKinds.StartZipCreation,
            new StartZipCreationRequest(
                destination,
                [new ArchiveCreationFile(source, "private-entry-name.bin")],
                ArchiveCompressionPreset.Balanced));
        OperationEvent? running = null;
        for (var count = 0; count < 8 && running is null; count++)
        {
            var frame = await harness.ReadFrameAsync();
            if (frame.Kind != ProtocolMessageKinds.OperationEvent)
            {
                continue;
            }

            var operationEvent = frame.ReadPayload<OperationEvent>();
            if (operationEvent.OperationId == started.OperationId
                && operationEvent.Kind == OperationEventKind.StateChanged
                && operationEvent.Phase == OperationPhase.Running)
            {
                running = operationEvent;
            }
        }

        Assert.IsNotNull(running);
        await writerProvider.EntryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var active = await harness.SendAsync<
            GetOperationActivityRequest,
            GetOperationActivityResponse>(
            ProtocolMessageKinds.GetOperationActivity,
            new GetOperationActivityRequest());
        Assert.AreEqual(ProtocolConstants.MaximumQueuedOperations, active.MaximumActiveAndQueuedOperations);
        Assert.AreEqual(ProtocolConstants.MaximumConcurrentOperations, active.MaximumConcurrentOperations);
        Assert.AreEqual(OperationQueueOrdering.Unspecified, active.QueueOrdering);
        Assert.AreEqual(
            ProtocolConstants.MaximumRetainedOperationActivity,
            active.MaximumRetainedTerminalOperations);
        Assert.IsFalse(active.SupportsReordering);
        Assert.IsFalse(active.SupportsPause);
        Assert.IsFalse(active.SupportsRetry);
        Assert.IsFalse(active.PersistsAcrossWorkerRestart);
        Assert.IsFalse(active.HistoryTruncated);
        var activeOperation = active.Operations.Single(operation =>
            operation.OperationId == started.OperationId);
        Assert.AreEqual(OperationKind.CreateArchive, activeOperation.Kind);
        Assert.AreEqual(OperationPhase.Running, activeOperation.Phase);
        Assert.AreEqual(running.Sequence, activeOperation.LastEventSequence);
        Assert.IsTrue(activeOperation.CanCancel);
        Assert.IsNull(activeOperation.CompletedAtUtc);
        Assert.AreEqual(TimeSpan.Zero, activeOperation.CreatedAtUtc.Offset);
        Assert.IsTrue(activeOperation.CreatedAtUtc <= activeOperation.UpdatedAtUtc);
        Assert.IsTrue(activeOperation.UpdatedAtUtc <= active.SnapshotAtUtc);
        var activeJson = JsonSerializer.Serialize(active, ProtocolJson.Options);
        Assert.IsFalse(activeJson.Contains(source, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(activeJson.Contains(destination, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(activeJson.Contains("private-entry-name", StringComparison.Ordinal));

        var activeDiagnostics = await harness.SendAsync<
            GetSupportDiagnosticsRequest,
            GetSupportDiagnosticsResponse>(
            ProtocolMessageKinds.GetSupportDiagnostics,
            new GetSupportDiagnosticsRequest());
        Assert.AreEqual(ProtocolConstants.MajorVersion, activeDiagnostics.ProtocolMajor);
        Assert.AreEqual(ProtocolConstants.MinorVersion, activeDiagnostics.ProtocolMinor);
        Assert.AreEqual(
            ProtocolFeatures.SupportedVersion1.Count,
            activeDiagnostics.EnabledFeatureCount);
        Assert.AreEqual(
            WorkerContainmentModel.SameUserWorkerProcess,
            activeDiagnostics.ContainmentModel);
        Assert.IsFalse(activeDiagnostics.IsSameUserSecuritySandbox);
        Assert.AreEqual(1, activeDiagnostics.ActiveAndQueuedOperationCount);
        Assert.AreEqual(0, activeDiagnostics.RetainedTerminalOperationCount);
        Assert.IsFalse(activeDiagnostics.OperationHistoryTruncated);
        Assert.IsFalse(activeDiagnostics.SupportsQueueReordering);
        Assert.IsFalse(activeDiagnostics.SupportsPause);
        Assert.IsFalse(activeDiagnostics.SupportsRetry);
        Assert.IsFalse(activeDiagnostics.SupportsAutomaticRecoveryDisposition);
        Assert.IsFalse(activeDiagnostics.PersistsOperationActivityAcrossWorkerRestart);
        var activeDiagnosticsJson = JsonSerializer.Serialize(
            activeDiagnostics,
            ProtocolJson.Options);
        Assert.IsFalse(activeDiagnosticsJson.Contains(source, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(activeDiagnosticsJson.Contains(destination, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(activeDiagnosticsJson.Contains(
            started.OperationId.Value.ToString(),
            StringComparison.OrdinalIgnoreCase));

        writerProvider.ContinueWriting.TrySetResult();
        var terminal = await ReadTerminalAsync(harness, started.OperationId);
        Assert.AreEqual(OperationPhase.Completed, terminal.Phase);
        var retained = await harness.SendAsync<
            GetOperationActivityRequest,
            GetOperationActivityResponse>(
            ProtocolMessageKinds.GetOperationActivity,
            new GetOperationActivityRequest());
        var retainedOperation = retained.Operations.Single(operation =>
            operation.OperationId == started.OperationId);
        Assert.AreEqual(OperationPhase.Completed, retainedOperation.Phase);
        Assert.AreEqual(terminal.Sequence, retainedOperation.LastEventSequence);
        Assert.IsFalse(retainedOperation.CanCancel);
        Assert.IsNotNull(retainedOperation.CompletedAtUtc);
        Assert.AreEqual(retainedOperation.UpdatedAtUtc, retainedOperation.CompletedAtUtc);
        Assert.AreEqual(activeOperation.CreatedAtUtc, retainedOperation.CreatedAtUtc);

        var retainedDiagnostics = await harness.SendAsync<
            GetSupportDiagnosticsRequest,
            GetSupportDiagnosticsResponse>(
            ProtocolMessageKinds.GetSupportDiagnostics,
            new GetSupportDiagnosticsRequest());
        Assert.AreEqual(0, retainedDiagnostics.ActiveAndQueuedOperationCount);
        Assert.AreEqual(1, retainedDiagnostics.RetainedTerminalOperationCount);
        Assert.IsFalse(retainedDiagnostics.OperationHistoryTruncated);
        Assert.IsTrue(File.Exists(destination));
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task OperationQueueRefusesThirtyThirdRequestAndSettlesItsSecretSlot()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            providers: [new MisleadingEntryProvider()]);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(FixturePath(
                "archives",
                "zip",
                "plain.zip")));

        using var executionHold = harness.HoldOperationExecutionSlots();
        var operationIds = new List<OperationId>(ProtocolConstants.MaximumQueuedOperations);
        for (var index = 0; index < ProtocolConstants.MaximumQueuedOperations; index++)
        {
            var request = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.StartIntegrityTest,
                new StartIntegrityTestRequest(opened.Archive.SessionId, []));
            var started = (await ReadResponseFrameForRequestAsync(
                harness,
                request,
                ProtocolMessageKinds.StartIntegrityTest)).ReadPayload<StartOperationResponse>();
            operationIds.Add(started.OperationId);
        }

        Assert.HasCount(ProtocolConstants.MaximumQueuedOperations, operationIds);
        var activityRequest = await harness.SendWithoutWaitingAsync(
            ProtocolMessageKinds.GetOperationActivity,
            new GetOperationActivityRequest());
        var activity = (await ReadResponseFrameForRequestAsync(
            harness,
            activityRequest,
            ProtocolMessageKinds.GetOperationActivity)).ReadPayload<GetOperationActivityResponse>();
        Assert.HasCount(ProtocolConstants.MaximumQueuedOperations, activity.Operations);
        CollectionAssert.AreEquivalent(
            operationIds,
            activity.Operations.Select(operation => operation.OperationId).ToArray());
        Assert.IsTrue(activity.Operations.All(operation =>
            operation.Phase == OperationPhase.Queued));

        var slot = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        await harness.StoreSecretAsync(slot.SlotId, "refused-operation-secret");
        var refusedRequest = await harness.SendWithoutWaitingAsync(
            ProtocolMessageKinds.StartIntegrityTest,
            new StartIntegrityTestRequest(opened.Archive.SessionId, [], slot.SlotId));
        var refusedFrame = await ReadResponseFrameForRequestAsync(
            harness,
            refusedRequest,
            ProtocolMessageKinds.StartIntegrityTest);
        Assert.AreEqual(ProtocolMessageKinds.Error, refusedFrame.Kind);
        var refusedError = refusedFrame.ReadPayload<ErrorResponse>().Error;
        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, refusedError.Category);
        Assert.AreEqual("operation.queue_limit", refusedError.Code);

        var observedTerminalEvents = new List<OperationEvent>();
        foreach (var operationId in operationIds)
        {
            var cancelRequest = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.CancelOperation,
                new CancelOperationRequest(operationId));
            var cancelFrame = await ReadResponseFrameForRequestAsync(
                harness,
                cancelRequest,
                ProtocolMessageKinds.CancelOperation,
                frame =>
                {
                    if (frame.Kind == ProtocolMessageKinds.OperationEvent
                        && frame.ReadPayload<OperationEvent>() is { Kind: OperationEventKind.Terminal } terminal)
                    {
                        observedTerminalEvents.Add(terminal);
                    }
                });
            Assert.IsTrue(cancelFrame.ReadPayload<CancelOperationResponse>().CancellationRequested);
        }
        executionHold.Dispose();
        var terminalIds = new HashSet<OperationId>();
        foreach (var terminal in observedTerminalEvents)
        {
            terminalIds.Add(terminal.OperationId);
            Assert.AreEqual(OperationPhase.Cancelled, terminal.Phase);
        }
        for (var count = 0; count < 512 && terminalIds.Count < operationIds.Count; count++)
        {
            var frame = await harness.ReadFrameAsync();
            if (frame.Kind != ProtocolMessageKinds.OperationEvent)
            {
                continue;
            }

            var operationEvent = frame.ReadPayload<OperationEvent>();
            if (operationEvent.Kind == OperationEventKind.Terminal)
            {
                terminalIds.Add(operationEvent.OperationId);
                Assert.AreEqual(OperationPhase.Cancelled, operationEvent.Phase);
            }
        }

        Assert.IsTrue(
            terminalIds.SetEquals(operationIds),
            "Every admitted operation must reach a terminal event after the gate is released.");
        var settled = await harness.SendForErrorAsync(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(
                FixturePath("archives", "zip", "plain.zip"),
                slot.SlotId));
        Assert.AreEqual("secret.invalid_slot", settled.Code);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task MinorTwentyOneSupportDiagnosticsRefusesBeforePayloadDeserialization()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            ProtocolConstants.NestedWorkspaceIndependentReopenMinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(
                ProtocolConstants.NestedWorkspaceIndependentReopenMinorVersion));
        using var malformedDocument = JsonDocument.Parse("{\"unexpected\":true}");

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.GetSupportDiagnostics,
            malformedDocument.RootElement.Clone());

        Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, error.Category);
        Assert.AreEqual("protocol.feature_not_negotiated", error.Code);
        var proof = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        Assert.AreNotEqual(Guid.Empty, proof.SlotId);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task MinorEighteenOperationActivityRefusesBeforePayloadDeserialization()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            ProtocolConstants.ManagedWriteCapabilitiesMinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(
                ProtocolConstants.ManagedWriteCapabilitiesMinorVersion));
        using var malformedDocument = JsonDocument.Parse("{\"unexpected\":true}");

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.GetOperationActivity,
            malformedDocument.RootElement.Clone());

        Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, error.Category);
        Assert.AreEqual("protocol.feature_not_negotiated", error.Code);
        var proof = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        Assert.AreNotEqual(Guid.Empty, proof.SlotId);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task OperationActivityRetainsOnlyTheNewestBoundedTerminalHistory()
    {
        using var root = new TemporaryDirectory();
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            providers: [new NoMatchArchiveProvider()]);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var operationIds = new List<OperationId>();
        var operationCount = ProtocolConstants.MaximumRetainedOperationActivity + 1;
        for (var index = 0; index < operationCount; index++)
        {
            var destination = Path.Combine(root.Path, $"retained-{index:D2}.zip");
            var started = await harness.SendAsync<StartZipCreationRequest, StartOperationResponse>(
                ProtocolMessageKinds.StartZipCreation,
                new StartZipCreationRequest(
                    destination,
                    [],
                    ArchiveCompressionPreset.Store));
            operationIds.Add(started.OperationId);
            var terminal = await ReadTerminalAsync(harness, started.OperationId);
            Assert.AreEqual(OperationPhase.Completed, terminal.Phase);
        }

        var activity = await harness.SendAsync<
            GetOperationActivityRequest,
            GetOperationActivityResponse>(
            ProtocolMessageKinds.GetOperationActivity,
            new GetOperationActivityRequest());

        Assert.IsTrue(activity.HistoryTruncated);
        Assert.HasCount(ProtocolConstants.MaximumRetainedOperationActivity, activity.Operations);
        Assert.IsFalse(activity.Operations.Any(operation =>
            operation.OperationId == operationIds[0]));
        Assert.IsTrue(activity.Operations.Any(operation =>
            operation.OperationId == operationIds[^1]));
        Assert.IsTrue(activity.Operations.All(operation =>
            operation.Phase == OperationPhase.Completed
            && !operation.CanCancel
            && operation.CompletedAtUtc == operation.UpdatedAtUtc));
        for (var index = 1; index < activity.Operations.Count; index++)
        {
            Assert.IsTrue(
                activity.Operations[index - 1].CompletedAtUtc
                    >= activity.Operations[index].CompletedAtUtc);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ZipTreeMutationFailsClosedAndOperationGateRecovers()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "tree");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(tree, "original.txt"), "original");
        var destination = Path.Combine(root.Path, "mutated.zip");
        var writerProvider = new GateableZipWriterProvider();
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            providers: [new NoMatchArchiveProvider()],
            writerProviders: [writerProvider]);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var started = await harness.SendAsync<StartZipTreeCreationRequest, StartOperationResponse>(
            ProtocolMessageKinds.StartZipTreeCreation,
            new StartZipTreeCreationRequest(
                destination,
                [],
                [new ArchiveCreationDirectory(tree, "tree")],
                ArchiveCompressionPreset.Balanced));
        for (var count = 0; count < 4 && !writerProvider.EntryStarted.Task.IsCompleted; count++)
        {
            var frame = await harness.ReadFrameAsync();
            if (frame.Kind == ProtocolMessageKinds.OperationEvent)
            {
                var operationEvent = frame.ReadPayload<OperationEvent>();
                if (operationEvent.OperationId == started.OperationId
                    && operationEvent.Kind == OperationEventKind.Terminal)
                {
                    Assert.Fail(
                        $"ZIP tree creation failed before reaching the injected writer: "
                        + $"{operationEvent.Error?.Code} ({operationEvent.Error?.Category}) {operationEvent.Error?.Message}");
                }
            }
        }

        await writerProvider.EntryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(Path.Combine(tree, "added.txt"), "added");
        writerProvider.ContinueWriting.TrySetResult();

        OperationEvent? terminal = null;
        for (var count = 0; count < 32 && terminal is null; count++)
        {
            var frame = await harness.ReadFrameAsync();
            if (frame.Kind == ProtocolMessageKinds.OperationEvent)
            {
                var operationEvent = frame.ReadPayload<OperationEvent>();
                if (operationEvent.OperationId == started.OperationId
                    && operationEvent.Kind == OperationEventKind.Terminal)
                {
                    terminal = operationEvent;
                }
            }
        }

        Assert.IsNotNull(terminal);
        Assert.AreEqual(OperationPhase.Failed, terminal.Phase);
        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, terminal.Error?.Category);
        Assert.AreEqual("source.directory_tree_changed", terminal.Error?.Code);
        Assert.IsFalse(File.Exists(destination));
        Assert.HasCount(
            0,
            Directory.GetFiles(
                root.Path,
                ".rainarchives-*.zip.staging",
                SearchOption.TopDirectoryOnly));

        var recoveryDestination = Path.Combine(root.Path, "recovered.zip");
        var recovery = await harness.SendAsync<StartZipCreationRequest, StartOperationResponse>(
            ProtocolMessageKinds.StartZipCreation,
            new StartZipCreationRequest(
                recoveryDestination,
                [],
                ArchiveCompressionPreset.Balanced));
        OperationEvent? recoveryTerminal = null;
        for (var count = 0; count < 16 && recoveryTerminal is null; count++)
        {
            var frame = await harness.ReadFrameAsync();
            if (frame.Kind == ProtocolMessageKinds.OperationEvent)
            {
                var operationEvent = frame.ReadPayload<OperationEvent>();
                if (operationEvent.OperationId == recovery.OperationId
                    && operationEvent.Kind == OperationEventKind.Terminal)
                {
                    recoveryTerminal = operationEvent;
                }
            }
        }

        Assert.IsNotNull(recoveryTerminal);
        Assert.AreEqual(OperationPhase.Completed, recoveryTerminal.Phase);
        Assert.IsTrue(File.Exists(recoveryDestination));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ZipRebuildDestinationRacePreservesRacerAndWorkerRemainsHealthy()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "source.zip");
        var destinationPath = Path.Combine(root.Path, "raced.zip");
        var recoveryRoot = Path.Combine(root.Path, "creation-recovery");
        var sourceBytes = "immutable source lease"u8.ToArray();
        var payload = "retained payload"u8.ToArray();
        await File.WriteAllBytesAsync(sourcePath, sourceBytes);
        var writerProvider = new GateableZipWriterProvider();
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            providers: [new RebuildZipProvider(payload)],
            writerProviders: [writerProvider],
            creationRecoveryRoot: recoveryRoot);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));
        Assert.IsTrue(opened.Archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite));
        var started = await harness.SendAsync<StartZipRebuildRenameRequest, StartOperationResponse>(
            ProtocolMessageKinds.StartZipRebuildRename,
            new StartZipRebuildRenameRequest(
                opened.Archive.SessionId,
                destinationPath,
                [],
                [],
                [new ArchiveRebuildRename(
                    new ArchiveEntryKey("keep.bin", 0),
                    "moved/keep.bin")],
                ArchiveCompressionPreset.Balanced));

        var expectedPhases = new[]
        {
            OperationPhase.Queued,
            OperationPhase.Validating,
            OperationPhase.Running,
        };
        foreach (var expectedPhase in expectedPhases)
        {
            var frame = await harness.ReadFrameAsync();
            Assert.AreEqual(ProtocolMessageKinds.OperationEvent, frame.Kind);
            var operationEvent = frame.ReadPayload<OperationEvent>();
            Assert.AreEqual(started.OperationId, operationEvent.OperationId);
            Assert.AreEqual(OperationEventKind.StateChanged, operationEvent.Kind);
            Assert.AreEqual(expectedPhase, operationEvent.Phase);
        }

        await writerProvider.EntryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var racer = "racer sentinel"u8.ToArray();
        await File.WriteAllBytesAsync(destinationPath, racer);
        writerProvider.ContinueWriting.TrySetResult();
        var terminal = await ReadTerminalAsync(harness, started.OperationId);

        Assert.AreEqual(OperationPhase.Failed, terminal.Phase);
        Assert.AreEqual(ArchiveErrorCategory.DestinationConflict, terminal.Error?.Category);
        Assert.AreEqual("destination.appeared_before_commit", terminal.Error?.Code);
        CollectionAssert.AreEqual(racer, await File.ReadAllBytesAsync(destinationPath));
        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
        Assert.HasCount(
            0,
            Directory.GetFiles(
                root.Path,
                ".rainarchives-*.zip.staging",
                SearchOption.TopDirectoryOnly));
        Assert.HasCount(
            0,
            Directory.Exists(recoveryRoot)
                ? Directory.GetDirectories(
                    recoveryRoot,
                    ArchiveCreationRecoveryStore.OperationPrefix + "*",
                    SearchOption.TopDirectoryOnly)
                : []);

        var proof = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        Assert.AreNotEqual(Guid.Empty, proof.SlotId);
        _ = await harness.SendAsync<DiscardSecretSlotRequest, DiscardSecretSlotResponse>(
            ProtocolMessageKinds.DiscardSecretSlot,
            new DiscardSecretSlotRequest(proof.SlotId));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ZipRebuildRuntimeLowSpaceFailsThroughWorkerAndCleansOwnedState()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "source.zip");
        var destinationPath = Path.Combine(root.Path, "low-space.zip");
        var recoveryRoot = Path.Combine(root.Path, "creation-recovery");
        var sourceBytes = "immutable source lease"u8.ToArray();
        var payload = new byte[128 * 1024];
        RandomNumberGenerator.Fill(payload);
        await File.WriteAllBytesAsync(sourcePath, sourceBytes);
        var diskSpace = new SequenceAvailableDiskSpace(
            10L * 1024 * 1024 * 1024,
            512L * 1024 * 1024);
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            providers: [new RebuildZipProvider(payload)],
            creationRecoveryRoot: recoveryRoot,
            zipRebuildCreationResourceGuard: new ArchiveCreationResourceGuard(diskSpace));
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));
        Assert.IsTrue(opened.Archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite));
        var started = await harness.SendAsync<StartZipRebuildRenameRequest, StartOperationResponse>(
            ProtocolMessageKinds.StartZipRebuildRename,
            new StartZipRebuildRenameRequest(
                opened.Archive.SessionId,
                destinationPath,
                [],
                [],
                [new ArchiveRebuildRename(
                    new ArchiveEntryKey("keep.bin", 0),
                    "moved/keep.bin")],
                ArchiveCompressionPreset.Balanced));
        var terminal = await ReadTerminalAsync(harness, started.OperationId);

        Assert.AreEqual(OperationPhase.Failed, terminal.Phase);
        Assert.AreEqual(
            ArchiveErrorCategory.InsufficientSpace,
            terminal.Error?.Category,
            $"{terminal.Error?.Code}: {terminal.Error?.Message}");
        Assert.AreEqual(
            "creation.insufficient_space_during_write",
            terminal.Error?.Code,
            terminal.Error?.Message);
        Assert.IsTrue(diskSpace.QueryCount >= 2);
        Assert.IsFalse(File.Exists(destinationPath));
        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
        Assert.HasCount(
            0,
            Directory.GetFiles(
                root.Path,
                ".rainarchives-*.zip.staging",
                SearchOption.TopDirectoryOnly));
        Assert.HasCount(
            0,
            Directory.Exists(recoveryRoot)
                ? Directory.GetDirectories(
                    recoveryRoot,
                    ArchiveCreationRecoveryStore.OperationPrefix + "*",
                    SearchOption.TopDirectoryOnly)
                : []);

        var proof = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        _ = await harness.SendAsync<DiscardSecretSlotRequest, DiscardSecretSlotResponse>(
            ProtocolMessageKinds.DiscardSecretSlot,
            new DiscardSecretSlotRequest(proof.SlotId));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MinorTwoListingOmitsMinorThreePersistentEntryFields()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "RainArchives.WorkerProtocolIntegrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archivePath = Path.Combine(root, "duplicates.zip");
            using (var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var content in new[] { "first", "second" })
                {
                    var entry = archive.CreateEntry("same.txt");
                    await using var writer = new StreamWriter(entry.Open());
                    await writer.WriteAsync(content);
                }
            }

            await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
            await harness.SendWelcomeAsync(
                protocolMinor: 2,
                ProtocolFeatures.SupportedForMinorVersion(2));
            var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
                ProtocolMessageKinds.OpenArchive,
                new OpenArchiveRequest(archivePath));

            var request = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.ListEntries,
                new ListEntriesRequest(opened.Archive.SessionId, 0, 16));
            var frame = await harness.ReadFrameAsync();
            Assert.AreEqual(request.RequestId, frame.RequestId);
            Assert.AreEqual(request.Sequence, frame.Sequence);
            Assert.AreEqual($"{ProtocolMessageKinds.ListEntries}.response", frame.Kind);
            var entries = frame.Payload.GetProperty("entries");
            Assert.AreEqual(2, entries.GetArrayLength());
            foreach (var entry in entries.EnumerateArray())
            {
                Assert.IsFalse(entry.TryGetProperty("normalizedPath", out _));
                Assert.IsFalse(entry.TryGetProperty("duplicateOrdinal", out _));
            }

            var closed = await harness.SendAsync<CloseArchiveRequest, CloseArchiveResponse>(
                ProtocolMessageKinds.CloseArchive,
                new CloseArchiveRequest(opened.Archive.SessionId));
            Assert.IsTrue(closed.Closed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MinorThreeOpenOmitsMinorFourVolumeSetIdentity()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            protocolMinor: 3,
            ProtocolFeatures.SupportedForMinorVersion(3));

        var request = await harness.SendWithoutWaitingAsync(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(Path.Combine(
                AppContext.BaseDirectory,
                "fixtures",
                "corpus",
                "archives",
                "zip",
                "plain.zip")));
        var frame = await harness.ReadFrameAsync();
        Assert.AreEqual(request.RequestId, frame.RequestId);
        Assert.AreEqual($"{ProtocolMessageKinds.OpenArchive}.response", frame.Kind);
        var archiveJson = frame.Payload.GetProperty("archive");
        Assert.IsFalse(archiveJson.TryGetProperty("volumeSetKind", out _));

        var opened = frame.ReadPayload<OpenArchiveResponse>();
        Assert.IsNull(opened.Archive.VolumeSetKind);
        var closed = await harness.SendAsync<CloseArchiveRequest, CloseArchiveResponse>(
            ProtocolMessageKinds.CloseArchive,
            new CloseArchiveRequest(opened.Archive.SessionId));
        Assert.IsTrue(closed.Closed);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UnknownVolumeSetIsListOnlyAndRejectedBeforeOperationAdmission()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "unknown.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [new VolumeSetTestProvider(ArchiveVolumeSetKind.Unknown, ArchiveCapability.List)]);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));
        Assert.AreEqual(ArchiveVolumeSetKind.Unknown, opened.Archive.VolumeSetKind);
        Assert.IsFalse(opened.Archive.IsMultipart);
        Assert.AreEqual(ArchiveCapability.List, opened.Archive.Capabilities);
        Assert.IsTrue(opened.Archive.Warnings.Any(
            warning => warning.Code == "archive.volume_set_unknown"));

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.StartIntegrityTest,
            new StartIntegrityTestRequest(opened.Archive.SessionId, []));
        Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, error.Category);
        Assert.AreEqual("archive.volume_set_unknown", error.Code);

        var streamError = await harness.SendForErrorAsync(
            ProtocolMessageKinds.OpenEntryStream,
            new OpenEntryStreamRequest(
                opened.Archive.SessionId,
                EntryId.New(),
                1024,
                CreateEntryPipeName(),
                CreateEntryNonceProof()));
        Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, streamError.Category);
        Assert.AreEqual("archive.volume_set_unknown", streamError.Code);

        var closed = await harness.SendAsync<CloseArchiveRequest, CloseArchiveResponse>(
            ProtocolMessageKinds.CloseArchive,
            new CloseArchiveRequest(opened.Archive.SessionId));
        Assert.IsTrue(closed.Closed);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WorkerRejectsInconsistentProviderVolumeSetCapabilities()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "invalid.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [new VolumeSetTestProvider(
                ArchiveVolumeSetKind.Unknown,
                ArchiveCapability.List | ArchiveCapability.Extract)]);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));
        Assert.AreEqual(ArchiveErrorCategory.ProviderFailure, error.Category);
        Assert.AreEqual("provider.invalid_volume_set_contract", error.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task SingleVolumeExtractOnlySessionExplainsUnavailablePreview()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "extract-only.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [new VolumeSetTestProvider(
                ArchiveVolumeSetKind.Single,
                ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract)]);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));

        Assert.AreEqual(ArchiveVolumeSetKind.Single, opened.Archive.VolumeSetKind);
        Assert.IsFalse(opened.Archive.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream));
        var warning = opened.Archive.Warnings.Single(
            candidate => candidate.Code == "archive.preview_unavailable");
        Assert.IsFalse(string.IsNullOrWhiteSpace(warning.Message));

        var closed = await harness.SendAsync<CloseArchiveRequest, CloseArchiveResponse>(
            ProtocolMessageKinds.CloseArchive,
            new CloseArchiveRequest(opened.Archive.SessionId));
        Assert.IsTrue(closed.Closed);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NestedRecoveryProtocolListsInspectsDiscardsAndReplaysPathFreeEvidence()
    {
        using var root = new TemporaryDirectory();
        var recoveryId = new string('6', 64);
        var workspacePath = CreateStaleNestedWorkspace(root.Path, recoveryId, [4, 7]);
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [new NoMatchArchiveProvider()],
            root.Path);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));

        var inventory = await harness.SendAsync<
            ListNestedWorkspaceRecoveriesRequest,
            ListNestedWorkspaceRecoveriesResponse>(
            ProtocolMessageKinds.ListNestedWorkspaceRecoveries,
            new ListNestedWorkspaceRecoveriesRequest());
        Assert.HasCount(1, inventory.Recoveries);
        Assert.AreEqual(recoveryId, inventory.Recoveries[0].RecoveryId);
        Assert.IsFalse(JsonSerializer.Serialize(inventory, ProtocolJson.Options)
            .Contains(root.Path, StringComparison.OrdinalIgnoreCase));

        var inspection = await harness.SendAsync<
            InspectNestedWorkspaceRecoveryRequest,
            InspectNestedWorkspaceRecoveryResponse>(
            ProtocolMessageKinds.InspectNestedWorkspaceRecovery,
            new InspectNestedWorkspaceRecoveryRequest(recoveryId));
        Assert.AreEqual(
            NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
            inspection.Assessment);
        Assert.AreEqual(2, inspection.SourceCount);
        Assert.AreEqual(11L, inspection.TotalSourceBytes);
        Assert.IsNotNull(inspection.Revision);
        Assert.IsFalse(JsonSerializer.Serialize(inspection, ProtocolJson.Options)
            .Contains(root.Path, StringComparison.OrdinalIgnoreCase));
        var sourceCount = inspection.SourceCount
            ?? throw new InvalidOperationException("The verified source count was absent.");
        var totalSourceBytes = inspection.TotalSourceBytes
            ?? throw new InvalidOperationException("The verified source byte total was absent.");

        var request = new DiscardNestedWorkspaceRecoveryRequest(
            recoveryId,
            inspection.Revision,
            sourceCount,
            totalSourceBytes);
        var discarded = await harness.SendAsync<
            DiscardNestedWorkspaceRecoveryRequest,
            DiscardNestedWorkspaceRecoveryResponse>(
            ProtocolMessageKinds.DiscardNestedWorkspaceRecovery,
            request);
        Assert.AreEqual(NestedWorkspaceRecoveryDiscardResult.Discarded, discarded.Result);
        Assert.IsFalse(Directory.Exists(workspacePath));

        var replay = await harness.SendAsync<
            DiscardNestedWorkspaceRecoveryRequest,
            DiscardNestedWorkspaceRecoveryResponse>(
            ProtocolMessageKinds.DiscardNestedWorkspaceRecovery,
            request);
        Assert.AreEqual(NestedWorkspaceRecoveryDiscardResult.AlreadyAbsent, replay.Result);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NestedRecoveryProtocolPreservesChangedOrUnexpectedTrees()
    {
        using var root = new TemporaryDirectory();
        var recoveryId = new string('7', 64);
        var workspacePath = CreateStaleNestedWorkspace(root.Path, recoveryId, [5]);
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [new NoMatchArchiveProvider()],
            root.Path);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var inspection = await harness.SendAsync<
            InspectNestedWorkspaceRecoveryRequest,
            InspectNestedWorkspaceRecoveryResponse>(
            ProtocolMessageKinds.InspectNestedWorkspaceRecovery,
            new InspectNestedWorkspaceRecoveryRequest(recoveryId));
        var sourcePath = Directory
            .EnumerateFiles(
                workspacePath,
                OwnedTemporaryArchiveSource.SourceFileName,
                SearchOption.AllDirectories)
            .Single();
        await File.AppendAllBytesAsync(sourcePath, [0x44]);

        var changed = await harness.SendForErrorAsync(
            ProtocolMessageKinds.DiscardNestedWorkspaceRecovery,
            new DiscardNestedWorkspaceRecoveryRequest(
                recoveryId,
                inspection.Revision!,
                inspection.SourceCount!.Value,
                inspection.TotalSourceBytes!.Value));
        Assert.AreEqual("nested_recovery.changed", changed.Code);
        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, changed.Category);
        Assert.IsTrue(Directory.Exists(workspacePath));

        var unexpectedPath = Path.Combine(workspacePath, "preserve.txt");
        await File.WriteAllTextAsync(unexpectedPath, "preserve me");
        var unexpected = await harness.SendAsync<
            InspectNestedWorkspaceRecoveryRequest,
            InspectNestedWorkspaceRecoveryResponse>(
            ProtocolMessageKinds.InspectNestedWorkspaceRecovery,
            new InspectNestedWorkspaceRecoveryRequest(recoveryId));
        Assert.AreEqual(
            NestedWorkspaceRecoveryAssessment.UnexpectedContents,
            unexpected.Assessment);
        Assert.AreEqual("preserve me", await File.ReadAllTextAsync(unexpectedPath));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task QueuedNestedRecoveryDiscardCancellationPreservesExactTree()
    {
        using var root = new TemporaryDirectory();
        var recoveryId = new string('8', 64);
        var workspacePath = CreateStaleNestedWorkspace(root.Path, recoveryId, [9]);
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [new NoMatchArchiveProvider()],
            root.Path);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var inspection = await harness.SendAsync<
            InspectNestedWorkspaceRecoveryRequest,
            InspectNestedWorkspaceRecoveryResponse>(
            ProtocolMessageKinds.InspectNestedWorkspaceRecovery,
            new InspectNestedWorkspaceRecoveryRequest(recoveryId));

        using (harness.HoldNormalDispatchSlots())
        {
            var discardRequest = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.DiscardNestedWorkspaceRecovery,
                new DiscardNestedWorkspaceRecoveryRequest(
                    recoveryId,
                    inspection.Revision!,
                    inspection.SourceCount!.Value,
                    inspection.TotalSourceBytes!.Value));
            var cancelRequest = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.CancelRequest,
                new CancelRequestRequest(discardRequest.RequestId));
            var first = await harness.ReadFrameAsync();
            var second = await harness.ReadFrameAsync();
            var responses = new[] { first, second }.ToDictionary(frame => frame.RequestId);

            Assert.IsTrue(responses[cancelRequest.RequestId]
                .ReadPayload<CancelRequestResponse>()
                .CancellationRequested);
            var discardFrame = responses[discardRequest.RequestId];
            Assert.AreEqual(ProtocolMessageKinds.Error, discardFrame.Kind);
            Assert.AreEqual(
                ArchiveErrorCategory.Cancelled,
                discardFrame.ReadPayload<ErrorResponse>().Error.Category);
        }

        Assert.IsTrue(Directory.Exists(workspacePath));
        var after = await harness.SendAsync<
            InspectNestedWorkspaceRecoveryRequest,
            InspectNestedWorkspaceRecoveryResponse>(
            ProtocolMessageKinds.InspectNestedWorkspaceRecovery,
            new InspectNestedWorkspaceRecoveryRequest(recoveryId));
        Assert.IsTrue(string.Equals(
            inspection.Revision,
            after.Revision,
            StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task TokenVerifiedStaleNestedWorkspaceIsReportedAndPreservedWithoutPathDisclosure()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "source.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        var token = new string('d', 64);
        var workspaceName = $"rainarchives-worker-{token}";
        var workspacePath = Path.Combine(root.Path, workspaceName);
        Directory.CreateDirectory(workspacePath);
        await File.WriteAllBytesAsync(
            Path.Combine(workspacePath, "owner.lock"),
            Encoding.ASCII.GetBytes($"RainArchives nested workspace v1\n{token}\n"));
        var preservedPath = Path.Combine(workspacePath, "preserved.bin");
        await File.WriteAllBytesAsync(preservedPath, [0x7f]);

        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [new VolumeSetTestProvider(ArchiveVolumeSetKind.Single, ArchiveCapability.List)],
            root.Path);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));

        var warning = opened.Archive.Warnings.Single(
            candidate => candidate.Code == "worker.nested_recovery_required");
        Assert.IsTrue(warning.Message.Contains("1", StringComparison.Ordinal));
        Assert.IsFalse(warning.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(warning.Message.Contains(workspaceName, StringComparison.Ordinal));
        CollectionAssert.AreEqual(new byte[] { 0x7f }, await File.ReadAllBytesAsync(preservedPath));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UnverifiedNestedWorkspaceIsReportedAndPreservedWithoutPathDisclosure()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "source.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        var workspaceName = $"rainarchives-worker-{new string('e', 64)}";
        var workspacePath = Path.Combine(root.Path, workspaceName);
        Directory.CreateDirectory(workspacePath);
        var ownerPath = Path.Combine(workspacePath, "owner.lock");
        await File.WriteAllTextAsync(ownerPath, "unverified ownership data");

        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [new VolumeSetTestProvider(ArchiveVolumeSetKind.Single, ArchiveCapability.List)],
            root.Path);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));

        var warning = opened.Archive.Warnings.Single(
            candidate => candidate.Code == "worker.nested_inventory_incomplete");
        Assert.IsFalse(warning.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(warning.Message.Contains(workspaceName, StringComparison.Ordinal));
        Assert.AreEqual("unverified ownership data", await File.ReadAllTextAsync(ownerPath));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UnverifiedCreationRecoveryStateIsReportedAndPreservedWithoutPathDisclosure()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "source.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        var recoveryRoot = Path.Combine(root.Path, "creation-recovery");
        Directory.CreateDirectory(recoveryRoot);
        var unknownPath = Path.Combine(recoveryRoot, "unverified-user-state");
        await File.WriteAllTextAsync(unknownPath, "preserve me");

        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [new VolumeSetTestProvider(ArchiveVolumeSetKind.Single, ArchiveCapability.List)],
            nestedTemporaryRoot: root.Path,
            creationRecoveryRoot: recoveryRoot);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));

        var warning = opened.Archive.Warnings.Single(
            candidate => candidate.Code == "worker.creation_recovery_inventory_incomplete");
        Assert.IsFalse(warning.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(warning.Message.Contains("unverified-user-state", StringComparison.Ordinal));
        Assert.AreEqual("preserve me", await File.ReadAllTextAsync(unknownPath));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MinorFourWorkerConnectionRejectsEntryStreamRequestsBeforePipeAdmission()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            protocolMinor: 4,
            ProtocolFeatures.SupportedForMinorVersion(4));

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.OpenEntryStream,
            new OpenEntryStreamRequest(
                ArchiveSessionId.New(),
                EntryId.New(),
                1024,
                CreateEntryPipeName(),
                CreateEntryNonceProof()));

        Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, error.Category);
        Assert.AreEqual("protocol.feature_not_negotiated", error.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MinorFiveWorkerConnectionRejectsNestedOpenBeforeAdmission()
    {
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync();
        await harness.SendWelcomeAsync(
            protocolMinor: 5,
            ProtocolFeatures.SupportedForMinorVersion(5));

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.OpenNestedArchive,
            new OpenNestedArchiveRequest(
                ArchiveSessionId.New(),
                EntryId.New(),
                MaximumBytes: 1024));

        Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, error.Category);
        Assert.AreEqual("protocol.feature_not_negotiated", error.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NestedOpenCancellationInterruptsProviderCopyAndDeletesPartialSource()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "blocking.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        var provider = new BlockingNestedCopyProvider();
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [provider],
            root.Path);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));
        var listed = await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
            ProtocolMessageKinds.ListEntries,
            new ListEntriesRequest(opened.Archive.SessionId, 0, 16));
        var entry = listed.Entries.Single(item => item.Kind == ArchiveEntryKind.File);

        var nestedEnvelope = await harness.SendWithoutWaitingAsync(
            ProtocolMessageKinds.OpenNestedArchive,
            new OpenNestedArchiveRequest(
                opened.Archive.SessionId,
                entry.EntryId,
                MaximumBytes: 1024));
        await provider.CopyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelEnvelope = await harness.SendWithoutWaitingAsync(
            ProtocolMessageKinds.CancelRequest,
            new CancelRequestRequest(nestedEnvelope.RequestId));

        var responses = new[] { await harness.ReadFrameAsync(), await harness.ReadFrameAsync() };
        var cancelFrame = responses.Single(frame => frame.RequestId == cancelEnvelope.RequestId);
        var nestedFrame = responses.Single(frame => frame.RequestId == nestedEnvelope.RequestId);
        Assert.AreEqual(cancelEnvelope.Sequence, cancelFrame.Sequence);
        Assert.IsTrue(cancelFrame.ReadPayload<CancelRequestResponse>().CancellationRequested);
        Assert.AreEqual(ProtocolMessageKinds.Error, nestedFrame.Kind);
        var nestedError = nestedFrame.ReadPayload<ErrorResponse>().Error;
        Assert.AreEqual(ArchiveErrorCategory.Cancelled, nestedError.Category);
        Assert.HasCount(
            0,
            GetNestedSourceDirectories(root.Path));
        Assert.HasCount(1, GetNestedWorkspaceDirectories(root.Path));

        var healthy = await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
            ProtocolMessageKinds.ListEntries,
            new ListEntriesRequest(opened.Archive.SessionId, 0, 16));
        Assert.HasCount(1, healthy.Entries);
        Assert.IsTrue((await harness.SendAsync<CloseArchiveRequest, CloseArchiveResponse>(
            ProtocolMessageKinds.CloseArchive,
            new CloseArchiveRequest(opened.Archive.SessionId))).Closed);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NestedOpenActualByteLimitDeletesPartialSourceAndKeepsWorkerHealthy()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "oversized.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [new OversizedNestedCopyProvider()],
            root.Path);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));
        var entry = (await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
                ProtocolMessageKinds.ListEntries,
                new ListEntriesRequest(opened.Archive.SessionId, 0, 16)))
            .Entries
            .Single(item => item.Kind == ArchiveEntryKind.File);

        var error = await harness.SendForErrorAsync(
            ProtocolMessageKinds.OpenNestedArchive,
            new OpenNestedArchiveRequest(
                opened.Archive.SessionId,
                entry.EntryId,
                MaximumBytes: 1024));

        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, error.Category);
        Assert.AreEqual("resource.output_limit", error.Code);
        Assert.HasCount(
            0,
            GetNestedSourceDirectories(root.Path));
        Assert.HasCount(1, GetNestedWorkspaceDirectories(root.Path));
        Assert.HasCount(
            1,
            (await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
                ProtocolMessageKinds.ListEntries,
                new ListEntriesRequest(opened.Archive.SessionId, 0, 16))).Entries);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ActiveNestedByteReservationsRejectThirdMaximumRequestAndReleaseAfterCancellation()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "active-budget.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        var provider = new BlockingNestedCopyProvider();
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [provider],
            root.Path);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));
        var entry = (await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
                ProtocolMessageKinds.ListEntries,
                new ListEntriesRequest(opened.Archive.SessionId, 0, 16)))
            .Entries
            .Single(item => item.Kind == ArchiveEntryKind.File);
        var request = new OpenNestedArchiveRequest(
            opened.Archive.SessionId,
            entry.EntryId,
            ProtocolConstants.MaximumNestedArchiveBytes);

        var first = await harness.SendWithoutWaitingAsync(
            ProtocolMessageKinds.OpenNestedArchive,
            request);
        var second = await harness.SendWithoutWaitingAsync(
            ProtocolMessageKinds.OpenNestedArchive,
            request);
        await provider.CopyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var budgetDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (harness.GetActiveNestedArchiveBytes() != ProtocolConstants.MaximumActiveNestedArchiveBytes)
        {
            if (DateTimeOffset.UtcNow >= budgetDeadline)
            {
                Assert.Fail("Two nested archive requests did not reserve the expected active byte budget.");
            }

            await Task.Delay(10);
        }

        var thirdError = await harness.SendForErrorAsync(
            ProtocolMessageKinds.OpenNestedArchive,
            request);
        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, thirdError.Category);
        Assert.AreEqual("nested.active_byte_limit", thirdError.Code);

        var cancelFirst = await harness.SendWithoutWaitingAsync(
            ProtocolMessageKinds.CancelRequest,
            new CancelRequestRequest(first.RequestId));
        var cancelSecond = await harness.SendWithoutWaitingAsync(
            ProtocolMessageKinds.CancelRequest,
            new CancelRequestRequest(second.RequestId));
        var expectedIds = new HashSet<Guid>
        {
            first.RequestId,
            second.RequestId,
            cancelFirst.RequestId,
            cancelSecond.RequestId,
        };
        var responses = new List<ProtocolFrame>();
        while (responses.Count < expectedIds.Count)
        {
            var response = await harness.ReadFrameAsync();
            if (expectedIds.Contains(response.RequestId))
            {
                responses.Add(response);
            }
        }

        Assert.IsTrue(responses
            .Single(frame => frame.RequestId == cancelFirst.RequestId)
            .ReadPayload<CancelRequestResponse>()
            .CancellationRequested);
        Assert.IsTrue(responses
            .Single(frame => frame.RequestId == cancelSecond.RequestId)
            .ReadPayload<CancelRequestResponse>()
            .CancellationRequested);
        foreach (var nestedId in new[] { first.RequestId, second.RequestId })
        {
            var error = responses
                .Single(frame => frame.RequestId == nestedId)
                .ReadPayload<ErrorResponse>()
                .Error;
            Assert.AreEqual(ArchiveErrorCategory.Cancelled, error.Category);
        }

        Assert.AreEqual(0, harness.GetActiveNestedArchiveBytes());
        Assert.HasCount(
            0,
            GetNestedSourceDirectories(root.Path));
        Assert.HasCount(1, GetNestedWorkspaceDirectories(root.Path));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ClosingNestedSessionReleasesCommittedBytesAndExactTemporarySource()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "successful.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [new SuccessfulNestedCopyProvider()],
            root.Path);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var parent = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));
        var entry = (await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
                ProtocolMessageKinds.ListEntries,
                new ListEntriesRequest(parent.Archive.SessionId, 0, 16)))
            .Entries
            .Single(item => item.Kind == ArchiveEntryKind.File);

        var nested = await harness.SendAsync<OpenNestedArchiveRequest, OpenNestedArchiveResponse>(
            ProtocolMessageKinds.OpenNestedArchive,
            new OpenNestedArchiveRequest(
                parent.Archive.SessionId,
                entry.EntryId,
                MaximumBytes: 1024));

        Assert.AreEqual(1, nested.MaterializedBytes);
        Assert.AreEqual(1, harness.GetActiveNestedArchiveBytes());
        Assert.HasCount(
            1,
            GetNestedSourceDirectories(root.Path));
        Assert.HasCount(1, GetNestedWorkspaceDirectories(root.Path));
        Assert.IsTrue((await harness.SendAsync<CloseArchiveRequest, CloseArchiveResponse>(
            ProtocolMessageKinds.CloseArchive,
            new CloseArchiveRequest(nested.Archive.SessionId))).Closed);
        Assert.AreEqual(0, harness.GetActiveNestedArchiveBytes());
        Assert.HasCount(
            0,
            GetNestedSourceDirectories(root.Path));
        Assert.HasCount(1, GetNestedWorkspaceDirectories(root.Path));
        Assert.HasCount(
            1,
            (await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
                ProtocolMessageKinds.ListEntries,
                new ListEntriesRequest(parent.Archive.SessionId, 0, 16))).Entries);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WorkerShutdownReleasesActiveNestedSessionAndTemporarySource()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "shutdown.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [new SuccessfulNestedCopyProvider()],
            root.Path);
        try
        {
            await harness.SendWelcomeAsync(
                ProtocolConstants.MinorVersion,
                ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
            var parent = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
                ProtocolMessageKinds.OpenArchive,
                new OpenArchiveRequest(sourcePath));
            var entry = (await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
                    ProtocolMessageKinds.ListEntries,
                    new ListEntriesRequest(parent.Archive.SessionId, 0, 16)))
                .Entries
                .Single(item => item.Kind == ArchiveEntryKind.File);

            _ = await harness.SendAsync<OpenNestedArchiveRequest, OpenNestedArchiveResponse>(
                ProtocolMessageKinds.OpenNestedArchive,
                new OpenNestedArchiveRequest(
                    parent.Archive.SessionId,
                    entry.EntryId,
                    MaximumBytes: 1024));
            Assert.HasCount(
                1,
                GetNestedSourceDirectories(root.Path));
            Assert.HasCount(1, GetNestedWorkspaceDirectories(root.Path));
        }
        finally
        {
            await harness.DisposeAsync();
        }

        Assert.HasCount(
            0,
            GetNestedSourceDirectories(root.Path));
        Assert.HasCount(0, GetNestedWorkspaceDirectories(root.Path));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CleanupRefusalStillReleasesNestedBudgetAndLeavesWorkerHealthy()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "cleanup-refusal.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        var provider = new BlockingNestedCopyProvider();
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync(
            [provider],
            root.Path);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var parent = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));
        var entry = (await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
                ProtocolMessageKinds.ListEntries,
                new ListEntriesRequest(parent.Archive.SessionId, 0, 16)))
            .Entries
            .Single(item => item.Kind == ArchiveEntryKind.File);

        var nestedEnvelope = await harness.SendWithoutWaitingAsync(
            ProtocolMessageKinds.OpenNestedArchive,
            new OpenNestedArchiveRequest(
                parent.Archive.SessionId,
                entry.EntryId,
                MaximumBytes: 1024));
        await provider.CopyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var temporaryDirectory = GetNestedSourceDirectories(root.Path).Single();
        var unexpectedPath = Path.Combine(temporaryDirectory, "unexpected-user-data.bin");
        await File.WriteAllBytesAsync(unexpectedPath, [0x7f]);

        var cancelEnvelope = await harness.SendWithoutWaitingAsync(
            ProtocolMessageKinds.CancelRequest,
            new CancelRequestRequest(nestedEnvelope.RequestId));
        var responses = new[] { await harness.ReadFrameAsync(), await harness.ReadFrameAsync() };
        Assert.IsTrue(responses
            .Single(frame => frame.RequestId == cancelEnvelope.RequestId)
            .ReadPayload<CancelRequestResponse>()
            .CancellationRequested);
        var nestedError = responses
            .Single(frame => frame.RequestId == nestedEnvelope.RequestId)
            .ReadPayload<ErrorResponse>()
            .Error;
        Assert.AreEqual(ArchiveErrorCategory.ProviderFailure, nestedError.Category);
        Assert.AreEqual("filesystem.native_failure", nestedError.Code);
        Assert.AreEqual(0, harness.GetActiveNestedArchiveBytes());
        Assert.IsTrue(File.Exists(unexpectedPath));
        Assert.HasCount(
            1,
            (await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
                ProtocolMessageKinds.ListEntries,
                new ListEntriesRequest(parent.Archive.SessionId, 0, 16))).Entries);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task QueuedNestedCancellationSettlesBothPasswordSlotsBeforeDispatch()
    {
        await using var harness = await RawWorkerHarness.StartAsync();
        var parentSlot = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        var nestedSlot = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        await harness.StoreSecretAsync(parentSlot.SlotId, "parent-password");
        await harness.StoreSecretAsync(nestedSlot.SlotId, "nested-password");

        using (harness.HoldNormalDispatchSlots())
        {
            var nestedEnvelope = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.OpenNestedArchive,
                new OpenNestedArchiveRequest(
                    ArchiveSessionId.New(),
                    EntryId.New(),
                    MaximumBytes: 1024,
                    ParentPasswordSlotId: parentSlot.SlotId,
                    NestedPasswordSlotId: nestedSlot.SlotId));
            var cancelEnvelope = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.CancelRequest,
                new CancelRequestRequest(nestedEnvelope.RequestId));
            var responses = new[] { await harness.ReadFrameAsync(), await harness.ReadFrameAsync() };
            Assert.IsTrue(responses
                .Single(frame => frame.RequestId == cancelEnvelope.RequestId)
                .ReadPayload<CancelRequestResponse>()
                .CancellationRequested);
            Assert.AreEqual(
                ArchiveErrorCategory.Cancelled,
                responses
                    .Single(frame => frame.RequestId == nestedEnvelope.RequestId)
                    .ReadPayload<ErrorResponse>()
                    .Error
                    .Category);
        }

        Assert.IsFalse((await harness.SendAsync<DiscardSecretSlotRequest, DiscardSecretSlotResponse>(
            ProtocolMessageKinds.DiscardSecretSlot,
            new DiscardSecretSlotRequest(parentSlot.SlotId))).Discarded);
        Assert.IsFalse((await harness.SendAsync<DiscardSecretSlotRequest, DiscardSecretSlotResponse>(
            ProtocolMessageKinds.DiscardSecretSlot,
            new DiscardSecretSlotRequest(nestedSlot.SlotId))).Discarded);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WorkerRejectsNonCanonicalEntryPipeAndNonceBeforeConnecting()
    {
        await using var harness = await RawWorkerHarness.StartAsync();
        var invalidPipe = await harness.SendForErrorAsync(
            ProtocolMessageKinds.OpenEntryStream,
            new OpenEntryStreamRequest(
                ArchiveSessionId.New(),
                EntryId.New(),
                1024,
                "untrusted-pipe-name",
                CreateEntryNonceProof()));
        Assert.AreEqual(ArchiveErrorCategory.InvalidRequest, invalidPipe.Category);
        Assert.AreEqual("entry.stream_invalid_request", invalidPipe.Code);

        var uppercaseNonce = await harness.SendForErrorAsync(
            ProtocolMessageKinds.OpenEntryStream,
            new OpenEntryStreamRequest(
                ArchiveSessionId.New(),
                EntryId.New(),
                1024,
                CreateEntryPipeName(),
                CreateEntryNonceProof().ToUpperInvariant()));
        Assert.AreEqual(ArchiveErrorCategory.InvalidRequest, uppercaseNonce.Category);
        Assert.AreEqual("entry.stream_invalid_request", uppercaseNonce.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task EntryStreamRequestUsesAuthenticatedFramedPipeAndExactCompletionTotal()
    {
        await using var harness = await RawWorkerHarness.StartAsync();
        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(Path.Combine(
                AppContext.BaseDirectory,
                "fixtures",
                "corpus",
                "archives",
                "zip",
                "plain.zip")));
        var listed = await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
            ProtocolMessageKinds.ListEntries,
            new ListEntriesRequest(opened.Archive.SessionId, 0, 16));
        var entry = listed.Entries.First(item => item.Kind == ArchiveEntryKind.File);
        var pipeName = CreateEntryPipeName();
        var nonce = RandomNumberGenerator.GetBytes(EntryStreamPipeProtocol.BootstrapNonceBytes);
        try
        {
            await using var dataPipe = CreateEntryPipe(pipeName);
            var connected = dataPipe.WaitForConnectionAsync();
            var request = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.OpenEntryStream,
                new OpenEntryStreamRequest(
                    opened.Archive.SessionId,
                    entry.EntryId,
                    1024 * 1024,
                    pipeName,
                    Convert.ToHexString(nonce).ToLowerInvariant()));
            var responseFrame = await harness.ReadFrameAsync();
            Assert.AreEqual(request.RequestId, responseFrame.RequestId);
            var response = responseFrame.ReadPayload<OpenEntryStreamResponse>();
            await connected.WaitAsync(TimeSpan.FromSeconds(5));
            var hello = await EntryStreamPipeProtocol.ReadAsync(dataPipe);
            EntryStreamPipeProtocol.ValidateHello(hello, response.StreamId, nonce);

            using var decoded = new MemoryStream();
            while (true)
            {
                var dataFrame = await EntryStreamPipeProtocol.ReadAsync(dataPipe);
                if (dataFrame.Type == EntryStreamFrameType.Data)
                {
                    await decoded.WriteAsync(dataFrame.Payload);
                    continue;
                }

                Assert.AreEqual(EntryStreamFrameType.Complete, dataFrame.Type);
                Assert.AreEqual(decoded.Length, EntryStreamPipeProtocol.ReadCompletionTotal(dataFrame));
                break;
            }

            Assert.AreEqual(response.DeclaredLength, decoded.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }

        var closed = await harness.SendAsync<CloseArchiveRequest, CloseArchiveResponse>(
            ProtocolMessageKinds.CloseArchive,
            new CloseArchiveRequest(opened.Archive.SessionId));
        Assert.IsTrue(closed.Closed);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RuntimeDecodedByteLimitDefeatsMisleadingProviderMetadata()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "misleading.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync([new MisleadingEntryProvider()]);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));
        var listed = await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
            ProtocolMessageKinds.ListEntries,
            new ListEntriesRequest(opened.Archive.SessionId, 0, 16));
        var entry = listed.Entries.Single(item => item.Kind == ArchiveEntryKind.File);
        Assert.AreEqual(1, entry.OriginalSize);

        var pipeName = CreateEntryPipeName();
        var nonce = RandomNumberGenerator.GetBytes(EntryStreamPipeProtocol.BootstrapNonceBytes);
        try
        {
            await using var dataPipe = CreateEntryPipe(pipeName);
            var connected = dataPipe.WaitForConnectionAsync();
            var request = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.OpenEntryStream,
                new OpenEntryStreamRequest(
                    opened.Archive.SessionId,
                    entry.EntryId,
                    MaximumBytes: 3,
                    pipeName,
                    Convert.ToHexString(nonce).ToLowerInvariant()));
            var responseFrame = await harness.ReadFrameAsync();
            Assert.AreEqual(request.RequestId, responseFrame.RequestId);
            var response = responseFrame.ReadPayload<OpenEntryStreamResponse>();
            await connected.WaitAsync(TimeSpan.FromSeconds(5));
            var hello = await EntryStreamPipeProtocol.ReadAsync(dataPipe);
            EntryStreamPipeProtocol.ValidateHello(hello, response.StreamId, nonce);

            var terminal = await EntryStreamPipeProtocol.ReadAsync(dataPipe);
            Assert.AreEqual(EntryStreamFrameType.Error, terminal.Type);
            var error = EntryStreamPipeProtocol.ReadError(terminal);
            Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, error.Category);
            Assert.AreEqual("entry.stream_byte_limit", error.Code);
            Assert.AreEqual(entry.EntryId, error.EntryId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PasswordReopenCannotChangeProviderIdentityOrCapabilities()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "changing-reopen.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync([new ChangingReopenProvider()]);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));
        var entry = (await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
                ProtocolMessageKinds.ListEntries,
                new ListEntriesRequest(opened.Archive.SessionId, 0, 16)))
            .Entries
            .Single(item => item.Kind == ArchiveEntryKind.File);
        var slot = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        await harness.StoreSecretAsync(slot.SlotId, "provider-contract-password");
        var pipeName = CreateEntryPipeName();
        var nonce = RandomNumberGenerator.GetBytes(EntryStreamPipeProtocol.BootstrapNonceBytes);
        try
        {
            await using var dataPipe = CreateEntryPipe(pipeName);
            var connected = dataPipe.WaitForConnectionAsync();
            var request = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.OpenEntryStream,
                new OpenEntryStreamRequest(
                    opened.Archive.SessionId,
                    entry.EntryId,
                    1024,
                    pipeName,
                    Convert.ToHexString(nonce).ToLowerInvariant(),
                    slot.SlotId));
            var responseFrame = await harness.ReadFrameAsync();
            Assert.AreEqual(request.RequestId, responseFrame.RequestId);
            var response = responseFrame.ReadPayload<OpenEntryStreamResponse>();
            await connected.WaitAsync(TimeSpan.FromSeconds(5));
            EntryStreamPipeProtocol.ValidateHello(
                await EntryStreamPipeProtocol.ReadAsync(dataPipe),
                response.StreamId,
                nonce);

            var terminal = await EntryStreamPipeProtocol.ReadAsync(dataPipe);
            var error = EntryStreamPipeProtocol.ReadError(terminal);
            Assert.AreEqual(ArchiveErrorCategory.ProviderFailure, error.Category);
            Assert.AreEqual("provider.password_reopen_contract_changed", error.Code);
            Assert.IsFalse(error.Message.Contains("provider-contract-password", StringComparison.Ordinal));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task QueuedEntryStreamCancellationSettlesTransferredSecretSlot()
    {
        await using var harness = await RawWorkerHarness.StartAsync();
        var slot = await harness.SendAsync<CreateSecretSlotRequest, CreateSecretSlotResponse>(
            ProtocolMessageKinds.CreateSecretSlot,
            new CreateSecretSlotRequest());
        await harness.StoreSecretAsync(slot.SlotId, "queued-entry-stream-secret");

        using (harness.HoldNormalDispatchSlots())
        {
            var streamRequest = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.OpenEntryStream,
                new OpenEntryStreamRequest(
                    ArchiveSessionId.New(),
                    EntryId.New(),
                    1024,
                    CreateEntryPipeName(),
                    CreateEntryNonceProof(),
                    slot.SlotId));
            var cancelRequest = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.CancelRequest,
                new CancelRequestRequest(streamRequest.RequestId));

            var first = await harness.ReadFrameAsync();
            var second = await harness.ReadFrameAsync();
            var byRequest = new[] { first, second }.ToDictionary(frame => frame.RequestId);
            Assert.IsTrue(byRequest[cancelRequest.RequestId]
                .ReadPayload<CancelRequestResponse>()
                .CancellationRequested);
            var streamError = byRequest[streamRequest.RequestId]
                .ReadPayload<ErrorResponse>()
                .Error;
            Assert.AreEqual(ArchiveErrorCategory.Cancelled, streamError.Category);
        }

        var reuse = await harness.SendForErrorAsync(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "fixtures",
                    "corpus",
                    "archives",
                    "zip",
                    "plain.zip"),
                slot.SlotId));
        Assert.AreEqual("secret.invalid_slot", reuse.Code);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task EntryStreamCancellationInterruptsBlockedProviderRead()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "blocking.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync([new BlockingEntryProvider()]);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));
        var entry = (await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
                ProtocolMessageKinds.ListEntries,
                new ListEntriesRequest(opened.Archive.SessionId, 0, 16)))
            .Entries
            .Single(item => item.Kind == ArchiveEntryKind.File);
        var pipeName = CreateEntryPipeName();
        var nonce = RandomNumberGenerator.GetBytes(EntryStreamPipeProtocol.BootstrapNonceBytes);
        try
        {
            await using var dataPipe = CreateEntryPipe(pipeName);
            var connected = dataPipe.WaitForConnectionAsync();
            var request = await harness.SendWithoutWaitingAsync(
                ProtocolMessageKinds.OpenEntryStream,
                new OpenEntryStreamRequest(
                    opened.Archive.SessionId,
                    entry.EntryId,
                    1024,
                    pipeName,
                    Convert.ToHexString(nonce).ToLowerInvariant()));
            var responseFrame = await harness.ReadFrameAsync();
            Assert.AreEqual(request.RequestId, responseFrame.RequestId);
            var response = responseFrame.ReadPayload<OpenEntryStreamResponse>();
            await connected.WaitAsync(TimeSpan.FromSeconds(5));
            EntryStreamPipeProtocol.ValidateHello(
                await EntryStreamPipeProtocol.ReadAsync(dataPipe),
                response.StreamId,
                nonce);

            var cancelled = await harness.SendAsync<CancelEntryStreamRequest, CancelEntryStreamResponse>(
                ProtocolMessageKinds.CancelEntryStream,
                new CancelEntryStreamRequest(response.StreamId));
            Assert.IsTrue(cancelled.CancellationRequested);
            var terminal = await EntryStreamPipeProtocol.ReadAsync(dataPipe)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var error = EntryStreamPipeProtocol.ReadError(terminal);
            Assert.AreEqual(ArchiveErrorCategory.Cancelled, error.Category);
            Assert.AreEqual(entry.EntryId, error.EntryId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }

        var closed = await harness.SendAsync<CloseArchiveRequest, CloseArchiveResponse>(
            ProtocolMessageKinds.CloseArchive,
            new CloseArchiveRequest(opened.Archive.SessionId));
        Assert.IsTrue(closed.Closed);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NinthEntryStreamIsRejectedWhileEightLeasesRemainActive()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "blocking-limit.fake");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        await using var harness = await RawWorkerHarness.StartUnnegotiatedAsync([new BlockingEntryProvider()]);
        await harness.SendWelcomeAsync(
            ProtocolConstants.MinorVersion,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
        var opened = await harness.SendAsync<OpenArchiveRequest, OpenArchiveResponse>(
            ProtocolMessageKinds.OpenArchive,
            new OpenArchiveRequest(sourcePath));
        var entry = (await harness.SendAsync<ListEntriesRequest, ListEntriesResponse>(
                ProtocolMessageKinds.ListEntries,
                new ListEntriesRequest(opened.Archive.SessionId, 0, 16)))
            .Entries
            .Single(item => item.Kind == ArchiveEntryKind.File);
        var pipes = new List<NamedPipeServerStream>();
        var streamIds = new List<EntryStreamId>();
        try
        {
            for (var index = 0; index < ProtocolConstants.MaximumEntryStreamLeases; index++)
            {
                var pipeName = CreateEntryPipeName();
                var nonce = RandomNumberGenerator.GetBytes(EntryStreamPipeProtocol.BootstrapNonceBytes);
                try
                {
                    var dataPipe = CreateEntryPipe(pipeName);
                    pipes.Add(dataPipe);
                    var connected = dataPipe.WaitForConnectionAsync();
                    var request = await harness.SendWithoutWaitingAsync(
                        ProtocolMessageKinds.OpenEntryStream,
                        new OpenEntryStreamRequest(
                            opened.Archive.SessionId,
                            entry.EntryId,
                            1024,
                            pipeName,
                            Convert.ToHexString(nonce).ToLowerInvariant()));
                    var responseFrame = await harness.ReadFrameAsync();
                    Assert.AreEqual(request.RequestId, responseFrame.RequestId);
                    var response = responseFrame.ReadPayload<OpenEntryStreamResponse>();
                    await connected.WaitAsync(TimeSpan.FromSeconds(5));
                    EntryStreamPipeProtocol.ValidateHello(
                        await EntryStreamPipeProtocol.ReadAsync(dataPipe),
                        response.StreamId,
                        nonce);
                    streamIds.Add(response.StreamId);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nonce);
                }
            }

            var limitError = await harness.SendForErrorAsync(
                ProtocolMessageKinds.OpenEntryStream,
                new OpenEntryStreamRequest(
                    opened.Archive.SessionId,
                    entry.EntryId,
                    1024,
                    CreateEntryPipeName(),
                    CreateEntryNonceProof()));
            Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, limitError.Category);
            Assert.AreEqual("entry.stream_limit", limitError.Code);

            foreach (var streamId in streamIds)
            {
                var cancelled = await harness.SendAsync<CancelEntryStreamRequest, CancelEntryStreamResponse>(
                    ProtocolMessageKinds.CancelEntryStream,
                    new CancelEntryStreamRequest(streamId));
                Assert.IsTrue(cancelled.CancellationRequested);
            }

            var closed = await harness.SendAsync<CloseArchiveRequest, CloseArchiveResponse>(
                    ProtocolMessageKinds.CloseArchive,
                    new CloseArchiveRequest(opened.Archive.SessionId))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(closed.Closed);
        }
        finally
        {
            foreach (var pipe in pipes)
            {
                await pipe.DisposeAsync();
            }
        }
    }

    private static string CreateEntryPipeName() =>
        $"rainarchives-entry-{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";

    private static string CreateEntryNonceProof() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(EntryStreamPipeProtocol.BootstrapNonceBytes))
            .ToLowerInvariant();

    private static NamedPipeServerStream CreateEntryPipe(string pipeName) =>
        new(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            ProtocolConstants.MaximumEntryStreamFrameBytes,
            ProtocolConstants.MaximumEntryStreamFrameBytes);

    private sealed class ChangingReopenProvider : IArchiveProvider
    {
        public string Id => "changing-reopen-test";

        public ArchiveProbeResult Probe(ReadOnlySpan<byte> prefix, string sourcePath) =>
            new("fake", 100, "Changing password reopen test provider selected.");

        public ValueTask<IArchiveHandle> OpenAsync(
            string sourcePath,
            ReadOnlyMemory<byte> passwordUtf8,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capabilities = passwordUtf8.IsEmpty
                ? ArchiveCapability.List | ArchiveCapability.OpenEntryStream
                : ArchiveCapability.List;
            return ValueTask.FromResult<IArchiveHandle>(new ChangingReopenHandle(capabilities));
        }
    }

    private sealed class ChangingReopenHandle(ArchiveCapability capabilities) : IArchiveHandle
    {
        private static readonly IReadOnlyList<ProviderArchiveEntry> TestEntries =
        [
            new ProviderArchiveEntry(
                0,
                "changing.bin",
                ArchiveEntryKind.File,
                OriginalSize: 4,
                CompressedSize: 4,
                ModifiedTimeUtc: null,
                WindowsAttributes: null,
                CompressionMethod: "fixture",
                IntegrityValue: null,
                IsEncrypted: true,
                IsSolid: false,
                LinkTarget: null),
        ];

        public string Format => "fake";
        public string ProviderId => "changing-reopen-test";
        public ArchiveCapability Capabilities => capabilities;
        public bool IsSolid => false;
        public ArchiveVolumeSetKind VolumeSetKind => ArchiveVolumeSetKind.Single;
        public IReadOnlyList<ProviderArchiveEntry> Entries => TestEntries;

        public ValueTask<Stream> OpenEntryStreamAsync(
            int providerOrdinal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream("data"u8.ToArray(), writable: false));
        }

        public ValueTask CopyEntryToAsync(
            int providerOrdinal,
            Stream destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingEntryProvider : IArchiveProvider
    {
        public string Id => "blocking-entry-test";

        public ArchiveProbeResult Probe(ReadOnlySpan<byte> prefix, string sourcePath) =>
            new("fake", 100, "Blocking stream test provider selected.");

        public ValueTask<IArchiveHandle> OpenAsync(
            string sourcePath,
            ReadOnlyMemory<byte> passwordUtf8,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IArchiveHandle>(new BlockingEntryHandle());
        }
    }

    private sealed class BlockingEntryHandle : IArchiveHandle
    {
        private static readonly IReadOnlyList<ProviderArchiveEntry> TestEntries =
        [
            new ProviderArchiveEntry(
                0,
                "blocking.bin",
                ArchiveEntryKind.File,
                OriginalSize: null,
                CompressedSize: 1,
                ModifiedTimeUtc: null,
                WindowsAttributes: null,
                CompressionMethod: "fixture",
                IntegrityValue: null,
                IsEncrypted: false,
                IsSolid: false,
                LinkTarget: null),
        ];

        public string Format => "fake";
        public string ProviderId => "blocking-entry-test";
        public ArchiveCapability Capabilities => ArchiveCapability.List | ArchiveCapability.OpenEntryStream;
        public bool IsSolid => false;
        public ArchiveVolumeSetKind VolumeSetKind => ArchiveVolumeSetKind.Single;
        public IReadOnlyList<ProviderArchiveEntry> Entries => TestEntries;

        public ValueTask<Stream> OpenEntryStreamAsync(
            int providerOrdinal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new BlockingReadStream());
        }

        public ValueTask CopyEntryToAsync(
            int providerOrdinal,
            Stream destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class MisleadingEntryProvider : IArchiveProvider
    {
        public string Id => "misleading-entry-test";

        public ArchiveProbeResult Probe(ReadOnlySpan<byte> prefix, string sourcePath) =>
            new("fake", 100, "Misleading metadata test provider selected.");

        public ValueTask<IArchiveHandle> OpenAsync(
            string sourcePath,
            ReadOnlyMemory<byte> passwordUtf8,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IArchiveHandle>(new MisleadingEntryHandle());
        }
    }

    private sealed class MisleadingEntryHandle : IArchiveHandle
    {
        private static readonly IReadOnlyList<ProviderArchiveEntry> TestEntries =
        [
            new ProviderArchiveEntry(
                0,
                "misleading.bin",
                ArchiveEntryKind.File,
                OriginalSize: 1,
                CompressedSize: 1,
                ModifiedTimeUtc: null,
                WindowsAttributes: null,
                CompressionMethod: "fixture",
                IntegrityValue: null,
                IsEncrypted: false,
                IsSolid: false,
                LinkTarget: null),
        ];

        public string Format => "fake";
        public string ProviderId => "misleading-entry-test";
        public ArchiveCapability Capabilities =>
            ArchiveCapability.List | ArchiveCapability.OpenEntryStream | ArchiveCapability.Test;
        public bool IsSolid => false;
        public ArchiveVolumeSetKind VolumeSetKind => ArchiveVolumeSetKind.Single;
        public IReadOnlyList<ProviderArchiveEntry> Entries => TestEntries;

        public ValueTask<Stream> OpenEntryStreamAsync(
            int providerOrdinal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream("four"u8.ToArray(), writable: false));
        }

        public ValueTask CopyEntryToAsync(
            int providerOrdinal,
            Stream destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class VolumeSetTestProvider(
        ArchiveVolumeSetKind volumeSetKind,
        ArchiveCapability capabilities) : IArchiveProvider
    {
        public string Id => "volume-set-test";

        public ArchiveProbeResult Probe(ReadOnlySpan<byte> prefix, string sourcePath) =>
            new("fake", 100, "Test provider selected.");

        public ValueTask<IArchiveHandle> OpenAsync(
            string sourcePath,
            ReadOnlyMemory<byte> passwordUtf8,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IArchiveHandle>(
                new VolumeSetTestHandle(volumeSetKind, capabilities));
        }
    }

    private sealed class BlockingNestedCopyProvider : IArchiveProvider
    {
        public TaskCompletionSource CopyStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "blocking-nested-copy-test";

        public ArchiveProbeResult Probe(ReadOnlySpan<byte> prefix, string sourcePath) =>
            new("fake", 100, "Blocking nested-copy test provider selected.");

        public ValueTask<IArchiveHandle> OpenAsync(
            string sourcePath,
            ReadOnlyMemory<byte> passwordUtf8,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IArchiveHandle>(new BlockingNestedCopyHandle(CopyStarted));
        }
    }

    private sealed class BlockingNestedCopyHandle(TaskCompletionSource copyStarted) : IArchiveHandle
    {
        private static readonly IReadOnlyList<ProviderArchiveEntry> TestEntries =
        [
            new ProviderArchiveEntry(
                0,
                "nested.bin",
                ArchiveEntryKind.File,
                OriginalSize: null,
                CompressedSize: 1,
                ModifiedTimeUtc: null,
                WindowsAttributes: null,
                CompressionMethod: "fixture",
                IntegrityValue: null,
                IsEncrypted: false,
                IsSolid: false,
                LinkTarget: null),
        ];

        public string Format => "fake";
        public string ProviderId => "blocking-nested-copy-test";
        public ArchiveCapability Capabilities =>
            ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract;
        public bool IsSolid => false;
        public ArchiveVolumeSetKind VolumeSetKind => ArchiveVolumeSetKind.Single;
        public IReadOnlyList<ProviderArchiveEntry> Entries => TestEntries;

        public ValueTask<Stream> OpenEntryStreamAsync(
            int providerOrdinal,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new NotSupportedException());

        public async ValueTask CopyEntryToAsync(
            int providerOrdinal,
            Stream destination,
            CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 0x50 }, cancellationToken);
            copyStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OversizedNestedCopyProvider : IArchiveProvider
    {
        public string Id => "oversized-nested-copy-test";

        public ArchiveProbeResult Probe(ReadOnlySpan<byte> prefix, string sourcePath) =>
            new("fake", 100, "Oversized nested-copy test provider selected.");

        public ValueTask<IArchiveHandle> OpenAsync(
            string sourcePath,
            ReadOnlyMemory<byte> passwordUtf8,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IArchiveHandle>(new OversizedNestedCopyHandle());
        }
    }

    private sealed class OversizedNestedCopyHandle : IArchiveHandle
    {
        private static readonly IReadOnlyList<ProviderArchiveEntry> TestEntries =
        [
            new ProviderArchiveEntry(
                0,
                "nested.bin",
                ArchiveEntryKind.File,
                OriginalSize: null,
                CompressedSize: 1,
                ModifiedTimeUtc: null,
                WindowsAttributes: null,
                CompressionMethod: "fixture",
                IntegrityValue: null,
                IsEncrypted: false,
                IsSolid: false,
                LinkTarget: null),
        ];

        public string Format => "fake";
        public string ProviderId => "oversized-nested-copy-test";
        public ArchiveCapability Capabilities =>
            ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract;
        public bool IsSolid => false;
        public ArchiveVolumeSetKind VolumeSetKind => ArchiveVolumeSetKind.Single;
        public IReadOnlyList<ProviderArchiveEntry> Entries => TestEntries;

        public ValueTask<Stream> OpenEntryStreamAsync(
            int providerOrdinal,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new NotSupportedException());

        public async ValueTask CopyEntryToAsync(
            int providerOrdinal,
            Stream destination,
            CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[1024], cancellationToken);
            await destination.WriteAsync(new byte[] { 0x01 }, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SuccessfulNestedCopyProvider : IArchiveProvider
    {
        public string Id => "successful-nested-copy-test";

        public ArchiveProbeResult Probe(ReadOnlySpan<byte> prefix, string sourcePath) =>
            new("fake", 100, "Successful nested-copy test provider selected.");

        public ValueTask<IArchiveHandle> OpenAsync(
            string sourcePath,
            ReadOnlyMemory<byte> passwordUtf8,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IArchiveHandle>(new SuccessfulNestedCopyHandle());
        }
    }

    private sealed class SuccessfulNestedCopyHandle : IArchiveHandle
    {
        private static readonly IReadOnlyList<ProviderArchiveEntry> TestEntries =
        [
            new ProviderArchiveEntry(
                0,
                "nested.bin",
                ArchiveEntryKind.File,
                OriginalSize: 1,
                CompressedSize: 1,
                ModifiedTimeUtc: null,
                WindowsAttributes: null,
                CompressionMethod: "fixture",
                IntegrityValue: null,
                IsEncrypted: false,
                IsSolid: false,
                LinkTarget: null),
        ];

        public string Format => "fake";
        public string ProviderId => "successful-nested-copy-test";
        public ArchiveCapability Capabilities =>
            ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract;
        public bool IsSolid => false;
        public ArchiveVolumeSetKind VolumeSetKind => ArchiveVolumeSetKind.Single;
        public IReadOnlyList<ProviderArchiveEntry> Entries => TestEntries;

        public ValueTask<Stream> OpenEntryStreamAsync(
            int providerOrdinal,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new NotSupportedException());

        public ValueTask CopyEntryToAsync(
            int providerOrdinal,
            Stream destination,
            CancellationToken cancellationToken) =>
            destination.WriteAsync(new byte[] { 0x01 }, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class VolumeSetTestHandle(
        ArchiveVolumeSetKind volumeSetKind,
        ArchiveCapability capabilities) : IArchiveHandle
    {
        public string Format => "fake";
        public string ProviderId => "volume-set-test";
        public ArchiveCapability Capabilities => capabilities;
        public bool IsSolid => false;
        public ArchiveVolumeSetKind VolumeSetKind => volumeSetKind;
        public IReadOnlyList<ProviderArchiveEntry> Entries => [];

        public ValueTask<Stream> OpenEntryStreamAsync(
            int providerOrdinal,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new NotSupportedException());

        public ValueTask CopyEntryToAsync(
            int providerOrdinal,
            Stream destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoMatchArchiveProvider : IArchiveProvider
    {
        public string Id => "no-match-test";

        public ArchiveProbeResult Probe(ReadOnlySpan<byte> prefix, string sourcePath) =>
            ArchiveProbeResult.NoMatch("Creation-only protocol fixture.");

        public ValueTask<IArchiveHandle> OpenAsync(
            string sourcePath,
            ReadOnlyMemory<byte> passwordUtf8,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IArchiveHandle>(new NotSupportedException());
    }

    private sealed class BlockingZipWriterProvider : IArchiveWriterProvider
    {
        private static readonly IReadOnlySet<ArchiveCompressionPreset> Presets =
            new HashSet<ArchiveCompressionPreset> { ArchiveCompressionPreset.Balanced };

        public TaskCompletionSource EntryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "blocking-zip-writer-test";
        public string Format => "zip";
        public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets => Presets;

        public ValueTask<IArchiveWriterHandle> CreateAsync(
            Stream destination,
            ArchiveCompressionPreset compressionPreset,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IArchiveWriterHandle>(new BlockingZipWriterHandle(destination, EntryStarted));
    }

    private sealed class GateableZipWriterProvider : IArchiveWriterProvider
    {
        private static readonly IReadOnlySet<ArchiveCompressionPreset> Presets =
            new HashSet<ArchiveCompressionPreset> { ArchiveCompressionPreset.Balanced };

        public TaskCompletionSource EntryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ContinueWriting { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => "gateable-zip-writer-test";
        public string Format => "zip";
        public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets => Presets;

        public ValueTask<IArchiveWriterHandle> CreateAsync(
            Stream destination,
            ArchiveCompressionPreset compressionPreset,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IArchiveWriterHandle>(new GateableZipWriterHandle(
                destination,
                EntryStarted,
                ContinueWriting));
    }

    private sealed class GateableZipWriterHandle(
        Stream destination,
        TaskCompletionSource entryStarted,
        TaskCompletionSource continueWriting) : IArchiveWriterHandle
    {
        private ZipArchive? _archive = new(destination, ZipArchiveMode.Create, leaveOpen: true);

        public ValueTask AddDirectoryAsync(
            ArchiveWriteDirectory entry,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var zipEntry = _archive!.CreateEntry(
                entry.ArchivePath.EndsWith('/')
                    ? entry.ArchivePath
                    : $"{entry.ArchivePath}/",
                CompressionLevel.NoCompression);
            zipEntry.LastWriteTime = entry.LastWriteTime;
            zipEntry.ExternalAttributes = (entry.WindowsAttributes ?? 0)
                | (int)FileAttributes.Directory;
            return ValueTask.CompletedTask;
        }

        public async ValueTask AddFileAsync(
            ArchiveWriteEntry entry,
            Stream source,
            Func<long, ValueTask> reportEntryBytes,
            CancellationToken cancellationToken)
        {
            entryStarted.TrySetResult();
            await continueWriting.Task.WaitAsync(cancellationToken);
            var zipEntry = CreateFileEntry(entry);
            await using var output = zipEntry.Open();
            await source.CopyToAsync(output, cancellationToken);
            await reportEntryBytes(entry.Length);
        }

        public async ValueTask AddFileFromProducerAsync(
            ArchiveWriteEntry entry,
            Func<Stream, CancellationToken, ValueTask> writeSource,
            Func<long, ValueTask> reportEntryBytes,
            CancellationToken cancellationToken)
        {
            entryStarted.TrySetResult();
            await continueWriting.Task.WaitAsync(cancellationToken);
            var zipEntry = CreateFileEntry(entry);
            await using var output = zipEntry.Open();
            await writeSource(output, cancellationToken);
            await reportEntryBytes(entry.Length);
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _archive, null)!.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _archive, null)?.Dispose();
            return ValueTask.CompletedTask;
        }

        private ZipArchiveEntry CreateFileEntry(ArchiveWriteEntry entry)
        {
            var zipEntry = _archive!.CreateEntry(entry.ArchivePath, CompressionLevel.NoCompression);
            zipEntry.LastWriteTime = entry.LastWriteTime;
            if (entry.WindowsAttributes is { } attributes)
            {
                zipEntry.ExternalAttributes = attributes & ~(int)FileAttributes.Directory;
            }

            return zipEntry;
        }
    }

    private sealed class BlockingZipWriterHandle(
        Stream destination,
        TaskCompletionSource entryStarted) : IArchiveWriterHandle
    {
        public ValueTask AddDirectoryAsync(
            ArchiveWriteDirectory entry,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async ValueTask AddFileAsync(
            ArchiveWriteEntry entry,
            Stream source,
            Func<long, ValueTask> reportEntryBytes,
            CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 0x50 }, cancellationToken);
            await reportEntryBytes(Math.Min(1, entry.Length));
            entryStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<OperationEvent> ReadTerminalAsync(
        RawWorkerHarness harness,
        OperationId operationId)
    {
        for (var count = 0; count < 64; count++)
        {
            var frame = await harness.ReadFrameAsync();
            if (frame.Kind != ProtocolMessageKinds.OperationEvent)
            {
                continue;
            }

            var operationEvent = frame.ReadPayload<OperationEvent>();
            if (operationEvent.OperationId == operationId
                && operationEvent.Kind == OperationEventKind.Terminal)
            {
                return operationEvent;
            }
        }

        Assert.Fail("The Worker did not emit the expected operation terminal event.");
        throw new InvalidOperationException();
    }

    private static async Task<ProtocolFrame> ReadResponseFrameForRequestAsync(
        RawWorkerHarness harness,
        (Guid RequestId, long Sequence) request,
        string requestKind,
        Action<ProtocolFrame>? onUnmatchedFrame = null)
    {
        for (var count = 0; count < 512; count++)
        {
            var frame = await harness.ReadFrameAsync();
            if (frame.RequestId != request.RequestId)
            {
                onUnmatchedFrame?.Invoke(frame);
                continue;
            }

            Assert.AreEqual(request.Sequence, frame.Sequence);
            if (frame.Kind != ProtocolMessageKinds.Error)
            {
                Assert.AreEqual($"{requestKind}.response", frame.Kind);
            }

            return frame;
        }

        Assert.Fail($"The Worker did not respond to '{requestKind}' before the bounded frame read limit.");
        throw new InvalidOperationException();
    }

    private static string FixturePath(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "fixtures", "corpus", .. parts]);

    private sealed class RebuildZipProvider(byte[] payload) : IArchiveProvider
    {
        public string Id => "rebuild-zip-test";

        public ArchiveProbeResult Probe(ReadOnlySpan<byte> prefix, string sourcePath) =>
            new("zip", 100, "ZIP Rebuild Copy test provider selected.");

        public ValueTask<IArchiveHandle> OpenAsync(
            string sourcePath,
            ReadOnlyMemory<byte> passwordUtf8,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IArchiveHandle>(new RebuildZipHandle(payload));
        }
    }

    private sealed class RebuildZipHandle(byte[] payload) : IArchiveHandle
    {
        private readonly IReadOnlyList<ProviderArchiveEntry> _entries =
        [
            new ProviderArchiveEntry(
                0,
                "keep.bin",
                ArchiveEntryKind.File,
                payload.LongLength,
                payload.LongLength,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                null,
                "Store",
                "test-checksum",
                false,
                false,
                null),
        ];

        public string Format => "zip";
        public string ProviderId => "rebuild-zip-test";
        public ArchiveCapability Capabilities => ArchiveCapability.List | ArchiveCapability.Extract;
        public bool IsSolid => false;
        public ArchiveVolumeSetKind VolumeSetKind => ArchiveVolumeSetKind.Single;
        public IReadOnlyList<ProviderArchiveEntry> Entries => _entries;

        public ValueTask<Stream> OpenEntryStreamAsync(
            int providerOrdinal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(payload, writable: false));
        }

        public async ValueTask CopyEntryToAsync(
            int providerOrdinal,
            Stream destination,
            CancellationToken cancellationToken)
        {
            Assert.AreEqual(0, providerOrdinal);
            await destination.WriteAsync(payload, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SequenceAvailableDiskSpace(params long[] values) : IAvailableDiskSpace
    {
        private int _index;

        public int QueryCount => _index;

        public long GetAvailableFreeSpace(string driveRoot)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(driveRoot));
            Assert.IsTrue(values.Length > 0);
            var index = Interlocked.Increment(ref _index) - 1;
            return values[Math.Min(index, values.Length - 1)];
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string? parent = null)
        {
            Path = System.IO.Path.Combine(
                parent ?? System.IO.Path.GetTempPath(),
                "RainArchives.WorkerProtocolIntegrationTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private static string CreateStaleNestedWorkspace(
        string rootPath,
        string recoveryId,
        IReadOnlyList<int> sourceLengths)
    {
        var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + recoveryId;
        var workspacePath = Path.Combine(rootPath, workspaceName);
        Directory.CreateDirectory(workspacePath);
        File.WriteAllBytes(
            Path.Combine(workspacePath, OwnedNestedTemporaryWorkspace.OwnerFileName),
            OwnedNestedTemporaryWorkspace.CreateOwnerRecord(workspaceName));
        for (var index = 0; index < sourceLengths.Count; index++)
        {
            var sourceDirectory = Path.Combine(
                workspacePath,
                OwnedTemporaryArchiveSource.SourceDirectoryPrefix + index.ToString("x64"));
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllBytes(
                Path.Combine(sourceDirectory, OwnedTemporaryArchiveSource.SourceFileName),
                new byte[sourceLengths[index]]);
        }

        return workspacePath;
    }

    private static string[] GetNestedWorkspaceDirectories(string rootPath) =>
        Directory.GetDirectories(
            rootPath,
            "rainarchives-worker-*",
            SearchOption.TopDirectoryOnly);

    private static string[] GetNestedSourceDirectories(string rootPath) =>
        GetNestedWorkspaceDirectories(rootPath)
            .SelectMany(workspacePath => Directory.GetDirectories(
                workspacePath,
                "rainarchives-nested-*",
                SearchOption.TopDirectoryOnly))
            .ToArray();

    internal sealed class RawWorkerHarness : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _workerStream;
        private readonly NamedPipeClientStream _clientStream;
        private readonly WorkerHost _host;
        private readonly CancellationTokenSource _lifetime;
        private long _nextSequence;
        private bool _negotiated;

        private RawWorkerHarness(
            NamedPipeServerStream workerStream,
            NamedPipeClientStream clientStream,
            WorkerHost host,
            CancellationTokenSource lifetime,
            Task workerRunTask)
        {
            _workerStream = workerStream;
            _clientStream = clientStream;
            _host = host;
            _lifetime = lifetime;
            WorkerRunTask = workerRunTask;
        }

        public Task WorkerRunTask { get; }

        public static async Task<RawWorkerHarness> StartAsync()
        {
            var harness = await StartUnnegotiatedAsync();
            try
            {
                await harness.SendWelcomeAsync(
                    ProtocolConstants.MinorVersion,
                    ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion));
                return harness;
            }
            catch
            {
                await harness.DisposeAsync();
                throw;
            }
        }

        public static async Task<RawWorkerHarness> StartUnnegotiatedAsync(
            IEnumerable<IArchiveProvider>? providers = null,
            string? nestedTemporaryRoot = null,
            IEnumerable<IArchiveWriterProvider>? writerProviders = null,
            string? creationRecoveryRoot = null,
            DateTimeOffset? recoveryMaintenanceNowUtc = null,
            ArchiveCreationResourceGuard? zipRebuildCreationResourceGuard = null,
            ExtractionResourceGuard? zipRebuildDecodedResourceGuard = null,
            string? extractionRecoveryRoot = null,
            Func<WinRarWorkspaceMaintenanceResult>? rarWorkspaceMaintenanceAction = null)
        {
            var pipeName = $"rainarchives-worker-test-{Guid.NewGuid():N}";
            var workerStream = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            var clientStream = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                var serverConnection = workerStream.WaitForConnectionAsync();
                await clientStream.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await serverConnection.WaitAsync(TimeSpan.FromSeconds(5));

                var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                var host = providers is null
                    && writerProviders is null
                    && creationRecoveryRoot is null
                    && extractionRecoveryRoot is null
                    && recoveryMaintenanceNowUtc is null
                    && zipRebuildCreationResourceGuard is null
                    && zipRebuildDecodedResourceGuard is null
                    && rarWorkspaceMaintenanceAction is null
                    ? new WorkerHost(workerStream, nonce)
                    : new WorkerHost(
                        workerStream,
                        nonce,
                        providers ?? [new NoMatchArchiveProvider()],
                        nestedTemporaryRoot,
                        writerProviders,
                        creationRecoveryRoot,
                        recoveryMaintenanceNowUtc,
                        zipRebuildCreationResourceGuard: zipRebuildCreationResourceGuard,
                        zipRebuildDecodedResourceGuard: zipRebuildDecodedResourceGuard,
                        extractionRecoveryRoot: extractionRecoveryRoot,
                        rarWorkspaceMaintenanceAction: rarWorkspaceMaintenanceAction);
                var lifetime = new CancellationTokenSource();
                var workerRunTask = host.RunAsync(lifetime.Token);
                var harness = new RawWorkerHarness(
                    workerStream,
                    clientStream,
                    host,
                    lifetime,
                    workerRunTask);

                var incoming = await ProtocolStream.ReadAsync(clientStream)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(ProtocolFrameType.Json, incoming.Type);
                Assert.AreEqual(ProtocolMessageKinds.WorkerHello, incoming.Json!.Kind);
                var hello = incoming.Json.ReadPayload<WorkerHelloRequest>();
                Assert.AreEqual(nonce, hello.BootstrapNonce);
                return harness;
            }
            catch
            {
                clientStream.Dispose();
                workerStream.Dispose();
                throw;
            }
        }

        public async Task SendWelcomeAsync(int protocolMinor, IReadOnlyList<string> enabledFeatures)
        {
            await ProtocolStream.WriteJsonAsync(
                _clientStream,
                ProtocolMessageKinds.ClientWelcome,
                Guid.Empty,
                new ClientWelcomeResponse(
                    ProtocolConstants.MajorVersion,
                    protocolMinor,
                    "integration-test",
                    enabledFeatures),
                0)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            _negotiated = true;
        }

        public async Task<TResponse> SendAsync<TRequest, TResponse>(string kind, TRequest request)
        {
            var frame = await SendForFrameAsync(kind, request);
            if (frame.Kind == ProtocolMessageKinds.Error)
            {
                var error = frame.ReadPayload<ErrorResponse>().Error;
                Assert.Fail(
                    $"Expected '{kind}.response' but received '{error.Code}' ({error.Category}): {error.Message}");
            }

            Assert.AreEqual($"{kind}.response", frame.Kind);
            return frame.ReadPayload<TResponse>();
        }

        public async Task<ArchiveError> SendForErrorAsync<TRequest>(string kind, TRequest request)
        {
            var frame = await SendForFrameAsync(kind, request);
            Assert.AreEqual(ProtocolMessageKinds.Error, frame.Kind);
            return frame.ReadPayload<ErrorResponse>().Error;
        }

        public async Task StoreSecretAsync(Guid slotId, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                await ProtocolStream.WriteSecretAsync(_clientStream, slotId, bytes)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public IDisposable HoldNormalDispatchSlots()
        {
            var tasksField = typeof(WorkerHost).GetField(
                "_dispatchTasks",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Worker dispatch task registry was not found.");
            var tasks = (ConcurrentDictionary<long, Task>)(tasksField.GetValue(_host)
                ?? throw new InvalidOperationException("Worker dispatch task registry was unavailable."));
            Assert.IsTrue(
                SpinWait.SpinUntil(() => tasks.IsEmpty, TimeSpan.FromSeconds(5)),
                "Normal worker dispatches did not settle before the test tried to hold every slot.");

            var field = typeof(WorkerHost).GetField(
                "_dispatchGate",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Worker dispatch gate was not found.");
            var gate = (SemaphoreSlim)(field.GetValue(_host)
                ?? throw new InvalidOperationException("Worker dispatch gate was unavailable."));
            var held = 0;
            while (gate.Wait(0))
            {
                held++;
            }

            Assert.IsTrue(held > 0, "No normal worker dispatch slots were available to hold.");
            return new DispatchSlotHold(gate, held);
        }

        public IDisposable HoldOperationExecutionSlots()
        {
            var field = typeof(WorkerHost).GetField(
                "_operationExecutionGate",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Worker operation execution gate was not found.");
            var gate = (SemaphoreSlim)(field.GetValue(_host)
                ?? throw new InvalidOperationException("Worker operation execution gate was unavailable."));
            var held = 0;
            while (gate.Wait(0))
            {
                held++;
            }

            Assert.AreEqual(
                ProtocolConstants.MaximumConcurrentOperations,
                held,
                "The test must hold every operation execution slot before admitting operations.");
            return new DispatchSlotHold(gate, held);
        }

        public long GetActiveNestedArchiveBytes()
        {
            var field = typeof(WorkerHost).GetField(
                "_activeNestedArchiveBytes",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Worker nested byte accounting was not found.");
            return (long)(field.GetValue(_host)
                ?? throw new InvalidOperationException("Worker nested byte accounting was unavailable."));
        }

        public async Task<(Guid RequestId, long Sequence)> SendWithoutWaitingAsync<TRequest>(
            string kind,
            TRequest request)
        {
            Assert.IsTrue(_negotiated, "The raw worker handshake is not complete.");
            var requestId = Guid.NewGuid();
            var sequence = Interlocked.Increment(ref _nextSequence);
            await ProtocolStream.WriteJsonAsync(
                _clientStream,
                kind,
                requestId,
                request,
                sequence)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            return (requestId, sequence);
        }

        public async Task<ProtocolFrame> ReadFrameAsync()
        {
            var incoming = await ProtocolStream.ReadAsync(_clientStream)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(ProtocolFrameType.Json, incoming.Type);
            return incoming.Json!;
        }

        private async Task<ProtocolFrame> SendForFrameAsync<TRequest>(string kind, TRequest request)
        {
            var requestEnvelope = await SendWithoutWaitingAsync(kind, request);
            var incoming = await ReadFrameAsync();
            Assert.AreEqual(requestEnvelope.RequestId, incoming.RequestId);
            Assert.AreEqual(requestEnvelope.Sequence, incoming.Sequence);
            return incoming;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _lifetime.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }

            _clientStream.Dispose();
            try
            {
                await WorkerRunTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (
                exception is OperationCanceledException
                    or ObjectDisposedException
                    or IOException
                    or TimeoutException
                    or InvalidDataException)
            {
            }

            await _host.DisposeAsync();
            _workerStream.Dispose();
            _lifetime.Dispose();
        }

        private sealed class DispatchSlotHold(SemaphoreSlim gate, int count) : IDisposable
        {
            private int _count = count;

            public void Dispose()
            {
                var release = Interlocked.Exchange(ref _count, 0);
                if (release > 0)
                {
                    gate.Release(release);
                }
            }
        }
    }
}
