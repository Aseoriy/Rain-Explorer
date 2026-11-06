using System.Diagnostics;
using System.Collections.Concurrent;
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
public sealed class ZipRebuildTreeEditEndToEndTests
{
    private const string OfficialSevenZipSha256 =
        "35D4D69D7CD6CB44558F208C3B1334268013F9DAF82D2DDA848893A1C30C59C2";
    private const string WindowsBsdtarSha256 =
        "9B77D4C912F2EDAE8C241D0ECE1094D2AC068B084269CEAF85D7C7B085D2AE86";

    [TestMethod]
    [Timeout(240_000)]
    public async Task RealWorkerAppliesClosedMixedTreePlanForEveryPresetAndExternalToolsAgree()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.zip");
        var replacementSource = Path.Combine(root.Path, "replacement.txt");
        var additionSource = Path.Combine(root.Path, "addition.txt");
        var folderSource = Path.Combine(root.Path, "folder-source");
        var folderNested = Path.Combine(folderSource, "deep");
        Directory.CreateDirectory(Path.Combine(folderSource, "empty"));
        Directory.CreateDirectory(folderNested);

        var nestedBytes = "retained nested"u8.ToArray();
        var renamedBytes = "retained renamed"u8.ToArray();
        var replacementBytes = "replacement after move"u8.ToArray();
        var additionBytes = "explicit mapped addition"u8.ToArray();
        var importedBytes = Encoding.UTF8.GetBytes("mapped tree payload 雨");
        var outsideBytes = "outside"u8.ToArray();
        var syntheticBytes = "synthetic parent"u8.ToArray();
        await File.WriteAllBytesAsync(replacementSource, replacementBytes);
        await File.WriteAllBytesAsync(additionSource, additionBytes);
        var importedSource = Path.Combine(folderNested, "雨.txt");
        await File.WriteAllBytesAsync(importedSource, importedBytes);
        await File.WriteAllTextAsync(
            importedSource + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\n");
        CreateSourceZip(source, nestedBytes, renamedBytes, outsideBytes, syntheticBytes);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));

        var expectedFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Imported/deep/雨.txt"] = importedBytes,
            ["Moved/nested/keep.txt"] = nestedBytes,
            ["Moved/replace.txt"] = replacementBytes,
            ["New/added.txt"] = additionBytes,
            ["Overrides/renamed.txt"] = renamedBytes,
            ["outside.txt"] = outsideBytes,
            ["synthetic/leaf.txt"] = syntheticBytes,
        };
        var expectedDirectories = new[]
        {
            "Imported",
            "Imported/deep",
            "Imported/empty",
            "Moved",
            "Moved/empty",
            "Moved/nested",
            "New",
            "Overrides",
            "synthetic",
        };
        var expectedArchiveManifest = expectedDirectories
            .Select(path => path + "/")
            .Concat(expectedFiles.Keys)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var sevenZip = GetOfficialSevenZipPath();
        AssertPinnedTool(sevenZip, OfficialSevenZipSha256, 1_334_784L);
        var bsdtar = GetWindowsBsdtarPath();
        AssertPinnedTool(bsdtar, WindowsBsdtarSha256);
        var version = await RunToolAsync(bsdtar, root.Path, "--version");
        StringAssert.Contains(version.StandardOutput, "bsdtar 3.8.4");

        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());
        var observedEvents = new ConcurrentQueue<OperationEvent>();
        client.OperationEventReceived += (_, operationEvent) => observedEvents.Enqueue(operationEvent);
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.ZipRebuildTreeEdit));
        var opened = await client.OpenArchiveAsync(source);
        Assert.IsTrue(opened.Archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite));
        var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 64);
        var firstPlan = await client.GetZipRebuildTreePlanAsync(opened.Archive.SessionId);
        var secondPlan = await client.GetZipRebuildTreePlanAsync(opened.Archive.SessionId);
        AssertTreePlan(firstPlan, secondPlan);

        var moveNode = firstPlan.Directories.Single(node => node.NormalizedPath == "move");
        var deleteNode = firstPlan.Directories.Single(node => node.NormalizedPath == "delete-tree");
        var rename = listed.Entries.Single(entry => entry.RawPath == "move/rename-me.txt");
        var deleteFile = listed.Entries.Single(entry => entry.RawPath == "move/remove-me.txt");
        Assert.IsNotNull(rename.NormalizedPath);
        Assert.IsNotNull(deleteFile.NormalizedPath);

        foreach (var preset in Enum.GetValues<ArchiveCompressionPreset>())
        {
            var destination = Path.Combine(root.Path, $"tree-{preset}.zip");
            var started = await client.StartZipRebuildTreeEditAsync(
                opened.Archive.SessionId,
                destination,
                [new ArchiveEntryKey(deleteFile.NormalizedPath!, deleteFile.DuplicateOrdinal)],
                [
                    new ArchiveRebuildCopyFile(replacementSource, "Moved/replace.txt"),
                    new ArchiveRebuildCopyFile(additionSource, "New/added.txt"),
                ],
                [new ArchiveRebuildRename(
                    new ArchiveEntryKey(rename.NormalizedPath!, rename.DuplicateOrdinal),
                    "Overrides/renamed.txt")],
                [new ArchiveCreationDirectory(folderSource, "Imported")],
                [
                    new ArchiveRebuildTreeAction(
                        moveNode.NormalizedPath,
                        moveNode.SubtreeRevision,
                        ArchiveRebuildTreeActionKind.Move,
                        "Moved"),
                    new ArchiveRebuildTreeAction(
                        deleteNode.NormalizedPath,
                        deleteNode.SubtreeRevision,
                        ArchiveRebuildTreeActionKind.Delete),
                ],
                preset);
            var terminal = await client
                .WaitForTerminalAsync(started.OperationId)
                .WaitAsync(TimeSpan.FromSeconds(90));
            Assert.AreEqual(
                OperationPhase.CompletedWithWarnings,
                terminal.Phase,
                $"{preset}: {terminal.Error?.Code} ({terminal.Error?.Category}) {terminal.Error?.Message}");
            Assert.IsNull(terminal.Error, preset.ToString());
            Assert.AreEqual("provenance.source_ambiguous", terminal.Warning?.Code, preset.ToString());
            CollectionAssert.Contains(
                observedEvents
                    .Where(operationEvent => operationEvent.OperationId == started.OperationId)
                    .Select(operationEvent => operationEvent.Warning?.Code)
                    .OfType<string>()
                    .ToArray(),
                "zip.rebuild_metadata_limited",
                preset.ToString());
            CollectionAssert.AreEqual(
                sourceHash,
                SHA256.HashData(await File.ReadAllBytesAsync(source)),
                preset.ToString());
            AssertZip(destination, expectedArchiveManifest, expectedFiles);
            AssertNoStagingFiles(root.Path);

            var rebuilt = await client.OpenArchiveAsync(destination);
            var rebuiltList = await client.ListEntriesAsync(rebuilt.Archive.SessionId, 0, 64);
            CollectionAssert.AreEqual(
                expectedArchiveManifest,
                rebuiltList.Entries
                    .Where(entry => entry.ProviderOrdinal >= 0)
                    .Select(entry => entry.RawPath)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                preset.ToString());
            var tested = await client.StartIntegrityTestAsync(rebuilt.Archive.SessionId, []);
            var testTerminal = await client.WaitForTerminalAsync(tested.OperationId);
            Assert.AreEqual(OperationPhase.Completed, testTerminal.Phase, preset.ToString());

            var rainExtraction = Path.Combine(root.Path, $"rain-extract-{preset}");
            var extraction = await client.StartExtractionAsync(
                rebuilt.Archive.SessionId,
                [],
                rainExtraction);
            var extractionTerminal = await client.WaitForTerminalAsync(extraction.OperationId);
            Assert.AreEqual(
                OperationPhase.Completed,
                extractionTerminal.Phase,
                $"{preset}: {extractionTerminal.Error?.Code} {extractionTerminal.Error?.Message}");
            await AssertExtractedTreeAsync(rainExtraction, expectedDirectories, expectedFiles);
            Assert.IsTrue((await client.CloseArchiveAsync(rebuilt.Archive.SessionId)).Closed);

            if (preset == ArchiveCompressionPreset.Balanced)
            {
                await AssertSevenZipAsync(
                    sevenZip,
                    root.Path,
                    destination,
                    expectedDirectories,
                    expectedFiles);
                await AssertBsdtarAsync(
                    bsdtar,
                    root.Path,
                    destination,
                    expectedArchiveManifest,
                    expectedDirectories,
                    expectedFiles);
            }
        }

        Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);
        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task StaleSubtreeRevisionFailsBeforeDestinationAccessAndPreservesOriginal()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.zip");
        CreateSourceZip(
            source,
            "nested"u8.ToArray(),
            "rename"u8.ToArray(),
            "outside"u8.ToArray(),
            "synthetic"u8.ToArray());
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var forbiddenParent = Path.Combine(root.Path, "must-not-be-created");
        var destination = Path.Combine(forbiddenParent, "stale.zip");

        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());
        var opened = await client.OpenArchiveAsync(source);
        var plan = await client.GetZipRebuildTreePlanAsync(opened.Archive.SessionId);
        var move = plan.Directories.Single(node => node.NormalizedPath == "move");
        var staleRevision = move.SubtreeRevision[0] == '0'
            ? "1" + move.SubtreeRevision[1..]
            : "0" + move.SubtreeRevision[1..];
        var started = await client.StartZipRebuildTreeEditAsync(
            opened.Archive.SessionId,
            destination,
            [],
            [],
            [],
            [],
            [new ArchiveRebuildTreeAction(
                move.NormalizedPath,
                staleRevision,
                ArchiveRebuildTreeActionKind.Move,
                "Moved")]);
        var terminal = await client.WaitForTerminalAsync(started.OperationId);

        Assert.AreEqual(OperationPhase.Failed, terminal.Phase);
        Assert.AreEqual("zip.rebuild_tree_action_invalid", terminal.Error?.Code);
        Assert.IsFalse(Directory.Exists(forbiddenParent));
        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        AssertNoStagingFiles(root.Path);
        Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task CancellationAfterTreeProgressPublishesNothingAndSameWorkerRemainsUsable()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "large-tree-source.zip");
        var payload = new byte[32 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        CreateLargeTreeSourceZip(source, payload);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var cancelledDestination = Path.Combine(root.Path, "cancelled-tree.zip");

        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());
        var opened = await client.OpenArchiveAsync(source);
        var plan = await client.GetZipRebuildTreePlanAsync(opened.Archive.SessionId);
        var tree = plan.Directories.Single(node => node.NormalizedPath == "tree");
        var progressSeen = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        OperationId operationId = default;
        client.OperationEventReceived += (_, operationEvent) =>
        {
            if (operationEvent.OperationId == operationId
                && operationEvent.Kind == OperationEventKind.Progress
                && operationEvent.Progress is { BytesProcessed: > 0 })
            {
                progressSeen.TrySetResult(true);
            }
        };

        var started = await client.StartZipRebuildTreeEditAsync(
            opened.Archive.SessionId,
            cancelledDestination,
            [],
            [],
            [],
            [],
            [new ArchiveRebuildTreeAction(
                tree.NormalizedPath,
                tree.SubtreeRevision,
                ArchiveRebuildTreeActionKind.Move,
                "Moved")],
            ArchiveCompressionPreset.Maximum);
        operationId = started.OperationId;
        await progressSeen.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var latency = Stopwatch.StartNew();
        var cancellation = await client.CancelOperationAsync(operationId);
        Assert.IsTrue(cancellation.CancellationRequested);
        var terminal = await client
            .WaitForTerminalAsync(operationId)
            .WaitAsync(TimeSpan.FromSeconds(30));
        latency.Stop();

        Assert.AreEqual(OperationPhase.Cancelled, terminal.Phase);
        Assert.IsTrue(latency.Elapsed < TimeSpan.FromSeconds(10), latency.Elapsed.ToString());
        Assert.IsFalse(File.Exists(cancelledDestination));
        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        AssertNoStagingFiles(root.Path);

        var recoveryDestination = Path.Combine(root.Path, "after-tree-cancel.zip");
        var recovered = await client.StartZipRebuildTreeEditAsync(
            opened.Archive.SessionId,
            recoveryDestination,
            [],
            [],
            [],
            [],
            [new ArchiveRebuildTreeAction(
                tree.NormalizedPath,
                tree.SubtreeRevision,
                ArchiveRebuildTreeActionKind.Move,
                "Moved")],
            ArchiveCompressionPreset.Store);
        var recoveredTerminal = await client.WaitForTerminalAsync(recovered.OperationId);
        Assert.AreEqual(OperationPhase.CompletedWithWarnings, recoveredTerminal.Phase);
        AssertZip(
            recoveryDestination,
            ["Moved/", "Moved/large.bin"],
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["Moved/large.bin"] = payload,
            });
        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ExactPidCrashDuringTreeEditPublishesNothingAndLeavesReviewedRecoveryState()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "crash-tree-source.zip");
        var destination = Path.Combine(root.Path, "crashed-tree.zip");
        var workerTemporaryRoot = Path.Combine(root.Path, "worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        var payload = new byte[64 * 1024 * 1024];
        RandomNumberGenerator.Fill(payload);
        CreateLargeTreeSourceZip(source, payload);
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
            var plan = await client.GetZipRebuildTreePlanAsync(opened.Archive.SessionId);
            var tree = plan.Directories.Single(node => node.NormalizedPath == "tree");
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

            var started = await client.StartZipRebuildTreeEditAsync(
                opened.Archive.SessionId,
                destination,
                [],
                [],
                [],
                [],
                [new ArchiveRebuildTreeAction(
                    tree.NormalizedPath,
                    tree.SubtreeRevision,
                    ArchiveRebuildTreeActionKind.Move,
                    "Moved")],
                ArchiveCompressionPreset.Maximum);
            operationId = started.OperationId;
            var progress = await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(operationId, progress.OperationId);
            Assert.IsTrue(progress.Progress!.BytesProcessed < payload.Length);

            Assert.AreEqual(
                0,
                NativeMethods.NtSuspendProcess(worker.Handle),
                "The test could not suspend its exact Worker during ZIP tree editing.");
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
        AssertNoStagingFiles(root.Path);
        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        var recoveryRoot = Path.Combine(workerTemporaryRoot, "rainarchives-creation-recovery");
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
            Assert.IsTrue(reopened.Archive.Warnings.Any(
                warning => warning.Code == "worker.creation_recovery_required"));
            Assert.IsTrue((await recoveryClient.CloseArchiveAsync(reopened.Archive.SessionId)).Closed);
        }

        Assert.AreEqual(1, new ArchiveCreationRecoveryStore(recoveryRoot).Inspect().PotentialRecoveryCount);
        Assert.IsFalse(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
    }

    private static void AssertTreePlan(
        GetZipRebuildTreePlanResponse first,
        GetZipRebuildTreePlanResponse second)
    {
        CollectionAssert.AreEqual(
            first.Directories.ToArray(),
            second.Directories.ToArray(),
            "The leased snapshot must produce a stable tree plan.");
        CollectionAssert.AreEqual(
            first.Directories.Select(node => node.NormalizedPath).Order(StringComparer.Ordinal).ToArray(),
            first.Directories.Select(node => node.NormalizedPath).ToArray());
        Assert.IsTrue(first.Directories.All(node =>
            node.SubtreeRevision.Length == 64
            && node.SubtreeRevision.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f')));

        var move = first.Directories.Single(node => node.NormalizedPath == "move");
        Assert.IsTrue(move.HasExplicitDirectoryEntry);
        Assert.AreEqual(6, move.DescendantCount);
        Assert.AreEqual(5, move.MaterialEntryCount);
        var synthetic = first.Directories.Single(node => node.NormalizedPath == "synthetic");
        Assert.IsFalse(synthetic.HasExplicitDirectoryEntry);
        Assert.AreEqual(1, synthetic.DescendantCount);
        Assert.AreEqual(1, synthetic.MaterialEntryCount);
        var empty = first.Directories.Single(node => node.NormalizedPath == "move/empty");
        Assert.IsTrue(empty.HasExplicitDirectoryEntry);
        Assert.AreEqual(0, empty.DescendantCount);
        Assert.AreEqual(0, empty.MaterialEntryCount);
    }

    private static void CreateSourceZip(
        string path,
        byte[] nestedBytes,
        byte[] renamedBytes,
        byte[] outsideBytes,
        byte[] syntheticBytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        _ = archive.CreateEntry("move/", CompressionLevel.NoCompression);
        _ = archive.CreateEntry("move/empty/", CompressionLevel.NoCompression);
        WriteEntry(archive, "move/nested/keep.txt", nestedBytes);
        WriteEntry(archive, "move/rename-me.txt", renamedBytes);
        WriteEntry(archive, "move/remove-me.txt", "remove"u8.ToArray());
        WriteEntry(archive, "move/replace.txt", "old replacement"u8.ToArray());
        _ = archive.CreateEntry("delete-tree/", CompressionLevel.NoCompression);
        WriteEntry(archive, "delete-tree/file.txt", "deleted subtree"u8.ToArray());
        WriteEntry(archive, "synthetic/leaf.txt", syntheticBytes);
        WriteEntry(archive, "outside.txt", outsideBytes);
    }

    private static void CreateLargeTreeSourceZip(string path, byte[] payload)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        _ = archive.CreateEntry("tree/", CompressionLevel.NoCompression);
        WriteEntry(archive, "tree/large.bin", payload);
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] payload)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var destination = entry.Open();
        destination.Write(payload);
    }

    private static void AssertZip(
        string path,
        IReadOnlyList<string> expectedManifest,
        IReadOnlyDictionary<string, byte[]> expectedFiles)
    {
        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        CollectionAssert.AreEqual(expectedManifest.ToArray(), archive.Entries.Select(entry => entry.FullName).ToArray());
        foreach (var expected in expectedFiles)
        {
            var entry = archive.GetEntry(expected.Key);
            Assert.IsNotNull(entry, expected.Key);
            using var source = entry.Open();
            using var payload = new MemoryStream();
            source.CopyTo(payload);
            CollectionAssert.AreEqual(expected.Value, payload.ToArray(), expected.Key);
        }
    }

    private static async Task AssertSevenZipAsync(
        string executable,
        string workingDirectory,
        string archive,
        IReadOnlyList<string> expectedDirectories,
        IReadOnlyDictionary<string, byte[]> expectedFiles)
    {
        var archiveHash = SHA256.HashData(await File.ReadAllBytesAsync(archive));
        _ = await RunToolAsync(executable, workingDirectory, "t", "-bd", "-y", "--", archive);
        CollectionAssert.AreEqual(archiveHash, SHA256.HashData(await File.ReadAllBytesAsync(archive)));
        var extraction = Path.Combine(workingDirectory, "sevenzip-extract");
        _ = await RunToolAsync(
            executable,
            workingDirectory,
            "x",
            "-bd",
            "-y",
            $"-o{extraction}",
            "--",
            archive);
        await AssertExtractedTreeAsync(extraction, expectedDirectories, expectedFiles);
        CollectionAssert.AreEqual(archiveHash, SHA256.HashData(await File.ReadAllBytesAsync(archive)));
    }

    private static async Task AssertBsdtarAsync(
        string executable,
        string workingDirectory,
        string archive,
        IReadOnlyList<string> expectedManifest,
        IReadOnlyList<string> expectedDirectories,
        IReadOnlyDictionary<string, byte[]> expectedFiles)
    {
        var archiveHash = SHA256.HashData(await File.ReadAllBytesAsync(archive));
        var listing = await RunToolAsync(executable, workingDirectory, "-tf", archive);
        var manifest = listing.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/'))
            .ToArray();
        Assert.AreEqual(expectedManifest.Count, manifest.Length);
        CollectionAssert.IsSubsetOf(
            expectedManifest.Where(path => path.All(char.IsAscii)).ToArray(),
            manifest,
            "Windows bsdtar must report every ASCII manifest path exactly; its redirected console replaces the non-ASCII glyph in this corpus.");
        var extraction = Path.Combine(workingDirectory, "bsdtar-extract");
        Directory.CreateDirectory(extraction);
        _ = await RunToolAsync(executable, workingDirectory, "-xf", archive, "-C", extraction);
        await AssertExtractedTreeAsync(extraction, expectedDirectories, expectedFiles);
        CollectionAssert.AreEqual(archiveHash, SHA256.HashData(await File.ReadAllBytesAsync(archive)));
    }

    private static async Task AssertExtractedTreeAsync(
        string root,
        IReadOnlyList<string> expectedDirectories,
        IReadOnlyDictionary<string, byte[]> expectedFiles)
    {
        CollectionAssert.AreEqual(
            expectedDirectories.Order(StringComparer.Ordinal).ToArray(),
            Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray());
        CollectionAssert.AreEqual(
            expectedFiles.Keys.Order(StringComparer.Ordinal).ToArray(),
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray());
        foreach (var expected in expectedFiles)
        {
            var path = Path.Combine(root, expected.Key.Replace('/', Path.DirectorySeparatorChar));
            CollectionAssert.AreEqual(
                SHA256.HashData(expected.Value),
                SHA256.HashData(await File.ReadAllBytesAsync(path)),
                expected.Key);
        }
    }

    private static async Task<ToolResult> RunToolAsync(
        string executable,
        string workingDirectory,
        params string[] arguments)
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
        startInfo.Environment["LANG"] = "en_US.UTF-8";
        startInfo.Environment["LC_ALL"] = "en_US.UTF-8";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not launch '{Path.GetFileName(executable)}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }

            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        Assert.AreEqual(
            0,
            process.ExitCode,
            $"{Path.GetFileName(executable)} failed. stdout: {Truncate(output)} stderr: {Truncate(error)}");
        return new ToolResult(output, error);
    }

    private static void AssertPinnedTool(
        string path,
        string expectedSha256,
        long? expectedLength = null)
    {
        Assert.IsTrue(File.Exists(path), $"Pinned tool was not found at '{path}'.");
        if (expectedLength is { } length)
        {
            Assert.AreEqual(length, new FileInfo(path).Length);
        }

        Assert.AreEqual(
            expectedSha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static string Truncate(string value) =>
        value.Length <= 4_096 ? value : value[..4_096];

    private static string GetOfficialSevenZipPath() =>
        Path.Combine(
            Path.GetTempPath(),
            "RainArchives.Tooling",
            "7zip-26.02",
            "expanded",
            "x64",
            "7za.exe");

    private static string GetWindowsBsdtarPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "tar.exe");

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

    private static void AssertNoStagingFiles(string parent) =>
        Assert.HasCount(
            0,
            Directory.GetFiles(parent, ".rainarchives-*.zip.staging", SearchOption.TopDirectoryOnly));

    private sealed record ToolResult(string StandardOutput, string StandardError);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ZipRebuildTreeEditEndToEndTests",
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
        [DllImport("ntdll.dll")]
        public static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        public static extern int NtResumeProcess(IntPtr processHandle);
    }
}
