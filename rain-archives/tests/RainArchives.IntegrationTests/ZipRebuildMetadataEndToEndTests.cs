using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;
using RainArchives.Security;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class ZipRebuildMetadataEndToEndTests
{
    private const string OfficialSevenZipSha256 =
        "35D4D69D7CD6CB44558F208C3B1334268013F9DAF82D2DDA848893A1C30C59C2";

    [TestMethod]
    [Timeout(180_000)]
    public async Task RealWorkerAppliesMetadataToRetainedAndMappedEntriesAfterNamespaceEdits()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.zip");
        var destination = Path.Combine(root.Path, "metadata-rebuilt.zip");
        var importedRoot = Path.Combine(root.Path, "imported-source");
        Directory.CreateDirectory(Path.Combine(importedRoot, "empty"));
        var importedBytes = "mapped directory payload"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(importedRoot, "imported.txt"), importedBytes);

        var retainedBytes = "retained through rename"u8.ToArray();
        var replacementBytes = "replacement payload"u8.ToArray();
        var additionBytes = Encoding.UTF8.GetBytes("new mapped payload 雨");
        var movedBytes = "tree move payload"u8.ToArray();
        CreateZip(
            source,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["keep.txt"] = retainedBytes,
                ["replace.txt"] = "old replacement"u8.ToArray(),
                ["delete.txt"] = "delete"u8.ToArray(),
                ["tree/"] = [],
                ["tree/moved.txt"] = movedBytes,
                ["empty/"] = [],
            });
        var replacementSource = Path.Combine(root.Path, "replacement.txt");
        var additionSource = Path.Combine(root.Path, "addition.txt");
        await File.WriteAllBytesAsync(replacementSource, replacementBytes);
        await File.WriteAllBytesAsync(additionSource, additionBytes);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));

        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.ZipRebuildMetadata));
        var opened = await client.OpenArchiveAsync(source);
        var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 64);
        var plan = await client.GetZipRebuildTreePlanAsync(opened.Archive.SessionId);
        var tree = plan.Directories.Single(node => node.NormalizedPath == "tree");
        var keep = listed.Entries.Single(entry => entry.RawPath == "keep.txt");
        var delete = listed.Entries.Single(entry => entry.RawPath == "delete.txt");
        Assert.IsNotNull(keep.NormalizedPath);
        Assert.IsNotNull(delete.NormalizedPath);

        var metadata = new ArchiveRebuildMetadataDirective[]
        {
            new(
                new ArchiveRebuildMetadataTarget(
                    ArchiveRebuildMetadataTargetKind.RetainedEntryKey,
                    RetainedEntryKey: new ArchiveEntryKey(keep.NormalizedPath!, keep.DuplicateOrdinal)),
                new ArchiveDosLocalTimestamp(2107, 12, 31, 23, 59, 58),
                0x0021),
            new(
                new ArchiveRebuildMetadataTarget(
                    ArchiveRebuildMetadataTargetKind.MappedFinalPath,
                    MappedFinalArchivePath: "new/add.txt",
                    MappedKind: ArchiveEntryKind.File),
                new ArchiveDosLocalTimestamp(1980, 1, 1, 0, 0, 0),
                0x0006),
            new(
                new ArchiveRebuildMetadataTarget(
                    ArchiveRebuildMetadataTargetKind.MappedFinalPath,
                    MappedFinalArchivePath: "imported",
                    MappedKind: ArchiveEntryKind.Directory),
                new ArchiveDosLocalTimestamp(2024, 6, 15, 12, 34, 56),
                0x0020),
        };

        var started = await client.StartZipRebuildMetadataAsync(
            opened.Archive.SessionId,
            destination,
            [new ArchiveEntryKey(delete.NormalizedPath!, delete.DuplicateOrdinal)],
            [
                new ArchiveRebuildCopyFile(replacementSource, "replace.txt"),
                new ArchiveRebuildCopyFile(additionSource, "new/add.txt"),
            ],
            [new ArchiveRebuildRename(
                new ArchiveEntryKey(keep.NormalizedPath!, keep.DuplicateOrdinal),
                "renamed/keep.txt")],
            [new ArchiveCreationDirectory(importedRoot, "imported")],
            [new ArchiveRebuildTreeAction(
                tree.NormalizedPath,
                tree.SubtreeRevision,
                ArchiveRebuildTreeActionKind.Move,
                "moved")],
            metadata,
            ArchiveCompressionPreset.Balanced);
        var terminal = await client.WaitForTerminalAsync(started.OperationId)
            .WaitAsync(TimeSpan.FromSeconds(90));

        Assert.IsTrue(
            terminal.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings,
            $"{terminal.Phase}: {terminal.Error?.Code} {terminal.Error?.Message}");
        Assert.IsNull(terminal.Error);
        Assert.IsTrue(File.Exists(destination));
        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        AssertZipContents(
            destination,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["empty/"] = [],
                ["imported/"] = [],
                ["imported/empty/"] = [],
                ["imported/imported.txt"] = importedBytes,
                ["moved/"] = [],
                ["moved/moved.txt"] = movedBytes,
                ["new/"] = [],
                ["new/add.txt"] = additionBytes,
                ["replace.txt"] = replacementBytes,
                ["renamed/"] = [],
                ["renamed/keep.txt"] = retainedBytes,
            });
        AssertRawZipMetadata(
            destination,
            "renamed/keep.txt",
            new ArchiveDosLocalTimestamp(2107, 12, 31, 23, 59, 58),
            0x0021);
        AssertRawZipMetadata(
            destination,
            "new/add.txt",
            new ArchiveDosLocalTimestamp(1980, 1, 1, 0, 0, 0),
            0x0006);
        AssertRawZipMetadata(
            destination,
            "imported/",
            new ArchiveDosLocalTimestamp(2024, 6, 15, 12, 34, 56),
            0x0030);

        var rebuilt = await client.OpenArchiveAsync(destination);
        var rebuiltList = await client.ListEntriesAsync(rebuilt.Archive.SessionId, 0, 64);
        AssertMetadata(
            rebuiltList.Entries.Single(entry => entry.RawPath == "renamed/keep.txt"),
            0x0021,
            ArchiveEntryKind.File);
        AssertMetadata(
            rebuiltList.Entries.Single(entry => entry.RawPath == "new/add.txt"),
            0x0006,
            ArchiveEntryKind.File);
        AssertMetadata(
            rebuiltList.Entries.Single(entry => entry.RawPath == "imported/"),
            0x0030,
            ArchiveEntryKind.Directory);

        var integrity = await client.StartIntegrityTestAsync(rebuilt.Archive.SessionId, []);
        var integrityTerminal = await client.WaitForTerminalAsync(integrity.OperationId);
        Assert.AreEqual(OperationPhase.Completed, integrityTerminal.Phase);
        var extractionRoot = Path.Combine(root.Path, "rain-extract");
        var extraction = await client.StartExtractionAsync(
            rebuilt.Archive.SessionId,
            [],
            extractionRoot);
        var extractionTerminal = await client.WaitForTerminalAsync(extraction.OperationId);
        Assert.AreEqual(OperationPhase.Completed, extractionTerminal.Phase);
        CollectionAssert.AreEqual(
            importedBytes,
            await File.ReadAllBytesAsync(Path.Combine(extractionRoot, "imported", "imported.txt")));
        CollectionAssert.AreEqual(
            additionBytes,
            await File.ReadAllBytesAsync(Path.Combine(extractionRoot, "new", "add.txt")));
        CollectionAssert.AreEqual(
            retainedBytes,
            await File.ReadAllBytesAsync(Path.Combine(extractionRoot, "renamed", "keep.txt")));
        Assert.IsTrue(Directory.Exists(Path.Combine(extractionRoot, "imported", "empty")));

        var sevenZip = GetOfficialSevenZipPath();
        AssertPinnedSevenZip(sevenZip);
        await RunToolAsync(sevenZip, root.Path, "t", "-bd", "-y", "--", destination);
        var sevenZipListing = await RunToolAsync(sevenZip, root.Path, "l", "-slt", "-bd", "-y", "--", destination);
        AssertSevenZipMetadata(sevenZipListing);
        var externalExtraction = Path.Combine(root.Path, "sevenzip-extract");
        await RunToolAsync(sevenZip, root.Path, "x", "-bd", "-y", $"-o{externalExtraction}", "--", destination);
        CollectionAssert.AreEqual(
            additionBytes,
            await File.ReadAllBytesAsync(Path.Combine(externalExtraction, "new", "add.txt")));
        CollectionAssert.AreEqual(
            retainedBytes,
            await File.ReadAllBytesAsync(Path.Combine(externalExtraction, "renamed", "keep.txt")));

        Assert.IsTrue((await client.CloseArchiveAsync(rebuilt.Archive.SessionId)).Closed);
        Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task CancellationAfterMetadataProgressPublishesNothingAndPreservesOriginal()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "large-source.zip");
        var destination = Path.Combine(root.Path, "cancelled-metadata.zip");
        var payload = new byte[32 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        CreateZip(source, new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["large.bin"] = payload }, CompressionLevel.NoCompression);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));

        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());
        var opened = await client.OpenArchiveAsync(source);
        var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 8);
        var large = listed.Entries.Single(entry => entry.RawPath == "large.bin");
        Assert.IsNotNull(large.NormalizedPath);
        var progress = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationId operationId = default;
        client.OperationEventReceived += (_, operationEvent) =>
        {
            if (operationEvent.OperationId == operationId
                && operationEvent.Kind == OperationEventKind.Progress
                && operationEvent.Progress is { BytesProcessed: > 0 })
            {
                progress.TrySetResult(true);
            }
        };

        var started = await client.StartZipRebuildMetadataAsync(
            opened.Archive.SessionId,
            destination,
            [],
            [],
            [new ArchiveRebuildRename(
                new ArchiveEntryKey(large.NormalizedPath!, large.DuplicateOrdinal),
                "moved/large.bin")],
            [],
            [],
            [new ArchiveRebuildMetadataDirective(
                new ArchiveRebuildMetadataTarget(
                    ArchiveRebuildMetadataTargetKind.RetainedEntryKey,
                    RetainedEntryKey: new ArchiveEntryKey(large.NormalizedPath!, large.DuplicateOrdinal)),
                new ArchiveDosLocalTimestamp(2024, 6, 15, 12, 34, 56),
                0x0020)],
            ArchiveCompressionPreset.Maximum);
        operationId = started.OperationId;
        await progress.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue((await client.CancelOperationAsync(operationId)).CancellationRequested);
        var terminal = await client.WaitForTerminalAsync(operationId).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.AreEqual(OperationPhase.Cancelled, terminal.Phase);
        Assert.IsFalse(File.Exists(destination));
        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task ExactPidCrashDuringMetadataWritingPublishesNothingAndLeavesReviewedRecoveryState()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "crash-metadata-source.zip");
        var destination = Path.Combine(root.Path, "crashed-metadata.zip");
        var workerTemporaryRoot = Path.Combine(root.Path, "worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        var payload = new byte[64 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        CreateZip(
            source,
            new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["large.bin"] = payload },
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

            var started = await client.StartZipRebuildMetadataAsync(
                opened.Archive.SessionId,
                destination,
                [],
                [],
                [new ArchiveRebuildRename(
                    new ArchiveEntryKey(large.NormalizedPath!, large.DuplicateOrdinal),
                    "moved/large.bin")],
                [],
                [],
                [new ArchiveRebuildMetadataDirective(
                    new ArchiveRebuildMetadataTarget(
                        ArchiveRebuildMetadataTargetKind.RetainedEntryKey,
                        RetainedEntryKey: new ArchiveEntryKey(
                            large.NormalizedPath!,
                            large.DuplicateOrdinal)),
                    new ArchiveDosLocalTimestamp(2024, 6, 15, 12, 34, 56),
                    0x0020)],
                ArchiveCompressionPreset.Maximum);
            operationId = started.OperationId;
            var progress = await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(operationId, progress.OperationId);
            Assert.IsTrue(progress.Progress!.BytesProcessed < payload.Length);

            Assert.AreEqual(
                0,
                NativeMethods.NtSuspendProcess(worker.Handle),
                "The test could not suspend its exact Worker during ZIP metadata writing.");
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
                await client.WaitForTerminalAsync(operationId));
            Assert.AreEqual(ArchiveErrorCategory.WorkerFailure, disconnected.Error.Category);
            Assert.AreEqual("worker.disconnected", disconnected.Error.Code);
        }

        Assert.IsFalse(IsProcessRunning(killedWorkerId));
        Assert.IsFalse(File.Exists(destination));
        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        AssertNoStagingFiles(root.Path);

        var recoveryRoot = Path.Combine(workerTemporaryRoot, "rainarchives-creation-recovery");
        var inventory = new ArchiveCreationRecoveryStore(recoveryRoot).Inspect();
        Assert.AreEqual(1, inventory.PotentialRecoveryCount);
        Assert.AreEqual(0, inventory.SuspiciousOperationCount);
        Assert.AreEqual(0, inventory.IncompleteJournalCount);
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
            Assert.IsTrue(reopened.Archive.Warnings.Any(
                warning => warning.Code == "worker.creation_recovery_required"));
            Assert.IsTrue((await recoveryClient.CloseArchiveAsync(reopened.Archive.SessionId)).Closed);
        }

        var afterRestart = new ArchiveCreationRecoveryStore(recoveryRoot).Inspect();
        Assert.AreEqual(1, afterRestart.PotentialRecoveryCount);
        Assert.AreEqual(0, afterRestart.SuspiciousOperationCount);
        Assert.IsFalse(File.Exists(destination));
        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        AssertNoStagingFiles(root.Path);
    }

    private static void AssertMetadata(
        ArchiveEntryDescriptor entry,
        ushort expectedAttributes,
        ArchiveEntryKind expectedKind)
    {
        Assert.AreEqual(expectedKind, entry.Kind, entry.RawPath);
        Assert.IsNotNull(entry.ModifiedTimeUtc, entry.RawPath);
        // Rain's legacy list descriptor exposes a provider-projected UTC value.
        // It proves the entry can be reopened, but it is not raw DOS-time evidence.
        Assert.IsNotNull(entry.WindowsAttributes, entry.RawPath);
        Assert.AreEqual((int)expectedAttributes, entry.WindowsAttributes!.Value & 0x0037, entry.RawPath);
    }

    private static void AssertRawZipMetadata(
        string archivePath,
        string entryPath,
        ArchiveDosLocalTimestamp expectedTimestamp,
        int expectedAttributes)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(entryPath);
        Assert.IsNotNull(entry, entryPath);
        Assert.AreEqual(
            ZipRebuildMetadataPolicy.ToDateTime(expectedTimestamp),
            entry!.LastWriteTime.DateTime,
            entryPath);
        Assert.AreEqual(expectedAttributes, entry.ExternalAttributes, entryPath);
    }

    private static void AssertZipContents(string path, IReadOnlyDictionary<string, byte[]> expected)
    {
        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        CollectionAssert.AreEqual(
            expected.Keys.Order(StringComparer.Ordinal).ToArray(),
            archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToArray());
        foreach (var item in expected)
        {
            var entry = archive.GetEntry(item.Key);
            Assert.IsNotNull(entry, item.Key);
            if (item.Key.EndsWith('/')) continue;
            using var input = entry!.Open();
            using var payload = new MemoryStream();
            input.CopyTo(payload);
            CollectionAssert.AreEqual(item.Value, payload.ToArray(), item.Key);
        }
    }

    private static void CreateZip(
        string path,
        IReadOnlyDictionary<string, byte[]> entries,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Key, compressionLevel);
            if (item.Key.EndsWith('/')) continue;
            using var output = entry.Open();
            output.Write(item.Value);
        }
    }

    private static void AssertSevenZipMetadata(string listing)
    {
        var records = new Dictionary<string, (string Modified, string Attributes)>(StringComparer.Ordinal);
        string? path = null;
        string? modified = null;
        string? attributes = null;
        void Finish()
        {
            if (path is not null && modified is not null && attributes is not null)
            {
                records[path] = (modified, attributes.Replace("-", string.Empty, StringComparison.Ordinal));
            }
        }

        foreach (var line in listing.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (line.StartsWith("Path = ", StringComparison.Ordinal))
            {
                Finish();
                path = line[7..].Replace('\\', '/');
                modified = null;
                attributes = null;
            }
            else if (line.StartsWith("Modified = ", StringComparison.Ordinal))
            {
                modified = line[11..];
            }
            else if (line.StartsWith("Attributes = ", StringComparison.Ordinal))
            {
                attributes = line[13..];
            }
        }

        Finish();
        Assert.IsTrue(records.TryGetValue("renamed/keep.txt", out var retained));
        Assert.AreEqual("2107-12-31 23:59:58", retained.Modified.Split('.', 2)[0]);
        StringAssert.Contains(retained.Attributes, "R");
        StringAssert.Contains(retained.Attributes, "A");
        Assert.IsTrue(records.TryGetValue("new/add.txt", out var mapped));
        Assert.AreEqual("1980-01-01 00:00:00", mapped.Modified.Split('.', 2)[0]);
        StringAssert.Contains(mapped.Attributes, "H");
        StringAssert.Contains(mapped.Attributes, "S");
    }

    private static async Task<string> RunToolAsync(string executable, string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not launch official 7-Zip.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        var output = await stdout;
        var error = await stderr;
        Assert.AreEqual(0, process.ExitCode, $"7-Zip failed: {error}\n{output}");
        return output;
    }

    private static void AssertPinnedSevenZip(string path)
    {
        Assert.IsTrue(File.Exists(path), path);
        Assert.AreEqual(OfficialSevenZipSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static string GetOfficialSevenZipPath() => Path.Combine(
        Path.GetTempPath(), "RainArchives.Tooling", "7zip-26.02", "expanded", "x64", "7za.exe");

    private static string GetWorkerPath()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        if (!File.Exists(worker)) throw new FileNotFoundException("RainArchives.Worker.exe was not found.", worker);
        return worker;
    }

    private static void AssertNoStagingFiles(string parent) =>
        Assert.HasCount(0, Directory.GetFiles(parent, ".rainarchives-*.zip.staging", SearchOption.TopDirectoryOnly));

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
                "RainArchives.ZipRebuildMetadataEndToEndTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            foreach (var directory in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories)
                         .OrderByDescending(value => value.Length))
            {
                File.SetAttributes(directory, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
    }

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054
        [DllImport("ntdll.dll")]
        public static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        public static extern int NtResumeProcess(IntPtr processHandle);
#pragma warning restore SYSLIB1054
    }
}
