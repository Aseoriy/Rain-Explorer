using System.Formats.Tar;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Cli;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class CliTarCreationTests
{
    [TestMethod]
    public void ParserBuildsStoreOnlyTarRequestsAndRejectsUnsafeWriteOptions()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "created.tar");
        var file = Path.Combine(root.Path, "source.txt");
        var directory = Path.Combine(root.Path, "tree");
        var worker = Path.Combine(root.Path, "worker.exe");

        Assert.IsTrue(CliArguments.TryParse(
            [
                "create-tar",
                destination,
                "--file",
                file,
                "Folder/source.txt",
                "--directory",
                directory,
                "Tree",
                "--preset",
                "store",
                "--worker",
                worker,
            ],
            out var options,
            out var error), error);
        Assert.AreEqual(CliCommand.CreateTar, options.Command);
        Assert.AreEqual(Path.GetFullPath(destination), options.ArchivePath);
        Assert.AreEqual(Path.GetFullPath(worker), options.WorkerPath);
        Assert.AreEqual(ArchiveCompressionPreset.Store, options.CompressionPreset);
        Assert.HasCount(1, options.CreationFiles);
        Assert.HasCount(1, options.CreationDirectories);
        Assert.AreEqual("Folder/source.txt", options.CreationFiles[0].ArchivePath);
        Assert.AreEqual("Tree", options.CreationDirectories[0].ArchivePath);

        foreach (var preset in new[] { "fast", "balanced", "maximum" })
        {
            Assert.IsFalse(CliArguments.TryParse(
                ["create-tar", destination, "--preset", preset],
                out _,
                out var presetError));
            StringAssert.Contains(presetError, "only --preset store");
        }

        Assert.IsFalse(CliArguments.TryParse(
            ["create-tar", destination, "--password-stdin"],
            out _,
            out var passwordError));
        StringAssert.Contains(passwordError, "does not support encryption");
        Assert.IsFalse(CliArguments.TryParse(
            ["create-tar", destination, "--overwrite"],
            out _,
            out var overwriteError));
        StringAssert.Contains(overwriteError, "Unknown option");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task DiagnosticCliCreatesExplicitAndTreeTarThroughPackagedWorker()
    {
        using var root = new TemporaryDirectory();
        var explicitSource = Path.Combine(root.Path, "explicit.txt");
        var tree = Path.Combine(root.Path, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "empty"));
        var treeSource = Path.Combine(tree, "雨.txt");
        await File.WriteAllBytesAsync(
            explicitSource,
            Encoding.UTF8.GetBytes("CLI explicit TAR"));
        await File.WriteAllBytesAsync(
            treeSource,
            Encoding.UTF8.GetBytes("CLI tree TAR — 雨"));

        var explicitDestination = Path.Combine(root.Path, "explicit.tar");
        Assert.AreEqual(0, await CliProgram.RunAsync(
            [
                "create-tar",
                explicitDestination,
                "--file",
                explicitSource,
                "Folder/explicit.txt",
                "--worker",
                GetWorkerPath(),
            ]));
        var explicitEntries = await ReadTarAsync(explicitDestination);
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("CLI explicit TAR"),
            explicitEntries.Single(entry => entry.Name == "Folder/explicit.txt").Payload);

        var treeDestination = Path.Combine(root.Path, "tree.tar");
        Assert.AreEqual(0, await CliProgram.RunAsync(
            [
                "create-tar",
                treeDestination,
                "--directory",
                tree,
                "Mapped",
                "--worker",
                GetWorkerPath(),
            ]));
        var treeEntries = await ReadTarAsync(treeDestination);
        CollectionAssert.Contains(treeEntries.Select(entry => entry.Name).ToArray(), "Mapped/");
        CollectionAssert.Contains(treeEntries.Select(entry => entry.Name).ToArray(), "Mapped/empty/");
        CollectionAssert.AreEqual(
            Encoding.UTF8.GetBytes("CLI tree TAR — 雨"),
            treeEntries.Single(entry => entry.Name == "Mapped/雨.txt").Payload);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DiagnosticCliPreservesExistingTarDestination()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "existing.tar");
        await File.WriteAllTextAsync(source, "source");
        var sentinel = "existing destination"u8.ToArray();
        await File.WriteAllBytesAsync(destination, sentinel);

        var exitCode = await CliProgram.RunAsync(
            [
                "create-tar",
                destination,
                "--file",
                source,
                "source.txt",
                "--worker",
                GetWorkerPath(),
            ]);

        Assert.AreNotEqual(0, exitCode);
        CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(destination));
        Assert.HasCount(
            0,
            Directory.GetFiles(root.Path, ".rainarchives-*.zip.staging", SearchOption.TopDirectoryOnly));
    }

    private static async Task<IReadOnlyList<TarItem>> ReadTarAsync(string path)
    {
        var entries = new List<TarItem>();
        await using var source = File.OpenRead(path);
        await using var reader = new TarReader(source, leaveOpen: false);
        while (await reader.GetNextEntryAsync(copyData: true, CancellationToken.None) is { } entry)
        {
            using var payload = new MemoryStream();
            if (entry.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(payload);
            }

            entries.Add(new TarItem(entry.Name, entry.EntryType, payload.ToArray()));
        }

        return entries;
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

    private sealed record TarItem(string Name, TarEntryType Type, byte[] Payload);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                ".cli-tar-fixtures",
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
