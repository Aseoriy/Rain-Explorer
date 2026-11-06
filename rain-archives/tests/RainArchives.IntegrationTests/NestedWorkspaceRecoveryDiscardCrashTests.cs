using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;
using RainArchives.Security;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class NestedWorkspaceRecoveryDiscardCrashTests
{
    private const string CheckpointConfigurationFileName =
        ".rainarchives-nested-recovery-discard-checkpoint";
    private const string CheckpointReadyFileName =
        ".rainarchives-nested-recovery-discard-checkpoint.ready";

    [TestMethod]
    [Timeout(45_000)]
    public async Task WorkerIpcHardKillAfterFirstExactDeletePreservesPartialTreeAsSuspicious()
    {
        using var root = new TemporaryDirectory();
        var workerTemporaryRoot = Path.Combine(root.Path, "worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        var recoveryId = new string('a', 64);
        var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + recoveryId;
        var workspacePath = Path.Combine(workerTemporaryRoot, workspaceName);
        Directory.CreateDirectory(workspacePath);
        var ownerPath = Path.Combine(
            workspacePath,
            OwnedNestedTemporaryWorkspace.OwnerFileName);
        await File.WriteAllBytesAsync(
            ownerPath,
            OwnedNestedTemporaryWorkspace.CreateOwnerRecord(workspaceName));
        var sourceDirectory = Path.Combine(
            workspacePath,
            OwnedTemporaryArchiveSource.SourceDirectoryPrefix + new string('b', 64));
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(
            sourceDirectory,
            OwnedTemporaryArchiveSource.SourceFileName);
        await File.WriteAllBytesAsync(sourcePath, [0x01, 0x02, 0x03, 0x04]);

        var checkpoint = NestedTemporaryWorkspaceRecoveryStore
            .NestedWorkspaceRecoveryDiscardCheckpoint
            .FirstArtifactMutationApplied;
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
            var inspection = await interruptedClient
                .InspectNestedWorkspaceRecoveryAsync(recoveryId);
            Assert.AreEqual(
                NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
                inspection.Assessment);
            var discardTask = interruptedClient.DiscardNestedWorkspaceRecoveryAsync(
                recoveryId,
                inspection.Revision!,
                inspection.SourceCount!.Value,
                inspection.TotalSourceBytes!.Value);
            await WaitForCheckpointAsync(readyPath, checkpoint);

            Assert.IsFalse(File.Exists(sourcePath));
            Assert.IsTrue(Directory.Exists(sourceDirectory));
            Assert.IsTrue(File.Exists(ownerPath));
            Assert.IsTrue(Directory.Exists(workspacePath));
            await KillProcessAsync(interruptedWorkerId);

            var disconnected = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await discardTask.WaitAsync(TimeSpan.FromSeconds(10)));
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

        await using var recoveryClient = await ArchiveClient.StartWithTemporaryRootAsync(
            FindWorker(),
            workerTemporaryRoot);
        var inventory = await recoveryClient.ListNestedWorkspaceRecoveriesAsync();
        Assert.HasCount(0, inventory.Recoveries);
        Assert.AreEqual(0, inventory.ActiveWorkspaceCount);
        Assert.AreEqual(1, inventory.SuspiciousWorkspaceCount);
        var partial = await recoveryClient.InspectNestedWorkspaceRecoveryAsync(recoveryId);
        Assert.AreEqual(
            NestedWorkspaceRecoveryAssessment.UnexpectedContents,
            partial.Assessment);
        Assert.IsTrue(Directory.Exists(workspacePath));
        Assert.IsTrue(Directory.Exists(sourceDirectory));
        Assert.IsTrue(File.Exists(ownerPath));
        Assert.IsFalse(File.Exists(sourcePath));
    }

    private static async Task WaitForCheckpointAsync(
        string readyPath,
        NestedTemporaryWorkspaceRecoveryStore.NestedWorkspaceRecoveryDiscardCheckpoint checkpoint)
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

        throw new TimeoutException("The nested-recovery discard checkpoint was not reached.");
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
                "RainArchives.NestedWorkspaceRecoveryDiscardCrashTests",
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
