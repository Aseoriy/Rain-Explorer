using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Cli;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class CliZipRebuildCopyTests
{
    [TestMethod]
    public void MetadataParserAcceptsExactDosBoundariesAndTypedTargets()
    {
        using var root = new TemporaryDirectory();
        var parsed = CliArguments.TryParse(
            [
                "rebuild-zip", Path.Combine(root.Path, "source.zip"), Path.Combine(root.Path, "new.zip"),
                "--metadata-key", "folder/file.txt", "2", "1980-01-01T00:00:00", "ro,archive",
                "--metadata-path", "empty/", "directory", "2107-12-31T23:59:58", "0x0000",
                "--confirm-rebuild-copy",
            ],
            out var options,
            out var error);

        Assert.IsTrue(parsed, error);
        Assert.HasCount(2, options.RebuildMetadata!);
        var metadata = options.RebuildMetadata!;
        Assert.AreEqual(
            new ArchiveRebuildMetadataTarget(
                ArchiveRebuildMetadataTargetKind.RetainedEntryKey,
                new ArchiveEntryKey("folder/file.txt", 2)),
            metadata[0].Target);
        Assert.AreEqual(
            new ArchiveDosLocalTimestamp(1980, 1, 1, 0, 0, 0),
            metadata[0].LastWriteTime);
        Assert.AreEqual((ushort)0x0021, metadata[0].AttributesLowWord);
        Assert.AreEqual(ArchiveEntryKind.Directory, metadata[1].Target.MappedKind);
    }

    [TestMethod]
    public void MetadataParserRejectsOffsetsFractionalOddInvalidAndUnsupportedValues()
    {
        using var root = new TemporaryDirectory();
        var prefix = new[] { "rebuild-zip", Path.Combine(root.Path, "source.zip"), Path.Combine(root.Path, "new.zip") };
        foreach (var timestamp in new[]
        {
            "1979-12-31T23:59:58", "2108-01-01T00:00:00", "2026-02-30T00:00:00",
            "2026-01-01T00:00:01", "2026-01-01T00:00:59", "2026-01-01T00:00:00.000",
            "2026-01-01T00:00:00Z", "2026-01-01T00:00:00+01:00",
        })
        {
            Assert.IsFalse(
                CliArguments.TryParse(
                    [.. prefix, "--metadata-path", "file.txt", "file", timestamp, "none", "--confirm-rebuild-copy"],
                    out _,
                    out var error),
                timestamp);
            StringAssert.Contains(error, "exact archive time", timestamp);
        }

        foreach (var attributes in new[] { "0x0010", "0x0040", "directory", ",", "ro,unknown" })
        {
            Assert.IsFalse(
                CliArguments.TryParse(
                    [.. prefix, "--metadata-path", "file.txt", "file", "2026-01-01T00:00:00", attributes, "--confirm-rebuild-copy"],
                    out _,
                    out var error),
                attributes);
            StringAssert.Contains(error, "allowed attributes", attributes);
        }

        Assert.IsFalse(
            CliArguments.TryParse(
                [.. prefix, "--metadata-path", "file.txt", "file", "-", "-", "--confirm-rebuild-copy"],
                out _,
                out var noOpError));
        StringAssert.Contains(noOpError, "exact archive time");
    }

    [TestMethod]
    public void ParserBuildsExplicitRebuildCopyRequestAndPreservesOperationKinds()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var destination = Path.Combine(root.Path, "new.zip");
        var addSource = Path.Combine(root.Path, "add.txt");
        var replaceSource = Path.Combine(root.Path, "replace.txt");
        var worker = Path.Combine(root.Path, "worker.exe");

        var parsed = CliArguments.TryParse(
            [
                "rebuild-zip",
                sourceArchive,
                destination,
                "--preset",
                "maximum",
                "--delete",
                "folder/delete.txt",
                "1",
                "--add",
                addSource,
                "folder/added.txt",
                "--replace",
                replaceSource,
                "folder/replaced.txt",
                "--confirm-rebuild-copy",
                "--password-stdin",
                "--worker",
                worker,
            ],
            out var options,
            out var error);

        Assert.IsTrue(parsed, error);
        Assert.AreEqual(CliCommand.RebuildZip, options.Command);
        Assert.AreEqual(Path.GetFullPath(sourceArchive), options.ArchivePath);
        Assert.AreEqual(Path.GetFullPath(destination), options.DestinationPath);
        Assert.AreEqual(Path.GetFullPath(worker), options.WorkerPath);
        Assert.IsTrue(options.PasswordFromStandardInput);
        Assert.AreEqual(ArchiveCompressionPreset.Maximum, options.CompressionPreset);
        Assert.HasCount(1, options.RebuildEntriesToDelete);
        Assert.AreEqual(
            new ArchiveEntryKey("folder/delete.txt", 1),
            options.RebuildEntriesToDelete[0]);
        Assert.HasCount(2, options.RebuildFiles);
        Assert.AreEqual(Path.GetFullPath(addSource), options.RebuildFiles[0].SourcePath);
        Assert.AreEqual("folder/added.txt", options.RebuildFiles[0].ArchivePath);
        Assert.IsFalse(options.RebuildFiles[0].IsReplacement);
        Assert.AreEqual(Path.GetFullPath(replaceSource), options.RebuildFiles[1].SourcePath);
        Assert.AreEqual("folder/replaced.txt", options.RebuildFiles[1].ArchivePath);
        Assert.IsTrue(options.RebuildFiles[1].IsReplacement);
    }

    [TestMethod]
    public void ParserRejectsUnconfirmedMalformedAndConflictingRebuildCopyRequests()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var destination = Path.Combine(root.Path, "new.zip");
        var source = Path.Combine(root.Path, "source.txt");
        var replacementSource = Path.Combine(root.Path, "replacement.txt");

        Assert.IsFalse(CliArguments.TryParse(
            ["rebuild-zip", sourceArchive, destination],
            out _,
            out var missingConfirmation));
        StringAssert.Contains(missingConfirmation, "--confirm-rebuild-copy");

        Assert.IsFalse(CliArguments.TryParse(
            [
                "rebuild-zip", sourceArchive, destination,
                "--delete", "entry.txt", "-1",
                "--confirm-rebuild-copy",
            ],
            out _,
            out var malformedOrdinal));
        StringAssert.Contains(malformedOrdinal, "non-negative decimal");

        Assert.IsFalse(CliArguments.TryParse(
            [
                "rebuild-zip", sourceArchive, destination,
                "--add", source,
                "--confirm-rebuild-copy",
            ],
            out _,
            out var missingAddValue));
        StringAssert.Contains(missingAddValue, "--add requires");

        Assert.IsFalse(CliArguments.TryParse(
            [
                "rebuild-zip", sourceArchive, destination,
                "--add", source, "entry.txt",
                "--replace", replacementSource, "entry.txt",
                "--confirm-rebuild-copy",
            ],
            out _,
            out var conflictingTarget));
        StringAssert.Contains(conflictingTarget, "may not conflict");

        Assert.IsFalse(CliArguments.TryParse(
            [
                "rebuild-zip", sourceArchive, destination,
                "--delete", "entry.txt", "0",
                "--delete", "entry.txt", "0",
                "--confirm-rebuild-copy",
            ],
            out _,
            out var duplicateDelete));
        StringAssert.Contains(duplicateDelete, "may not be repeated");

        Assert.IsFalse(CliArguments.TryParse(
            [
                "rebuild-zip", sourceArchive, destination,
                "--add", destination, "entry.txt",
                "--confirm-rebuild-copy",
            ],
            out _,
            out var destinationAlias));
        StringAssert.Contains(destinationAlias, "must not alias the destination");
    }

    [TestMethod]
    public void ParserBuildsAndValidatesExplicitPersistentRename()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var destination = Path.Combine(root.Path, "new.zip");

        var parsed = CliArguments.TryParse(
            [
                "rebuild-zip", sourceArchive, destination,
                "--rename", "folder\\old.txt", "2", "renamed/new.txt",
                "--confirm-rebuild-copy",
            ],
            out var options,
            out var error);

        Assert.IsTrue(parsed, error);
        Assert.HasCount(1, options.RebuildRenames);
        Assert.AreEqual(
            new CliRebuildRename(
                new ArchiveEntryKey("folder/old.txt", 2),
                "renamed/new.txt"),
            options.RebuildRenames[0]);

        Assert.IsFalse(CliArguments.TryParse(
            [
                "rebuild-zip", sourceArchive, destination,
                "--rename", "folder/old.txt", "-1", "renamed/new.txt",
                "--confirm-rebuild-copy",
            ],
            out _,
            out var malformedOrdinal));
        StringAssert.Contains(malformedOrdinal, "non-negative decimal");

        Assert.IsFalse(CliArguments.TryParse(
            [
                "rebuild-zip", sourceArchive, destination,
                "--rename", "folder/old.txt", "0", "renamed/",
                "--confirm-rebuild-copy",
            ],
            out _,
            out var directoryTarget));
        StringAssert.Contains(directoryTarget, "final archive-relative file path");

        Assert.IsFalse(CliArguments.TryParse(
            [
                "rebuild-zip", sourceArchive, destination,
                "--rename", "folder/old.txt", "0", "renamed/new.txt",
                "--rename", "FOLDER/OLD.TXT", "0", "other.txt",
                "--confirm-rebuild-copy",
            ],
            out _,
            out var duplicateKey));
        StringAssert.Contains(duplicateKey, "replaced, or renamed only once");
    }

    [TestMethod]
    public void ParserBuildsCanonicalBoundedTreeEditArguments()
    {
        using var root = new TemporaryDirectory();
        var revision = new string('a', 64);
        var parsed = CliArguments.TryParse(
            [
                "rebuild-zip",
                Path.Combine(root.Path, "source.zip"),
                Path.Combine(root.Path, "destination.zip"),
                "--add-folder",
                Path.Combine(root.Path, "folder"),
                "Imported/",
                "--move-folder",
                "tree/",
                revision,
                "Moved/",
                "--delete-folder",
                "old/",
                revision,
                "--confirm-rebuild-copy",
            ],
            out var options,
            out var error);

        Assert.IsTrue(parsed, error);
        CollectionAssert.AreEqual(
            new[] { new ArchiveCreationDirectory(Path.Combine(root.Path, "folder"), "Imported") },
            options.RebuildDirectories!.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                new CliRebuildTreeAction(
                    "tree",
                    revision,
                    ArchiveRebuildTreeActionKind.Move,
                    "Moved"),
                new CliRebuildTreeAction(
                    "old",
                    revision,
                    ArchiveRebuildTreeActionKind.Delete),
            },
            options.RebuildTreeActions!.ToArray());

        Assert.IsFalse(CliArguments.TryParse(
            [
                "rebuild-zip",
                Path.Combine(root.Path, "source.zip"),
                Path.Combine(root.Path, "destination.zip"),
                "--delete-folder",
                "old",
                new string('A', 64),
                "--confirm-rebuild-copy",
            ],
            out _,
            out var invalidRevision));
        StringAssert.Contains(invalidRevision, "normalized path and revision");
    }

    [TestMethod]
    public void ParserRejectsRebuildPathAliasesTraversalAndInvalidPresets()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var destination = Path.Combine(root.Path, "new.zip");

        Assert.IsFalse(CliArguments.TryParse(
            ["rebuild-zip", sourceArchive, sourceArchive.ToUpperInvariant(), "--confirm-rebuild-copy"],
            out _,
            out var sourceDestinationAlias));
        StringAssert.Contains(sourceDestinationAlias, "must not be aliases");

        Assert.IsFalse(CliArguments.TryParse(
            [
                "rebuild-zip", sourceArchive, destination,
                "--add", Path.Combine(root.Path, "source.txt"), "folder/../entry.txt",
                "--confirm-rebuild-copy",
            ],
            out _,
            out var traversal));
        StringAssert.Contains(traversal, "valid archive-relative");

        foreach (var invalidArchivePath in new[]
        {
            "bad*.txt",
            "bad?.txt",
            "bad\".txt",
            "bad<name>.txt",
            "bad|name.txt",
            "control\u0001.txt",
        })
        {
            Assert.IsFalse(
                CliArguments.TryParse(
                    [
                        "rebuild-zip", sourceArchive, destination,
                        "--add", Path.Combine(root.Path, "source.txt"), invalidArchivePath,
                        "--confirm-rebuild-copy",
                    ],
                    out _,
                    out var invalidCharacterError),
                invalidArchivePath);
            StringAssert.Contains(invalidCharacterError, "valid archive-relative");
        }

        Assert.IsFalse(CliArguments.TryParse(
            [
                "rebuild-zip", sourceArchive, destination,
                "--preset", "1",
                "--confirm-rebuild-copy",
            ],
            out _,
            out var invalidPreset));
        StringAssert.Contains(invalidPreset, "store, fast, balanced, or maximum");

        Assert.IsFalse(CliArguments.TryParse(
            [
                "rebuild-zip", sourceArchive, destination,
                "--overwrite",
                "--confirm-rebuild-copy",
            ],
            out _,
            out var overwrite));
        StringAssert.Contains(overwrite, "Unknown option");

        Assert.IsFalse(CliArguments.TryParse(
            [
                "rebuild-zip", sourceArchive, destination,
                "--add", Path.Combine(root.Path, "first.txt"), "café.txt",
                "--replace", Path.Combine(root.Path, "second.txt"), "CAFÉ.TXT",
                "--confirm-rebuild-copy",
            ],
            out _,
            out var unicodeCaseCollision));
        StringAssert.Contains(unicodeCaseCollision, "may not conflict");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RouteRejectsAddTargetAlreadyPresentBeforeCreatingDestination()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = FixturePath("archives", "zip", "plain.zip");
        var addSource = Path.Combine(root.Path, "add.txt");
        var destination = Path.Combine(root.Path, "new.zip");
        await File.WriteAllTextAsync(addSource, "must not be copied");

        var exitCode = await CliProgram.RunAsync(
            [
                "rebuild-zip",
                sourceArchive,
                destination,
                "--add",
                addSource,
                "alpha.txt",
                "--confirm-rebuild-copy",
                "--worker",
                GetWorkerPath(),
            ]);

        Assert.AreEqual(1, exitCode);
        Assert.IsFalse(File.Exists(destination));
        Assert.IsFalse(Directory.Exists(destination));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task RealCliCompletesMixedRebuildAndLeavesOriginalByteIdentical()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var destination = Path.Combine(root.Path, "rebuilt.zip");
        var replacement = Path.Combine(root.Path, "replacement.txt");
        var addition = Path.Combine(root.Path, "addition.txt");
        var keepBytes = "keep"u8.ToArray();
        var replacementBytes = "replacement"u8.ToArray();
        var additionBytes = "雨"u8.ToArray();
        using (var stream = new FileStream(
                   sourceArchive,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "keep.txt", keepBytes);
            WriteEntry(archive, "replace.txt", "old"u8.ToArray());
            WriteEntry(archive, "delete.txt", "delete"u8.ToArray());
        }

        await File.WriteAllBytesAsync(replacement, replacementBytes);
        await File.WriteAllBytesAsync(addition, additionBytes);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive));

        var exitCode = await CliProgram.RunAsync(
            [
                "rebuild-zip",
                sourceArchive,
                destination,
                "--delete",
                "delete.txt",
                "0",
                "--replace",
                replacement,
                "REPLACE.TXT",
                "--rename",
                "keep.txt",
                "0",
                "Moved/keep.txt",
                "--add",
                addition,
                "Unicode/雨.txt",
                "--preset",
                "fast",
                "--confirm-rebuild-copy",
                "--worker",
                GetWorkerPath(),
            ]);

        Assert.AreEqual(0, exitCode);
        CollectionAssert.AreEqual(
            sourceHash,
            SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive)));
        using var outputStream = File.OpenRead(destination);
        using var output = new ZipArchive(outputStream, ZipArchiveMode.Read);
        CollectionAssert.AreEqual(
            new[] { "Moved/keep.txt", "Unicode/雨.txt", "replace.txt" },
            output.Entries.Select(entry => entry.FullName).ToArray());
        await AssertEntryAsync(output.GetEntry("Moved/keep.txt")!, keepBytes);
        await AssertEntryAsync(output.GetEntry("replace.txt")!, replacementBytes);
        await AssertEntryAsync(output.GetEntry("Unicode/雨.txt")!, additionBytes);
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task RealCliQueriesAndAppliesMixedFolderPlanWithoutChangingOriginal()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var destination = Path.Combine(root.Path, "tree-rebuilt.zip");
        var replacement = Path.Combine(root.Path, "replacement.txt");
        var sourceFolder = Path.Combine(root.Path, "source-folder");
        Directory.CreateDirectory(Path.Combine(sourceFolder, "empty"));
        var importedBytes = "imported 雨"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(sourceFolder, "雨.txt"), importedBytes);
        var replacementBytes = "replacement"u8.ToArray();
        await File.WriteAllBytesAsync(replacement, replacementBytes);
        using (var stream = new FileStream(sourceArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            _ = archive.CreateEntry("tree/", CompressionLevel.NoCompression);
            WriteEntry(archive, "tree/file.txt", "kept"u8.ToArray());
            WriteEntry(archive, "tree/replace.txt", "old"u8.ToArray());
            WriteEntry(archive, "tree/rename.txt", "renamed"u8.ToArray());
            _ = archive.CreateEntry("remove-tree/", CompressionLevel.NoCompression);
            WriteEntry(archive, "remove-tree/delete.txt", "delete"u8.ToArray());
            WriteEntry(archive, "outside.txt", "outside"u8.ToArray());
        }

        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive));
        string moveRevision;
        string deleteRevision;
        await using (var client = await ArchiveClient.StartAsync(GetWorkerPath()))
        {
            var opened = await client.OpenArchiveAsync(sourceArchive);
            var plan = await client.GetZipRebuildTreePlanAsync(opened.Archive.SessionId);
            moveRevision = plan.Directories.Single(node => node.NormalizedPath == "tree").SubtreeRevision;
            deleteRevision = plan.Directories.Single(node => node.NormalizedPath == "remove-tree").SubtreeRevision;
            var folderReview = await client.GetZipRebuildSourceFolderReviewAsync(
                opened.Archive.SessionId,
                [new ArchiveCreationDirectory(sourceFolder, "Imported/")]);
            Assert.AreEqual("Imported", folderReview.Directories.Single().ArchiveRoot);
            Assert.AreEqual(2, folderReview.Directories.Single().DescendantCount);
            Assert.AreEqual(1, folderReview.Directories.Single().FileCount);
            Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);
        }

        var exitCode = await CliProgram.RunAsync(
            [
                "rebuild-zip",
                sourceArchive,
                destination,
                "--replace",
                replacement,
                "tree/replace.txt",
                "--rename",
                "tree/rename.txt",
                "0",
                "Override/renamed.txt",
                "--add-folder",
                sourceFolder,
                "Imported",
                "--move-folder",
                "tree",
                moveRevision,
                "Moved",
                "--delete-folder",
                "remove-tree",
                deleteRevision,
                "--confirm-rebuild-copy",
                "--worker",
                GetWorkerPath(),
            ]);

        Assert.AreEqual(0, exitCode);
        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive)));
        using var outputStream = File.OpenRead(destination);
        using var output = new ZipArchive(outputStream, ZipArchiveMode.Read);
        CollectionAssert.AreEqual(
            new[]
            {
                "Imported/",
                "Imported/empty/",
                "Imported/雨.txt",
                "Moved/",
                "Moved/file.txt",
                "Moved/replace.txt",
                "Override/",
                "Override/renamed.txt",
                "outside.txt",
            },
            output.Entries.Select(entry => entry.FullName).ToArray());
        await AssertEntryAsync(output.GetEntry("Imported/雨.txt")!, importedBytes);
        await AssertEntryAsync(output.GetEntry("Moved/replace.txt")!, replacementBytes);
        Assert.IsNull(output.GetEntry("remove-tree/delete.txt"));
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] payload)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(payload);
    }

    private static async Task AssertEntryAsync(ZipArchiveEntry entry, byte[] expected)
    {
        await using var stream = entry.Open();
        using var payload = new MemoryStream();
        await stream.CopyToAsync(payload);
        CollectionAssert.AreEqual(expected, payload.ToArray(), entry.FullName);
    }

    private static string FixturePath(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "fixtures", "corpus", .. parts]);

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
                "RainArchives.CliZipRebuildCopyTests",
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
