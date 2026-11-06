using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ArchiveCreationRecoveryStoreTests
{
    private const string TestFileIdentity = "01234567:0123456789ABCDEF";

    [TestMethod]
    public void MissingRecoveryRootIsAnEmptyInventory()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);

        var inventory = store.Inspect();

        Assert.AreEqual(0, inventory.ActiveOperationCount);
        Assert.AreEqual(0, inventory.PotentialRecoveryCount);
        Assert.AreEqual(0, inventory.SuspiciousOperationCount);
        Assert.AreEqual(0, inventory.IncompleteJournalCount);
        Assert.IsFalse(inventory.IsTruncated);
        Assert.IsFalse(inventory.InspectionIncomplete);
        Assert.IsFalse(inventory.RequiresAttention);
        Assert.HasCount(0, inventory.RecoveryRecords);
    }

    [TestMethod]
    public async Task UnknownTopLevelStateIsReportedAndPreserved()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        Directory.CreateDirectory(store.RootDirectory);
        var unknownPath = Path.Combine(store.RootDirectory, "unknown-user-state.txt");
        await File.WriteAllTextAsync(unknownPath, "preserve me");

        var inventory = store.Inspect();

        Assert.AreEqual(1, inventory.SuspiciousOperationCount);
        Assert.IsTrue(inventory.RequiresAttention);
        Assert.AreEqual("preserve me", await File.ReadAllTextAsync(unknownPath));
    }

    [TestMethod]
    public async Task NetworkRecoveryRootIsRejectedWithoutContactingIt()
    {
        using var root = new TemporaryDirectory();
        var store = new ArchiveCreationRecoveryStore(
            @"\\203.0.113.1\RainArchivesRecovery\must-not-be-contacted");

        var inventory = store.Inspect();
        Assert.IsTrue(inventory.InspectionIncomplete);

        var exception = await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await store.BeginAsync(
                OperationId.New(),
                Path.Combine(root.Path, "created.zip"),
                FileSystemIdentityService.CaptureExistingDirectoryIdentity(root.Path),
                CancellationToken.None));
        Assert.IsTrue(exception.Message.Contains("local fixed-drive", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RecoveryRootAncestorReparseIsNeverFollowed()
    {
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var outsideStore = CreateStore(outside.Path);
        var transaction = await BeginAsync(outsideStore, outside.Path);
        await transaction.DisposeAsync();
        var outsideRecoveryId = outsideStore.Inspect().RecoveryRecords.Single().RecoveryId;
        var linkedAncestor = Path.Combine(root.Path, "linked-ancestor");
        Directory.CreateSymbolicLink(linkedAncestor, outside.Path);
        var redirectedStore = new ArchiveCreationRecoveryStore(
            Path.Combine(linkedAncestor, "creation-recovery"));

        var inventory = redirectedStore.Inspect();

        Assert.AreEqual(0, inventory.PotentialRecoveryCount);
        Assert.IsTrue(inventory.InspectionIncomplete);
        var failure = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await redirectedStore.InspectArtifactsAsync(outsideRecoveryId));
        Assert.AreEqual("recovery.record_unverified", failure.Code);
        Assert.IsFalse(failure.Message.Contains(outside.Path, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ExclusiveJournalIsActiveThenBecomesAValidStaleAdmission()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var operationId = OperationId.New();
        var destination = Path.Combine(root.Path, "created.zip");
        var parentIdentity = FileSystemIdentityService.CaptureExistingDirectoryIdentity(root.Path);
        var transaction = await store.BeginAsync(
            operationId,
            destination,
            parentIdentity,
            CancellationToken.None);

        var active = store.Inspect();
        Assert.AreEqual(1, active.ActiveOperationCount);
        Assert.AreEqual(0, active.PotentialRecoveryCount);

        await transaction.DisposeAsync();

        var stale = store.Inspect();
        Assert.AreEqual(0, stale.ActiveOperationCount);
        Assert.AreEqual(1, stale.PotentialRecoveryCount);
        Assert.AreEqual(0, stale.SuspiciousOperationCount);
        Assert.IsTrue(stale.RequiresAttention);
        var record = stale.RecoveryRecords.Single();
        Assert.AreEqual(64, record.RecoveryId.Length);
        Assert.IsTrue(record.RecoveryId.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Assert.AreEqual(operationId, record.OperationId);
        Assert.AreEqual(destination, record.DestinationPath);
        Assert.AreEqual(parentIdentity, record.DestinationParentIdentity);
        Assert.AreEqual(ArchiveCreationRecoveryPhase.Admitted, record.Phase);
        Assert.AreEqual(1, record.Sequence);
        Assert.IsNull(record.StagingFileIdentity);
        Assert.IsNull(record.StagingLength);
        Assert.IsFalse(record.HasIncompleteTail);
        Assert.IsTrue(record.StagingFileName.StartsWith(
            ".rainarchives-",
            StringComparison.Ordinal));
        Assert.IsTrue(record.StagingFileName.EndsWith(
            ".zip.staging",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AllDurablePhasesRoundTripWithImmutableIdentityAndLength()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        await transaction.RecordWritingAsync(TestFileIdentity, CancellationToken.None);
        await transaction.RecordFinalizedAsync(4096, CancellationToken.None);
        await transaction.RecordCommitIntentAsync(CancellationToken.None);
        await transaction.RecordCommittedAsync(CancellationToken.None);
        await transaction.DisposeAsync();

        var inventory = store.Inspect();

        Assert.AreEqual(1, inventory.PotentialRecoveryCount);
        Assert.AreEqual(0, inventory.SuspiciousOperationCount);
        var record = inventory.RecoveryRecords.Single();
        Assert.AreEqual(ArchiveCreationRecoveryPhase.Committed, record.Phase);
        Assert.AreEqual(5, record.Sequence);
        Assert.AreEqual(TestFileIdentity, record.StagingFileIdentity);
        Assert.AreEqual(4096, record.StagingLength);
    }

    [TestMethod]
    public async Task TornTailRetainsLastChecksummedPhaseAndIsFlagged()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        await transaction.RecordWritingAsync(TestFileIdentity, CancellationToken.None);
        var journalPath = Path.Combine(
            transaction.OperationDirectoryPath,
            ArchiveCreationRecoveryStore.JournalFileName);
        await transaction.DisposeAsync();
        await using (var journal = new FileStream(
                         journalPath,
                         FileMode.Append,
                         FileAccess.Write,
                         FileShare.None))
        {
            await journal.WriteAsync(new byte[] { 0x52, 0x41, 0x43 });
            journal.Flush(flushToDisk: true);
        }

        var inventory = store.Inspect();

        Assert.AreEqual(1, inventory.PotentialRecoveryCount);
        Assert.AreEqual(1, inventory.IncompleteJournalCount);
        Assert.AreEqual(0, inventory.SuspiciousOperationCount);
        var record = inventory.RecoveryRecords.Single();
        Assert.AreEqual(ArchiveCreationRecoveryPhase.Writing, record.Phase);
        Assert.IsTrue(record.HasIncompleteTail);
    }

    [TestMethod]
    public async Task ChecksumMismatchIsSuspiciousAndNeverReturnedAsAuthority()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        var journalPath = Path.Combine(
            transaction.OperationDirectoryPath,
            ArchiveCreationRecoveryStore.JournalFileName);
        await transaction.DisposeAsync();
        var bytes = await File.ReadAllBytesAsync(journalPath);
        bytes[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(journalPath, bytes);

        var inventory = store.Inspect();

        Assert.AreEqual(0, inventory.PotentialRecoveryCount);
        Assert.AreEqual(1, inventory.SuspiciousOperationCount);
        Assert.HasCount(0, inventory.RecoveryRecords);
        Assert.IsTrue(File.Exists(journalPath));
    }

    [TestMethod]
    public async Task MissingJournalTimestampIsSuspiciousAndNeverReturnedAsAuthority()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        var journalPath = Path.Combine(
            transaction.OperationDirectoryPath,
            ArchiveCreationRecoveryStore.JournalFileName);
        await transaction.DisposeAsync();
        var frame = await File.ReadAllBytesAsync(journalPath);
        const int headerBytes = 4 + sizeof(int) + SHA256.HashSizeInBytes;
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            frame.AsSpan(4, sizeof(int)));
        var state = JsonNode.Parse(frame.AsSpan(headerBytes, payloadLength))?.AsObject()
            ?? throw new InvalidDataException("The test journal payload is not JSON.");
        Assert.IsTrue(state.Remove("recordedAtUtc"));
        var payload = JsonSerializer.SerializeToUtf8Bytes(state);
        var rewritten = new byte[headerBytes + payload.Length];
        frame.AsSpan(0, 4).CopyTo(rewritten);
        BinaryPrimitives.WriteInt32LittleEndian(
            rewritten.AsSpan(4, sizeof(int)),
            payload.Length);
        SHA256.HashData(payload).CopyTo(rewritten.AsSpan(8, SHA256.HashSizeInBytes));
        payload.CopyTo(rewritten.AsSpan(headerBytes));
        await File.WriteAllBytesAsync(journalPath, rewritten);

        var inventory = store.Inspect();

        Assert.AreEqual(0, inventory.PotentialRecoveryCount);
        Assert.AreEqual(1, inventory.SuspiciousOperationCount);
        Assert.HasCount(0, inventory.RecoveryRecords);
    }

    [TestMethod]
    public async Task PhaseSkippingIsRejectedBeforeAnotherFrameIsWritten()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await transaction.RecordFinalizedAsync(1, CancellationToken.None));
        await transaction.DisposeAsync();

        var record = store.Inspect().RecoveryRecords.Single();
        Assert.AreEqual(ArchiveCreationRecoveryPhase.Admitted, record.Phase);
        Assert.AreEqual(1, record.Sequence);
    }

    [TestMethod]
    public async Task ExactCleanupDeletesOnlyAProvenSingleJournalTree()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        var operationPath = transaction.OperationDirectoryPath;

        await transaction.DeleteAsync();

        Assert.IsFalse(Directory.Exists(operationPath));
        Assert.AreEqual(0, store.Inspect().PotentialRecoveryCount);
    }

    [TestMethod]
    public async Task UnexpectedWorkspaceContentIsPreservedAndLaterClassifiedAsSuspicious()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        var unexpectedPath = Path.Combine(transaction.OperationDirectoryPath, "do-not-delete.txt");
        await File.WriteAllTextAsync(unexpectedPath, "preserve me");

        Exception? cleanupFailure = null;
        try
        {
            await transaction.DeleteAsync();
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            cleanupFailure = exception;
        }

        Assert.IsNotNull(cleanupFailure);
        Assert.AreEqual("preserve me", await File.ReadAllTextAsync(unexpectedPath));
        var inventory = store.Inspect();
        Assert.AreEqual(1, inventory.SuspiciousOperationCount);
        Assert.AreEqual(0, inventory.PotentialRecoveryCount);
    }

    [TestMethod]
    public async Task InventoryLimitIsBoundedAndReportsTruncation()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        for (var index = 0; index < 2; index++)
        {
            var transaction = await BeginAsync(store, root.Path);
            await transaction.DisposeAsync();
        }

        var inventory = store.Inspect(maximumEntries: 1);

        Assert.AreEqual(1, inventory.PotentialRecoveryCount);
        Assert.IsTrue(inventory.IsTruncated);
        Assert.IsTrue(inventory.RequiresAttention);
    }

    [TestMethod]
    public async Task CompletedReceiptDoesNotConsumeTheActionableInventoryBound()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var completedTransaction = await BeginAsync(store, root.Path);
        await completedTransaction.DisposeAsync();
        var completedRecord = store.Inspect().RecoveryRecords.Single();
        _ = await store.ApplyDispositionAsync(
            completedRecord.RecoveryId,
            completedRecord.Phase,
            completedRecord.Sequence,
            completedRecord.RecordedAtUtc,
            ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);

        var pendingTransaction = await BeginAsync(store, root.Path);
        var pendingOperationPath = pendingTransaction.OperationDirectoryPath;
        await pendingTransaction.DisposeAsync();
        var pendingRecoveryId = Path.GetFileName(pendingOperationPath)[
            ArchiveCreationRecoveryStore.OperationPrefix.Length..];

        var inventory = store.Inspect(maximumEntries: 1);

        Assert.AreEqual(1, inventory.PotentialRecoveryCount);
        Assert.AreEqual(pendingRecoveryId, inventory.RecoveryRecords.Single().RecoveryId);
        Assert.IsFalse(inventory.IsTruncated);
    }

    [TestMethod]
    public async Task RecoveryInventoryIsSortedByOpaqueIdentifier()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        for (var index = 0; index < 4; index++)
        {
            var transaction = await BeginAsync(store, root.Path);
            await transaction.DisposeAsync();
        }

        var identifiers = store.Inspect().RecoveryRecords
            .Select(record => record.RecoveryId)
            .ToArray();

        CollectionAssert.AreEqual(
            identifiers.Order(StringComparer.Ordinal).ToArray(),
            identifiers);
    }

    [TestMethod]
    public async Task ExplicitInspectionDoesNotMutateAnExactFinalArtifact()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var destination = Path.Combine(root.Path, "created.zip");
        var transaction = await BeginForDestinationAsync(store, destination);
        var stagingPath = Path.Combine(root.Path, transaction.StagingFileName);
        var bytes = Enumerable.Range(0, 257).Select(value => (byte)value).ToArray();
        var identity = await CreateFileAndCaptureIdentityAsync(stagingPath, bytes);
        await transaction.RecordWritingAsync(identity, CancellationToken.None);
        await transaction.RecordFinalizedAsync(bytes.Length, CancellationToken.None);
        await transaction.RecordCommitIntentAsync(CancellationToken.None);
        File.Move(stagingPath, destination);
        await transaction.DisposeAsync();
        var recoveryId = store.Inspect().RecoveryRecords.Single().RecoveryId;

        var response = await store.InspectArtifactsAsync(recoveryId);

        Assert.AreEqual(
            ArchiveCreationRecoveryParentState.MatchesJournal,
            response.DestinationParent);
        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.Absent,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.ExactMatch,
            response.FinalArtifact.State);
        Assert.AreEqual(bytes.Length, response.FinalArtifact.ObservedLength);
        Assert.AreEqual(1L, response.FinalArtifact.LinkCount);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.OwnedFinalObserved,
            response.Assessment);
        Assert.AreEqual(destination, response.DestinationPath);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(destination));
        Assert.IsFalse(File.Exists(stagingPath));
    }

    [TestMethod]
    public async Task WritingPhaseMatchesIdentityWithoutClaimingFinalLength()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var destination = Path.Combine(root.Path, "created.zip");
        var transaction = await BeginForDestinationAsync(store, destination);
        var stagingPath = Path.Combine(root.Path, transaction.StagingFileName);
        var identity = await CreateFileAndCaptureIdentityAsync(stagingPath, [1, 2, 3, 4]);
        await transaction.RecordWritingAsync(identity, CancellationToken.None);
        await transaction.DisposeAsync();
        var recoveryId = store.Inspect().RecoveryRecords.Single().RecoveryId;

        var response = await store.InspectArtifactsAsync(recoveryId);

        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.IdentityMatchLengthUnbound,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.OwnedStagingObserved,
            response.Assessment);
        Assert.IsNull(response.JournalBoundLength);
    }

    [TestMethod]
    public async Task AdmissionCannotClaimAnArtifactThatWasNeverIdentityBound()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var destination = Path.Combine(root.Path, "created.zip");
        var transaction = await BeginForDestinationAsync(store, destination);
        var stagingPath = Path.Combine(root.Path, transaction.StagingFileName);
        await File.WriteAllBytesAsync(stagingPath, [1, 2, 3]);
        await transaction.DisposeAsync();
        var recoveryId = store.Inspect().RecoveryRecords.Single().RecoveryId;

        var response = await store.InspectArtifactsAsync(recoveryId);

        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.NotBoundByJournal,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts,
            response.Assessment);
    }

    [TestMethod]
    public async Task ReplacedStagingIdentityIsReportedAsConflict()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var destination = Path.Combine(root.Path, "created.zip");
        var transaction = await BeginForDestinationAsync(store, destination);
        var stagingPath = Path.Combine(root.Path, transaction.StagingFileName);
        var identity = await CreateFileAndCaptureIdentityAsync(stagingPath, [1, 2, 3]);
        await transaction.RecordWritingAsync(identity, CancellationToken.None);
        var originalPath = Path.Combine(root.Path, "original-staging.bin");
        File.Move(stagingPath, originalPath);
        await File.WriteAllBytesAsync(stagingPath, [1, 2, 3]);
        await transaction.DisposeAsync();
        var recoveryId = store.Inspect().RecoveryRecords.Single().RecoveryId;

        var response = await store.InspectArtifactsAsync(recoveryId);

        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.IdentityMismatch,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts,
            response.Assessment);
    }

    [TestMethod]
    public async Task FinalizedLengthMismatchIsReportedAsConflict()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var destination = Path.Combine(root.Path, "created.zip");
        var transaction = await BeginForDestinationAsync(store, destination);
        var stagingPath = Path.Combine(root.Path, transaction.StagingFileName);
        var identity = await CreateFileAndCaptureIdentityAsync(stagingPath, [1, 2, 3]);
        await transaction.RecordWritingAsync(identity, CancellationToken.None);
        await transaction.RecordFinalizedAsync(3, CancellationToken.None);
        await File.AppendAllTextAsync(stagingPath, "changed");
        await transaction.DisposeAsync();
        var recoveryId = store.Inspect().RecoveryRecords.Single().RecoveryId;

        var response = await store.InspectArtifactsAsync(recoveryId);

        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.LengthMismatch,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts,
            response.Assessment);
    }

    [TestMethod]
    public async Task TornTailForcesIncompleteAssessmentEvenWhenStagingIdentityMatches()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var destination = Path.Combine(root.Path, "created.zip");
        var transaction = await BeginForDestinationAsync(store, destination);
        var stagingPath = Path.Combine(root.Path, transaction.StagingFileName);
        var identity = await CreateFileAndCaptureIdentityAsync(stagingPath, [1, 2, 3]);
        await transaction.RecordWritingAsync(identity, CancellationToken.None);
        var journalPath = Path.Combine(
            transaction.OperationDirectoryPath,
            ArchiveCreationRecoveryStore.JournalFileName);
        await transaction.DisposeAsync();
        await using (var journal = new FileStream(
                         journalPath,
                         FileMode.Append,
                         FileAccess.Write,
                         FileShare.None))
        {
            await journal.WriteAsync(new byte[] { 0x52, 0x41, 0x43 });
            journal.Flush(flushToDisk: true);
        }

        var recoveryId = store.Inspect().RecoveryRecords.Single().RecoveryId;
        var response = await store.InspectArtifactsAsync(recoveryId);

        Assert.IsTrue(response.Recovery.HasIncompleteTail);
        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.IdentityMatchLengthUnbound,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.InspectionIncomplete,
            response.Assessment);
    }

    [TestMethod]
    public async Task DirectoryAtStagingNameIsReportedAsWrongKind()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var destination = Path.Combine(root.Path, "created.zip");
        var transaction = await BeginForDestinationAsync(store, destination);
        var stagingPath = Path.Combine(root.Path, transaction.StagingFileName);
        Directory.CreateDirectory(stagingPath);
        await transaction.DisposeAsync();
        var recoveryId = store.Inspect().RecoveryRecords.Single().RecoveryId;

        var response = await store.InspectArtifactsAsync(recoveryId);

        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.WrongKind,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts,
            response.Assessment);
    }

    [TestMethod]
    public async Task SimultaneousStagingAndFinalNamesAreReportedAsConflict()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var destination = Path.Combine(root.Path, "created.zip");
        var transaction = await BeginForDestinationAsync(store, destination);
        var stagingPath = Path.Combine(root.Path, transaction.StagingFileName);
        var bytes = new byte[] { 1, 2, 3 };
        var identity = await CreateFileAndCaptureIdentityAsync(stagingPath, bytes);
        await transaction.RecordWritingAsync(identity, CancellationToken.None);
        await transaction.RecordFinalizedAsync(bytes.Length, CancellationToken.None);
        await transaction.RecordCommitIntentAsync(CancellationToken.None);
        await File.WriteAllBytesAsync(destination, bytes);
        await transaction.DisposeAsync();
        var recoveryId = store.Inspect().RecoveryRecords.Single().RecoveryId;

        var response = await store.InspectArtifactsAsync(recoveryId);

        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.ExactMatch,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.IdentityMismatch,
            response.FinalArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts,
            response.Assessment);
    }

    [TestMethod]
    public async Task ReplacedDestinationParentPreventsChildInspection()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var destinationParent = Path.Combine(root.Path, "destination-parent");
        Directory.CreateDirectory(destinationParent);
        var destination = Path.Combine(destinationParent, "created.zip");
        var transaction = await BeginForDestinationAsync(store, destination);
        await transaction.DisposeAsync();
        Directory.Move(destinationParent, destinationParent + "-original");
        Directory.CreateDirectory(destinationParent);
        await File.WriteAllTextAsync(Path.Combine(destinationParent, "created.zip"), "do not inspect");
        var recoveryId = store.Inspect().RecoveryRecords.Single().RecoveryId;

        var response = await store.InspectArtifactsAsync(recoveryId);

        Assert.AreEqual(
            ArchiveCreationRecoveryParentState.IdentityMismatch,
            response.DestinationParent);
        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.NotInspected,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.NotInspected,
            response.FinalArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.ParentChanged,
            response.Assessment);
    }

    [TestMethod]
    public async Task MissingArtifactsRemainAnEvidenceOnlyResult()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        await transaction.DisposeAsync();
        var recoveryId = store.Inspect().RecoveryRecords.Single().RecoveryId;

        var response = await store.InspectArtifactsAsync(recoveryId);

        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.Absent,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.Absent,
            response.FinalArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved,
            response.Assessment);
    }

    [TestMethod]
    public async Task InvalidAndMissingRecoveryIdentifiersArePathFreeErrors()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);

        var invalid = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await store.InspectArtifactsAsync("../not-a-token"));
        Assert.AreEqual("recovery.invalid_id", invalid.Code);
        Assert.IsFalse(invalid.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));

        var missing = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await store.InspectArtifactsAsync(new string('a', 64)));
        Assert.AreEqual("recovery.not_found", missing.Code);
        Assert.IsFalse(missing.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task CancellationBeforeInspectionDoesNotContactTheDestination()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        await transaction.DisposeAsync();
        var recoveryId = store.Inspect().RecoveryRecords.Single().RecoveryId;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await store.InspectArtifactsAsync(recoveryId, cancellation.Token));
    }

    [TestMethod]
    public async Task ActiveRecoveryRecordCannotBeInspected()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        try
        {
            var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await store.InspectArtifactsAsync(transaction.Token));

            Assert.AreEqual("recovery.active", exception.Code);
            Assert.AreEqual(ArchiveErrorCategory.SourceChanged, exception.Category);
            Assert.IsFalse(exception.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RecordCorruptedAfterListingIsNotUsedForDestinationInspection()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        var journalPath = Path.Combine(
            transaction.OperationDirectoryPath,
            ArchiveCreationRecoveryStore.JournalFileName);
        await transaction.DisposeAsync();
        var recoveryId = store.Inspect().RecoveryRecords.Single().RecoveryId;
        var bytes = await File.ReadAllBytesAsync(journalPath);
        bytes[^1] ^= 0x4D;
        await File.WriteAllBytesAsync(journalPath, bytes);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await store.InspectArtifactsAsync(recoveryId));

        Assert.AreEqual("recovery.record_unverified", exception.Code);
        Assert.IsTrue(File.Exists(journalPath));
    }

    [TestMethod]
    public async Task MissingDestinationParentPreventsChildInspection()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var destinationParent = Path.Combine(root.Path, "removed-parent");
        Directory.CreateDirectory(destinationParent);
        var transaction = await BeginForDestinationAsync(
            store,
            Path.Combine(destinationParent, "created.zip"));
        await transaction.DisposeAsync();
        Directory.Delete(destinationParent);
        var recoveryId = store.Inspect().RecoveryRecords.Single().RecoveryId;

        var response = await store.InspectArtifactsAsync(recoveryId);

        Assert.AreEqual(
            ArchiveCreationRecoveryParentState.Missing,
            response.DestinationParent);
        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.NotInspected,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.ParentChanged,
            response.Assessment);
    }

    [TestMethod]
    public async Task HardLinkedArtifactIsNeverClassifiedAsOwned()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var destination = Path.Combine(root.Path, "created.zip");
        var transaction = await BeginForDestinationAsync(store, destination);
        var stagingPath = Path.Combine(root.Path, transaction.StagingFileName);
        var identity = await CreateFileAndCaptureIdentityAsync(stagingPath, [1, 2, 3]);
        await transaction.RecordWritingAsync(identity, CancellationToken.None);
        var secondLink = Path.Combine(root.Path, "second-link.bin");
        if (!NativeMethods.CreateHardLink(secondLink, stagingPath, IntPtr.Zero))
        {
            throw new InvalidOperationException(
                $"This test filesystem cannot create hard links (Win32 {Marshal.GetLastPInvokeError()}).");
        }

        await transaction.DisposeAsync();
        var recoveryId = store.Inspect().RecoveryRecords.Single().RecoveryId;

        var response = await store.InspectArtifactsAsync(recoveryId);

        Assert.AreEqual(
            ArchiveCreationRecoveryArtifactState.UnexpectedHardLinks,
            response.StagingArtifact.State);
        Assert.AreEqual(2L, response.StagingArtifact.LinkCount);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.MultipleOrConflictingArtifacts,
            response.Assessment);
    }

    private static ArchiveCreationRecoveryStore CreateStore(string testRoot) =>
        new(Path.Combine(testRoot, "creation-recovery"));

    private static ValueTask<ArchiveCreationRecoveryStore.ArchiveCreationRecoveryTransaction>
        BeginAsync(ArchiveCreationRecoveryStore store, string destinationParent) =>
        store.BeginAsync(
            OperationId.New(),
            Path.Combine(destinationParent, $"created-{Guid.NewGuid():N}.zip"),
            FileSystemIdentityService.CaptureExistingDirectoryIdentity(destinationParent),
            CancellationToken.None);

    private static ValueTask<ArchiveCreationRecoveryStore.ArchiveCreationRecoveryTransaction>
        BeginForDestinationAsync(
            ArchiveCreationRecoveryStore store,
            string destinationPath)
    {
        var parent = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The test destination requires a parent.");
        return store.BeginAsync(
            OperationId.New(),
            destinationPath,
            FileSystemIdentityService.CaptureExistingDirectoryIdentity(parent),
            CancellationToken.None);
    }

    private static async Task<string> CreateFileAndCaptureIdentityAsync(
        string path,
        byte[] bytes)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
        stream.Flush(flushToDisk: true);
        return FileSystemIdentityService.CaptureOpenFile(stream, path).Identity
            ?? throw new InvalidOperationException("Windows did not return a test file identity.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ArchiveCreationRecoveryStoreTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 50 && Directory.Exists(Path); attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 49)
                {
                    Thread.Sleep(10);
                }
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateHardLinkW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLink(
            string newFileName,
            string existingFileName,
            IntPtr securityAttributes);
    }
}
