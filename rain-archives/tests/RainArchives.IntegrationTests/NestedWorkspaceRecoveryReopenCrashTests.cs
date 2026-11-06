using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;
using RainArchives.Security;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class NestedWorkspaceRecoveryReopenCrashTests
{
    private const string CheckpointConfigurationFileName =
        ".rainarchives-nested-recovery-discard-checkpoint";
    private const string CheckpointReadyFileName =
        ".rainarchives-nested-recovery-discard-checkpoint.ready";

    [TestMethod]
    [Timeout(60_000)]
    public async Task WorkerIpcHardKillAfterReopenSourceCopyPreservesOriginalAndCrashResidue()
    {
        using var root = new TemporaryDirectory();
        var workerTemporaryRoot = Path.Combine(root.Path, "worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        var recoveryId = new string('c', 64);
        var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + recoveryId;
        var workspacePath = Path.Combine(workerTemporaryRoot, workspaceName);
        Directory.CreateDirectory(workspacePath);
        var ownerPath = Path.Combine(workspacePath, OwnedNestedTemporaryWorkspace.OwnerFileName);
        await File.WriteAllBytesAsync(
            ownerPath,
            OwnedNestedTemporaryWorkspace.CreateOwnerRecord(workspaceName));

        var sourceDirectory = Path.Combine(
            workspacePath,
            OwnedTemporaryArchiveSource.SourceDirectoryPrefix + new string('d', 64));
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, OwnedTemporaryArchiveSource.SourceFileName);
        var originalBytes = CreateArchiveBytes();
        await File.WriteAllBytesAsync(sourcePath, originalBytes);
        var originalHash = SHA256.HashData(originalBytes);

        var checkpoint = NestedTemporaryWorkspaceRecoveryStore
            .NestedWorkspaceRecoveryDiscardCheckpoint
            .IndependentReopenSourceCopiedAndFlushed;
        await File.WriteAllTextAsync(
            Path.Combine(workerTemporaryRoot, CheckpointConfigurationFileName),
            checkpoint.ToString());
        var readyPath = Path.Combine(workerTemporaryRoot, CheckpointReadyFileName);

        var interruptedClient = await ArchiveClient.StartWithTemporaryRootAsync(
            FindRecoveryCrashProbe(),
            workerTemporaryRoot);
        var interruptedWorkerId = interruptedClient.WorkerProcessId;
        InspectNestedWorkspaceReopenResponse before;
        try
        {
            before = await interruptedClient.InspectNestedWorkspaceReopenAsync(recoveryId);
            Assert.AreEqual(NestedWorkspaceRecoveryAssessment.VerifiedDisposable, before.Assessment);
            Assert.IsNotNull(before.Revision);
            Assert.IsNotNull(before.Sources);
            Assert.HasCount(1, before.Sources);
            var source = before.Sources[0];
            var reopenTask = interruptedClient.ReopenNestedWorkspaceRecoveryAsync(
                recoveryId,
                before.Revision!,
                before.SourceCount!.Value,
                before.TotalSourceBytes!.Value,
                source.SourceId,
                source.Length,
                source.ContentSha256);
            await WaitForCheckpointAsync(readyPath, checkpoint);

            // The source copy has been flushed, but CompleteAsync and session registration
            // have not run. Killing the exact owned Worker must not create reopen authority.
            await KillProcessAsync(interruptedWorkerId);
            var disconnected = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await reopenTask.WaitAsync(TimeSpan.FromSeconds(10)));
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

        Assert.IsTrue(File.Exists(sourcePath));
        CollectionAssert.AreEqual(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)));
        var workspacesAfterCrash = Directory
            .EnumerateDirectories(
                workerTemporaryRoot,
                OwnedNestedTemporaryWorkspace.WorkspacePrefix + "*",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.HasCount(
            2,
            workspacesAfterCrash,
            "The exact crash point must preserve the original recovery and the incomplete copy workspace.");
        CollectionAssert.Contains(workspacesAfterCrash, Path.GetFullPath(workspacePath));
        var incompleteCopyWorkspace = workspacesAfterCrash.Single(path =>
            !string.Equals(path, Path.GetFullPath(workspacePath), StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(Directory.Exists(incompleteCopyWorkspace));
        Assert.IsTrue(OwnedNestedTemporaryWorkspace.TryGetToken(
            Path.GetFileName(incompleteCopyWorkspace),
            out var incompleteCopyRecoveryId));

        await using var recoveryClient = await ArchiveClient.StartWithTemporaryRootAsync(
            FindWorker(),
            workerTemporaryRoot);
        var inventory = await recoveryClient.ListNestedWorkspaceRecoveriesAsync();
        CollectionAssert.AreEquivalent(
            new[] { recoveryId, incompleteCopyRecoveryId },
            inventory.Recoveries.Select(recovery => recovery.RecoveryId).ToArray(),
            "Restart inventory must preserve both the original and the unclaimed copied source without inferring a successful reopen.");
        Assert.AreEqual(0, inventory.SuspiciousWorkspaceCount);
        Assert.IsTrue(Directory.Exists(incompleteCopyWorkspace));
        var copiedSourceRecovery = await recoveryClient
            .InspectNestedWorkspaceReopenAsync(incompleteCopyRecoveryId);
        Assert.AreEqual(
            NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
            copiedSourceRecovery.Assessment);
        var copiedSources = copiedSourceRecovery.Sources
            ?? throw new AssertFailedException("The copied recovery omitted source evidence.");
        var originalSource = before.Sources![0];
        Assert.HasCount(1, copiedSources);
        Assert.AreEqual(originalSource.Length, copiedSources[0].Length);
        Assert.AreEqual(
            originalSource.ContentSha256,
            copiedSources[0].ContentSha256);

        var warningSource = await recoveryClient.OpenArchiveAsync(
            FixturePath("archives", "zip", "plain.zip"));
        Assert.IsTrue(warningSource.Archive.Warnings.Any(warning =>
            warning.Code == "worker.nested_recovery_required"));
        Assert.IsFalse(warningSource.Archive.Warnings.Any(warning =>
            warning.Message.Contains(workerTemporaryRoot, StringComparison.OrdinalIgnoreCase)));
        await recoveryClient.CloseArchiveAsync(warningSource.Archive.SessionId);

        var after = await recoveryClient.InspectNestedWorkspaceReopenAsync(recoveryId);
        Assert.AreEqual(before.Assessment, after.Assessment);
        Assert.AreEqual(before.Revision, after.Revision);
        Assert.AreEqual(before.SourceCount, after.SourceCount);
        Assert.AreEqual(before.TotalSourceBytes, after.TotalSourceBytes);
        Assert.AreEqual(before.Sources![0].SourceId, after.Sources![0].SourceId);
        Assert.AreEqual(before.Sources[0].Length, after.Sources[0].Length);
        Assert.AreEqual(before.Sources[0].ContentSha256, after.Sources[0].ContentSha256);

        var reopenedSource = after.Sources[0];
        var reopened = await recoveryClient.ReopenNestedWorkspaceRecoveryAsync(
            recoveryId,
            after.Revision!,
            after.SourceCount!.Value,
            after.TotalSourceBytes!.Value,
            reopenedSource.SourceId,
            reopenedSource.Length,
            reopenedSource.ContentSha256);
        Assert.AreEqual(0, reopened.Depth);
        Assert.AreEqual(reopenedSource.Length, reopened.MaterializedBytes);
        var entries = await recoveryClient.ListEntriesAsync(reopened.Archive.SessionId);
        Assert.IsTrue(entries.Entries.Any(entry => entry.RawPath == "payload.txt"));
        await recoveryClient.CloseArchiveAsync(reopened.Archive.SessionId);
    }

    private static byte[] CreateArchiveBytes()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("payload.txt", CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
            writer.Write("nested recovery crash proof");
        }

        return stream.ToArray();
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

        throw new TimeoutException("The nested-recovery reopen checkpoint was not reached.");
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

    private static string FindRecoveryCrashProbe() => FindExecutable(
        "RainArchives.RecoveryCrashProbe.exe",
        "The integration test output does not contain the recovery crash probe.");

    private static string FindWorker() => FindExecutable(
        "RainArchives.Worker.exe",
        "The integration test output does not contain RainArchives.Worker.exe.");

    private static string FindExecutable(string fileName, string missingMessage)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(executable)
            ? executable
            : throw new FileNotFoundException(missingMessage, executable);
    }

    private static string FixturePath(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "fixtures", "corpus", .. parts]);

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
                "RainArchives.NestedWorkspaceRecoveryReopenCrashTests",
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
