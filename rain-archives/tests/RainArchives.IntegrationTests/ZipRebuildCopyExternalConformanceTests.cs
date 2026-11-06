using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class ZipRebuildCopyExternalConformanceTests
{
    private const string OfficialSevenZipSha256 =
        "35D4D69D7CD6CB44558F208C3B1334268013F9DAF82D2DDA848893A1C30C59C2";

    [TestMethod]
    [Timeout(180_000)]
    public async Task OfficialSevenZipExtra2602TestsAndExtractsEveryRebuildPreset()
    {
        using var root = new TemporaryDirectory();
        var officialTool = GetOfficialSevenZipPath();
        AssertOfficialSevenZip(officialTool);
        var ownedTool = Path.Combine(root.Path, "7za.exe");
        File.Copy(officialTool, ownedTool, overwrite: false);
        AssertOfficialSevenZip(ownedTool);

        var source = Path.Combine(root.Path, "source.zip");
        var replacement = Path.Combine(root.Path, "replacement.txt");
        var addition = Path.Combine(root.Path, "addition.txt");
        var retainedBytes = "retained payload"u8.ToArray();
        var replacementBytes = "new replacement"u8.ToArray();
        var additionBytes = "雨 addition"u8.ToArray();
        var zeroBytes = Array.Empty<byte>();
        CreateSourceZip(source, retainedBytes);
        await File.WriteAllBytesAsync(replacement, replacementBytes);
        await File.WriteAllBytesAsync(addition, additionBytes);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var expectedFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Moved/keep.txt"] = retainedBytes,
            ["replace.txt"] = replacementBytes,
            ["Unicode/雨.txt"] = additionBytes,
            ["zero.bin"] = zeroBytes,
        };

        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());
        var opened = await client.OpenArchiveAsync(source);
        Assert.IsTrue(opened.Archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite));
        var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 64);
        var deletion = listed.Entries.Single(entry => entry.RawPath == "delete.txt");
        var retained = listed.Entries.Single(entry => entry.RawPath == "keep.txt");
        Assert.IsNotNull(deletion.NormalizedPath);
        Assert.IsNotNull(retained.NormalizedPath);

        foreach (var preset in Enum.GetValues<ArchiveCompressionPreset>())
        {
            var destination = Path.Combine(root.Path, $"rebuilt-{preset}.zip");
            var started = await client.StartZipRebuildRenameAsync(
                opened.Archive.SessionId,
                destination,
                [new ArchiveEntryKey(deletion.NormalizedPath, deletion.DuplicateOrdinal)],
                [
                    new ArchiveRebuildCopyFile(replacement, "REPLACE.TXT"),
                    new ArchiveRebuildCopyFile(addition, "Unicode/雨.txt"),
                ],
                [new ArchiveRebuildRename(
                    new ArchiveEntryKey(retained.NormalizedPath!, retained.DuplicateOrdinal),
                    "Moved/keep.txt")],
                preset);
            var terminal = await client
                .WaitForTerminalAsync(started.OperationId)
                .WaitAsync(TimeSpan.FromSeconds(60));
            Assert.AreEqual(
                OperationPhase.CompletedWithWarnings,
                terminal.Phase,
                $"{preset}: {terminal.Error?.Code} {terminal.Error?.Message}");
            Assert.AreEqual("zip.rebuild_metadata_limited", terminal.Warning?.Code);
            CollectionAssert.AreEqual(
                sourceHash,
                SHA256.HashData(await File.ReadAllBytesAsync(source)),
                preset.ToString());

            var archiveHash = SHA256.HashData(await File.ReadAllBytesAsync(destination));
            await RunSevenZipAsync(ownedTool, root.Path, "t", "-bd", "-y", "--", destination);
            CollectionAssert.AreEqual(
                archiveHash,
                SHA256.HashData(await File.ReadAllBytesAsync(destination)),
                $"{preset}: 7-Zip test changed the rebuilt archive.");

            var extraction = Path.Combine(root.Path, $"official-extract-{preset}");
            await RunSevenZipAsync(
                ownedTool,
                root.Path,
                "x",
                "-bd",
                "-y",
                $"-o{extraction}",
                "--",
                destination);
            CollectionAssert.AreEqual(
                archiveHash,
                SHA256.HashData(await File.ReadAllBytesAsync(destination)),
                $"{preset}: 7-Zip extraction changed the rebuilt archive.");

            var extractedFiles = Directory.GetFiles(extraction, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(extraction, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(
                expectedFiles.Keys.Order(StringComparer.Ordinal).ToArray(),
                extractedFiles,
                preset.ToString());
            foreach (var expected in expectedFiles)
            {
                var extractedPath = Path.Combine(
                    extraction,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar));
                CollectionAssert.AreEqual(
                    SHA256.HashData(expected.Value),
                    SHA256.HashData(await File.ReadAllBytesAsync(extractedPath)),
                    $"{preset}: {expected.Key}");
            }

            var extractedDirectories = Directory
                .GetDirectories(extraction, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(extraction, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(
                new[] { "Moved", "Unicode", "empty" },
                extractedDirectories,
                preset.ToString());
            Assert.IsFalse(File.Exists(Path.Combine(extraction, "delete.txt")), preset.ToString());
            AssertOfficialSevenZip(ownedTool);
        }

        AssertOfficialSevenZip(officialTool);
        CollectionAssert.AreEqual(
            sourceHash,
            SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.HasCount(
            0,
            Directory.GetFiles(
                root.Path,
                ".rainarchives-*.zip.staging",
                SearchOption.TopDirectoryOnly));
    }

    private static void CreateSourceZip(string path, byte[] retainedBytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        _ = archive.CreateEntry("empty/", CompressionLevel.NoCompression);
        WriteEntry(archive, "keep.txt", retainedBytes);
        WriteEntry(archive, "replace.txt", "old replacement"u8.ToArray());
        WriteEntry(archive, "delete.txt", "delete me"u8.ToArray());
        WriteEntry(archive, "zero.bin", []);
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] payload)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var destination = entry.Open();
        destination.Write(payload);
    }

    private static async Task RunSevenZipAsync(
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
            ?? throw new InvalidOperationException("Could not launch the pinned official 7-Zip binary.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
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
            $"Official 7-Zip failed. stdout: {Truncate(output)} stderr: {Truncate(error)}");
    }

    private static string Truncate(string value) =>
        value.Length <= 4_096 ? value : value[..4_096];

    private static void AssertOfficialSevenZip(string path)
    {
        Assert.IsTrue(File.Exists(path), $"Official 7-Zip Extra 26.02 was not found at '{path}'.");
        var file = new FileInfo(path);
        Assert.AreEqual(1_334_784L, file.Length);
        Assert.AreEqual(
            OfficialSevenZipSha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.AreEqual("26.02", FileVersionInfo.GetVersionInfo(path).ProductVersion);
    }

    private static string GetOfficialSevenZipPath() =>
        Path.Combine(
            Path.GetTempPath(),
            "RainArchives.Tooling",
            "7zip-26.02",
            "expanded",
            "x64",
            "7za.exe");

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
                "RainArchives.ZipRebuildCopyExternalConformanceTests",
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
