using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class TarCreationEndToEndTests
{
    private const string WindowsBsdtarSha256 =
        "9B77D4C912F2EDAE8C241D0ECE1094D2AC068B084269CEAF85D7C7B085D2AE86";
    [TestMethod]
    [Timeout(30_000)]
    public async Task RealWorkerReportsExactTarWriteCapabilities()
    {
        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());

        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.TarCreation));
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.TarDirectoryCreation));
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.TarWriteCapabilities));

        var capabilities = await client.GetTarWriteCapabilitiesAsync();

        Assert.IsTrue(capabilities.CanCreate);
        Assert.IsFalse(capabilities.CanRebuild);
        Assert.IsTrue(capabilities.SupportsTreeCreation);
        CollectionAssert.AreEqual(
            new[] { ArchiveCompressionPreset.Store },
            capabilities.CompressionPresets.ToArray());
        Assert.AreEqual(ProtocolConstants.MaximumTarCreationEntries, capabilities.MaximumEntries);
        Assert.AreEqual(ProtocolConstants.MaximumTarCreationSourceBytes, capabilities.MaximumSourceBytes);
        Assert.IsFalse(capabilities.SupportsEncryption);
        Assert.IsFalse(capabilities.SupportsFilenameEncryption);
        Assert.IsFalse(capabilities.SupportsSplitOutput);
        Assert.IsFalse(capabilities.SupportsExistingDestinationReplacement);
        Assert.IsFalse(capabilities.SupportsInPlaceUpdate);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task RealWorkerCreatesExplicitTarAndRoundTripsThroughRain()
    {
        using var root = new TemporaryDirectory();
        var unicodeSource = Path.Combine(root.Path, "unicode-source.txt");
        var zeroSource = Path.Combine(root.Path, "zero-source.bin");
        var unicodeBytes = Encoding.UTF8.GetBytes("TAR payload — 雨 — 🌧️");
        await File.WriteAllBytesAsync(unicodeSource, unicodeBytes);
        await File.WriteAllBytesAsync(zeroSource, []);
        SetSecondPrecisionTimestamp(unicodeSource);
        SetSecondPrecisionTimestamp(zeroSource);

        var expectedFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Unicode/雨.txt"] = unicodeBytes,
            ["zero.bin"] = [],
        };
        var destination = Path.Combine(root.Path, "explicit.tar");
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(GetWorkerPath(), root.Path);
        var observedEvents = new ConcurrentQueue<OperationEvent>();
        client.OperationEventReceived += (_, operationEvent) => observedEvents.Enqueue(operationEvent);

        var started = await client.StartTarCreationAsync(
            destination,
            [
                new ArchiveCreationFile(unicodeSource, "Unicode/雨.txt"),
                new ArchiveCreationFile(zeroSource, "zero.bin"),
            ]);
        await AssertSuccessfulOperationAsync(client, started.OperationId, observedEvents);
        Assert.IsTrue(File.Exists(destination));
        AssertNoTarStagingFiles(root.Path);

        await AssertRainRoundTripAsync(client, destination, expectedFiles, ["Unicode"]);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task RealWorkerCreatesTarTreeWithUnicodeZeroByteAndEmptyDirectory()
    {
        using var root = new TemporaryDirectory();
        var sourceTree = Path.Combine(root.Path, "source-tree");
        var emptyDirectory = Path.Combine(sourceTree, "empty-dir");
        Directory.CreateDirectory(emptyDirectory);
        var unicodeSource = Path.Combine(sourceTree, "雨.txt");
        var zeroSource = Path.Combine(sourceTree, "zero.bin");
        var unicodeBytes = Encoding.UTF8.GetBytes("tree payload — pluie — 雨");
        await File.WriteAllBytesAsync(unicodeSource, unicodeBytes);
        await File.WriteAllBytesAsync(zeroSource, []);
        SetSecondPrecisionTimestamp(unicodeSource);
        SetSecondPrecisionTimestamp(zeroSource);

        var expectedFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Mapped/雨.txt"] = unicodeBytes,
            ["Mapped/zero.bin"] = [],
        };
        var expectedDirectories = new[] { "Mapped/", "Mapped/empty-dir/" };
        var destination = Path.Combine(root.Path, "tree.tar");
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(GetWorkerPath(), root.Path);
        var observedEvents = new ConcurrentQueue<OperationEvent>();
        client.OperationEventReceived += (_, operationEvent) => observedEvents.Enqueue(operationEvent);

        var started = await client.StartTarTreeCreationAsync(
            destination,
            [],
            [new ArchiveCreationDirectory(sourceTree, "Mapped")]);
        await AssertSuccessfulOperationAsync(client, started.OperationId, observedEvents);
        Assert.IsTrue(File.Exists(destination));
        AssertNoTarStagingFiles(root.Path);

        await AssertRainRoundTripAsync(client, destination, expectedFiles, expectedDirectories);
        await AssertPinnedBsdtarConformanceAsync(
            destination,
            root.Path,
            expectedFiles,
            expectedDirectories);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task TarCreationRefusesExistingDestinationAndPreservesOriginal()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "existing.tar");
        await File.WriteAllTextAsync(source, "source remains unchanged");
        var sentinel = Encoding.UTF8.GetBytes("do not replace this TAR");
        await File.WriteAllBytesAsync(destination, sentinel);
        var originalHash = SHA256.HashData(sentinel);

        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(GetWorkerPath(), root.Path);
        var started = await client.StartTarCreationAsync(
            destination,
            [new ArchiveCreationFile(source, "source.txt")]);
        var terminal = await client.WaitForTerminalAsync(started.OperationId)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.AreEqual(OperationPhase.Failed, terminal.Phase);
        Assert.AreEqual(ArchiveErrorCategory.DestinationConflict, terminal.Error?.Category);
        Assert.AreEqual("destination.must_not_exist", terminal.Error?.Code);
        CollectionAssert.AreEqual(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(destination)));
        AssertNoTarStagingFiles(root.Path);
    }

    private static async Task AssertSuccessfulOperationAsync(
        ArchiveClient client,
        OperationId operationId,
        ConcurrentQueue<OperationEvent> observedEvents)
    {
        var terminal = await client.WaitForTerminalAsync(operationId)
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(
            OperationPhase.Completed,
            terminal.Phase,
            $"{terminal.Error?.Code} ({terminal.Error?.Category}) {terminal.Error?.Message}");
        Assert.IsNull(terminal.Error);

        await WaitUntilAsync(
            () => observedEvents.Any(operationEvent =>
                operationEvent.OperationId == operationId
                && operationEvent.Kind == OperationEventKind.Terminal),
            TimeSpan.FromSeconds(5));
        var operationEvents = observedEvents
            .Where(operationEvent => operationEvent.OperationId == operationId)
            .OrderBy(operationEvent => operationEvent.Sequence)
            .ToArray();
        CollectionAssert.AreEqual(
            Enumerable.Range(1, operationEvents.Length).Select(value => (long)value).ToArray(),
            operationEvents.Select(operationEvent => operationEvent.Sequence).ToArray());
        CollectionAssert.IsSubsetOf(
            new[]
            {
                OperationPhase.Queued,
                OperationPhase.Validating,
                OperationPhase.Running,
                OperationPhase.Finalizing,
                OperationPhase.Completed,
            },
            operationEvents.Select(operationEvent => operationEvent.Phase).Distinct().ToArray());
        Assert.IsTrue(operationEvents.Any(operationEvent => operationEvent.Kind == OperationEventKind.Progress));
    }

    private static async Task AssertRainRoundTripAsync(
        ArchiveClient client,
        string archivePath,
        IReadOnlyDictionary<string, byte[]> expectedFiles,
        IReadOnlyList<string> expectedDirectories)
    {
        var opened = await client.OpenArchiveAsync(archivePath);
        try
        {
            Assert.AreEqual("tar", opened.Archive.Format, ignoreCase: true);
            Assert.AreEqual(ArchiveVolumeSetKind.Single, opened.Archive.VolumeSetKind);
            var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 128);
            var expectedEntries = expectedDirectories
                .Concat(expectedFiles.Keys)
                .Order(StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(
                expectedEntries,
                listed.Entries.Select(entry => entry.RawPath).Order(StringComparer.Ordinal).ToArray(),
                $"Expected [{string.Join(", ", expectedEntries)}], actual [{string.Join(", ", listed.Entries.Select(entry => entry.RawPath))}].");

            foreach (var expected in expectedFiles)
            {
                var entry = listed.Entries.Single(item => item.RawPath == expected.Key);
                Assert.AreEqual(ArchiveEntryKind.File, entry.Kind, expected.Key);
                Assert.AreEqual(expected.Value.LongLength, entry.OriginalSize, expected.Key);
                await using var lease = await client.OpenEntryStreamAsync(
                    opened.Archive.SessionId,
                    entry.EntryId,
                    maximumBytes: Math.Max(1, expected.Value.LongLength + 1));
                using var payload = new MemoryStream();
                await lease.Content.CopyToAsync(payload);
                CollectionAssert.AreEqual(expected.Value, payload.ToArray(), expected.Key);
                Assert.AreEqual(
                    Convert.ToHexString(SHA256.HashData(expected.Value)),
                    Convert.ToHexString(SHA256.HashData(payload.ToArray())),
                    expected.Key);
            }

            var tested = await client.StartIntegrityTestAsync(opened.Archive.SessionId, []);
            var testTerminal = await client.WaitForTerminalAsync(tested.OperationId)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(OperationPhase.Completed, testTerminal.Phase, testTerminal.Error?.Message);

            var extractionRoot = Path.Combine(
                Path.GetDirectoryName(archivePath)!,
                $"rain-tar-extract-{Guid.NewGuid():N}");
            try
            {
                var extraction = await client.StartExtractionAsync(
                    opened.Archive.SessionId,
                    [],
                    extractionRoot);
                var extractionTerminal = await client.WaitForTerminalAsync(extraction.OperationId)
                    .WaitAsync(TimeSpan.FromSeconds(30));
                Assert.AreEqual(
                    OperationPhase.Completed,
                    extractionTerminal.Phase,
                    extractionTerminal.Error?.Message);
                AssertExtractedTree(extractionRoot, expectedFiles, expectedDirectories);
            }
            finally
            {
                if (Directory.Exists(extractionRoot))
                {
                    Directory.Delete(extractionRoot, recursive: true);
                }
            }
        }
        finally
        {
            Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);
        }
    }

    private static void AssertExtractedTree(
        string extractionRoot,
        IReadOnlyDictionary<string, byte[]> expectedFiles,
        IReadOnlyList<string> expectedDirectories)
    {
        CollectionAssert.AreEqual(
            expectedDirectories
                .Select(path => path.TrimEnd('/'))
                .Order(StringComparer.Ordinal)
                .ToArray(),
            Directory.GetDirectories(extractionRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(extractionRoot, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray());
        CollectionAssert.AreEqual(
            expectedFiles.Keys.Order(StringComparer.Ordinal).ToArray(),
            Directory.GetFiles(extractionRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(extractionRoot, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray());

        foreach (var expected in expectedFiles)
        {
            var path = Path.Combine(
                extractionRoot,
                expected.Key.Replace('/', Path.DirectorySeparatorChar));
            var actual = File.ReadAllBytes(path);
            CollectionAssert.AreEqual(expected.Value, actual, expected.Key);
            Assert.AreEqual(
                Convert.ToHexString(SHA256.HashData(expected.Value)),
                Convert.ToHexString(SHA256.HashData(actual)),
                expected.Key);
        }
    }

    private static void AssertNoTarStagingFiles(string parent) =>
        Assert.HasCount(
            0,
            Directory.GetFiles(
                parent,
                ".rainarchives-*.zip.staging",
                SearchOption.TopDirectoryOnly));

    private static void SetSecondPrecisionTimestamp(string path) =>
        File.SetLastWriteTimeUtc(
            path,
            new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));

    private static async Task AssertPinnedBsdtarConformanceAsync(
        string archivePath,
        string workingDirectory,
        IReadOnlyDictionary<string, byte[]> expectedFiles,
        IReadOnlyList<string> expectedDirectories)
    {
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "tar.exe");
        Assert.IsTrue(File.Exists(executable), $"Pinned tool was not found at '{executable}'.");
        Assert.AreEqual(
            WindowsBsdtarSha256,
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable))));
        var version = await RunToolAsync(executable, workingDirectory, "--version");
        StringAssert.Contains(version.StandardOutput, "bsdtar 3.8.4");

        var immutableArchiveHash = SHA256.HashData(await File.ReadAllBytesAsync(archivePath));
        var listing = await RunToolAsync(executable, workingDirectory, "-tf", archivePath);
        var manifest = listing.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/'))
            .ToArray();
        Assert.AreEqual(expectedDirectories.Count + expectedFiles.Count, manifest.Length);
        CollectionAssert.IsSubsetOf(
            expectedDirectories
                .Concat(expectedFiles.Keys)
                .Where(path => path.All(char.IsAscii))
                .ToArray(),
            manifest);

        var extractionRoot = Path.Combine(workingDirectory, "bsdtar-extract");
        Directory.CreateDirectory(extractionRoot);
        await RunToolAsync(executable, workingDirectory, "-xf", archivePath, "-C", extractionRoot);
        AssertExtractedTree(extractionRoot, expectedFiles, expectedDirectories);
        CollectionAssert.AreEqual(
            immutableArchiveHash,
            SHA256.HashData(await File.ReadAllBytesAsync(archivePath)));
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

    private static string Truncate(string value) =>
        value.Length <= 4_096 ? value : value[..4_096];

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("The expected TAR operation event state was not reached before timeout.");
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
                AppContext.BaseDirectory,
                ".tar-creation-fixtures",
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

    private sealed record ToolResult(string StandardOutput, string StandardError);
}
