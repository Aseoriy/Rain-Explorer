using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

/// <summary>Protocol 1.25: 7z, TAR.XZ and XZ written by the real Worker through the bundled 7-Zip
/// engine, then checked by the pinned official 7-Zip 26.02 tool and by Rain's own reader.</summary>
[TestClass]
[DoNotParallelize]
public sealed class SevenZipXzCreationEndToEndTests
{
    private const string OfficialSevenZipSha256 =
        "35D4D69D7CD6CB44558F208C3B1334268013F9DAF82D2DDA848893A1C30C59C2";

    [TestMethod]
    [Timeout(30_000)]
    public async Task RealWorkerNegotiatesAndReportsTheSevenZipFormats()
    {
        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());

        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.SevenZipXzCreation));
        var capabilities = await client.GetManagedWriteCapabilitiesAsync();
        Assert.HasCount(7, capabilities.Capabilities);
        foreach (var format in new[]
                 {
                     ManagedArchiveCreationFormat.SevenZip,
                     ManagedArchiveCreationFormat.TarXz,
                     ManagedArchiveCreationFormat.Xz,
                 })
        {
            var capability = capabilities.Capabilities.Single(item => item.Format == format);
            Assert.IsTrue(capability.CanCreate, $"{format} should be creatable with the bundled engine.");
            Assert.AreEqual(format != ManagedArchiveCreationFormat.Xz, capability.SupportsTreeCreation);
            Assert.IsFalse(capability.SupportsEncryption);
            Assert.IsFalse(capability.SupportsExistingDestinationReplacement);
        }
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task RealWorkerCreatesSevenZipTreeThatOfficialSevenZipAccepts()
    {
        using var root = new TemporaryDirectory();
        var (tree, files) = await CreateSourceTreeAsync(root.Path);
        var destination = Path.Combine(root.Path, "Photos.7z");

        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(GetWorkerPath(), root.Path);
        var started = await client.StartSevenZipTreeCreationAsync(
            destination, [], [new ArchiveCreationDirectory(tree, "Photos")]);
        await AssertCompletedAsync(client, started.OperationId);

        AssertNoLeftovers(root.Path, "Photos.7z");
        var extracted = await AssertOfficialToolExtractsAsync(destination, root.Path);
        if (extracted is not null) AssertTree(extracted, files, ["Photos/empty dir"]);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task RealWorkerCreatesTarXzTreeThatOfficialSevenZipAccepts()
    {
        using var root = new TemporaryDirectory();
        var (tree, files) = await CreateSourceTreeAsync(root.Path);
        var destination = Path.Combine(root.Path, "Photos.tar.xz");

        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(GetWorkerPath(), root.Path);
        var started = await client.StartTarXzTreeCreationAsync(
            destination, [], [new ArchiveCreationDirectory(tree, "Photos")], ArchiveCompressionPreset.Maximum);
        await AssertCompletedAsync(client, started.OperationId);

        AssertNoLeftovers(root.Path, "Photos.tar.xz");
        var tool = GetPinnedSevenZip();
        if (tool is null) return;
        // 7-Zip opens the .xz, which holds the .tar.
        var stage = Path.Combine(root.Path, "xz-out");
        await RunToolAsync(tool, $"x \"{destination}\" -o\"{stage}\" -y");
        var tar = Directory.GetFiles(stage).Single();
        var extracted = Path.Combine(root.Path, "tar-out");
        await RunToolAsync(tool, $"x \"{tar}\" -o\"{extracted}\" -y");
        AssertTree(extracted, files, ["Photos/empty dir"]);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task RealWorkerCreatesRawXzOfOneFile()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "notes.txt");
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("xz payload — 雨 — ", 4000)));
        await File.WriteAllBytesAsync(source, payload);
        var destination = Path.Combine(root.Path, "notes.txt.xz");

        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(GetWorkerPath(), root.Path);
        var started = await client.StartXzCreationAsync(destination, source, ArchiveCompressionPreset.Fast);
        await AssertCompletedAsync(client, started.OperationId);

        Assert.IsTrue(new FileInfo(destination).Length < payload.Length / 4, "XZ output should be compressed.");
        var tool = GetPinnedSevenZip();
        if (tool is null) return;
        var extracted = Path.Combine(root.Path, "xz-out");
        await RunToolAsync(tool, $"x \"{destination}\" -o\"{extracted}\" -y");
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Directory.GetFiles(extracted).Single()));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task RealWorkerCreatesSevenZipOfAnEmptyFolder()
    {
        using var root = new TemporaryDirectory();
        var empty = Path.Combine(root.Path, "Sail Stuff");
        Directory.CreateDirectory(empty);
        var destination = Path.Combine(root.Path, "Sail Stuff.7z");

        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(GetWorkerPath(), root.Path);
        var started = await client.StartSevenZipTreeCreationAsync(
            destination, [], [new ArchiveCreationDirectory(empty, "Sail Stuff")]);
        await AssertCompletedAsync(client, started.OperationId);

        var extracted = await AssertOfficialToolExtractsAsync(destination, root.Path);
        if (extracted is not null) AssertTree(extracted, [], ["Sail Stuff"]);
    }

    /// <summary>Regression: the TAR self-check used to treat two zero blocks inside file data
    /// (normal in .exe/.dll files) as the end of the archive and reject the output.</summary>
    [TestMethod]
    [Timeout(120_000)]
    public async Task RealWorkerTarFormatsAcceptFilesWithLongZeroRuns()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "Wub");
        Directory.CreateDirectory(tree);
        var binary = new byte[810_000];
        new Random(9).NextBytes(binary);
        binary.AsSpan(4_096, 64 * 1024).Clear();      // block-aligned zero run, then more data
        binary.AsSpan(300_000, 5_000).Clear();        // unaligned zero run
        await File.WriteAllBytesAsync(Path.Combine(tree, "Wub.exe"), binary);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["Wub/Wub.exe"] = binary };

        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(GetWorkerPath(), root.Path);
        var directories = new[] { new ArchiveCreationDirectory(tree, "Wub") };
        var tar = await client.StartTarTreeCreationAsync(Path.Combine(root.Path, "Wub.tar"), [], directories);
        await AssertCompletedAsync(client, tar.OperationId);
        var tarGz = await client.StartTarGZipTreeCreationAsync(Path.Combine(root.Path, "Wub.tar.gz"), [], directories);
        await AssertCompletedAsync(client, tarGz.OperationId);
        var tarXz = await client.StartTarXzTreeCreationAsync(Path.Combine(root.Path, "Wub.tar.xz"), [], directories);
        await AssertCompletedAsync(client, tarXz.OperationId);

        var tool = GetPinnedSevenZip();
        if (tool is null) return;
        var extracted = Path.Combine(root.Path, "tar-out");
        await RunToolAsync(tool, $"x \"{Path.Combine(root.Path, "Wub.tar")}\" -o\"{extracted}\" -y");
        AssertTree(extracted, files, []);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task SevenZipCreationRefusesExistingDestinationAndPreservesOriginal()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        await File.WriteAllTextAsync(source, "source");
        var destination = Path.Combine(root.Path, "existing.7z");
        var sentinel = Encoding.UTF8.GetBytes("do not replace this 7z");
        await File.WriteAllBytesAsync(destination, sentinel);

        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(GetWorkerPath(), root.Path);
        var started = await client.StartSevenZipCreationAsync(destination, [new ArchiveCreationFile(source, "source.txt")]);
        var terminal = await client.WaitForTerminalAsync(started.OperationId).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.AreEqual(OperationPhase.Failed, terminal.Phase);
        Assert.AreEqual(ArchiveErrorCategory.DestinationConflict, terminal.Error?.Category);
        CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(destination));
    }

    private static async Task<(string Tree, Dictionary<string, byte[]> Files)> CreateSourceTreeAsync(string root)
    {
        var tree = Path.Combine(root, "source-tree");
        Directory.CreateDirectory(Path.Combine(tree, "empty dir"));
        Directory.CreateDirectory(Path.Combine(tree, "sub"));
        var unicode = Encoding.UTF8.GetBytes("7z payload — pluie — 雨 — 🌧️");
        var big = new byte[3_000_000];
        new Random(25).NextBytes(big.AsSpan(0, 1_000_000));
        big.AsSpan(1_000_000).Fill((byte)'r');
        await File.WriteAllBytesAsync(Path.Combine(tree, "雨 notes.txt"), unicode);
        await File.WriteAllBytesAsync(Path.Combine(tree, "zero.bin"), []);
        await File.WriteAllBytesAsync(Path.Combine(tree, "sub", "big.bin"), big);
        return (tree, new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Photos/雨 notes.txt"] = unicode,
            ["Photos/zero.bin"] = [],
            ["Photos/sub/big.bin"] = big,
        });
    }

    private static async Task AssertCompletedAsync(ArchiveClient client, OperationId operationId)
    {
        var terminal = await client.WaitForTerminalAsync(operationId).WaitAsync(TimeSpan.FromSeconds(90));
        Assert.AreEqual(
            OperationPhase.Completed,
            terminal.Phase,
            $"{terminal.Error?.Code} ({terminal.Error?.Category}) {terminal.Error?.Message}");
    }

    private static void AssertNoLeftovers(string root, string archiveName)
    {
        var unexpected = Directory.GetFiles(root)
            .Select(Path.GetFileName)
            .Where(name => name != archiveName && name!.Contains(archiveName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.HasCount(0, unexpected, $"Staging leftovers: {string.Join(", ", unexpected)}");
    }

    private static async Task<string?> AssertOfficialToolExtractsAsync(string archive, string root)
    {
        var tool = GetPinnedSevenZip();
        if (tool is null) return null;
        var test = await RunToolAsync(tool, $"t \"{archive}\"");
        StringAssert.Contains(test, "Everything is Ok");
        var extracted = Path.Combine(root, "7z-out");
        await RunToolAsync(tool, $"x \"{archive}\" -o\"{extracted}\" -y");
        return extracted;
    }

    private static void AssertTree(string extracted, Dictionary<string, byte[]> files, string[] emptyDirectories)
    {
        foreach (var (path, bytes) in files)
        {
            var full = Path.Combine(extracted, path.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(full), $"Missing {path}");
            CollectionAssert.AreEqual(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(full)), path);
        }

        foreach (var directory in emptyDirectories)
        {
            var full = Path.Combine(extracted, directory.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(Directory.Exists(full), $"Missing empty folder {directory}");
            Assert.HasCount(0, Directory.GetFileSystemEntries(full));
        }
    }

    /// <summary>The pinned official 7-Zip Extra 26.02 7za.exe, used only as an outside reader.
    /// Returns null (checks skipped, Rain's own read-back still ran) when it isn't installed.</summary>
    private static string? GetPinnedSevenZip()
    {
        var path = Path.Combine(Path.GetTempPath(), "RainArchives.Tooling", "7zip-26.02", "expanded", "x64", "7za.exe");
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        Assert.AreEqual(OfficialSevenZipSha256, Convert.ToHexString(SHA256.HashData(stream)));
        return path;
    }

    private static async Task<string> RunToolAsync(string executable, string arguments)
    {
        var info = new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, $"7za {arguments} failed: {output} {error}");
        return output;
    }

    private static string GetWorkerPath()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        if (!File.Exists(worker))
        {
            throw new FileNotFoundException("The integration test output does not contain RainArchives.Worker.exe.", worker);
        }

        return worker;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(AppContext.BaseDirectory, "sevenzip-xz-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
