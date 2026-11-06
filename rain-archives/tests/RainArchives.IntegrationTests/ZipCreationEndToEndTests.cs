using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class ZipCreationEndToEndTests
{
    [TestMethod]
    [Timeout(120_000)]
    public async Task PackagedWorkerCreatesEveryZipPresetAndRoundTripsThroughReader()
    {
        using var root = new TemporaryDirectory();
        var alpha = Path.Combine(root.Path, "alpha-source.txt");
        var unicode = Path.Combine(root.Path, "unicode-source.txt");
        var zero = Path.Combine(root.Path, "zero-source.bin");
        var alphaBytes = Encoding.UTF8.GetBytes("Rain Archives secure ZIP creation\r\n");
        var unicodeBytes = Encoding.UTF8.GetBytes("こんにちは — pluie — 🌧️");
        await File.WriteAllBytesAsync(alpha, alphaBytes);
        await File.WriteAllBytesAsync(unicode, unicodeBytes);
        await File.WriteAllBytesAsync(zero, []);
        var files = new[]
        {
            new ArchiveCreationFile(alpha, "Folder/alpha.txt"),
            new ArchiveCreationFile(unicode, "Unicode/雨.txt"),
            new ArchiveCreationFile(zero, "zero.bin"),
        };
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Folder/alpha.txt"] = alphaBytes,
            ["Unicode/雨.txt"] = unicodeBytes,
            ["zero.bin"] = [],
        };

        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.ZipCreation));
        var observedEvents = new ConcurrentQueue<OperationEvent>();
        client.OperationEventReceived += (_, operationEvent) => observedEvents.Enqueue(operationEvent);

        foreach (var preset in Enum.GetValues<ArchiveCompressionPreset>())
        {
            var destination = Path.Combine(root.Path, $"created-{preset}.zip");
            var started = await client.StartZipCreationAsync(destination, files, preset);
            var terminal = await client
                .WaitForTerminalAsync(started.OperationId)
                .WaitAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual(
                OperationPhase.Completed,
                terminal.Phase,
                $"{preset}: {terminal.Error?.Code} ({terminal.Error?.Category}) {terminal.Error?.Message}");
            Assert.IsNull(terminal.Error, preset.ToString());
            Assert.IsTrue(File.Exists(destination), preset.ToString());
            AssertNoCreationStagingFiles(root.Path);
            AssertZipContents(destination, expected);

            await WaitUntilAsync(
                () => observedEvents.Any(operationEvent =>
                    operationEvent.OperationId == started.OperationId
                    && operationEvent.Kind == OperationEventKind.Terminal),
                TimeSpan.FromSeconds(5));
            var operationEvents = observedEvents
                .Where(operationEvent => operationEvent.OperationId == started.OperationId)
                .OrderBy(operationEvent => operationEvent.Sequence)
                .ToArray();
            CollectionAssert.AreEqual(
                Enumerable.Range(1, operationEvents.Length).Select(value => (long)value).ToArray(),
                operationEvents.Select(operationEvent => operationEvent.Sequence).ToArray(),
                preset.ToString());
            CollectionAssert.IsSubsetOf(
                new[]
                {
                    OperationPhase.Queued,
                    OperationPhase.Validating,
                    OperationPhase.Running,
                    OperationPhase.Finalizing,
                    OperationPhase.Completed,
                },
                operationEvents.Select(operationEvent => operationEvent.Phase).Distinct().ToArray(),
                preset.ToString());
            Assert.IsTrue(
                operationEvents.Any(operationEvent => operationEvent.Kind == OperationEventKind.Progress),
                $"{preset} emitted no progress event.");

            var opened = await client.OpenArchiveAsync(destination);
            var listed = await client.ListEntriesAsync(opened.Archive.SessionId, limit: 32);
            var listedFiles = listed.Entries
                .Where(entry => entry.Kind == ArchiveEntryKind.File)
                .ToArray();
            Assert.AreEqual(expected.Count, listedFiles.Length, preset.ToString());
            CollectionAssert.AreEquivalent(
                expected.Keys.ToArray(),
                listedFiles.Select(entry => entry.RawPath).ToArray(),
                preset.ToString());
            var tested = await client.StartIntegrityTestAsync(opened.Archive.SessionId, []);
            var testTerminal = await client.WaitForTerminalAsync(tested.OperationId);
            Assert.AreEqual(OperationPhase.Completed, testTerminal.Phase, preset.ToString());
            _ = await client.CloseArchiveAsync(opened.Archive.SessionId);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PackagedWorkerCreatesEmptyZip()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "empty.zip");
        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());

        var started = await client.StartZipCreationAsync(destination, []);
        var terminal = await client.WaitForTerminalAsync(started.OperationId);

        Assert.AreEqual(
            OperationPhase.Completed,
            terminal.Phase,
            $"{terminal.Error?.Code} ({terminal.Error?.Category}) {terminal.Error?.Message}");
        using var stream = File.OpenRead(destination);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.HasCount(0, archive.Entries);
        AssertNoCreationStagingFiles(root.Path);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task PackagedWorkerAppliesOnlyUnanimousCreationProvenance()
    {
        using var root = new TemporaryDirectory();
        var first = Path.Combine(root.Path, "first.txt");
        var second = Path.Combine(root.Path, "second.txt");
        var unmarked = Path.Combine(root.Path, "unmarked.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        await File.WriteAllTextAsync(unmarked, "unmarked");
        await File.WriteAllTextAsync(
            first + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://one.invalid/private\r\n");
        await File.WriteAllTextAsync(
            second + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://two.invalid/private\r\n");
        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());

        var unanimousDestination = Path.Combine(root.Path, "unanimous.zip");
        var unanimousStart = await client.StartZipCreationAsync(
            unanimousDestination,
            [
                new ArchiveCreationFile(first, "first.txt"),
                new ArchiveCreationFile(second, "second.txt"),
            ]);
        var unanimousTerminal = await client.WaitForTerminalAsync(unanimousStart.OperationId);

        Assert.AreEqual(
            OperationPhase.Completed,
            unanimousTerminal.Phase,
            $"{unanimousTerminal.Error?.Code} ({unanimousTerminal.Error?.Category}) {unanimousTerminal.Error?.Message}");
        Assert.AreEqual(
            "[ZoneTransfer]\r\nZoneId=3\r\n",
            await File.ReadAllTextAsync(unanimousDestination + ":Zone.Identifier"));
        AssertZipContents(
            unanimousDestination,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["first.txt"] = Encoding.UTF8.GetBytes("first"),
                ["second.txt"] = Encoding.UTF8.GetBytes("second"),
            });

        var mixedDestination = Path.Combine(root.Path, "mixed.zip");
        var mixedStart = await client.StartZipCreationAsync(
            mixedDestination,
            [
                new ArchiveCreationFile(first, "first.txt"),
                new ArchiveCreationFile(unmarked, "unmarked.txt"),
            ]);
        var mixedTerminal = await client.WaitForTerminalAsync(mixedStart.OperationId);

        Assert.AreEqual(
            OperationPhase.CompletedWithWarnings,
            mixedTerminal.Phase,
            $"{mixedTerminal.Error?.Code} ({mixedTerminal.Error?.Category}) {mixedTerminal.Error?.Message}");
        Assert.AreEqual("provenance.source_ambiguous", mixedTerminal.Warning?.Code);
        Assert.IsFalse(
            mixedTerminal.Warning?.Message.Contains("one.invalid", StringComparison.Ordinal)
                ?? true);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            async () => await File.ReadAllTextAsync(mixedDestination + ":Zone.Identifier"));
        AssertNoCreationStagingFiles(root.Path);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PackagedWorkerCreatesMixedTreeAndPreservesEmptyDirectories()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "Assets");
        var nested = Path.Combine(tree, "nested");
        Directory.CreateDirectory(Path.Combine(tree, "empty"));
        Directory.CreateDirectory(nested);
        var treeFile = Path.Combine(nested, "雨.txt");
        var explicitFile = Path.Combine(root.Path, "README.txt");
        var treePayload = Encoding.UTF8.GetBytes("tree payload 🌧️");
        var explicitPayload = Encoding.UTF8.GetBytes("explicit payload");
        await File.WriteAllBytesAsync(treeFile, treePayload);
        await File.WriteAllBytesAsync(explicitFile, explicitPayload);
        await File.WriteAllTextAsync(
            treeFile + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\n");
        await File.WriteAllTextAsync(
            explicitFile + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\n");
        var destination = Path.Combine(root.Path, "tree.zip");
        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.ZipDirectoryCreation));

        var started = await client.StartZipTreeCreationAsync(
            destination,
            [new ArchiveCreationFile(explicitFile, "README.txt")],
            [new ArchiveCreationDirectory(tree, "assets")]);
        var terminal = await client.WaitForTerminalAsync(started.OperationId);

        Assert.AreEqual(
            OperationPhase.Completed,
            terminal.Phase,
            $"{terminal.Error?.Code} ({terminal.Error?.Category}) {terminal.Error?.Message}");
        Assert.AreEqual(
            "[ZoneTransfer]\r\nZoneId=3\r\n",
            await File.ReadAllTextAsync(destination + ":Zone.Identifier"));
        AssertNoCreationStagingFiles(root.Path);
        using (var stream = File.OpenRead(destination))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    "README.txt",
                    "assets/",
                    "assets/empty/",
                    "assets/nested/",
                    "assets/nested/雨.txt",
                },
                archive.Entries.Select(entry => entry.FullName).ToArray());
            Assert.IsTrue(
                (archive.GetEntry("assets/empty/")!.ExternalAttributes
                    & (int)FileAttributes.Directory) != 0);
            await AssertEntryBytesAsync(archive.GetEntry("README.txt")!, explicitPayload);
            await AssertEntryBytesAsync(archive.GetEntry("assets/nested/雨.txt")!, treePayload);
        }

        var opened = await client.OpenArchiveAsync(destination);
        var listed = await client.ListEntriesAsync(opened.Archive.SessionId, limit: 32);
        Assert.IsTrue(listed.Entries.Any(entry =>
            entry.RawPath == "assets/empty/"
            && entry.Kind == ArchiveEntryKind.Directory));
        _ = await client.CloseArchiveAsync(opened.Archive.SessionId);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task TreeEntryOrderIsStableAcrossReorderedInputs()
    {
        using var root = new TemporaryDirectory();
        var firstTree = Path.Combine(root.Path, "first-tree");
        var secondTree = Path.Combine(root.Path, "second-tree");
        Directory.CreateDirectory(firstTree);
        Directory.CreateDirectory(secondTree);
        await File.WriteAllTextAsync(Path.Combine(firstTree, "z.txt"), "z");
        await File.WriteAllTextAsync(Path.Combine(secondTree, "a.txt"), "a");
        var firstExplicit = Path.Combine(root.Path, "first.txt");
        var secondExplicit = Path.Combine(root.Path, "second.txt");
        await File.WriteAllTextAsync(firstExplicit, "first");
        await File.WriteAllTextAsync(secondExplicit, "second");
        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());

        var firstDestination = Path.Combine(root.Path, "first.zip");
        var firstStart = await client.StartZipTreeCreationAsync(
            firstDestination,
            [
                new ArchiveCreationFile(secondExplicit, "z-explicit.txt"),
                new ArchiveCreationFile(firstExplicit, "a-explicit.txt"),
            ],
            [
                new ArchiveCreationDirectory(secondTree, "z-tree"),
                new ArchiveCreationDirectory(firstTree, "a-tree"),
            ]);
        var firstTerminal = await client.WaitForTerminalAsync(firstStart.OperationId);
        Assert.AreEqual(OperationPhase.Completed, firstTerminal.Phase);

        var secondDestination = Path.Combine(root.Path, "second.zip");
        var secondStart = await client.StartZipTreeCreationAsync(
            secondDestination,
            [
                new ArchiveCreationFile(firstExplicit, "a-explicit.txt"),
                new ArchiveCreationFile(secondExplicit, "z-explicit.txt"),
            ],
            [
                new ArchiveCreationDirectory(firstTree, "a-tree"),
                new ArchiveCreationDirectory(secondTree, "z-tree"),
            ]);
        var secondTerminal = await client.WaitForTerminalAsync(secondStart.OperationId);
        Assert.AreEqual(OperationPhase.Completed, secondTerminal.Phase);

        var firstEntries = ReadZipEntryOrder(firstDestination);
        var secondEntries = ReadZipEntryOrder(secondDestination);
        CollectionAssert.AreEqual(firstEntries, secondEntries);
        CollectionAssert.AreEqual(
            new[]
            {
                "a-explicit.txt",
                "a-tree/",
                "a-tree/z.txt",
                "z-explicit.txt",
                "z-tree/",
                "z-tree/a.txt",
            },
            firstEntries);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task OutOfRangeSourceTimestampCompletesWithClampingWarning()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "old.txt");
        var destination = Path.Combine(root.Path, "old.zip");
        await File.WriteAllTextAsync(source, "old");
        File.SetLastWriteTimeUtc(source, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());

        var started = await client.StartZipCreationAsync(
            destination,
            [new ArchiveCreationFile(source, "old.txt")]);
        var terminal = await client.WaitForTerminalAsync(started.OperationId);

        Assert.AreEqual(
            OperationPhase.CompletedWithWarnings,
            terminal.Phase,
            $"{terminal.Error?.Code} ({terminal.Error?.Category}) {terminal.Error?.Message}");
        Assert.AreEqual("zip.timestamp_clamped", terminal.Warning?.Code);
        using var stream = File.OpenRead(destination);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.AreEqual(
            new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            archive.Entries.Single().LastWriteTime.DateTime);
        AssertNoCreationStagingFiles(root.Path);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RepresentableSourceTimestampPreservesTheInstantWithinZipResolution()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "timestamp.txt");
        var destination = Path.Combine(root.Path, "timestamp.zip");
        await File.WriteAllTextAsync(source, "timestamp");
        var expectedUtc = new DateTime(2024, 6, 15, 12, 34, 56, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, expectedUtc);
        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());

        var started = await client.StartZipCreationAsync(
            destination,
            [new ArchiveCreationFile(source, "timestamp.txt")]);
        var terminal = await client.WaitForTerminalAsync(started.OperationId);

        Assert.AreEqual(OperationPhase.Completed, terminal.Phase);
        using var stream = File.OpenRead(destination);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.AreEqual(expectedUtc, archive.Entries.Single().LastWriteTime.UtcDateTime);
        AssertNoCreationStagingFiles(root.Path);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task InvalidOrConflictingRequestsLeaveNoOutputAndWorkerRemainsHealthy()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        await File.WriteAllTextAsync(source, "source payload");
        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());

        var unsafeDestination = Path.Combine(root.Path, "unsafe.zip");
        var unsafeStart = await client.StartZipCreationAsync(
            unsafeDestination,
            [new ArchiveCreationFile(source, "../escape.txt")]);
        var unsafeTerminal = await client.WaitForTerminalAsync(unsafeStart.OperationId);
        Assert.AreEqual(OperationPhase.Failed, unsafeTerminal.Phase);
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, unsafeTerminal.Error?.Category);
        Assert.AreEqual("creation.unsafe_archive_path", unsafeTerminal.Error?.Code);
        Assert.IsFalse(File.Exists(unsafeDestination));
        AssertNoCreationStagingFiles(root.Path);

        var collisionDestination = Path.Combine(root.Path, "collision.zip");
        var collisionStart = await client.StartZipCreationAsync(
            collisionDestination,
            [
                new ArchiveCreationFile(source, "same.txt"),
                new ArchiveCreationFile(source, "SAME.txt"),
            ]);
        var collisionTerminal = await client.WaitForTerminalAsync(collisionStart.OperationId);
        Assert.AreEqual(OperationPhase.Failed, collisionTerminal.Phase);
        Assert.AreEqual(ArchiveErrorCategory.PathCollision, collisionTerminal.Error?.Category);
        Assert.AreEqual("creation.archive_path_collision", collisionTerminal.Error?.Code);
        Assert.IsFalse(File.Exists(collisionDestination));
        AssertNoCreationStagingFiles(root.Path);

        var existingDestination = Path.Combine(root.Path, "existing.zip");
        var sentinel = Encoding.UTF8.GetBytes("do not replace");
        await File.WriteAllBytesAsync(existingDestination, sentinel);
        var existingStart = await client.StartZipCreationAsync(
            existingDestination,
            [new ArchiveCreationFile(source, "source.txt")]);
        var existingTerminal = await client.WaitForTerminalAsync(existingStart.OperationId);
        Assert.AreEqual(OperationPhase.Failed, existingTerminal.Phase);
        Assert.AreEqual(ArchiveErrorCategory.DestinationConflict, existingTerminal.Error?.Category);
        Assert.AreEqual("destination.must_not_exist", existingTerminal.Error?.Code);
        CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(existingDestination));
        AssertNoCreationStagingFiles(root.Path);

        var recoveryDestination = Path.Combine(root.Path, "healthy.zip");
        var recoveryStart = await client.StartZipCreationAsync(
            recoveryDestination,
            [new ArchiveCreationFile(source, "source.txt")]);
        var recoveryTerminal = await client.WaitForTerminalAsync(recoveryStart.OperationId);
        Assert.AreEqual(
            OperationPhase.Completed,
            recoveryTerminal.Phase,
            $"{recoveryTerminal.Error?.Code} ({recoveryTerminal.Error?.Category}) {recoveryTerminal.Error?.Message}");
        AssertZipContents(
            recoveryDestination,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["source.txt"] = await File.ReadAllBytesAsync(source),
            });
        AssertNoCreationStagingFiles(root.Path);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RawFilesystemAliasesAreRejectedBeforeWorkerCanonicalization()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        await File.WriteAllTextAsync(source, "source payload");
        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());

        var trailingDestination = Path.Combine(root.Path, "trailing.zip. ");
        var trailingStart = await client.StartZipCreationAsync(trailingDestination, []);
        var trailingTerminal = await client.WaitForTerminalAsync(trailingStart.OperationId);
        Assert.AreEqual(OperationPhase.Failed, trailingTerminal.Phase);
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, trailingTerminal.Error?.Category);
        Assert.AreEqual("creation.unsafe_destination_path", trailingTerminal.Error?.Code);
        Assert.IsFalse(File.Exists(Path.Combine(root.Path, "trailing.zip")));

        var sourceAliasDestination = Path.Combine(root.Path, "source-alias.zip");
        var sourceAliasStart = await client.StartZipCreationAsync(
            sourceAliasDestination,
            [new ArchiveCreationFile(source + ". ", "source.txt")]);
        var sourceAliasTerminal = await client.WaitForTerminalAsync(sourceAliasStart.OperationId);
        Assert.AreEqual(OperationPhase.Failed, sourceAliasTerminal.Phase);
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, sourceAliasTerminal.Error?.Category);
        Assert.AreEqual("creation.unsafe_source_path", sourceAliasTerminal.Error?.Code);
        Assert.IsFalse(File.Exists(sourceAliasDestination));

        var dotSegmentDestination = Path.Combine(root.Path, "missing", "..", "dot-segment.zip");
        var dotSegmentStart = await client.StartZipCreationAsync(dotSegmentDestination, []);
        var dotSegmentTerminal = await client.WaitForTerminalAsync(dotSegmentStart.OperationId);
        Assert.AreEqual(OperationPhase.Failed, dotSegmentTerminal.Phase);
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, dotSegmentTerminal.Error?.Category);
        Assert.AreEqual("creation.unsafe_destination_path", dotSegmentTerminal.Error?.Code);
        Assert.IsFalse(File.Exists(Path.Combine(root.Path, "dot-segment.zip")));

        var sourceTree = Path.Combine(root.Path, "source-tree");
        Directory.CreateDirectory(sourceTree);
        var directoryAliasDestination = Path.Combine(root.Path, "directory-alias.zip");
        var directoryAliasStart = await client.StartZipTreeCreationAsync(
            directoryAliasDestination,
            [],
            [new ArchiveCreationDirectory(sourceTree + ". ", "tree")]);
        var directoryAliasTerminal = await client.WaitForTerminalAsync(
            directoryAliasStart.OperationId);
        Assert.AreEqual(OperationPhase.Failed, directoryAliasTerminal.Phase);
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, directoryAliasTerminal.Error?.Category);
        Assert.AreEqual("creation.unsafe_source_path", directoryAliasTerminal.Error?.Code);
        Assert.IsFalse(File.Exists(directoryAliasDestination));

        var recoveryDestination = Path.Combine(root.Path, "after-alias-refusals.zip");
        var recoveryStart = await client.StartZipCreationAsync(
            recoveryDestination,
            [new ArchiveCreationFile(source, "source.txt")]);
        var recoveryTerminal = await client.WaitForTerminalAsync(recoveryStart.OperationId);
        Assert.AreEqual(
            OperationPhase.Completed,
            recoveryTerminal.Phase,
            $"{recoveryTerminal.Error?.Code} ({recoveryTerminal.Error?.Category}) {recoveryTerminal.Error?.Message}");
        Assert.IsTrue(File.Exists(recoveryDestination));
        AssertNoCreationStagingFiles(root.Path);
    }

    private static void AssertZipContents(
        string archivePath,
        IReadOnlyDictionary<string, byte[]> expected)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), archive.Entries.Select(entry => entry.FullName).ToArray());
        foreach (var entry in archive.Entries)
        {
            using var entryStream = entry.Open();
            using var payload = new MemoryStream();
            entryStream.CopyTo(payload);
            CollectionAssert.AreEqual(expected[entry.FullName], payload.ToArray(), entry.FullName);
        }
    }

    private static async Task AssertEntryBytesAsync(ZipArchiveEntry entry, byte[] expected)
    {
        await using var entryStream = entry.Open();
        using var payload = new MemoryStream();
        await entryStream.CopyToAsync(payload);
        CollectionAssert.AreEqual(expected, payload.ToArray(), entry.FullName);
    }

    private static string[] ReadZipEntryOrder(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return archive.Entries.Select(entry => entry.FullName).ToArray();
    }

    private static void AssertNoCreationStagingFiles(string parent) =>
        Assert.HasCount(
            0,
            Directory.GetFiles(
                parent,
                ".rainarchives-*.zip.staging",
                SearchOption.TopDirectoryOnly));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("The expected ZIP creation event state was not reached before timeout.");
            }

            await Task.Delay(20);
        }
    }

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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ZipCreationEndToEndTests",
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
}
