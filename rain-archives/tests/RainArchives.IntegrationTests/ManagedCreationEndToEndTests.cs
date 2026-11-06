using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class ManagedCreationEndToEndTests
{
    private const string OfficialSevenZipSha256 =
        "35D4D69D7CD6CB44558F208C3B1334268013F9DAF82D2DDA848893A1C30C59C2";

    [TestMethod]
    [Timeout(30_000)]
    public async Task RealWorkerReportsExactClosedManagedWriteCapabilities()
    {
        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());

        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.ManagedWriteCapabilities));
        var response = await client.GetManagedWriteCapabilitiesAsync();
        Assert.HasCount(ProtocolConstants.MaximumManagedWriteCapabilityFormats, response.Capabilities);
        CollectionAssert.AreEqual(
            Enum.GetValues<ManagedArchiveCreationFormat>(),
            response.Capabilities.Select(capability => capability.Format).ToArray());

        foreach (var capability in response.Capabilities)
        {
            Assert.IsTrue(capability.CanCreate, capability.Format.ToString());
            Assert.IsFalse(capability.CanRebuild, capability.Format.ToString());
            Assert.AreEqual(
                capability.Format is ManagedArchiveCreationFormat.TarGZip
                    or ManagedArchiveCreationFormat.TarBZip2,
                capability.SupportsTreeCreation,
                capability.Format.ToString());
            CollectionAssert.AreEqual(
                capability.Format is ManagedArchiveCreationFormat.TarBZip2
                    or ManagedArchiveCreationFormat.BZip2
                    ? new[] { ArchiveCompressionPreset.Balanced }
                    : Enum.GetValues<ArchiveCompressionPreset>(),
                capability.CompressionPresets.ToArray(),
                capability.Format.ToString());
            Assert.AreEqual(
                capability.SupportsTreeCreation
                    ? ProtocolConstants.MaximumTarCreationEntries
                    : ProtocolConstants.MaximumRawCreationEntries,
                capability.MaximumEntries,
                capability.Format.ToString());
            Assert.IsFalse(capability.SupportsEncryption, capability.Format.ToString());
            Assert.IsFalse(capability.SupportsFilenameEncryption, capability.Format.ToString());
            Assert.IsFalse(capability.SupportsSplitOutput, capability.Format.ToString());
            Assert.IsFalse(capability.SupportsExistingDestinationReplacement, capability.Format.ToString());
            Assert.IsFalse(capability.SupportsInPlaceUpdate, capability.Format.ToString());
        }
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task RealWorkerCreatesCompressedTarPresetsAndRoundTripsThroughRainAndOfficialSevenZip()
    {
        using var root = new TemporaryDirectory();
        var sourceTree = Path.Combine(root.Path, "source-tree");
        Directory.CreateDirectory(Path.Combine(sourceTree, "empty"));
        var unicodeBytes = Encoding.UTF8.GetBytes("compressed TAR — 雨 — 🌧️");
        var unicodeSource = Path.Combine(sourceTree, "雨.txt");
        var zeroSource = Path.Combine(sourceTree, "zero.bin");
        await File.WriteAllBytesAsync(unicodeSource, unicodeBytes);
        await File.WriteAllBytesAsync(zeroSource, []);
        var sourceHash = Hash(unicodeSource);
        var officialTool = CopyPinnedSevenZip(root.Path);
        var recoveryRoot = Path.Combine(root.Path, "recovery");
        Directory.CreateDirectory(recoveryRoot);
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            GetWorkerPath(),
            recoveryRoot);

        var cases = Enum.GetValues<ArchiveCompressionPreset>()
            .Select(preset => (Format: ManagedArchiveCreationFormat.TarGZip, Preset: preset))
            .Append((Format: ManagedArchiveCreationFormat.TarBZip2, Preset: ArchiveCompressionPreset.Balanced));
        foreach (var item in cases)
        {
            var suffix = item.Format == ManagedArchiveCreationFormat.TarGZip ? ".tar.gz" : ".tar.bz2";
            var destination = Path.Combine(root.Path, $"tree-{item.Format}-{item.Preset}{suffix}");
            var started = item.Format == ManagedArchiveCreationFormat.TarGZip
                ? await client.StartTarGZipTreeCreationAsync(
                    destination,
                    [],
                    [new ArchiveCreationDirectory(sourceTree, "Mapped")],
                    item.Preset)
                : await client.StartTarBZip2TreeCreationAsync(
                    destination,
                    [],
                    [new ArchiveCreationDirectory(sourceTree, "Mapped")],
                    item.Preset);
            await AssertCompletedAsync(client, started.OperationId, item.ToString());
            CollectionAssert.AreEqual(sourceHash, Hash(unicodeSource), item.ToString());

            var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["Mapped/雨.txt"] = unicodeBytes,
                ["Mapped/zero.bin"] = [],
            };
            await AssertRainRoundTripAsync(
                client,
                destination,
                item.Format == ManagedArchiveCreationFormat.TarGZip ? "tar.gz" : "tar.bz2",
                expected,
                ["Mapped/", "Mapped/empty/"]);
            await AssertSevenZipRoundTripAsync(
                officialTool,
                root.Path,
                destination,
                expected,
                item.ToString());
        }

        AssertNoStagingArtifacts(root.Path);
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task RealWorkerCreatesRawPresetsWithFinalNameAndExactHashes()
    {
        using var root = new TemporaryDirectory();
        var payload = Encoding.UTF8.GetBytes("raw stream payload — pluie — 雨");
        var source = Path.Combine(root.Path, "unrelated-source-name.txt");
        await File.WriteAllBytesAsync(source, payload);
        var sourceHash = Hash(source);
        var officialTool = CopyPinnedSevenZip(root.Path);
        var recoveryRoot = Path.Combine(root.Path, "recovery");
        Directory.CreateDirectory(recoveryRoot);
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            GetWorkerPath(),
            recoveryRoot);

        var cases = Enum.GetValues<ArchiveCompressionPreset>()
            .Select(preset => (Format: ManagedArchiveCreationFormat.GZip, Preset: preset))
            .Append((Format: ManagedArchiveCreationFormat.BZip2, Preset: ArchiveCompressionPreset.Balanced));
        foreach (var item in cases)
        {
            var suffix = item.Format == ManagedArchiveCreationFormat.GZip ? ".gz" : ".bz2";
            var logicalName = $"final-{item.Format}-{item.Preset}";
            var destination = Path.Combine(root.Path, logicalName + suffix);
            var started = item.Format == ManagedArchiveCreationFormat.GZip
                ? await client.StartGZipCreationAsync(destination, source, item.Preset)
                : await client.StartBZip2CreationAsync(destination, source, item.Preset);
            await AssertCompletedAsync(client, started.OperationId, item.ToString());
            CollectionAssert.AreEqual(sourceHash, Hash(source), item.ToString());

            await AssertRainRoundTripAsync(
                client,
                destination,
                item.Format == ManagedArchiveCreationFormat.GZip ? "gzip" : "bzip2",
                new Dictionary<string, byte[]>(StringComparer.Ordinal) { [logicalName] = payload },
                []);
            await AssertSevenZipRoundTripAsync(
                officialTool,
                root.Path,
                destination,
                new Dictionary<string, byte[]>(StringComparer.Ordinal) { [logicalName] = payload },
                item.ToString());
        }

        AssertNoStagingArtifacts(root.Path);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ManagedCreationPreservesExistingDestinationsAndRejectsRawAlias()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        await File.WriteAllTextAsync(source, "source");
        var sentinel = "existing output"u8.ToArray();
        var recoveryRoot = Path.Combine(root.Path, "recovery");
        Directory.CreateDirectory(recoveryRoot);
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            GetWorkerPath(),
            recoveryRoot);

        foreach (var destination in new[]
        {
            Path.Combine(root.Path, "existing.tar.gz"),
            Path.Combine(root.Path, "existing.bz2"),
        })
        {
            await File.WriteAllBytesAsync(destination, sentinel);
            var started = destination.EndsWith(".tar.gz", StringComparison.Ordinal)
                ? await client.StartTarGZipCreationAsync(
                    destination,
                    [new ArchiveCreationFile(source, "source.txt")])
                : await client.StartBZip2CreationAsync(destination, source);
            var terminal = await client.WaitForTerminalAsync(started.OperationId)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(OperationPhase.Failed, terminal.Phase, destination);
            Assert.AreEqual(ArchiveErrorCategory.DestinationConflict, terminal.Error?.Category, destination);
            Assert.AreEqual("destination.must_not_exist", terminal.Error?.Code, destination);
            CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(destination), destination);
        }

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await client.StartGZipCreationAsync(source, source));
        AssertNoStagingArtifacts(root.Path);
    }

    [TestMethod]
    [TestCategory("ExternalQualification")]
    [DataRow(3)]
    [DataRow(10)]
    [Timeout(120_000)]
    public async Task RealWorkerCreatesRarWithLongPrivateMaterializationPaths(int depth)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RAINARCHIVES_TEST_WINRAR_723")))
        {
            return;
        }

        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source");
        Directory.CreateDirectory(source);
        var current = source;
        var archiveDirectory = "project";
        var expectedDirectories = new List<string> { archiveDirectory + "/" };
        for (var index = 0; index < depth; index++)
        {
            var component = $"capture-空-{index:D2}-0123456789abcdef0123456789abcdef";
            current = Path.Combine(current, component);
            Directory.CreateDirectory(current);
            archiveDirectory += "/" + component;
            expectedDirectories.Add(archiveDirectory + "/");
        }
        Directory.CreateDirectory(Path.Combine(current, "empty"));
        expectedDirectories.Add(archiveDirectory + "/empty/");
        var payload = "Synthetic long-path RAR regression payload."u8.ToArray();
        var sourceFile = Path.Combine(current, "payload.txt");
        await File.WriteAllBytesAsync(sourceFile, payload);
        var originalHash = Hash(sourceFile);
        var destination = Path.Combine(root.Path, "long-path.rar");
        var worker = Environment.GetEnvironmentVariable("RAINARCHIVES_TEST_LONG_PATH_WORKER") ?? GetWorkerPath();
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(worker, root.Path);
        var capabilities = await client.GetRarWriteCapabilitiesAsync();
        Assert.IsTrue(capabilities.CanCreate);
        var started = await client.StartRarTreeCreationAsync(
            destination, [], [new ArchiveCreationDirectory(source, "project")], ArchiveCompressionPreset.Balanced);
        await AssertCompletedAsync(client, started.OperationId, "Long-path RAR creation");
        await AssertRainRoundTripAsync(client, destination, "rar",
            new Dictionary<string, byte[]> { [archiveDirectory + "/payload.txt"] = payload },
            expectedDirectories);
        CollectionAssert.AreEqual(originalHash, Hash(sourceFile));
        AssertNoStagingArtifacts(root.Path);
    }

    private static async Task AssertRainRoundTripAsync(
        ArchiveClient client,
        string archivePath,
        string expectedFormat,
        IReadOnlyDictionary<string, byte[]> expectedFiles,
        IReadOnlyList<string> expectedDirectories)
    {
        var archiveHash = Hash(archivePath);
        var opened = await client.OpenArchiveAsync(archivePath);
        try
        {
            Assert.AreEqual(expectedFormat, opened.Archive.Format);
            var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 128);
            CollectionAssert.AreEqual(
                expectedDirectories.Concat(expectedFiles.Keys).Select(NormalizeListedPath).Order(StringComparer.Ordinal).ToArray(),
                listed.Entries.Select(entry => NormalizeListedPath(entry.RawPath)).Order(StringComparer.Ordinal).ToArray());

            var tested = await client.StartIntegrityTestAsync(opened.Archive.SessionId, []);
            await AssertCompletedAsync(client, tested.OperationId, archivePath);
            var extraction = Path.Combine(
                Path.GetDirectoryName(archivePath)!,
                $"rain-extract-{Guid.NewGuid():N}");
            var extracted = await client.StartExtractionAsync(opened.Archive.SessionId, [], extraction);
            await AssertCompletedAsync(client, extracted.OperationId, archivePath);
            foreach (var expected in expectedFiles)
            {
                var path = Path.Combine(
                    extraction,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar));
                CollectionAssert.AreEqual(expected.Value, await File.ReadAllBytesAsync(path), expected.Key);
            }
        }
        finally
        {
            Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);
        }

        CollectionAssert.AreEqual(archiveHash, Hash(archivePath), archivePath);

        string NormalizeListedPath(string path) => expectedFormat == "rar"
            ? path.Replace('\\', '/').TrimEnd('/')
            : path;
    }

    private static async Task AssertSevenZipRoundTripAsync(
        string tool,
        string root,
        string archive,
        IReadOnlyDictionary<string, byte[]> expectedFiles,
        string context)
    {
        var immutableHash = Hash(archive);
        await RunToolAsync(tool, root, "t", "-bd", "-y", "--", archive);
        var extractionLayer = Path.Combine(root, $"7zip-layer-{Guid.NewGuid():N}");
        await RunToolAsync(tool, root, "x", "-bd", "-y", $"-o{extractionLayer}", "--", archive);
        var extraction = extractionLayer;
        if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || archive.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase))
        {
            var innerTar = Directory.GetFiles(extractionLayer, "*.tar", SearchOption.TopDirectoryOnly)
                .Single();
            extraction = Path.Combine(root, $"7zip-tree-{Guid.NewGuid():N}");
            await RunToolAsync(tool, root, "x", "-bd", "-y", $"-o{extraction}", "--", innerTar);
        }

        foreach (var expected in expectedFiles)
        {
            var path = Path.Combine(
                extraction,
                expected.Key.Replace('/', Path.DirectorySeparatorChar));
            CollectionAssert.AreEqual(expected.Value, await File.ReadAllBytesAsync(path), $"{context}: {expected.Key}");
        }

        CollectionAssert.AreEqual(immutableHash, Hash(archive), context);
    }

    private static async Task AssertCompletedAsync(
        ArchiveClient client,
        OperationId operationId,
        string context)
    {
        var terminal = await client.WaitForTerminalAsync(operationId)
            .WaitAsync(TimeSpan.FromSeconds(60));
        Assert.AreEqual(
            OperationPhase.Completed,
            terminal.Phase,
            $"{context}: {terminal.Error?.Code} ({terminal.Error?.Category}) {terminal.Error?.Message}");
        Assert.IsNull(terminal.Error, context);
    }

    private static string CopyPinnedSevenZip(string root)
    {
        var source = Path.Combine(
            Path.GetTempPath(),
            "RainArchives.Tooling",
            "7zip-26.02",
            "expanded",
            "x64",
            "7za.exe");
        Assert.IsTrue(File.Exists(source), $"Pinned official 7-Zip was not found at '{source}'.");
        Assert.AreEqual(OfficialSevenZipSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
        Assert.AreEqual("26.02", FileVersionInfo.GetVersionInfo(source).ProductVersion);
        var owned = Path.Combine(root, "7za.exe");
        File.Copy(source, owned, overwrite: false);
        Assert.AreEqual(OfficialSevenZipSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(owned))));
        return owned;
    }

    private static async Task RunToolAsync(
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not launch pinned official 7-Zip.");
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

        Assert.AreEqual(
            0,
            process.ExitCode,
            $"7-Zip failed. stdout: {await standardOutput} stderr: {await standardError}");
    }

    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));

    private static void AssertNoStagingArtifacts(string root) =>
        Assert.HasCount(
            0,
            Directory.GetFiles(root, ".rainarchives-*.zip.staging", SearchOption.AllDirectories));

    private static string GetWorkerPath()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        return File.Exists(worker)
            ? worker
            : throw new FileNotFoundException(
                "The integration test output does not contain RainArchives.Worker.exe.",
                worker);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                ".managed-creation-fixtures",
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
