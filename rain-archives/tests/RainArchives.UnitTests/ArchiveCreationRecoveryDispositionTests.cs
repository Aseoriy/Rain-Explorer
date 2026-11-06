using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ArchiveCreationRecoveryDispositionTests
{
    [TestMethod]
    public async Task CommitOwnedStagingPublishesOnceAndReplaysWithoutDestinationAccess()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.CommitIntent);

        var completed = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);

        Assert.AreEqual(ArchiveCreationRecoveryDispositionResult.Completed, completed.Result);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.OwnedFinalObserved,
            completed.FinalAssessment);
        Assert.IsFalse(File.Exists(setup.StagingPath));
        CollectionAssert.AreEqual(setup.Bytes, await File.ReadAllBytesAsync(setup.DestinationPath));
        AssertResolvedInventory(setup.Store);

        var movedParent = setup.DestinationParent + "-moved";
        Directory.Move(setup.DestinationParent, movedParent);
        var replay = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);

        Assert.AreEqual(
            ArchiveCreationRecoveryDispositionResult.AlreadyCompleted,
            replay.Result);
        Assert.AreEqual(completed.ResolvedAtUtc, replay.ResolvedAtUtc);
        CollectionAssert.AreEqual(
            setup.Bytes,
            await File.ReadAllBytesAsync(Path.Combine(movedParent, "created.zip")));
    }

    [TestMethod]
    public async Task DiscardOwnedWritingStagingDeletesOnlyTheIdentityBoundArtifact()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.Writing);

        var completed = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging);

        Assert.AreEqual(ArchiveCreationRecoveryDispositionResult.Completed, completed.Result);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved,
            completed.FinalAssessment);
        Assert.IsFalse(File.Exists(setup.StagingPath));
        Assert.IsFalse(File.Exists(setup.DestinationPath));
        AssertResolvedInventory(setup.Store);

        var replay = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging);
        Assert.AreEqual(
            ArchiveCreationRecoveryDispositionResult.AlreadyCompleted,
            replay.Result);
        Assert.AreEqual(completed.ResolvedAtUtc, replay.ResolvedAtUtc);
    }

    [TestMethod]
    public async Task AcceptPublishedFinalRecordsResolutionWithoutChangingTheArchive()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.CommitIntent,
            publishFinal: true);

        var completed = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.AcceptPublishedFinal);

        Assert.AreEqual(ArchiveCreationRecoveryDispositionResult.Completed, completed.Result);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.OwnedFinalObserved,
            completed.FinalAssessment);
        Assert.IsFalse(File.Exists(setup.StagingPath));
        CollectionAssert.AreEqual(setup.Bytes, await File.ReadAllBytesAsync(setup.DestinationPath));
        AssertResolvedInventory(setup.Store);
    }

    [TestMethod]
    public async Task AcknowledgeNoOwnedArtifactResolvesAnEmptyAdmission()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.Admitted);

        var completed = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);

        Assert.AreEqual(ArchiveCreationRecoveryDispositionResult.Completed, completed.Result);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.NoOwnedArtifactObserved,
            completed.FinalAssessment);
        Assert.IsFalse(File.Exists(setup.StagingPath));
        Assert.IsFalse(File.Exists(setup.DestinationPath));
        AssertResolvedInventory(setup.Store);
    }

    [TestMethod]
    public async Task StaleRevisionIsRefusedBeforeAReceiptOrArtifactMutation()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.Writing);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await setup.Store.ApplyDispositionAsync(
                setup.Record.RecoveryId,
                setup.Record.Phase,
                setup.Record.Sequence,
                setup.Record.RecordedAtUtc.AddTicks(1),
                ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging));

        Assert.AreEqual("recovery.disposition_stale_revision", exception.Code);
        Assert.IsTrue(File.Exists(setup.StagingPath));
        Assert.IsFalse(File.Exists(GetDispositionPath(setup)));
        Assert.IsFalse(exception.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task DurableIntentReplaysTheSameActionButRefusesAConflictingAction()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.CommitIntent);

        await AssertCheckpointCrashAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging,
            ArchiveCreationRecoveryStore.ArchiveCreationRecoveryDispositionCheckpoint.IntentDurable);

        Assert.IsTrue(File.Exists(setup.StagingPath));
        Assert.IsFalse(File.Exists(setup.DestinationPath));
        Assert.IsTrue(File.Exists(GetDispositionPath(setup)));
        Assert.AreEqual(1, setup.Store.Inspect().PotentialRecoveryCount);

        var conflict = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await ApplyAsync(
                setup,
                ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging));
        Assert.AreEqual("recovery.disposition_conflict", conflict.Code);
        Assert.IsTrue(File.Exists(setup.StagingPath));

        var resumed = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);
        Assert.AreEqual(ArchiveCreationRecoveryDispositionResult.Completed, resumed.Result);
        Assert.IsFalse(File.Exists(setup.StagingPath));
        CollectionAssert.AreEqual(setup.Bytes, await File.ReadAllBytesAsync(setup.DestinationPath));
    }

    [TestMethod]
    public async Task CrashAfterCommitMutationResumesFromTheExactFinalIdentity()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.CommitIntent);

        await AssertCheckpointCrashAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging,
            ArchiveCreationRecoveryStore.ArchiveCreationRecoveryDispositionCheckpoint
                .ArtifactMutationApplied);

        Assert.IsFalse(File.Exists(setup.StagingPath));
        CollectionAssert.AreEqual(setup.Bytes, await File.ReadAllBytesAsync(setup.DestinationPath));
        Assert.AreEqual(1, setup.Store.Inspect().PotentialRecoveryCount);

        var resumed = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);
        Assert.AreEqual(ArchiveCreationRecoveryDispositionResult.Completed, resumed.Result);
        AssertResolvedInventory(setup.Store);

        var replay = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);
        Assert.AreEqual(
            ArchiveCreationRecoveryDispositionResult.AlreadyCompleted,
            replay.Result);
    }

    [TestMethod]
    public async Task TornInitialReceiptIsRestartedBeforeAnyArtifactMutation()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.Writing);
        await File.WriteAllBytesAsync(GetDispositionPath(setup), "RAC"u8.ToArray());

        var inventory = setup.Store.Inspect();
        Assert.AreEqual(1, inventory.PotentialRecoveryCount);
        Assert.AreEqual(0, inventory.SuspiciousOperationCount);

        var completed = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging);

        Assert.AreEqual(ArchiveCreationRecoveryDispositionResult.Completed, completed.Result);
        Assert.IsFalse(File.Exists(setup.StagingPath));
        AssertResolvedInventory(setup.Store);
    }

    [TestMethod]
    public async Task MalformedInitialReceiptIsSuspiciousAndNeverAuthorizesMutation()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.Writing);
        await File.WriteAllBytesAsync(GetDispositionPath(setup), "BAD"u8.ToArray());

        var inventory = setup.Store.Inspect();
        Assert.AreEqual(0, inventory.PotentialRecoveryCount);
        Assert.AreEqual(1, inventory.SuspiciousOperationCount);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await ApplyAsync(
                setup,
                ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging));
        Assert.AreEqual("recovery.record_unverified", exception.Code);
        Assert.IsTrue(File.Exists(setup.StagingPath));
        Assert.IsFalse(File.Exists(setup.DestinationPath));
        Assert.IsFalse(exception.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task HardLinkedStagingIsRefusedWithoutCreatingAReceipt()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.Writing);
        var secondLink = Path.Combine(setup.DestinationParent, "second-link.bin");
        if (!NativeMethods.CreateHardLink(secondLink, setup.StagingPath, IntPtr.Zero))
        {
            throw new InvalidOperationException(
                $"This test filesystem cannot create hard links (Win32 {Marshal.GetLastPInvokeError()}).");
        }

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await ApplyAsync(
                setup,
                ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging));

        Assert.AreEqual("recovery.disposition_not_applicable", exception.Code);
        Assert.IsTrue(File.Exists(setup.StagingPath));
        Assert.IsTrue(File.Exists(secondLink));
        Assert.IsFalse(File.Exists(GetDispositionPath(setup)));
    }

    [TestMethod]
    public async Task DestinationParentCannotMoveWhileDurableDispositionIsApplying()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.Writing);
        var movedParent = setup.DestinationParent + "-moved";
        var renameWasBlocked = false;

        var completed = await setup.Store.ApplyDispositionAsync(
            setup.Record.RecoveryId,
            setup.Record.Phase,
            setup.Record.Sequence,
            setup.Record.RecordedAtUtc,
            ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging,
            checkpoint =>
            {
                if (checkpoint
                    != ArchiveCreationRecoveryStore
                        .ArchiveCreationRecoveryDispositionCheckpoint.IntentDurable)
                {
                    return;
                }

                try
                {
                    Directory.Move(setup.DestinationParent, movedParent);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    renameWasBlocked = true;
                }
            });

        Assert.IsTrue(renameWasBlocked);
        Assert.AreEqual(ArchiveCreationRecoveryDispositionResult.Completed, completed.Result);
        Directory.Move(setup.DestinationParent, movedParent);
        Assert.IsTrue(Directory.Exists(movedParent));
        Assert.IsFalse(File.Exists(Path.Combine(movedParent, Path.GetFileName(setup.StagingPath))));
    }

    [TestMethod]
    public async Task ExpiredCompletedReceiptIsPrunedAndReplayFailsClosed()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.Admitted);
        var completed = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);

        var pruned = setup.Store.PruneCompletedDispositionReceipts(
            completed.ResolvedAtUtc
                + ArchiveCreationRecoveryStore.CompletedDispositionReceiptRetention
                + TimeSpan.FromTicks(1),
            maximumOperations: 16,
            maximumRetainedReceipts: 16,
            maximumPrunes: 16,
            CancellationToken.None);

        Assert.AreEqual(1, pruned);
        Assert.IsFalse(Directory.Exists(setup.OperationDirectoryPath));
        var replay = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await ApplyAsync(
                setup,
                ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact));
        Assert.AreEqual("recovery.not_found", replay.Code);
        Assert.IsFalse(replay.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task CountRetentionKeepsTheNewestCompletedReceiptsAndTheirReplayEvidence()
    {
        using var root = new TemporaryDirectory();
        var outcomes = new List<(
            RecoverySetup Setup,
            ApplyArchiveCreationRecoveryDispositionResponse Completed)>();
        for (var index = 0; index < 4; index++)
        {
            var setup = await CreateRecoveryAsync(
                root.Path,
                ArchiveCreationRecoveryPhase.Admitted);
            var completed = await ApplyAsync(
                setup,
                ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);
            outcomes.Add((setup, completed));
        }

        var newest = outcomes
            .OrderByDescending(outcome => outcome.Completed.ResolvedAtUtc)
            .ThenByDescending(outcome => outcome.Setup.Record.RecoveryId, StringComparer.Ordinal)
            .Take(2)
            .Select(outcome => outcome.Setup.Record.RecoveryId)
            .ToHashSet(StringComparer.Ordinal);
        var maintenanceNow = outcomes.Max(outcome => outcome.Completed.ResolvedAtUtc)
            + TimeSpan.FromSeconds(1);

        var pruned = outcomes[0].Setup.Store.PruneCompletedDispositionReceipts(
            maintenanceNow,
            maximumOperations: 16,
            maximumRetainedReceipts: 2,
            maximumPrunes: 16,
            CancellationToken.None);

        Assert.AreEqual(2, pruned);
        foreach (var outcome in outcomes)
        {
            if (newest.Contains(outcome.Setup.Record.RecoveryId))
            {
                Assert.IsTrue(Directory.Exists(outcome.Setup.OperationDirectoryPath));
                var replay = await ApplyAsync(
                    outcome.Setup,
                    ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);
                Assert.AreEqual(
                    ArchiveCreationRecoveryDispositionResult.AlreadyCompleted,
                    replay.Result);
                Assert.AreEqual(outcome.Completed.ResolvedAtUtc, replay.ResolvedAtUtc);
            }
            else
            {
                Assert.IsFalse(Directory.Exists(outcome.Setup.OperationDirectoryPath));
                var missing = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                    await ApplyAsync(
                        outcome.Setup,
                        ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact));
                Assert.AreEqual("recovery.not_found", missing.Code);
            }
        }
    }

    [TestMethod]
    public async Task StructurallyUnexpectedCompletedRecordIsNeverPruned()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.Admitted);
        var completed = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);
        var unexpectedPath = Path.Combine(setup.OperationDirectoryPath, "preserve-me.txt");
        await File.WriteAllTextAsync(unexpectedPath, "user state");

        var pruned = setup.Store.PruneCompletedDispositionReceipts(
            completed.ResolvedAtUtc
                + ArchiveCreationRecoveryStore.CompletedDispositionReceiptRetention
                + TimeSpan.FromTicks(1),
            maximumOperations: 16,
            maximumRetainedReceipts: 0,
            maximumPrunes: 16,
            CancellationToken.None);

        Assert.AreEqual(0, pruned);
        Assert.AreEqual("user state", await File.ReadAllTextAsync(unexpectedPath));
        var inventory = setup.Store.Inspect();
        Assert.AreEqual(1, inventory.SuspiciousOperationCount);
        Assert.IsTrue(inventory.RequiresAttention);
    }

    [TestMethod]
    public async Task HardLinkedCompletedReceiptIsNeverPruned()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.Admitted);
        var completed = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);
        var secondLink = Path.Combine(root.Path, "disposition-second-link.bin");
        if (!NativeMethods.CreateHardLink(secondLink, GetDispositionPath(setup), IntPtr.Zero))
        {
            throw new InvalidOperationException(
                $"This test filesystem cannot create hard links (Win32 {Marshal.GetLastPInvokeError()}).");
        }

        var pruned = setup.Store.PruneCompletedDispositionReceipts(
            completed.ResolvedAtUtc
                + ArchiveCreationRecoveryStore.CompletedDispositionReceiptRetention
                + TimeSpan.FromTicks(1),
            maximumOperations: 16,
            maximumRetainedReceipts: 0,
            maximumPrunes: 16,
            CancellationToken.None);

        Assert.AreEqual(0, pruned);
        Assert.IsTrue(Directory.Exists(setup.OperationDirectoryPath));
        Assert.IsTrue(File.Exists(secondLink));
        var inventory = setup.Store.Inspect();
        Assert.AreEqual(1, inventory.SuspiciousOperationCount);
    }

    [TestMethod]
    public async Task TruncatedRetentionScanNeverUsesCountAsDeletionAuthority()
    {
        using var root = new TemporaryDirectory();
        var outcomes = new List<(
            RecoverySetup Setup,
            ApplyArchiveCreationRecoveryDispositionResponse Completed)>();
        for (var index = 0; index < 2; index++)
        {
            var setup = await CreateRecoveryAsync(
                root.Path,
                ArchiveCreationRecoveryPhase.Admitted);
            var completed = await ApplyAsync(
                setup,
                ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);
            outcomes.Add((setup, completed));
        }

        var maintenanceNow = outcomes.Max(outcome => outcome.Completed.ResolvedAtUtc)
            + TimeSpan.FromSeconds(1);
        var pruned = outcomes[0].Setup.Store.PruneCompletedDispositionReceipts(
            maintenanceNow,
            maximumOperations: 1,
            maximumRetainedReceipts: 0,
            maximumPrunes: 16,
            CancellationToken.None);

        Assert.AreEqual(0, pruned);
        Assert.IsTrue(outcomes.All(outcome =>
            Directory.Exists(outcome.Setup.OperationDirectoryPath)));
        var replay = await ApplyAsync(
            outcomes[0].Setup,
            ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);
        Assert.AreEqual(
            ArchiveCreationRecoveryDispositionResult.AlreadyCompleted,
            replay.Result);
    }

    [TestMethod]
    public async Task CancelledRetentionPassPreservesCompletedReplayEvidence()
    {
        using var root = new TemporaryDirectory();
        var setup = await CreateRecoveryAsync(
            root.Path,
            ArchiveCreationRecoveryPhase.Admitted);
        var completed = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            setup.Store.PruneCompletedDispositionReceipts(
                completed.ResolvedAtUtc
                    + ArchiveCreationRecoveryStore.CompletedDispositionReceiptRetention
                    + TimeSpan.FromTicks(1),
                maximumOperations: 16,
                maximumRetainedReceipts: 0,
                maximumPrunes: 16,
                cancellation.Token));

        Assert.IsTrue(Directory.Exists(setup.OperationDirectoryPath));
        var replay = await ApplyAsync(
            setup,
            ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);
        Assert.AreEqual(
            ArchiveCreationRecoveryDispositionResult.AlreadyCompleted,
            replay.Result);
        Assert.AreEqual(completed.ResolvedAtUtc, replay.ResolvedAtUtc);
    }

    private static async Task AssertCheckpointCrashAsync(
        RecoverySetup setup,
        ArchiveCreationRecoveryDispositionAction action,
        ArchiveCreationRecoveryStore.ArchiveCreationRecoveryDispositionCheckpoint checkpoint)
    {
        var exception = await Assert.ThrowsExactlyAsync<SimulatedCrashException>(async () =>
            await setup.Store.ApplyDispositionAsync(
                setup.Record.RecoveryId,
                setup.Record.Phase,
                setup.Record.Sequence,
                setup.Record.RecordedAtUtc,
                action,
                observed =>
                {
                    if (observed == checkpoint)
                    {
                        throw new SimulatedCrashException();
                    }
                }));
        Assert.IsNotNull(exception);
    }

    private static ValueTask<ApplyArchiveCreationRecoveryDispositionResponse> ApplyAsync(
        RecoverySetup setup,
        ArchiveCreationRecoveryDispositionAction action) =>
        setup.Store.ApplyDispositionAsync(
            setup.Record.RecoveryId,
            setup.Record.Phase,
            setup.Record.Sequence,
            setup.Record.RecordedAtUtc,
            action);

    private static void AssertResolvedInventory(ArchiveCreationRecoveryStore store)
    {
        var inventory = store.Inspect();
        Assert.AreEqual(0, inventory.PotentialRecoveryCount);
        Assert.AreEqual(0, inventory.SuspiciousOperationCount);
        Assert.IsFalse(inventory.RequiresAttention);
        Assert.HasCount(0, inventory.RecoveryRecords);
    }

    private static string GetDispositionPath(RecoverySetup setup) =>
        Path.Combine(
            setup.OperationDirectoryPath,
            ArchiveCreationRecoveryStore.DispositionFileName);

    private static async Task<RecoverySetup> CreateRecoveryAsync(
        string testRoot,
        ArchiveCreationRecoveryPhase phase,
        bool publishFinal = false)
    {
        if (phase is < ArchiveCreationRecoveryPhase.Admitted
            or > ArchiveCreationRecoveryPhase.CommitIntent)
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        var store = new ArchiveCreationRecoveryStore(
            Path.Combine(testRoot, "creation-recovery"));
        var destinationParent = Path.Combine(testRoot, $"destination-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destinationParent);
        var destinationPath = Path.Combine(destinationParent, "created.zip");
        var transaction = await store.BeginAsync(
            OperationId.New(),
            destinationPath,
            FileSystemIdentityService.CaptureExistingDirectoryIdentity(destinationParent),
            CancellationToken.None);
        var stagingPath = Path.Combine(destinationParent, transaction.StagingFileName);
        var bytes = Enumerable.Range(0, 257).Select(value => (byte)value).ToArray();
        if (phase >= ArchiveCreationRecoveryPhase.Writing)
        {
            var identity = await CreateFileAndCaptureIdentityAsync(stagingPath, bytes);
            await transaction.RecordWritingAsync(identity, CancellationToken.None);
        }

        if (phase >= ArchiveCreationRecoveryPhase.Finalized)
        {
            await transaction.RecordFinalizedAsync(bytes.Length, CancellationToken.None);
        }

        if (phase >= ArchiveCreationRecoveryPhase.CommitIntent)
        {
            await transaction.RecordCommitIntentAsync(CancellationToken.None);
        }

        if (publishFinal)
        {
            if (phase != ArchiveCreationRecoveryPhase.CommitIntent)
            {
                throw new ArgumentException(
                    "Only a commit-intent fixture can publish its final archive.",
                    nameof(phase));
            }

            File.Move(stagingPath, destinationPath);
        }

        var operationDirectoryPath = transaction.OperationDirectoryPath;
        await transaction.DisposeAsync();
        var record = store.Inspect().RecoveryRecords.Single();
        return new RecoverySetup(
            store,
            record,
            destinationParent,
            destinationPath,
            stagingPath,
            operationDirectoryPath,
            bytes);
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

    private sealed record RecoverySetup(
        ArchiveCreationRecoveryStore Store,
        ArchiveCreationRecoveryRecord Record,
        string DestinationParent,
        string DestinationPath,
        string StagingPath,
        string OperationDirectoryPath,
        byte[] Bytes);

    private sealed class SimulatedCrashException : Exception;

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ArchiveCreationRecoveryDispositionTests",
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
        internal static extern bool CreateHardLink(
            string newFileName,
            string existingFileName,
            IntPtr securityAttributes);
    }
}
