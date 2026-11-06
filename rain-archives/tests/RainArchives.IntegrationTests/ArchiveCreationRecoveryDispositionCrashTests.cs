using System.Diagnostics;
using RainArchives.Client;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class ArchiveCreationRecoveryDispositionCrashTests
{
    private const string CheckpointConfigurationFileName =
        ".rainarchives-recovery-disposition-checkpoint";
    private const string CheckpointReadyFileName =
        ".rainarchives-recovery-disposition-checkpoint.ready";

    [TestMethod]
    [Timeout(45_000)]
    public async Task WorkerIpcHardKillAfterDurableDispositionIntentReplaysOneExactCommit()
    {
        await AssertWorkerHardKillAndReplayAsync(
            ArchiveCreationRecoveryStore
                .ArchiveCreationRecoveryDispositionCheckpoint.IntentDurable);
    }

    [TestMethod]
    [Timeout(45_000)]
    public async Task WorkerIpcHardKillAfterDispositionCommitReconcilesWithoutRepublishing()
    {
        await AssertWorkerHardKillAndReplayAsync(
            ArchiveCreationRecoveryStore
                .ArchiveCreationRecoveryDispositionCheckpoint.ArtifactMutationApplied);
    }

    private static async Task AssertWorkerHardKillAndReplayAsync(
        ArchiveCreationRecoveryStore.ArchiveCreationRecoveryDispositionCheckpoint checkpoint)
    {
        using var root = new TemporaryDirectory();
        var workerTemporaryRoot = Path.Combine(root.Path, "worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        var recoveryRoot = Path.Combine(
            workerTemporaryRoot,
            "rainarchives-creation-recovery");
        var destinationParent = Path.Combine(root.Path, "destination");
        Directory.CreateDirectory(destinationParent);
        var destinationPath = Path.Combine(destinationParent, "created.zip");
        var store = new ArchiveCreationRecoveryStore(recoveryRoot);
        var payload = Enumerable.Range(0, 4097).Select(value => (byte)value).ToArray();
        var transaction = await store.BeginAsync(
            OperationId.New(),
            destinationPath,
            FileSystemIdentityService.CaptureExistingDirectoryIdentity(destinationParent),
            CancellationToken.None);
        var stagingPath = Path.Combine(destinationParent, transaction.StagingFileName);
        string stagingIdentity;
        await using (var staging = new FileStream(
                         stagingPath,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.Read | FileShare.Write | FileShare.Delete,
                         bufferSize: 4096,
                         FileOptions.Asynchronous))
        {
            await staging.WriteAsync(payload);
            await staging.FlushAsync();
            staging.Flush(flushToDisk: true);
            stagingIdentity = FileSystemIdentityService.CaptureOpenFile(staging, stagingPath).Identity
                ?? throw new InvalidOperationException(
                    "Windows did not return a staging identity for the crash probe.");
        }

        await transaction.RecordWritingAsync(stagingIdentity, CancellationToken.None);
        await transaction.RecordFinalizedAsync(payload.Length, CancellationToken.None);
        await transaction.RecordCommitIntentAsync(CancellationToken.None);
        await transaction.DisposeAsync();
        var record = store.Inspect().RecoveryRecords.Single();
        var configurationPath = Path.Combine(
            workerTemporaryRoot,
            CheckpointConfigurationFileName);
        var readyPath = Path.Combine(workerTemporaryRoot, CheckpointReadyFileName);
        await File.WriteAllTextAsync(configurationPath, checkpoint.ToString());

        var interruptedClient = await ArchiveClient.StartWithTemporaryRootAsync(
            FindRecoveryCrashProbe(),
            workerTemporaryRoot);
        var interruptedWorkerId = interruptedClient.WorkerProcessId;
        try
        {
            Assert.IsTrue(
                interruptedClient.SupportsFeature(
                    ProtocolFeatures.ArchiveCreationRecoveryDisposition));
            var workerInventory = await interruptedClient.ListArchiveCreationRecoveriesAsync();
            Assert.AreEqual(record.RecoveryId, workerInventory.Recoveries.Single().RecoveryId);

            var dispositionTask = interruptedClient
                .ApplyArchiveCreationRecoveryDispositionAsync(
                    record.RecoveryId,
                    record.Phase,
                    record.Sequence,
                    record.RecordedAtUtc,
                    ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);
            await WaitForCheckpointAsync(readyPath, checkpoint);

            if (checkpoint
                == ArchiveCreationRecoveryStore
                    .ArchiveCreationRecoveryDispositionCheckpoint.IntentDurable)
            {
                Assert.IsTrue(File.Exists(stagingPath));
                Assert.IsFalse(File.Exists(destinationPath));
            }
            else
            {
                Assert.IsFalse(File.Exists(stagingPath));
                Assert.IsTrue(File.Exists(destinationPath));
            }

            await KillProcessAsync(interruptedWorkerId);
            var disconnected = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await dispositionTask.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(ArchiveErrorCategory.WorkerFailure, disconnected.Error.Category);
            Assert.AreEqual("worker.disconnected", disconnected.Error.Code);
        }
        finally
        {
            await interruptedClient.DisposeAsync();
            if (IsProcessRunning(interruptedWorkerId))
            {
                await KillProcessAsync(interruptedWorkerId);
            }
        }

        Assert.IsFalse(IsProcessRunning(interruptedWorkerId));
        if (checkpoint
            == ArchiveCreationRecoveryStore
                .ArchiveCreationRecoveryDispositionCheckpoint.IntentDurable)
        {
            Assert.IsTrue(File.Exists(stagingPath));
            Assert.IsFalse(File.Exists(destinationPath));
        }
        else
        {
            Assert.IsFalse(File.Exists(stagingPath));
            Assert.IsTrue(File.Exists(destinationPath));
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(destinationPath));
        }

        await using var recoveryClient = await ArchiveClient.StartWithTemporaryRootAsync(
            FindWorker(),
            workerTemporaryRoot);
        var interrupted = await recoveryClient.ListArchiveCreationRecoveriesAsync();
        Assert.AreEqual(0, interrupted.SuspiciousOperationCount);
        Assert.AreEqual(record.RecoveryId, interrupted.Recoveries.Single().RecoveryId);

        var completed = await recoveryClient.ApplyArchiveCreationRecoveryDispositionAsync(
            record.RecoveryId,
            record.Phase,
            record.Sequence,
            record.RecordedAtUtc,
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);
        Assert.AreEqual(ArchiveCreationRecoveryDispositionResult.Completed, completed.Result);
        Assert.AreEqual(
            ArchiveCreationRecoveryAssessment.OwnedFinalObserved,
            completed.FinalAssessment);
        Assert.IsFalse(File.Exists(stagingPath));
        Assert.IsTrue(File.Exists(destinationPath));
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(destinationPath));
        Assert.HasCount(
            0,
            (await recoveryClient.ListArchiveCreationRecoveriesAsync()).Recoveries);

        var movedParent = destinationParent + "-moved";
        Directory.Move(destinationParent, movedParent);
        var replay = await recoveryClient.ApplyArchiveCreationRecoveryDispositionAsync(
            record.RecoveryId,
            record.Phase,
            record.Sequence,
            record.RecordedAtUtc,
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);
        Assert.AreEqual(ArchiveCreationRecoveryDispositionResult.AlreadyCompleted, replay.Result);
        Assert.AreEqual(completed.ResolvedAtUtc, replay.ResolvedAtUtc);
        CollectionAssert.AreEqual(
            payload,
            await File.ReadAllBytesAsync(Path.Combine(movedParent, "created.zip")));
    }

    private static async Task WaitForCheckpointAsync(
        string readyPath,
        ArchiveCreationRecoveryStore.ArchiveCreationRecoveryDispositionCheckpoint checkpoint)
    {
        var expected = $"READY:{checkpoint}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(readyPath))
                {
                    var observed = await File.ReadAllTextAsync(readyPath, timeout.Token);
                    Assert.AreEqual(expected, observed);
                    return;
                }
            }
            catch (IOException)
            {
                // The checkpoint writer may still be durably closing the signal file.
            }

            await Task.Delay(10, timeout.Token);
        }

        throw new TimeoutException("The recovery-disposition worker checkpoint was not reached.");
    }

    private static async Task KillProcessAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (ArgumentException)
        {
            // The exact process has already exited.
        }

        Assert.IsFalse(IsProcessRunning(processId));
    }

    private static string FindRecoveryCrashProbe() =>
        FindExecutable(
            "RainArchives.RecoveryCrashProbe.exe",
            "The integration test output does not contain the recovery crash probe.");

    private static string FindWorker() =>
        FindExecutable(
            "RainArchives.Worker.exe",
            "The integration test output does not contain RainArchives.Worker.exe.");

    private static string FindExecutable(string fileName, string missingMessage)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(executable)
            ? executable
            : throw new FileNotFoundException(missingMessage, executable);
    }

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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ArchiveCreationRecoveryDispositionCrashTests",
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
}
