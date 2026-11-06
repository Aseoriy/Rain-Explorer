using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;
using RainArchives.Security;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class ZipRebuildCopyEndToEndTests
{
    [TestMethod]
    [Timeout(180_000)]
    public async Task RealWorkerRebuildsMixedRenameCopyForEveryPresetAndRoundTripsIt()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.zip");
        var replacement = Path.Combine(root.Path, "replacement.txt");
        var addition = Path.Combine(root.Path, "addition.txt");
        var keepBytes = "retained payload"u8.ToArray();
        var oldReplacementBytes = "old replacement"u8.ToArray();
        var deleteBytes = "delete me"u8.ToArray();
        var replacementBytes = "new replacement"u8.ToArray();
        var additionBytes = "雨 addition"u8.ToArray();
        CreateZip(
            source,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["empty/"] = [],
                ["keep.txt"] = keepBytes,
                ["replace.txt"] = oldReplacementBytes,
                ["delete.txt"] = deleteBytes,
                ["zero.bin"] = [],
            });
        await File.WriteAllBytesAsync(replacement, replacementBytes);
        await File.WriteAllBytesAsync(addition, additionBytes);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["empty/"] = [],
            ["Moved/keep.txt"] = keepBytes,
            ["replace.txt"] = replacementBytes,
            ["Unicode/雨.txt"] = additionBytes,
            ["zero.bin"] = [],
        };

        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.ZipRebuildCopy));
        var opened = await client.OpenArchiveAsync(source);
        Assert.IsTrue(opened.Archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite));
        var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 64);
        var delete = listed.Entries.Single(entry => entry.RawPath == "delete.txt");
        var keep = listed.Entries.Single(entry => entry.RawPath == "keep.txt");
        Assert.IsNotNull(delete.NormalizedPath);
        Assert.IsNotNull(keep.NormalizedPath);

        foreach (var preset in Enum.GetValues<ArchiveCompressionPreset>())
        {
            var destination = Path.Combine(root.Path, $"rebuilt-{preset}.zip");
            var start = await client.StartZipRebuildRenameAsync(
                opened.Archive.SessionId,
                destination,
                [new ArchiveEntryKey(delete.NormalizedPath!, delete.DuplicateOrdinal)],
                [
                    new ArchiveRebuildCopyFile(replacement, "REPLACE.TXT"),
                    new ArchiveRebuildCopyFile(addition, "Unicode/雨.txt"),
                ],
                [new ArchiveRebuildRename(
                    new ArchiveEntryKey(keep.NormalizedPath!, keep.DuplicateOrdinal),
                    "Moved/keep.txt")],
                preset);
            var terminal = await client
                .WaitForTerminalAsync(start.OperationId)
                .WaitAsync(TimeSpan.FromSeconds(60));

            Assert.AreEqual(
                OperationPhase.CompletedWithWarnings,
                terminal.Phase,
                $"{preset}: {terminal.Error?.Code} ({terminal.Error?.Category}) {terminal.Error?.Message}");
            Assert.IsNull(terminal.Error, preset.ToString());
            Assert.AreEqual("zip.rebuild_metadata_limited", terminal.Warning?.Code, preset.ToString());
            CollectionAssert.AreEqual(
                sourceHash,
                SHA256.HashData(await File.ReadAllBytesAsync(source)),
                preset.ToString());
            AssertZip(destination, expected);
            AssertNoStagingFiles(root.Path);

            var rebuilt = await client.OpenArchiveAsync(destination);
            var rebuiltList = await client.ListEntriesAsync(rebuilt.Archive.SessionId, 0, 64);
            CollectionAssert.AreEquivalent(
                expected.Keys.ToArray(),
                rebuiltList.Entries
                    .Where(entry => entry.ProviderOrdinal >= 0)
                    .Select(entry => entry.RawPath)
                    .ToArray(),
                preset.ToString());
            var test = await client.StartIntegrityTestAsync(rebuilt.Archive.SessionId, []);
            var testTerminal = await client.WaitForTerminalAsync(test.OperationId);
            Assert.AreEqual(OperationPhase.Completed, testTerminal.Phase, preset.ToString());

            var extraction = Path.Combine(root.Path, $"extracted-{preset}");
            var extract = await client.StartExtractionAsync(
                rebuilt.Archive.SessionId,
                [],
                extraction);
            var extractTerminal = await client.WaitForTerminalAsync(extract.OperationId);
            Assert.AreEqual(
                OperationPhase.Completed,
                extractTerminal.Phase,
                $"{preset}: {extractTerminal.Error?.Message}");
            foreach (var expectedFile in expected.Where(item => !item.Key.EndsWith('/')))
            {
                CollectionAssert.AreEqual(
                    expectedFile.Value,
                    await File.ReadAllBytesAsync(
                        Path.Combine(
                            extraction,
                            expectedFile.Key.Replace('/', Path.DirectorySeparatorChar))),
                    $"{preset}: {expectedFile.Key}");
            }

            Assert.IsTrue(Directory.Exists(Path.Combine(extraction, "empty")), preset.ToString());
            Assert.IsTrue((await client.CloseArchiveAsync(rebuilt.Archive.SessionId)).Closed);
        }

        Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task CancellationAfterProgressPublishesNothingAndWorkerRemainsUsable()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "large-source.zip");
        var payload = new byte[32 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        CreateZip(
            source,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["large.bin"] = payload,
            },
            CompressionLevel.NoCompression);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var cancelledDestination = Path.Combine(root.Path, "cancelled.zip");

        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());
        var opened = await client.OpenArchiveAsync(source);
        Assert.IsTrue(opened.Archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite));
        var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 8);
        var large = listed.Entries.Single(entry => entry.RawPath == "large.bin");
        Assert.IsNotNull(large.NormalizedPath);
        var progressSeen = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new ConcurrentQueue<OperationEvent>();
        OperationId operationId = default;
        client.OperationEventReceived += (_, operationEvent) =>
        {
            events.Enqueue(operationEvent);
            if (operationEvent.OperationId == operationId
                && operationEvent.Kind == OperationEventKind.Progress
                && operationEvent.Progress is { BytesProcessed: > 0 })
            {
                progressSeen.TrySetResult(true);
            }
        };

        var start = await client.StartZipRebuildRenameAsync(
            opened.Archive.SessionId,
            cancelledDestination,
            [],
            [],
            [new ArchiveRebuildRename(
                new ArchiveEntryKey(large.NormalizedPath!, large.DuplicateOrdinal),
                "moved/large.bin")],
            ArchiveCompressionPreset.Maximum);
        operationId = start.OperationId;
        await progressSeen.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var cancellation = await client.CancelOperationAsync(start.OperationId);
        Assert.IsTrue(cancellation.CancellationRequested);
        var terminal = await client
            .WaitForTerminalAsync(start.OperationId)
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.AreEqual(OperationPhase.Cancelled, terminal.Phase);
        Assert.IsFalse(File.Exists(cancelledDestination));
        AssertNoStagingFiles(root.Path);
        CollectionAssert.AreEqual(
            sourceHash,
            SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.IsTrue(events.Any(operationEvent =>
            operationEvent.OperationId == start.OperationId
            && operationEvent.Kind == OperationEventKind.Progress));

        var recoveryDestination = Path.Combine(root.Path, "after-cancel.zip");
        var recovery = await client.StartZipRebuildRenameAsync(
            opened.Archive.SessionId,
            recoveryDestination,
            [],
            [],
            [new ArchiveRebuildRename(
                new ArchiveEntryKey(large.NormalizedPath!, large.DuplicateOrdinal),
                "moved/large.bin")],
            ArchiveCompressionPreset.Store);
        var recoveryTerminal = await client.WaitForTerminalAsync(recovery.OperationId);
        Assert.AreEqual(
            OperationPhase.CompletedWithWarnings,
            recoveryTerminal.Phase,
            recoveryTerminal.Error?.Message);
        AssertZip(
            recoveryDestination,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["moved/large.bin"] = payload,
            });
        CollectionAssert.AreEqual(
            sourceHash,
            SHA256.HashData(await File.ReadAllBytesAsync(source)));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task AmbiguousDuplicateZipNeverAdvertisesOrStartsRebuild()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "duplicates.zip");
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "same.txt", "one"u8.ToArray(), CompressionLevel.NoCompression);
            WriteEntry(archive, "same.txt", "two"u8.ToArray(), CompressionLevel.NoCompression);
        }

        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var destination = Path.Combine(root.Path, "must-not-exist.zip");
        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());
        var opened = await client.OpenArchiveAsync(source);
        Assert.IsFalse(opened.Archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.StartZipRebuildCopyAsync(
                opened.Archive.SessionId,
                destination,
                [],
                []));

        Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, failure.Error.Category);
        Assert.AreEqual("zip.rebuild_source_not_eligible", failure.Error.Code);
        Assert.IsFalse(File.Exists(destination));
        CollectionAssert.AreEqual(
            sourceHash,
            SHA256.HashData(await File.ReadAllBytesAsync(source)));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task HardKilledWorkerPublishesNothingAndLeavesReviewedCreationRecoveryState()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.zip");
        var destination = Path.Combine(root.Path, "crashed-rebuild.zip");
        var workerTemporaryRoot = Path.Combine(root.Path, "worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        var payload = new byte[64 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        CreateZip(
            source,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["large.bin"] = payload,
            },
            CompressionLevel.NoCompression);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var killedWorkerId = 0;
        var operationId = default(OperationId);

        await using (var client = await ArchiveClient.StartWithTemporaryRootAsync(
                         GetWorkerPath(),
                         workerTemporaryRoot))
        {
            killedWorkerId = client.WorkerProcessId;
            using var worker = Process.GetProcessById(killedWorkerId);
            var opened = await client.OpenArchiveAsync(source);
            var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 8);
            var large = listed.Entries.Single(entry => entry.RawPath == "large.bin");
            Assert.IsNotNull(large.NormalizedPath);
            var firstProgress = new TaskCompletionSource<OperationEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            client.OperationEventReceived += (_, operationEvent) =>
            {
                if (operationEvent.Kind == OperationEventKind.Progress
                    && operationEvent.Progress is { BytesProcessed: > 0 })
                {
                    firstProgress.TrySetResult(operationEvent);
                }
            };

            var started = await client.StartZipRebuildRenameAsync(
                opened.Archive.SessionId,
                destination,
                [],
                [],
                [new ArchiveRebuildRename(
                    new ArchiveEntryKey(large.NormalizedPath!, large.DuplicateOrdinal),
                    "moved/large.bin")],
                ArchiveCompressionPreset.Maximum);
            operationId = started.OperationId;
            var progress = await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(started.OperationId, progress.OperationId);
            Assert.IsTrue(progress.Progress!.BytesProcessed < payload.Length);

            Assert.AreEqual(
                0,
                NativeMethods.NtSuspendProcess(worker.Handle),
                "The test could not suspend its exact Worker during ZIP Rebuild Copy.");
            var suspended = true;
            try
            {
                Assert.IsFalse(File.Exists(destination));
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

        Assert.IsFalse(IsProcessRunning(killedWorkerId));
        Assert.IsFalse(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
        CollectionAssert.AreEqual(
            sourceHash,
            SHA256.HashData(await File.ReadAllBytesAsync(source)));
        var recoveryRoot = Path.Combine(
            workerTemporaryRoot,
            "rainarchives-creation-recovery");
        var inventory = new ArchiveCreationRecoveryStore(recoveryRoot).Inspect();
        Assert.AreEqual(1, inventory.PotentialRecoveryCount);
        Assert.AreEqual(0, inventory.SuspiciousOperationCount);
        var record = inventory.RecoveryRecords.Single();
        Assert.AreEqual(operationId, record.OperationId);
        Assert.AreEqual(destination, record.DestinationPath);
        Assert.AreEqual(ArchiveCreationRecoveryPhase.Writing, record.Phase);
        Assert.IsNotNull(record.StagingFileIdentity);
        Assert.IsNull(record.StagingLength);

        await using (var recoveryClient = await ArchiveClient.StartWithTemporaryRootAsync(
                         GetWorkerPath(),
                         workerTemporaryRoot))
        {
            var reopened = await recoveryClient.OpenArchiveAsync(source);
            var warning = reopened.Archive.Warnings.Single(
                candidate => candidate.Code == "worker.creation_recovery_required");
            Assert.IsTrue(warning.Message.Contains("1", StringComparison.Ordinal));
            Assert.IsFalse(warning.Message.Contains(root.Path, StringComparison.OrdinalIgnoreCase));
            await recoveryClient.CloseArchiveAsync(reopened.Archive.SessionId);
        }

        Assert.AreEqual(1, new ArchiveCreationRecoveryStore(recoveryRoot).Inspect().PotentialRecoveryCount);
        Assert.IsFalse(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
    }

    private static void CreateZip(
        string path,
        IReadOnlyDictionary<string, byte[]> entries,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            if (entry.Key.EndsWith('/'))
            {
                _ = archive.CreateEntry(entry.Key, CompressionLevel.NoCompression);
                continue;
            }

            WriteEntry(archive, entry.Key, entry.Value, compressionLevel);
        }
    }

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        byte[] payload,
        CompressionLevel compressionLevel)
    {
        var entry = archive.CreateEntry(path, compressionLevel);
        using var entryStream = entry.Open();
        entryStream.Write(payload);
    }

    private static void AssertZip(
        string path,
        IReadOnlyDictionary<string, byte[]> expected)
    {
        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        CollectionAssert.AreEqual(
            expected.Keys.Order(StringComparer.Ordinal).ToArray(),
            archive.Entries.Select(entry => entry.FullName).ToArray());
        foreach (var entry in archive.Entries)
        {
            using var entryStream = entry.Open();
            using var payload = new MemoryStream();
            entryStream.CopyTo(payload);
            CollectionAssert.AreEqual(expected[entry.FullName], payload.ToArray(), entry.FullName);
        }
    }

    private static void AssertNoStagingFiles(string parent) =>
        Assert.HasCount(
            0,
            Directory.GetFiles(parent, ".rainarchives-*.zip.staging", SearchOption.TopDirectoryOnly));

    private static string GetWorkerPath()
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
                "RainArchives.ZipRebuildCopyEndToEndTests",
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
