using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RainArchives.Client;
using RainArchives.Contracts;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

[CollectionDefinition("Archive managed-write real Worker", DisableParallelization = true)]
public sealed class ArchiveManagedWriteRealWorkerCollection;

[Collection("Archive managed-write real Worker")]
public sealed class ArchiveManagedWriteRealWorkerTests
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ProductionRoutesCreateEveryAdvertisedFormatAndRebuildAnEquivalentZipCopy()
    {
        string workerPath = Environment.GetEnvironmentVariable(
            "RAINEXPLORER_TEST_WORKER_PATH")
            ?? Path.Combine(
                AppContext.BaseDirectory,
                "worker",
                "RainArchives.Worker.exe");
        Assert.True(File.Exists(workerPath), $"Worker payload missing: {workerPath}");

        HashSet<int> priorWorkerIds = SnapshotWorkerProcessIds();
        string root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "RainExplorer-M6F path with spaces 雨-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);

        try
        {
            await RunManagedWriteScenarioAsync(workerPath, root);
        }
        finally
        {
            try
            {
                await WaitForWorkerSetToReturnToBaselineAsync(
                    priorWorkerIds,
                    TimeSpan.FromSeconds(8));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task RunManagedWriteScenarioAsync(string workerPath, string root)
    {
        byte[] sourceBytes = Encoding.UTF8.GetBytes(
            "Rain Explorer managed creation payload — pluie — 雨 — 🌧️");
        string sourcePath = Path.Combine(root, "source file with spaces 雨.txt");
        await File.WriteAllBytesAsync(sourcePath, sourceBytes);
        File.SetLastWriteTimeUtc(sourcePath, new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc));
        string sourceHash = await HashFileAsync(sourcePath);

        Assert.True(ArchiveWriteValidation.TryBuildSources(
            [(SourcePath: sourcePath, IsDirectory: false)],
            out IReadOnlyList<ArchiveCreationSource> sources));
        ArchiveCreationSource source = Assert.Single(sources);

        string treeRoot = Path.Combine(root, "source tree with spaces 雨");
        string nestedDirectory = Path.Combine(treeRoot, "nested 子");
        string emptyDirectory = Path.Combine(treeRoot, "empty directory");
        Directory.CreateDirectory(nestedDirectory);
        Directory.CreateDirectory(emptyDirectory);
        byte[] nestedBytes = Encoding.UTF8.GetBytes(
            "Rain Explorer managed directory payload — pluie — 雨 — 🌧️");
        string nestedPath = Path.Combine(nestedDirectory, "payload 雨.txt");
        string zeroPath = Path.Combine(treeRoot, "zero.bin");
        await File.WriteAllBytesAsync(nestedPath, nestedBytes);
        await File.WriteAllBytesAsync(zeroPath, []);
        Assert.True(ArchiveWriteValidation.TryBuildSources(
            [(SourcePath: treeRoot, IsDirectory: true)],
            out IReadOnlyList<ArchiveCreationSource> treeSources));
        ArchiveCreationSource treeSource = Assert.Single(treeSources);
        var treeSourceHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nestedPath] = await HashFileAsync(nestedPath),
            [zeroPath] = await HashFileAsync(zeroPath),
        };
        string treeArchiveRoot = treeSource.ArchivePath;
        var expectedTreeFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{treeArchiveRoot}/nested 子/payload 雨.txt"] = nestedBytes,
            [$"{treeArchiveRoot}/zero.bin"] = [],
        };
        string[] expectedTreeDirectories =
        [
            $"{treeArchiveRoot}/",
            $"{treeArchiveRoot}/empty directory/",
            $"{treeArchiveRoot}/nested 子/",
        ];

        var outputPaths = new Dictionary<ArchiveWriteFormat, string>();
        var outputHashes = new Dictionary<ArchiveWriteFormat, string>();
        var treeOutputPaths = new Dictionary<ArchiveWriteFormat, string>();

        await using (var runtime = new ArchiveClientRuntime(async cancellationToken =>
                         await ArchiveClient.StartAsync(workerPath, cancellationToken)
                             .ConfigureAwait(false)))
        {
            await using (var creation = new ArchiveCreationService(
                             runtime,
                             NoOpArchiveActivitySink.Instance))
            {
                ArchiveCreationCapabilitySnapshot capabilities = await creation
                    .LoadCapabilitiesAsync()
                    .WaitAsync(ClientTimeout);
                Assert.Equal(ArchiveWriteCapabilityState.Ready, capabilities.State);
                ArchiveWriteFormat[] requiredManagedFormats =
                [
                    ArchiveWriteFormat.Zip,
                    ArchiveWriteFormat.Tar,
                    ArchiveWriteFormat.TarGZip,
                    ArchiveWriteFormat.TarBZip2,
                    ArchiveWriteFormat.GZip,
                    ArchiveWriteFormat.BZip2,
                ];
                Assert.All(requiredManagedFormats, format =>
                    Assert.Contains(capabilities.Formats, option => option.Format == format));
                Assert.Equal(
                    capabilities.RarAvailability == RarEncoderAvailability.Available,
                    capabilities.Formats.Any(option => option.Format == ArchiveWriteFormat.Rar));

                foreach (ArchiveCreationFormatOption option in capabilities.Formats)
                {
                    ArchiveWriteFormat format = option.Format;
                    ArchiveCompressionPreset preset = Assert.Single(
                        option.CompressionPresets.Take(1));
                    string destination = Path.Combine(
                        root,
                        $"created {format.ToString().ToLowerInvariant()} 雨{option.Extension}");
                    var request = new ArchiveCreationRequest(
                        destination,
                        sources,
                        format,
                        preset);

                    ArchiveOperationResult result = await creation.CreateAsync(request)
                        .WaitAsync(OperationTimeout);
                    Assert.True(
                        result.Succeeded,
                        $"{format} creation returned {result.Outcome}.");
                    Assert.True(File.Exists(destination), format.ToString());
                    Assert.Equal(sourceHash, await HashFileAsync(sourcePath));

                    outputPaths.Add(format, destination);

                    if (option.SupportsDirectories)
                    {
                        string treeDestination = Path.Combine(
                            root,
                            $"tree created {format.ToString().ToLowerInvariant()} 雨{option.Extension}");
                        var treeRequest = new ArchiveCreationRequest(
                            treeDestination,
                            treeSources,
                            format,
                            preset);

                        ArchiveOperationResult treeResult = await creation.CreateAsync(treeRequest)
                            .WaitAsync(OperationTimeout);
                        Assert.True(
                            treeResult.Succeeded,
                            $"{format} tree creation returned {treeResult.Outcome}.");
                        Assert.True(File.Exists(treeDestination), format.ToString());
                        await AssertSourceHashesAsync(treeSourceHashes);
                        treeOutputPaths.Add(format, treeDestination);
                    }
                }
            }

            IArchiveClient client = await runtime.GetClientAsync(default)
                .WaitAsync(ClientTimeout);
            foreach ((ArchiveWriteFormat format, string archivePath) in outputPaths)
            {
                string expectedEntry = format is ArchiveWriteFormat.GZip or ArchiveWriteFormat.BZip2
                    ? Path.GetFileName(archivePath)[..^ArchiveWriteValidation.ExtensionFor(format).Length]
                    : source.ArchivePath;
                string expectedFormat = format switch
                {
                    ArchiveWriteFormat.Zip => "zip",
                    ArchiveWriteFormat.Tar => "tar",
                    ArchiveWriteFormat.TarGZip => "tar.gz",
                    ArchiveWriteFormat.TarBZip2 => "tar.bz2",
                    ArchiveWriteFormat.GZip => "gzip",
                    ArchiveWriteFormat.BZip2 => "bzip2",
                    ArchiveWriteFormat.Rar => "rar",
                    _ => throw new ArgumentOutOfRangeException(nameof(format)),
                };

                outputHashes.Add(
                    format,
                    await AssertArchiveRoundTripAsync(
                        client,
                        archivePath,
                        expectedFormat,
                        expectedEntry,
                        sourceBytes));
                Assert.Equal(sourceHash, await HashFileAsync(sourcePath));
            }

            foreach ((ArchiveWriteFormat format, string archivePath) in treeOutputPaths)
            {
                string expectedFormat = format switch
                {
                    ArchiveWriteFormat.Zip => "zip",
                    ArchiveWriteFormat.Tar => "tar",
                    ArchiveWriteFormat.TarGZip => "tar.gz",
                    ArchiveWriteFormat.TarBZip2 => "tar.bz2",
                    ArchiveWriteFormat.Rar => "rar",
                    _ => throw new ArgumentOutOfRangeException(nameof(format)),
                };
                await AssertArchiveTreeRoundTripAsync(
                    client,
                    archivePath,
                    expectedFormat,
                    expectedTreeFiles,
                    expectedTreeDirectories);
                await AssertSourceHashesAsync(treeSourceHashes);
            }

            string zipPath = outputPaths[ArchiveWriteFormat.Zip];
            string originalZipHash = outputHashes[ArchiveWriteFormat.Zip];
            string rebuiltPath = Path.Combine(root, "rebuilt zip copy with spaces 雨.zip");
            await using (var adapter = new ArchiveLocationAdapter(
                             runtime,
                             NoOpArchiveActivitySink.Instance))
            {
                await adapter.OpenAsync(zipPath).WaitAsync(ClientTimeout);
                Assert.Equal(ArchiveLocationState.Ready, adapter.State);
                Assert.True(adapter.CanRebuildCopy);

                ArchiveRebuildCapabilitySnapshot authority = await adapter
                    .LoadRebuildCapabilitiesAsync()
                    .WaitAsync(ClientTimeout);
                Assert.Equal(ArchiveWriteCapabilityState.Ready, authority.State);
                Assert.Equal(zipPath, authority.SourcePath, ignoreCase: true);
                ArchiveCompressionPreset preset = Assert.Single(
                    authority.CompressionPresets.Take(1));

                ArchiveOperationResult rebuilt = await adapter.RebuildCopyAsync(
                        new ArchiveRebuildRequest(authority, rebuiltPath, preset))
                    .WaitAsync(OperationTimeout);
                Assert.True(
                    rebuilt.Succeeded,
                    $"ZIP rebuild returned {rebuilt.Outcome}.");
                Assert.True(File.Exists(rebuiltPath));
                Assert.Equal(originalZipHash, await HashFileAsync(zipPath));
            }

            string rebuiltHash = await HashFileAsync(rebuiltPath);
            Assert.Equal(
                rebuiltHash,
                await AssertArchiveRoundTripAsync(
                    client,
                    rebuiltPath,
                    "zip",
                    source.ArchivePath,
                    sourceBytes));
            Assert.Equal(originalZipHash, await HashFileAsync(zipPath));
            Assert.Equal(sourceHash, await HashFileAsync(sourcePath));
            await AssertSourceHashesAsync(treeSourceHashes);

            string[] expectedFiles = outputPaths.Values
                .Concat(treeOutputPaths.Values)
                .Append(sourcePath)
                .Append(nestedPath)
                .Append(zeroPath)
                .Append(rebuiltPath)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                expectedFiles,
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Order(StringComparer.Ordinal));
            Assert.Equal(
                new[] { emptyDirectory, nestedDirectory, treeRoot }.Order(StringComparer.Ordinal),
                Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                    .Order(StringComparer.Ordinal));
            Assert.Empty(Directory.EnumerateFileSystemEntries(
                root,
                ".rainarchives-*",
                SearchOption.AllDirectories));
        }
    }

    private static async Task<string> AssertArchiveRoundTripAsync(
        IArchiveClient client,
        string archivePath,
        string expectedFormat,
        string expectedEntry,
        byte[] expectedBytes)
    {
        string immutableHash = await HashFileAsync(archivePath);
        OpenArchiveResponse opened = await client.OpenArchiveAsync(archivePath)
            .WaitAsync(ClientTimeout);
        try
        {
            Assert.Equal(expectedFormat, opened.Archive.Format, ignoreCase: true);
            Assert.True(opened.Archive.Capabilities.HasFlag(ArchiveCapability.List));
            Assert.True(opened.Archive.Capabilities.HasFlag(ArchiveCapability.Test));
            Assert.True(opened.Archive.Capabilities.HasFlag(ArchiveCapability.Extract));

            ListEntriesResponse listed = await client.ListEntriesAsync(
                    opened.Archive.SessionId,
                    offset: 0,
                    limit: 16)
                .WaitAsync(ClientTimeout);
            ArchiveEntryDescriptor entry = Assert.Single(listed.Entries);
            Assert.Equal(ArchiveEntryKind.File, entry.Kind);
            Assert.Equal(expectedEntry, entry.RawPath);
            if (entry.OriginalSize is long reportedSize)
                Assert.Equal(expectedBytes.LongLength, reportedSize);

            StartOperationResponse tested = await client.StartIntegrityTestAsync(
                    opened.Archive.SessionId,
                    [])
                .WaitAsync(ClientTimeout);
            OperationEvent testTerminal = await client.WaitForTerminalAsync(tested.OperationId)
                .WaitAsync(OperationTimeout);
            Assert.True(
                testTerminal.Phase == OperationPhase.Completed,
                $"{archivePath}: {testTerminal.Error?.Code} ({testTerminal.Error?.Category}) "
                + testTerminal.Error?.Message);

            string extractionRoot = Path.Combine(
                Path.GetDirectoryName(archivePath)!,
                "extract with spaces 雨-" + Guid.NewGuid().ToString("N"));
            try
            {
                StartOperationResponse extracted = await client.StartExtractionAsync(
                        opened.Archive.SessionId,
                        [],
                        extractionRoot)
                    .WaitAsync(ClientTimeout);
                OperationEvent extractionTerminal = await client
                    .WaitForTerminalAsync(extracted.OperationId)
                    .WaitAsync(OperationTimeout);
                Assert.True(
                    extractionTerminal.Phase == OperationPhase.Completed,
                    $"{archivePath}: {extractionTerminal.Error?.Code} "
                    + $"({extractionTerminal.Error?.Category}) {extractionTerminal.Error?.Message}");

                string extractedPath = Path.Combine(
                    extractionRoot,
                    expectedEntry.Replace('/', Path.DirectorySeparatorChar));
                Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(extractedPath));
                Assert.Equal(
                    [expectedEntry],
                    Directory.EnumerateFiles(extractionRoot, "*", SearchOption.AllDirectories)
                        .Select(path => Path.GetRelativePath(extractionRoot, path).Replace('\\', '/')));
            }
            finally
            {
                if (Directory.Exists(extractionRoot))
                    Directory.Delete(extractionRoot, recursive: true);
            }
        }
        finally
        {
            CloseArchiveResponse closed = await client.CloseArchiveAsync(opened.Archive.SessionId)
                .WaitAsync(ClientTimeout);
            Assert.True(closed.Closed);
        }

        Assert.Equal(immutableHash, await HashFileAsync(archivePath));
        return immutableHash;
    }

    private static async Task AssertArchiveTreeRoundTripAsync(
        IArchiveClient client,
        string archivePath,
        string expectedFormat,
        IReadOnlyDictionary<string, byte[]> expectedFiles,
        IReadOnlyList<string> expectedDirectories)
    {
        string immutableHash = await HashFileAsync(archivePath);
        OpenArchiveResponse opened = await client.OpenArchiveAsync(archivePath)
            .WaitAsync(ClientTimeout);
        try
        {
            Assert.Equal(expectedFormat, opened.Archive.Format, ignoreCase: true);
            Assert.True(opened.Archive.Capabilities.HasFlag(ArchiveCapability.List));
            Assert.True(opened.Archive.Capabilities.HasFlag(ArchiveCapability.Test));
            Assert.True(opened.Archive.Capabilities.HasFlag(ArchiveCapability.Extract));

            ListEntriesResponse listed = await client.ListEntriesAsync(
                    opened.Archive.SessionId,
                    offset: 0,
                    limit: 64)
                .WaitAsync(ClientTimeout);
            ArchiveEntryDescriptor[] files = listed.Entries
                .Where(entry => entry.Kind == ArchiveEntryKind.File)
                .OrderBy(entry => entry.NormalizedPath, StringComparer.Ordinal)
                .ToArray();
            // RAR stores Windows separators. Compare the validated archive namespace,
            // preserving RawPath as provider evidence instead of rewriting it in production.
            Assert.All(listed.Entries, entry => Assert.NotNull(entry.NormalizedPath));
            Assert.Equal(
                expectedFiles.Keys.Order(StringComparer.Ordinal),
                files.Select(entry => entry.NormalizedPath));
            foreach (ArchiveEntryDescriptor file in files)
            {
                if (file.OriginalSize is long reportedSize)
                    Assert.Equal(expectedFiles[file.NormalizedPath!].LongLength, reportedSize);
            }
            Assert.Equal(
                expectedDirectories.Select(path => path.TrimEnd('/')).Order(StringComparer.Ordinal),
                listed.Entries
                    .Where(entry => entry.Kind == ArchiveEntryKind.Directory)
                    .Select(entry => entry.NormalizedPath)
                    .Order(StringComparer.Ordinal));

            StartOperationResponse tested = await client.StartIntegrityTestAsync(
                    opened.Archive.SessionId,
                    [])
                .WaitAsync(ClientTimeout);
            OperationEvent testTerminal = await client.WaitForTerminalAsync(tested.OperationId)
                .WaitAsync(OperationTimeout);
            Assert.True(
                testTerminal.Phase == OperationPhase.Completed,
                $"{archivePath}: {testTerminal.Error?.Code} ({testTerminal.Error?.Category}) "
                + testTerminal.Error?.Message);

            string extractionRoot = Path.Combine(
                Path.GetDirectoryName(archivePath)!,
                "tree extract with spaces 雨-" + Guid.NewGuid().ToString("N"));
            try
            {
                StartOperationResponse extracted = await client.StartExtractionAsync(
                        opened.Archive.SessionId,
                        [],
                        extractionRoot)
                    .WaitAsync(ClientTimeout);
                OperationEvent extractionTerminal = await client
                    .WaitForTerminalAsync(extracted.OperationId)
                    .WaitAsync(OperationTimeout);
                Assert.True(
                    extractionTerminal.Phase == OperationPhase.Completed,
                    $"{archivePath}: {extractionTerminal.Error?.Code} "
                    + $"({extractionTerminal.Error?.Category}) {extractionTerminal.Error?.Message}");

                foreach ((string relativePath, byte[] expectedBytes) in expectedFiles)
                {
                    string extractedPath = Path.Combine(
                        extractionRoot,
                        relativePath.Replace('/', Path.DirectorySeparatorChar));
                    Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(extractedPath));
                }
                foreach (string relativePath in expectedDirectories)
                {
                    string extractedPath = Path.Combine(
                        extractionRoot,
                        relativePath.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
                    Assert.True(Directory.Exists(extractedPath), relativePath);
                }
                Assert.Equal(
                    expectedFiles.Keys.Order(StringComparer.Ordinal),
                    Directory.EnumerateFiles(extractionRoot, "*", SearchOption.AllDirectories)
                        .Select(path => Path.GetRelativePath(extractionRoot, path).Replace('\\', '/'))
                        .Order(StringComparer.Ordinal));
                Assert.Equal(
                    expectedDirectories.Order(StringComparer.Ordinal),
                    Directory.EnumerateDirectories(extractionRoot, "*", SearchOption.AllDirectories)
                        .Select(path => Path.GetRelativePath(extractionRoot, path).Replace('\\', '/') + "/")
                        .Order(StringComparer.Ordinal));
            }
            finally
            {
                if (Directory.Exists(extractionRoot))
                    Directory.Delete(extractionRoot, recursive: true);
            }
        }
        finally
        {
            CloseArchiveResponse closed = await client.CloseArchiveAsync(opened.Archive.SessionId)
                .WaitAsync(ClientTimeout);
            Assert.True(closed.Closed);
        }

        Assert.Equal(immutableHash, await HashFileAsync(archivePath));
    }

    private static async Task AssertSourceHashesAsync(
        IReadOnlyDictionary<string, string> expectedHashes)
    {
        foreach ((string path, string expectedHash) in expectedHashes)
            Assert.Equal(expectedHash, await HashFileAsync(path));
    }

    private static async Task<string> HashFileAsync(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private static HashSet<int> SnapshotWorkerProcessIds()
    {
        Process[] processes = Process.GetProcessesByName("RainArchives.Worker");
        try
        {
            return processes.Select(process => process.Id).ToHashSet();
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }

    private static async Task WaitForWorkerSetToReturnToBaselineAsync(
        IReadOnlySet<int> priorWorkerIds,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (!HasNewWorkerProcess(priorWorkerIds)) return;
            await Task.Delay(50);
        }

        Assert.False(
            HasNewWorkerProcess(priorWorkerIds),
            "The real Worker test left a new RainArchives.Worker process running.");
    }

    private static bool HasNewWorkerProcess(IReadOnlySet<int> priorWorkerIds)
    {
        Process[] processes = Process.GetProcessesByName("RainArchives.Worker");
        try
        {
            return processes.Any(process => !priorWorkerIds.Contains(process.Id));
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }

    private sealed class NoOpArchiveActivitySink : IArchiveActivitySink
    {
        public static NoOpArchiveActivitySink Instance { get; } = new();

        public IArchiveActivityHandle Begin(ActivityOperationKind kind) => new Handle();

        private sealed class Handle : IArchiveActivityHandle
        {
            public void AttachCancel(Action cancel)
            {
            }

            public void MarkCancellationPending()
            {
            }

            public void Report(ActivityOperationUpdate update)
            {
            }
        }
    }
}
