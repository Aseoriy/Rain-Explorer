using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Cli;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class CliManagedCreationTests
{
    [TestMethod]
    public void ParserBuildsDistinctManagedRequestsAndRejectsUnsafeOptions()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var directory = Path.Combine(root.Path, "tree");
        var worker = Path.Combine(root.Path, "worker.exe");

        Assert.IsTrue(CliArguments.TryParse(
            [
                "create-tar-gz", Path.Combine(root.Path, "new.tar.gz"),
                "--file", source, "Mapped/source.txt",
                "--directory", directory, "Tree",
                "--preset", "maximum", "--worker", worker,
            ],
            out var tarGzip,
            out var tarGzipError), tarGzipError);
        Assert.AreEqual(CliCommand.CreateTarGZip, tarGzip.Command);
        Assert.AreEqual(ArchiveCompressionPreset.Maximum, tarGzip.CompressionPreset);
        Assert.HasCount(1, tarGzip.CreationFiles);
        Assert.HasCount(1, tarGzip.CreationDirectories);

        Assert.IsTrue(CliArguments.TryParse(
            [
                "create-gzip", Path.Combine(root.Path, "logical-name.gz"), source,
                "--preset", "fast", "--worker", worker,
            ],
            out var gzip,
            out var gzipError), gzipError);
        Assert.AreEqual(CliCommand.CreateGZip, gzip.Command);
        Assert.AreEqual(Path.GetFullPath(source), gzip.RawSourcePath);
        Assert.HasCount(0, gzip.CreationFiles);
        Assert.HasCount(0, gzip.CreationDirectories);

        Assert.IsFalse(CliArguments.TryParse(
            ["create-bzip2", Path.Combine(root.Path, "out.bz2"), source, "--preset", "fast"],
            out _,
            out var bzipPresetError));
        StringAssert.Contains(bzipPresetError, "only --preset balanced");
        Assert.IsFalse(CliArguments.TryParse(
            ["create-gzip", Path.Combine(root.Path, "out.zip"), source],
            out _,
            out var suffixError));
        StringAssert.Contains(suffixError, "must end in .gz or .gzip");
        Assert.IsFalse(CliArguments.TryParse(
            ["create-gzip", Path.Combine(root.Path, "out.gz"), source, "--directory", directory, "Tree"],
            out _,
            out var directoryError));
        StringAssert.Contains(directoryError, "TAR-family");
        Assert.IsFalse(CliArguments.TryParse(
            ["create-tar-gz", Path.Combine(root.Path, "out.tar.gz"), "--password-stdin"],
            out _,
            out var encryptionError));
        StringAssert.Contains(encryptionError, "does not support encryption");
        Assert.IsFalse(CliArguments.TryParse(
            ["create-tar-bz2", Path.Combine(root.Path, "out.tar.bz2"), "--overwrite"],
            out _,
            out var overwriteError));
        StringAssert.Contains(overwriteError, "Unknown option");
        Assert.IsFalse(CliArguments.TryParse(
            ["create-7z", Path.Combine(root.Path, "out.7z")],
            out _,
            out var sevenZipError));
        StringAssert.Contains(sevenZipError, "Unknown command");
        Assert.IsFalse(CliArguments.TryParse(
            ["create-rar", Path.Combine(root.Path, "out.rar")],
            out _,
            out var rarError));
        StringAssert.Contains(rarError, "Unknown command");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task DiagnosticCliCreatesEveryManagedFormatThroughPackagedWorker()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var payload = "CLI managed creation — 雨"u8.ToArray();
        await File.WriteAllBytesAsync(source, payload);
        var worker = GetWorkerPath();
        var cases = new[]
        {
            new CliCase(
                ["create-tar-gz", Path.Combine(root.Path, "cli.tar.gz"), "--file", source, "Mapped/item.txt", "--preset", "fast", "--worker", worker],
                Path.Combine(root.Path, "cli.tar.gz"), "tar.gz", "Mapped/item.txt"),
            new CliCase(
                ["create-tar-bz2", Path.Combine(root.Path, "cli.tar.bz2"), "--file", source, "Mapped/item.txt", "--worker", worker],
                Path.Combine(root.Path, "cli.tar.bz2"), "tar.bz2", "Mapped/item.txt"),
            new CliCase(
                ["create-gzip", Path.Combine(root.Path, "gzip-name.gz"), source, "--preset", "maximum", "--worker", worker],
                Path.Combine(root.Path, "gzip-name.gz"), "gzip", "gzip-name"),
            new CliCase(
                ["create-bzip2", Path.Combine(root.Path, "bzip-name.bz2"), source, "--worker", worker],
                Path.Combine(root.Path, "bzip-name.bz2"), "bzip2", "bzip-name"),
        };

        foreach (var item in cases)
        {
            Assert.AreEqual(0, await CliProgram.RunAsync(item.Arguments), item.ArchivePath);
            var archiveHash = SHA256.HashData(await File.ReadAllBytesAsync(item.ArchivePath));
            await using var client = await ArchiveClient.StartAsync(worker);
            var opened = await client.OpenArchiveAsync(item.ArchivePath);
            try
            {
                Assert.AreEqual(item.Format, opened.Archive.Format, item.ArchivePath);
                var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 16);
                var entry = listed.Entries.Single(candidate => candidate.RawPath == item.EntryPath);
                await using var lease = await client.OpenEntryStreamAsync(
                    opened.Archive.SessionId,
                    entry.EntryId,
                    payload.Length + 1);
                using var actual = new MemoryStream();
                await lease.Content.CopyToAsync(actual);
                CollectionAssert.AreEqual(payload, actual.ToArray(), item.ArchivePath);
            }
            finally
            {
                Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);
            }

            CollectionAssert.AreEqual(
                archiveHash,
                SHA256.HashData(await File.ReadAllBytesAsync(item.ArchivePath)),
                item.ArchivePath);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DiagnosticCliPreservesExistingManagedDestination()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "existing.gz");
        await File.WriteAllTextAsync(source, "source");
        var sentinel = "existing"u8.ToArray();
        await File.WriteAllBytesAsync(destination, sentinel);

        var exitCode = await CliProgram.RunAsync(
            ["create-gzip", destination, source, "--worker", GetWorkerPath()]);

        Assert.AreNotEqual(0, exitCode);
        CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(destination));
        Assert.HasCount(
            0,
            Directory.GetFiles(root.Path, ".rainarchives-*.zip.staging", SearchOption.AllDirectories));
    }

    private static string GetWorkerPath()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        return File.Exists(worker)
            ? worker
            : throw new FileNotFoundException(
                "The integration test output does not contain RainArchives.Worker.exe.",
                worker);
    }

    private sealed record CliCase(
        string[] Arguments,
        string ArchivePath,
        string Format,
        string EntryPath);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                ".cli-managed-fixtures",
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
