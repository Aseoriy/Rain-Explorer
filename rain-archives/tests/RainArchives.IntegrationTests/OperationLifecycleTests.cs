using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;
using RainArchives.Security;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class OperationLifecycleTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task RealWorkerSuccessfulExtractionRemovesRecoveryJournalAfterTerminal()
    {
        using var root = new TemporaryDirectory();
        var workerTemporaryRoot = Path.Combine(root.Path, "worker-temp");
        var destination = Path.Combine(root.Path, "extracted");
        Directory.CreateDirectory(workerTemporaryRoot);
        var archivePath = FixturePath("archives", "zip", "plain.zip");
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(archivePath));

        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            FindWorker(),
            workerTemporaryRoot);
        var opened = await client.OpenArchiveAsync(archivePath);
        var started = await client.StartExtractionAsync(
            opened.Archive.SessionId,
            [],
            destination);
        var terminal = await client.WaitForTerminalAsync(started.OperationId);
        Assert.AreEqual(OperationPhase.Completed, terminal.Phase);
        await client.CloseArchiveAsync(opened.Archive.SessionId);

        Assert.IsTrue(Directory.Exists(destination));
        Assert.HasCount(0, FindStagingDirectories(root.Path));
        Assert.IsEmpty((await client.ListExtractionRecoveriesAsync()).Recoveries);
        var recoveryRoot = Path.Combine(
            workerTemporaryRoot,
            "rainarchives-extraction-recovery");
        Assert.HasCount(
            0,
            Directory.Exists(recoveryRoot)
                ? Directory.GetDirectories(
                    recoveryRoot,
                    "rainarchives-extraction-*",
                    SearchOption.TopDirectoryOnly)
                : []);
        CollectionAssert.AreEqual(
            originalHash,
            SHA256.HashData(await File.ReadAllBytesAsync(archivePath)));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RealWorkerRetainsPathFreeTerminalOperationActivity()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "private-lifecycle-source.txt");
        var destination = Path.Combine(root.Path, "private-lifecycle-output.zip");
        await File.WriteAllTextAsync(source, "real worker operation activity\n");
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        await using var client = await ArchiveClient.StartAsync(FindWorker());

        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.OperationActivity));
        var initial = await client.GetOperationActivityAsync();
        Assert.IsEmpty(initial.Operations);
        var started = await client.StartZipCreationAsync(
            destination,
            [new ArchiveCreationFile(source, "payload.txt")],
            ArchiveCompressionPreset.Store);
        var terminal = await client.WaitForTerminalAsync(started.OperationId);
        Assert.AreEqual(OperationPhase.Completed, terminal.Phase);

        var activity = await client.GetOperationActivityAsync();
        var completed = activity.Operations.Single(operation =>
            operation.OperationId == started.OperationId);
        Assert.AreEqual(OperationKind.CreateArchive, completed.Kind);
        Assert.AreEqual(OperationPhase.Completed, completed.Phase);
        Assert.AreEqual(terminal.Sequence, completed.LastEventSequence);
        Assert.IsFalse(completed.CanCancel);
        Assert.IsNotNull(completed.CompletedAtUtc);
        Assert.AreEqual(completed.UpdatedAtUtc, completed.CompletedAtUtc);
        Assert.IsFalse(activity.PersistsAcrossWorkerRestart);
        var json = JsonSerializer.Serialize(activity, ProtocolJson.Options);
        Assert.IsFalse(json.Contains(source, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains(destination, StringComparison.OrdinalIgnoreCase));
        CollectionAssert.AreEqual(
            originalHash,
            SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.IsTrue(File.Exists(destination));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CancelResponseTimeoutAbortsOwnedWorkerInsteadOfHanging()
    {
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        using var worker = Process.GetProcessById(client.WorkerProcessId);
        Assert.AreEqual(0, NativeMethods.NtSuspendProcess(worker.Handle),
            "The test could not suspend its exact worker process.");
        var suspended = true;
        try
        {
            using var cancellation = new CancellationTokenSource();
            var open = client.OpenArchiveAsync(
                FixturePath("archives", "zip", "plain.zip"),
                cancellationToken: cancellation.Token);
            await Task.Delay(100);
            Assert.IsFalse(open.IsCompleted, "The suspended worker unexpectedly completed the Open request.");

            var elapsed = Stopwatch.StartNew();
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await open.WaitAsync(TimeSpan.FromSeconds(5)));
            elapsed.Stop();

            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(5),
                $"The bounded cancellation path took {elapsed.Elapsed}.");
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            suspended = false;
        }
        finally
        {
            if (suspended && !worker.HasExited)
            {
                _ = NativeMethods.NtResumeProcess(worker.Handle);
            }
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WorkerCloseTimeoutAbortsOwnedWorkerAndFailsPendingRequests()
    {
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        using var worker = Process.GetProcessById(client.WorkerProcessId);
        Assert.AreEqual(0, NativeMethods.NtSuspendProcess(worker.Handle),
            "The test could not suspend its exact worker process.");
        var suspended = true;
        try
        {
            var pendingOpen = client.OpenArchiveAsync(FixturePath("archives", "zip", "plain.zip"));
            await Task.Delay(100);
            Assert.IsFalse(pendingOpen.IsCompleted,
                "The suspended worker unexpectedly completed the pending Open request.");

            var elapsed = Stopwatch.StartNew();
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            elapsed.Stop();

            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(5),
                $"Bounded worker disposal took {elapsed.Elapsed}.");
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            suspended = false;

            var pendingFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await pendingOpen);
            Assert.AreEqual("worker.disconnected", pendingFailure.Error.Code);
        }
        finally
        {
            if (suspended && !worker.HasExited)
            {
                _ = NativeMethods.NtResumeProcess(worker.Handle);
            }
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task FailedOpenDiscardsPasswordSlotBeforeProviderConsumption()
    {
        using var root = new TemporaryDirectory();
        var missingArchive = Path.Combine(root.Path, "missing.zip");
        await using var client = await ArchiveClient.StartAsync(FindWorker());

        for (var index = 0; index < ProtocolConstants.MaximumSecretSlots; index++)
        {
            var slot = await client.CreateSecretAsync($"failed-open-{index}".AsMemory());
            var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.OpenArchiveAsync(missingArchive, slot));
            Assert.AreEqual("source.not_found", failure.Error.Code);

            var reused = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.OpenArchiveAsync(FixturePath("archives", "zip", "plain.zip"), slot));
            Assert.AreEqual("secret.invalid_slot", reused.Error.Code);
        }

        var capacityProof = await client.CreateSecretAsync("capacity-proof".AsMemory());
        var opened = await client.OpenArchiveAsync(
            FixturePath("archives", "zip", "plain.zip"),
            capacityProof);
        await client.CloseArchiveAsync(opened.Archive.SessionId);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task PasswordSlotsSettleForPreWriteAndQueuedOperationCancellation()
    {
        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "operation-secret-lifecycle.zip");
        CreateLifecycleZip(archivePath);
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        try
        {
            var preWriteSlot = await client.CreateSecretAsync("pre-write-operation-secret".AsMemory());
            using (var preCancelled = new CancellationTokenSource())
            {
                preCancelled.Cancel();
                try
                {
                    await client.StartIntegrityTestAsync(
                        opened.Archive.SessionId,
                        [],
                        preWriteSlot,
                        preCancelled.Token);
                    Assert.Fail("The pre-cancelled operation start unexpectedly reached the worker.");
                }
                catch (OperationCanceledException)
                {
                }
            }

            var preWriteReuse = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.OpenArchiveAsync(
                    FixturePath("archives", "zip", "plain.zip"),
                    preWriteSlot));
            Assert.AreEqual("secret.invalid_slot", preWriteReuse.Error.Code);

            var firstProgress = ObserveFirstProgress(client);
            var first = await client.StartExtractionAsync(
                opened.Archive.SessionId,
                [],
                Path.Combine(root.Path, "first-output"));
            await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(15));

            var queuedSlot = await client.CreateSecretAsync("queued-operation-secret".AsMemory());
            var queued = await client.StartIntegrityTestAsync(
                opened.Archive.SessionId,
                [],
                queuedSlot);
            var queuedCancel = await client.CancelOperationAsync(queued.OperationId);
            Assert.IsTrue(queuedCancel.CancellationRequested);
            var queuedTerminal = await client.WaitForTerminalAsync(queued.OperationId);
            Assert.AreEqual(OperationPhase.Cancelled, queuedTerminal.Phase);

            var queuedReuse = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.OpenArchiveAsync(
                    FixturePath("archives", "zip", "plain.zip"),
                    queuedSlot));
            Assert.AreEqual("secret.invalid_slot", queuedReuse.Error.Code);

            _ = await client.CancelOperationAsync(first.OperationId);
            var firstTerminal = await client.WaitForTerminalAsync(first.OperationId);
            Assert.AreEqual(OperationPhase.Cancelled, firstTerminal.Phase);
        }
        finally
        {
            await client.CloseArchiveAsync(opened.Archive.SessionId);
        }
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task OpenCancellationInterruptsQueuedIndexingAndDoesNotLeakSessionsOrSecret()
    {
        const int concurrentOpens = 10;
        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "open-cancellation.zip");
        CreateOpenCancellationZip(archivePath);

        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var secretSlot = await client.CreateSecretAsync("cancelled-open-secret".AsMemory());
        var cancellations = Enumerable
            .Range(0, concurrentOpens)
            .Select(_ => new CancellationTokenSource())
            .ToArray();
        var opens = cancellations
            .Select((source, index) => client.OpenArchiveAsync(
                archivePath,
                index == concurrentOpens - 1 ? secretSlot : null,
                source.Token))
            .ToArray();

        await Task.Delay(100);
        Assert.IsTrue(opens.Any(task => !task.IsCompleted),
            "The fixture completed before in-flight request cancellation could be exercised.");
        var cancellationLatency = Stopwatch.StartNew();
        foreach (var source in cancellations)
        {
            source.Cancel();
        }

        foreach (var open in opens)
        {
            try
            {
                var unexpected = await open;
                await client.CloseArchiveAsync(unexpected.Archive.SessionId);
                Assert.Fail("A large in-flight open completed successfully after cancellation was requested.");
            }
            catch (OperationCanceledException)
            {
            }
        }

        cancellationLatency.Stop();
        Assert.IsTrue(
            cancellationLatency.Elapsed < TimeSpan.FromSeconds(5),
            $"Open cancellation took {cancellationLatency.Elapsed}.");
        foreach (var source in cancellations)
        {
            source.Dispose();
        }

        var smallArchive = FixturePath("archives", "zip", "plain.zip");
        var reusedSecret = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.OpenArchiveAsync(smallArchive, secretSlot));
        Assert.AreEqual(
            ArchiveErrorCategory.InvalidRequest,
            reusedSecret.Error.Category,
            $"Unexpected reused-secret error: {reusedSecret.Error.Code} " +
            $"(diagnostic {reusedSecret.Error.DiagnosticId ?? "none"}).");
        Assert.AreEqual("secret.invalid_slot", reusedSecret.Error.Code);

        var sessions = new List<OpenArchiveResponse>();
        try
        {
            for (var index = 0; index < ProtocolConstants.MaximumOpenSessions; index++)
            {
                sessions.Add(await client.OpenArchiveAsync(smallArchive));
            }

            var limit = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.OpenArchiveAsync(smallArchive));
            Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, limit.Error.Category);
            Assert.AreEqual("session.limit", limit.Error.Code);
        }
        finally
        {
            foreach (var session in sessions)
            {
                await client.CloseArchiveAsync(session.Archive.SessionId);
            }
        }
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task CancellationCleansStagingWhileWorkerCrashLeavesOnlyOwnedOrphan()
    {
        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "lifecycle.zip");
        var workerTemporaryRoot = Path.Combine(root.Path, "worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        CreateLifecycleZip(archivePath);

        var cancelledDestination = Path.Combine(root.Path, "cancelled-output");
        await using (var client = await ArchiveClient.StartWithTemporaryRootAsync(
                         FindWorker(),
                         workerTemporaryRoot))
        {
            var opened = await client.OpenArchiveAsync(archivePath);
            try
            {
                var firstProgress = ObserveFirstProgress(client);
                var started = await client.StartExtractionAsync(
                    opened.Archive.SessionId,
                    [],
                    cancelledDestination);
                var progress = await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.AreEqual(started.OperationId, progress.OperationId);

                var cancelled = await client.CancelOperationAsync(started.OperationId);
                Assert.IsTrue(cancelled.CancellationRequested);
                var terminal = await client.WaitForTerminalAsync(started.OperationId);
                Assert.AreEqual(OperationPhase.Cancelled, terminal.Phase);
                Assert.AreEqual(ArchiveErrorCategory.Cancelled, terminal.Error!.Category);
                Assert.IsFalse(Directory.Exists(cancelledDestination));
                Assert.AreEqual(0, FindStagingDirectories(root.Path).Length);
            }
            finally
            {
                await client.CloseArchiveAsync(opened.Archive.SessionId);
            }
        }

        var extractionRecoveryRoot = Path.Combine(
            workerTemporaryRoot,
            "rainarchives-extraction-recovery");
        Assert.HasCount(
            0,
            Directory.Exists(extractionRecoveryRoot)
                ? Directory.GetDirectories(
                    extractionRecoveryRoot,
                    "rainarchives-extraction-*",
                    SearchOption.TopDirectoryOnly)
                : []);

        var crashedDestination = Path.Combine(root.Path, "crashed-output");
        var crashedWorkerId = 0;
        var crashedOperationId = default(OperationId);
        await using (var client = await ArchiveClient.StartWithTemporaryRootAsync(
                         FindWorker(),
                         workerTemporaryRoot))
        {
            crashedWorkerId = client.WorkerProcessId;
            var opened = await client.OpenArchiveAsync(archivePath);
            var firstProgress = ObserveFirstProgress(client);
            var started = await client.StartExtractionAsync(
                opened.Archive.SessionId,
                [],
                crashedDestination);
            crashedOperationId = started.OperationId;
            var progress = await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(started.OperationId, progress.OperationId);

            using (var worker = Process.GetProcessById(crashedWorkerId))
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            var disconnected = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.WaitForTerminalAsync(started.OperationId));
            Assert.AreEqual(ArchiveErrorCategory.WorkerFailure, disconnected.Error.Category);
            Assert.AreEqual("worker.disconnected", disconnected.Error.Code);
        }

        Assert.IsFalse(Directory.Exists(crashedDestination));
        var orphanedStaging = FindStagingDirectories(root.Path);
        Assert.AreEqual(1, orphanedStaging.Length,
            "A hard-killed worker should leave only its unpublished operation-owned staging tree for later reconciliation.");
        Assert.AreEqual(
            default(FileAttributes),
            File.GetAttributes(orphanedStaging[0]) & FileAttributes.ReparsePoint);
        Assert.IsFalse(IsProcessRunning(crashedWorkerId));

        var durableInventory = new ExtractionRecoveryStore(extractionRecoveryRoot).Inspect();
        Assert.AreEqual(1, durableInventory.PotentialRecoveryCount);
        Assert.AreEqual(0, durableInventory.SuspiciousOperationCount);
        var durableRecord = durableInventory.RecoveryRecords.Single();
        Assert.AreEqual(crashedOperationId, durableRecord.OperationId);
        Assert.AreEqual(crashedDestination, durableRecord.DestinationPath);
        Assert.AreEqual(ExtractionRecoveryPhase.Writing, durableRecord.Phase);
        Assert.IsNotNull(durableRecord.StagingDirectoryIdentity);
        Assert.IsNull(durableRecord.TreeRevision);

        await using (var recoveryClient = await ArchiveClient.StartWithTemporaryRootAsync(
                         FindWorker(),
                         workerTemporaryRoot))
        {
            Assert.IsTrue(recoveryClient.SupportsFeature(
                ProtocolFeatures.ExtractionRecoveryInspection));
            var listed = await recoveryClient.ListExtractionRecoveriesAsync();
            var listedRecovery = listed.Recoveries.Single();
            Assert.AreEqual(durableRecord.RecoveryId, listedRecovery.RecoveryId);
            Assert.AreEqual(ExtractionRecoveryPhase.Writing, listedRecovery.Phase);
            var listJson = JsonSerializer.Serialize(listed, ProtocolJson.Options);
            Assert.IsFalse(listJson.Contains(crashedDestination, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(listJson.Contains(root.Path, StringComparison.OrdinalIgnoreCase));

            var inspected = await recoveryClient.InspectExtractionRecoveryAsync(
                listedRecovery.RecoveryId);
            Assert.AreEqual(crashedDestination, inspected.DestinationPath);
            Assert.AreEqual(
                ExtractionRecoveryParentState.MatchesJournal,
                inspected.DestinationParent);
            Assert.AreEqual(
                ExtractionRecoveryArtifactState.IdentityMatchRevisionUnbound,
                inspected.StagingArtifact.State);
            Assert.AreEqual(
                ExtractionRecoveryArtifactState.Absent,
                inspected.FinalArtifact.State);
            Assert.AreEqual(
                ExtractionRecoveryAssessment.OwnedIncompleteStagingObserved,
                inspected.Assessment);
            Assert.IsFalse(inspected.SupportsMutation);
            Assert.IsFalse(inspected.SupportsRetry);
            Assert.IsFalse(inspected.SupportsCleanup);

            var opened = await recoveryClient.OpenArchiveAsync(archivePath);
            var warning = opened.Archive.Warnings.Single(candidate =>
                candidate.Code == "worker.extraction_recovery_required");
            Assert.IsFalse(warning.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(warning.Message.Contains(
                durableRecord.RecoveryId,
                StringComparison.Ordinal));
            await recoveryClient.CloseArchiveAsync(opened.Archive.SessionId);
        }

        var preserved = new ExtractionRecoveryStore(extractionRecoveryRoot)
            .Inspect()
            .RecoveryRecords
            .Single();
        Assert.AreEqual(durableRecord.RecoveryId, preserved.RecoveryId);
        Assert.IsTrue(Directory.Exists(orphanedStaging[0]));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task HardKilledZipCreationDuringCopyPublishesNothingAndDeletePendingStagingDisappears()
    {
        using var root = new TemporaryDirectory();
        var sourcePath = Path.Combine(root.Path, "creation-source.bin");
        var destinationPath = Path.Combine(root.Path, "crashed.zip");
        var workerTemporaryRoot = Path.Combine(root.Path, "worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        CreateDeterministicFile(sourcePath, 128L * 1024 * 1024);
        await File.WriteAllTextAsync(
            sourcePath + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\n");

        var killedWorkerId = 0;
        var createdOperationId = default(OperationId);
        await using (var client = await ArchiveClient.StartWithTemporaryRootAsync(
                         FindWorker(),
                         workerTemporaryRoot))
        {
            killedWorkerId = client.WorkerProcessId;
            using var worker = Process.GetProcessById(killedWorkerId);
            var firstProgress = ObserveFirstByteProgress(client);
            var started = await client.StartZipCreationAsync(
                destinationPath,
                [new ArchiveCreationFile(sourcePath, "creation-source.bin")],
                ArchiveCompressionPreset.Maximum);
            createdOperationId = started.OperationId;
            var progress = await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.AreEqual(started.OperationId, progress.OperationId);
            Assert.IsTrue(progress.Progress!.BytesProcessed > 0);
            Assert.IsTrue(progress.Progress.BytesProcessed < new FileInfo(sourcePath).Length);

            Assert.AreEqual(0, NativeMethods.NtSuspendProcess(worker.Handle),
                "The test could not suspend its exact worker during ZIP creation.");
            var suspended = true;
            try
            {
                Assert.IsFalse(File.Exists(destinationPath),
                    "ZIP creation published its destination before the first partial-copy progress event.");
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                suspended = false;
            }
            finally
            {
                if (suspended && !worker.HasExited)
                {
                    _ = NativeMethods.NtResumeProcess(worker.Handle);
                }
            }

            var disconnected = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.WaitForTerminalAsync(started.OperationId));
            Assert.AreEqual(ArchiveErrorCategory.WorkerFailure, disconnected.Error.Category);
            Assert.AreEqual("worker.disconnected", disconnected.Error.Code);
        }

        Assert.IsTrue(File.Exists(sourcePath));
        Assert.AreEqual(128L * 1024 * 1024, new FileInfo(sourcePath).Length);
        Assert.AreEqual(
            "[ZoneTransfer]\r\nZoneId=3\r\n",
            await File.ReadAllTextAsync(sourcePath + ":Zone.Identifier"));
        Assert.IsFalse(File.Exists(destinationPath));
        Assert.HasCount(0, FindCreationStagingFiles(root.Path));
        Assert.IsFalse(IsProcessRunning(killedWorkerId));
        var recoveryDirectories = FindCreationRecoveryDirectories(workerTemporaryRoot);
        Assert.HasCount(1, recoveryDirectories);
        var recoveryDirectory = recoveryDirectories.Single();
        var journalPath = Path.Combine(recoveryDirectory, "journal.bin");
        Assert.IsTrue(File.Exists(journalPath));
        var recoveryInventory = new ArchiveCreationRecoveryStore(
            Path.Combine(workerTemporaryRoot, "rainarchives-creation-recovery"))
            .Inspect();
        Assert.AreEqual(1, recoveryInventory.PotentialRecoveryCount);
        Assert.AreEqual(0, recoveryInventory.SuspiciousOperationCount);
        var recoveryRecord = recoveryInventory.RecoveryRecords.Single();
        Assert.AreEqual(createdOperationId, recoveryRecord.OperationId);
        Assert.AreEqual(destinationPath, recoveryRecord.DestinationPath);
        Assert.AreEqual(ArchiveCreationRecoveryPhase.Writing, recoveryRecord.Phase);
        Assert.IsNotNull(recoveryRecord.StagingFileIdentity);
        Assert.IsNull(recoveryRecord.StagingLength);
        Assert.IsFalse(recoveryRecord.HasIncompleteTail);

        await using (var recoveryClient = await ArchiveClient.StartWithTemporaryRootAsync(
                         FindWorker(),
                         workerTemporaryRoot))
        {
            var opened = await recoveryClient.OpenArchiveAsync(
                FixturePath("archives", "zip", "plain.zip"));
            var warning = opened.Archive.Warnings.Single(
                candidate => candidate.Code == "worker.creation_recovery_required");
            Assert.IsTrue(warning.Message.Contains("1", StringComparison.Ordinal));
            Assert.IsFalse(warning.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(warning.Message.Contains(
                Path.GetFileName(recoveryDirectory),
                StringComparison.Ordinal));
            await recoveryClient.CloseArchiveAsync(opened.Archive.SessionId);
        }

        Assert.AreEqual(recoveryDirectory, FindCreationRecoveryDirectories(workerTemporaryRoot).Single());
        Assert.IsTrue(File.Exists(journalPath));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task HardKilledZipTreeAfterFinalizationPublishesNothingAndDeletePendingStagingDisappears()
    {
        using var root = new TemporaryDirectory();
        var sourceTree = Path.Combine(root.Path, "maximum-entry-tree");
        var destinationPath = Path.Combine(root.Path, "crashed-finalizing.zip");
        var workerTemporaryRoot = Path.Combine(root.Path, "worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        Directory.CreateDirectory(sourceTree);
        for (var index = 0; index < ProtocolConstants.MaximumZipCreationEntries - 1; index++)
        {
            File.WriteAllBytes(Path.Combine(sourceTree, $"entry-{index:D4}.txt"), []);
        }

        var killedWorkerId = 0;
        await using (var client = await ArchiveClient.StartWithTemporaryRootAsync(
                         FindWorker(),
                         workerTemporaryRoot))
        {
            killedWorkerId = client.WorkerProcessId;
            using var worker = Process.GetProcessById(killedWorkerId);
            var finalizing = ObservePhase(client, OperationPhase.Finalizing);
            var started = await client.StartZipTreeCreationAsync(
                destinationPath,
                [],
                [new ArchiveCreationDirectory(sourceTree, "tree")],
                ArchiveCompressionPreset.Store);
            var state = await finalizing.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.AreEqual(started.OperationId, state.OperationId);

            Assert.AreEqual(0, NativeMethods.NtSuspendProcess(worker.Handle),
                "The test could not suspend its exact worker during final source-tree validation.");
            var suspended = true;
            try
            {
                Assert.IsFalse(File.Exists(destinationPath),
                    "ZIP tree creation committed before completing its final source-manifest validation.");
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                suspended = false;
            }
            finally
            {
                if (suspended && !worker.HasExited)
                {
                    _ = NativeMethods.NtResumeProcess(worker.Handle);
                }
            }

            var disconnected = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.WaitForTerminalAsync(started.OperationId));
            Assert.AreEqual(ArchiveErrorCategory.WorkerFailure, disconnected.Error.Category);
            Assert.AreEqual("worker.disconnected", disconnected.Error.Code);
        }

        Assert.AreEqual(
            ProtocolConstants.MaximumZipCreationEntries - 1,
            Directory.GetFiles(sourceTree, "*", SearchOption.TopDirectoryOnly).Length);
        Assert.IsFalse(File.Exists(destinationPath));
        Assert.HasCount(0, FindCreationStagingFiles(root.Path));
        Assert.HasCount(1, FindCreationRecoveryDirectories(workerTemporaryRoot));
        Assert.IsFalse(IsProcessRunning(killedWorkerId));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task HardKilledNestedWorkerLeavesTokenVerifiedWorkspaceThatNextWorkerReportsWithoutDeleting()
    {
        using var root = new TemporaryDirectory();
        var workerTemporaryRoot = Path.Combine(root.Path, "worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        var childBytes = CreateZipBytes("payload.txt", "hard-kill nested payload"u8.ToArray());
        var archivePath = Path.Combine(root.Path, "outer.zip");
        await File.WriteAllBytesAsync(archivePath, CreateZipBytes("child.bin", childBytes));

        string staleWorkspacePath;
        var killedWorkerId = 0;
        await using (var client = await ArchiveClient.StartWithTemporaryRootAsync(
                         FindWorker(),
                         workerTemporaryRoot))
        {
            var parent = await client.OpenArchiveAsync(archivePath);
            var entry = (await client.ListEntriesAsync(parent.Archive.SessionId, 0, 16))
                .Entries
                .Single(item => item.RawPath == "child.bin");
            var child = await client.OpenNestedArchiveAsync(
                parent.Archive.SessionId,
                entry.EntryId,
                maximumBytes: 1024 * 1024);
            Assert.AreEqual("zip", child.Archive.Format, ignoreCase: true);
            staleWorkspacePath = FindNestedWorkspaceDirectories(workerTemporaryRoot).Single();
            var sourceDirectory = FindNestedSourceDirectories(workerTemporaryRoot).Single();
            CollectionAssert.AreEqual(
                childBytes,
                await File.ReadAllBytesAsync(Path.Combine(sourceDirectory, "source.archive")));

            killedWorkerId = client.WorkerProcessId;
            using (var worker = Process.GetProcessById(killedWorkerId))
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            var disconnected = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.ListEntriesAsync(child.Archive.SessionId, 0, 16)
                    .WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(ArchiveErrorCategory.WorkerFailure, disconnected.Error.Category);
            Assert.AreEqual("worker.disconnected", disconnected.Error.Code);
        }

        Assert.IsFalse(IsProcessRunning(killedWorkerId));
        Assert.AreEqual(staleWorkspacePath, FindNestedWorkspaceDirectories(workerTemporaryRoot).Single());
        Assert.HasCount(1, FindNestedSourceDirectories(workerTemporaryRoot));
        var workspaceName = Path.GetFileName(staleWorkspacePath);
        var token = workspaceName["rainarchives-worker-".Length..];
        var ownerPath = Path.Combine(staleWorkspacePath, "owner.lock");
        using (var unlockedOwner = new FileStream(
                   ownerPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var expected = Encoding.ASCII.GetBytes(
                $"RainArchives nested workspace v1\n{token}\n");
            var actual = new byte[expected.Length];
            unlockedOwner.ReadExactly(actual);
            Assert.AreEqual(expected.Length, unlockedOwner.Length);
            CollectionAssert.AreEqual(expected, actual);
        }

        await using (var recoveryClient = await ArchiveClient.StartWithTemporaryRootAsync(
                         FindWorker(),
                         workerTemporaryRoot))
        {
            var reopened = await recoveryClient.OpenArchiveAsync(archivePath);
            var warning = reopened.Archive.Warnings.Single(
                candidate => candidate.Code == "worker.nested_recovery_required");
            Assert.IsTrue(warning.Message.Contains("1", StringComparison.Ordinal));
            Assert.IsFalse(warning.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(warning.Message.Contains(workspaceName, StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(ownerPath));
            Assert.HasCount(1, FindNestedSourceDirectories(workerTemporaryRoot));
        }

        Assert.AreEqual(staleWorkspacePath, FindNestedWorkspaceDirectories(workerTemporaryRoot).Single());
        Assert.HasCount(1, FindNestedSourceDirectories(workerTemporaryRoot));
    }

    private static TaskCompletionSource<OperationEvent> ObserveFirstProgress(ArchiveClient client)
    {
        var firstProgress = new TaskCompletionSource<OperationEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<OperationEvent>? handler = null;
        handler = (_, operationEvent) =>
        {
            if (operationEvent.Kind != OperationEventKind.Progress
                || operationEvent.Progress is not { EntriesProcessed: > 0 })
            {
                return;
            }

            client.OperationEventReceived -= handler;
            firstProgress.TrySetResult(operationEvent);
        };
        client.OperationEventReceived += handler;
        return firstProgress;
    }

    private static TaskCompletionSource<OperationEvent> ObserveFirstByteProgress(ArchiveClient client)
    {
        var firstProgress = new TaskCompletionSource<OperationEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<OperationEvent>? handler = null;
        handler = (_, operationEvent) =>
        {
            if (operationEvent.Kind != OperationEventKind.Progress
                || operationEvent.Progress is not { BytesProcessed: > 0 })
            {
                return;
            }

            client.OperationEventReceived -= handler;
            firstProgress.TrySetResult(operationEvent);
        };
        client.OperationEventReceived += handler;
        return firstProgress;
    }

    private static TaskCompletionSource<OperationEvent> ObservePhase(
        ArchiveClient client,
        OperationPhase phase)
    {
        var observed = new TaskCompletionSource<OperationEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<OperationEvent>? handler = null;
        handler = (_, operationEvent) =>
        {
            if (operationEvent.Kind != OperationEventKind.StateChanged
                || operationEvent.Phase != phase)
            {
                return;
            }

            client.OperationEventReceived -= handler;
            observed.TrySetResult(operationEvent);
        };
        client.OperationEventReceived += handler;
        return observed;
    }

    private static void CreateDeterministicFile(string path, long length)
    {
        var buffer = new byte[1024 * 1024];
        uint state = 0x5241494E;
        for (var index = 0; index < buffer.Length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            buffer[index] = (byte)state;
        }

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        long written = 0;
        while (written < length)
        {
            var count = (int)Math.Min(buffer.Length, length - written);
            stream.Write(buffer, 0, count);
            written += count;
        }

        stream.Flush(flushToDisk: true);
    }

    private static void CreateLifecycleZip(string archivePath)
    {
        var payload = new byte[64 * 1024];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)((index * 31 + 17) % 251);
        }

        using var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        for (var index = 0; index < 2048; index++)
        {
            var entry = archive.CreateEntry($"payloads/{index:D4}.bin", CompressionLevel.NoCompression);
            using var destination = entry.Open();
            destination.Write(payload);
        }
    }

    private static void CreateOpenCancellationZip(string archivePath)
    {
        using var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        for (var index = 0; index < 200_000; index++)
        {
            var entry = archive.CreateEntry($"entries/{index:D6}.txt", CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        }
    }

    private static byte[] CreateZipBytes(string entryPath, byte[] content)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryPath, CompressionLevel.SmallestSize);
            using var destination = entry.Open();
            destination.Write(content);
        }

        return stream.ToArray();
    }

    private static string[] FindStagingDirectories(string parent) =>
        Directory.GetDirectories(parent, "*.rainarchives-*.staging", SearchOption.TopDirectoryOnly);

    private static string[] FindCreationStagingFiles(string parent) =>
        Directory.GetFiles(parent, ".rainarchives-*.zip.staging", SearchOption.TopDirectoryOnly);

    private static string[] FindCreationRecoveryDirectories(string workerTemporaryRoot)
    {
        var recoveryRoot = Path.Combine(
            workerTemporaryRoot,
            "rainarchives-creation-recovery");
        return Directory.Exists(recoveryRoot)
            ? Directory.GetDirectories(
                recoveryRoot,
                "rainarchives-creation-*",
                SearchOption.TopDirectoryOnly)
            : [];
    }

    private static string[] FindNestedWorkspaceDirectories(string parent) =>
        Directory.GetDirectories(parent, "rainarchives-worker-*", SearchOption.TopDirectoryOnly);

    private static string[] FindNestedSourceDirectories(string parent) =>
        FindNestedWorkspaceDirectories(parent)
            .SelectMany(workspacePath => Directory.GetDirectories(
                workspacePath,
                "rainarchives-nested-*",
                SearchOption.TopDirectoryOnly))
            .ToArray();

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string FindWorker()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        if (!File.Exists(worker))
        {
            throw new FileNotFoundException(
                "The integration test output does not contain RainArchives.Worker.exe.",
                worker);
        }

        return worker;
    }

    private static string FixturePath(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "fixtures", "corpus", .. parts]);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.OperationLifecycleTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Directory.Exists(Path))
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException &&
                    DateTime.UtcNow < deadline)
                {
                    // A killed Worker can leave an already-unlinked staging file delete-pending
                    // for a short interval. Retry only test-owned root cleanup and preserve the
                    // final exception if Windows does not release it within the bounded deadline.
                    Thread.Sleep(25);
                }
            }
        }
    }

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054 // Test-only ntdll calls avoid enabling unsafe source generation for the project.
        [DllImport("ntdll.dll")]
        public static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        public static extern int NtResumeProcess(IntPtr processHandle);
#pragma warning restore SYSLIB1054
    }
}
