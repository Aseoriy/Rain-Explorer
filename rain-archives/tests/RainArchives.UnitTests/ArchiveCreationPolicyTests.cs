using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.SharpCompress;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ArchiveCreationPolicyTests
{
    [TestMethod]
    public async Task PlansExplicitFilesWithNormalizedArchivePathsAndStableSnapshots()
    {
        using var root = new TemporaryDirectory();
        var first = Path.Combine(root.Path, "alpha.txt");
        var second = Path.Combine(root.Path, "zero.bin");
        await File.WriteAllTextAsync(first, "rain archive creation");
        await File.WriteAllBytesAsync(second, []);

        var plan = await new ArchiveCreationPolicy().PlanAsync(
            [
                new ArchiveCreationFile(first, @"Folder\alpha.txt"),
                new ArchiveCreationFile(second, "unicodé/zero.bin"),
            ],
            Path.Combine(root.Path, "created.zip"),
            CancellationToken.None);

        Assert.HasCount(2, plan.Files);
        Assert.AreEqual("Folder/alpha.txt", plan.Files[0].ArchivePath);
        Assert.AreEqual("unicodé/zero.bin", plan.Files[1].ArchivePath);
        Assert.AreEqual(new FileInfo(first).Length, plan.TotalSourceBytes);
        Assert.AreEqual(0, plan.Files[1].SourceSnapshot.Length);
        Assert.IsGreaterThan(plan.TotalSourceBytes, plan.MaximumExpectedOutputBytes);
        Assert.AreEqual(0, plan.ClampedTimestampCount);
    }

    [TestMethod]
    public async Task RejectsUnsafeArchiveRelativePathsBeforeWriting()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        await File.WriteAllTextAsync(source, "payload");
        var policy = new ArchiveCreationPolicy();

        foreach (var archivePath in new[]
                 {
                     "../escape.txt",
                     @"C:\escape.txt",
                     @"\\server\share\escape.txt",
                     "folder//file.txt",
                     "CON.txt",
                     "name. ",
                     "file.txt:stream",
                 })
        {
            var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await policy.PlanAsync(
                    [new ArchiveCreationFile(source, archivePath)],
                    Path.Combine(root.Path, $"{Guid.NewGuid():N}.zip"),
                    CancellationToken.None));

            Assert.AreEqual(ArchiveErrorCategory.UnsafePath, exception.Category, archivePath);
            Assert.AreEqual("creation.unsafe_archive_path", exception.Code, archivePath);
        }
    }

    [TestMethod]
    public async Task RejectsCaseUnicodeParentAndFileDirectoryCollisions()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        await File.WriteAllTextAsync(source, "payload");
        var policy = new ArchiveCreationPolicy();
        IReadOnlyList<IReadOnlyList<string>> collisions =
        [
            ["same.txt", "SAME.txt"],
            ["cafe\u0301.txt", "café.txt"],
            ["Folder/first.txt", "folder/second.txt"],
            ["node", "node/child.txt"],
            ["branch/child.txt", "branch"],
        ];

        foreach (var pair in collisions)
        {
            var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await policy.PlanAsync(
                    pair.Select(path => new ArchiveCreationFile(source, path)).ToArray(),
                    Path.Combine(root.Path, $"{Guid.NewGuid():N}.zip"),
                    CancellationToken.None));

            Assert.AreEqual(ArchiveErrorCategory.PathCollision, exception.Category, string.Join(", ", pair));
            Assert.AreEqual("creation.archive_path_collision", exception.Code, string.Join(", ", pair));
        }
    }

    [TestMethod]
    public async Task RejectsSourceDestinationAliasingAndExcessiveFileCount()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.zip");
        await File.WriteAllTextAsync(source, "payload");
        var policy = new ArchiveCreationPolicy();

        var alias = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.PlanAsync(
                [new ArchiveCreationFile(source, "source.zip")],
                source,
                CancellationToken.None));
        Assert.AreEqual("creation.source_is_destination", alias.Code);

        var excessive = Enumerable
            .Range(0, ProtocolConstants.MaximumZipCreationFiles + 1)
            .Select(index => new ArchiveCreationFile(source, $"{index}.txt"))
            .ToArray();
        var limit = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.PlanAsync(
                excessive,
                Path.Combine(root.Path, "too-many.zip"),
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, limit.Category);
        Assert.AreEqual("creation.file_count_limit", limit.Code);
    }

    [TestMethod]
    public async Task RejectsDeviceAdsReservedAndRelativeFilesystemPaths()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        await File.WriteAllTextAsync(source, "payload");
        var policy = new ArchiveCreationPolicy();

        foreach (var destination in new[]
                 {
                     @"created.zip",
                     @"\\?\C:\RainArchives\created.zip",
                     Path.Combine(root.Path, "CON.zip"),
                     Path.Combine(root.Path, "trailing. "),
                     $"{Path.Combine(root.Path, "ads.zip")}:stream",
                 })
        {
            ArchiveProviderException? exception = null;
            try
            {
                _ = await policy.PlanAsync(
                    [new ArchiveCreationFile(source, "source.txt")],
                    destination,
                    CancellationToken.None);
            }
            catch (ArchiveProviderException caught)
            {
                exception = caught;
            }

            Assert.IsNotNull(exception, $"Unsafe destination was accepted: {destination}");
            Assert.AreEqual(ArchiveErrorCategory.UnsafePath, exception.Category, destination);
            Assert.AreEqual("creation.unsafe_destination_path", exception.Code, destination);
        }

        foreach (var unsafeSource in new[]
                 {
                     "source.txt",
                     @"\\?\C:\RainArchives\source.txt",
                     $"{source}:stream",
                 })
        {
            ArchiveProviderException? exception = null;
            try
            {
                _ = await policy.PlanAsync(
                    [new ArchiveCreationFile(unsafeSource, "source.txt")],
                    Path.Combine(root.Path, $"{Guid.NewGuid():N}.zip"),
                    CancellationToken.None);
            }
            catch (ArchiveProviderException caught)
            {
                exception = caught;
            }

            Assert.IsNotNull(exception, $"Unsafe source was accepted: {unsafeSource}");
            Assert.AreEqual(ArchiveErrorCategory.UnsafePath, exception.Category, unsafeSource);
            Assert.AreEqual("creation.unsafe_source_path", exception.Code, unsafeSource);
        }

        var oversizedDestination = @"C:\" + new string('a', ProtocolConstants.MaximumPathCharacters);
        var oversized = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.PlanAsync(
                [new ArchiveCreationFile(source, "source.txt")],
                oversizedDestination,
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, oversized.Category);
        Assert.AreEqual("creation.unsafe_destination_path", oversized.Code);
    }

    [TestMethod]
    public async Task AcceptsOrdinaryFullyQualifiedUncDestinationSyntax()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        await File.WriteAllTextAsync(source, "payload");
        const string destination = @"\\server\share\folder\created.zip";

        var plan = await new ArchiveCreationPolicy().PlanAsync(
            [new ArchiveCreationFile(source, "source.txt")],
            destination,
            CancellationToken.None);

        Assert.AreEqual(destination, plan.DestinationPath);
    }

    [TestMethod]
    public async Task ClampsTimestampsOutsideTheZipRepresentableRange()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "old.txt");
        await File.WriteAllTextAsync(source, "old");
        File.SetLastWriteTimeUtc(source, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var plan = await new ArchiveCreationPolicy().PlanAsync(
            [new ArchiveCreationFile(source, "old.txt")],
            Path.Combine(root.Path, "old.zip"),
            CancellationToken.None);

        Assert.AreEqual(1, plan.ClampedTimestampCount);
        Assert.IsTrue(plan.Files[0].TimestampWasClamped);
        Assert.AreEqual(
            new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            plan.Files[0].ZipLastWriteTime.DateTime);
    }

    [TestMethod]
    public async Task PlansMixedDirectoryTreesDeterministicallyAndPreservesEmptyDirectories()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "Assets");
        var nested = Path.Combine(tree, "themes");
        var empty = Path.Combine(tree, "empty");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(empty);
        var nestedFile = Path.Combine(nested, "café.txt");
        var explicitFile = Path.Combine(root.Path, "README.txt");
        await File.WriteAllTextAsync(nestedFile, "dark");
        await File.WriteAllTextAsync(explicitFile, "read me");

        var plan = await new ArchiveCreationPolicy().PlanTreeAsync(
            [new ArchiveCreationFile(explicitFile, "README.txt")],
            [new ArchiveCreationDirectory(tree, "assets")],
            Path.Combine(root.Path, "created.zip"),
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "assets", "assets/empty", "assets/themes" },
            plan.Directories.Select(item => item.ArchivePath).ToArray());
        CollectionAssert.AreEqual(
            new[] { "README.txt", "assets/themes/café.txt" },
            plan.Files.Select(item => item.ArchivePath).ToArray());
        Assert.AreEqual(5, plan.TotalEntries);
        Assert.AreEqual(
            new FileInfo(explicitFile).Length + new FileInfo(nestedFile).Length,
            plan.TotalSourceBytes);
        Assert.HasCount(1, plan.SourceTrees);
        Assert.HasCount(4, plan.SourceTrees[0].Manifest);
    }

    [TestMethod]
    public async Task RejectsSelfInclusionAndOverlappingTreeInputs()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "tree");
        var nested = Path.Combine(tree, "nested");
        Directory.CreateDirectory(nested);
        var insideFile = Path.Combine(tree, "inside.txt");
        await File.WriteAllTextAsync(insideFile, "inside");
        var policy = new ArchiveCreationPolicy();

        var selfInclusion = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.PlanTreeAsync(
                [],
                [new ArchiveCreationDirectory(tree, "tree")],
                Path.Combine(tree, "created.zip"),
                CancellationToken.None));
        Assert.AreEqual("creation.destination_within_source_directory", selfInclusion.Code);

        var duplicateFile = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.PlanTreeAsync(
                [new ArchiveCreationFile(insideFile, "copy.txt")],
                [new ArchiveCreationDirectory(tree, "tree")],
                Path.Combine(root.Path, "file-overlap.zip"),
                CancellationToken.None));
        Assert.AreEqual("creation.explicit_file_within_directory", duplicateFile.Code);

        var overlappingTrees = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.PlanTreeAsync(
                [],
                [
                    new ArchiveCreationDirectory(tree, "tree"),
                    new ArchiveCreationDirectory(nested, "nested"),
                ],
                Path.Combine(root.Path, "tree-overlap.zip"),
                CancellationToken.None));
        Assert.AreEqual("creation.overlapping_source_directories", overlappingTrees.Code);
    }

    [TestMethod]
    public async Task RejectsMixedFileDirectoryArchivePathCollisions()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "tree");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(tree, "child.txt"), "tree child");
        var explicitFile = Path.Combine(root.Path, "explicit.txt");
        await File.WriteAllTextAsync(explicitFile, "explicit");
        var policy = new ArchiveCreationPolicy();

        foreach (var archivePath in new[] { "tree", "tree/child.txt" })
        {
            var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await policy.PlanTreeAsync(
                    [new ArchiveCreationFile(explicitFile, archivePath)],
                    [new ArchiveCreationDirectory(tree, "tree")],
                    Path.Combine(root.Path, $"{Guid.NewGuid():N}.zip"),
                    CancellationToken.None));

            Assert.AreEqual(ArchiveErrorCategory.PathCollision, exception.Category, archivePath);
            Assert.AreEqual("creation.archive_path_collision", exception.Code, archivePath);
        }
    }

    [TestMethod]
    public async Task FinalTreeValidationDetectsAddedEntries()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "tree");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(tree, "original.txt"), "original");
        var policy = new ArchiveCreationPolicy();
        var plan = await policy.PlanTreeAsync(
            [],
            [new ArchiveCreationDirectory(tree, "tree")],
            Path.Combine(root.Path, "created.zip"),
            CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(tree, "added.txt"), "added");

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.ValidateSourceTreesAsync(plan, CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, exception.Category);
        Assert.AreEqual("source.directory_tree_changed", exception.Code);
    }

    [TestMethod]
    public async Task RejectsNestedDirectoryReparsePoints()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "tree");
        var outside = Path.Combine(root.Path, "outside");
        var link = Path.Combine(tree, "linked");
        Directory.CreateDirectory(tree);
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception unsupported) when (
            unsupported is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive(
                $"This Windows test context cannot create a directory reparse point: {unsupported.Message}");
        }

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new ArchiveCreationPolicy().PlanTreeAsync(
                [],
                [new ArchiveCreationDirectory(tree, "tree")],
                Path.Combine(root.Path, "created.zip"),
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, exception.Category);
        Assert.AreEqual("source.reparse_point", exception.Code);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task EnforcesExpandedMaterialObjectLimit()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "tree");
        Directory.CreateDirectory(tree);
        for (var index = 0; index < ProtocolConstants.MaximumZipCreationEntries; index++)
        {
            File.WriteAllBytes(Path.Combine(tree, $"{index:D4}.bin"), []);
        }

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new ArchiveCreationPolicy().PlanTreeAsync(
                [],
                [new ArchiveCreationDirectory(tree, "tree")],
                Path.Combine(root.Path, "too-many.zip"),
                CancellationToken.None));

        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, exception.Category);
        Assert.AreEqual("creation.entry_count_limit", exception.Code);

        File.Delete(Path.Combine(
            tree,
            $"{ProtocolConstants.MaximumZipCreationEntries - 1:D4}.bin"));
        var longUnicodeName = $"{new string('雨', 80)}.bin";
        File.Move(
            Path.Combine(tree, $"{ProtocolConstants.MaximumZipCreationEntries - 2:D4}.bin"),
            Path.Combine(tree, longUnicodeName));
        var boundary = await new ArchiveCreationPolicy().PlanTreeAsync(
            [],
            [new ArchiveCreationDirectory(tree, "tree")],
            Path.Combine(root.Path, "boundary.zip"),
            CancellationToken.None);
        Assert.AreEqual(ProtocolConstants.MaximumZipCreationEntries, boundary.TotalEntries);

        var tarDestination = Path.Combine(root.Path, "boundary.tar");
        var tarPlan = await new ArchiveCreationPolicy().PlanTreeAsync(
            [],
            [new ArchiveCreationDirectory(tree, "tree")],
            tarDestination,
            ArchiveCreationOutputFormat.Tar,
            CancellationToken.None);
        Assert.AreEqual(ProtocolConstants.MaximumTarCreationEntries, tarPlan.TotalEntries);
        Assert.AreEqual(ArchiveCreationOutputFormat.Tar, tarPlan.OutputFormat);
        Assert.IsGreaterThan(boundary.MaximumExpectedOutputBytes, tarPlan.MaximumExpectedOutputBytes);

        await new StagedArchiveCreationWriter(
                new ArchiveCreationRecoveryStore(Path.Combine(root.Path, "tar-recovery")))
            .CreateTarTreeAsync(
                new BuiltInTarArchiveWriterProvider(),
                [],
                [new ArchiveCreationDirectory(tree, "tree")],
                tarDestination,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
        Assert.IsTrue(File.Exists(tarDestination));
        Assert.IsLessThanOrEqualTo(
            tarPlan.MaximumExpectedOutputBytes,
            new FileInfo(tarDestination).Length);
    }

    [TestMethod]
    public async Task RelativeChildOpensRejectEnumeratedFileIdMismatches()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "folder"));
        await File.WriteAllTextAsync(Path.Combine(tree, "file.txt"), "file");
        using var source = HandleRelativeExtractionRoot.OpenStableSourceDirectory(tree);
        var children = source.EnumerateChildren(8, CancellationToken.None);
        var file = children.Single(child => child.Name == "file.txt");
        var directory = children.Single(child => child.Name == "folder");
        if (file.FileId is null || directory.FileId is null)
        {
            Assert.Inconclusive("This filesystem does not expose directory-enumeration file IDs.");
        }

        var fileMismatch = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            source.OpenChildFile(file.Name, file.FileId.Value ^ 1));
        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, fileMismatch.Category);
        Assert.AreEqual("source.directory_changed", fileMismatch.Code);

        var directoryMismatch = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            source.OpenChildDirectory(directory.Name, directory.FileId.Value ^ 1));
        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, directoryMismatch.Category);
        Assert.AreEqual("source.directory_changed", directoryMismatch.Code);
    }

    [TestMethod]
    public async Task FinalTreeValidationReappliesArchiveDepthLimit()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "tree");
        Directory.CreateDirectory(tree);
        var policy = new ArchiveCreationPolicy();
        var plan = await policy.PlanTreeAsync(
            [],
            [new ArchiveCreationDirectory(tree, "tree")],
            Path.Combine(root.Path, "created.zip"),
            CancellationToken.None);

        var current = tree;
        for (var depth = 0; depth < new ExtractionSecurityPolicy().MaximumPathDepth; depth++)
        {
            current = Path.Combine(current, "d");
            Directory.CreateDirectory(current);
        }

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.ValidateSourceTreesAsync(plan, CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, exception.Category);
        Assert.AreEqual("creation.unsafe_archive_path", exception.Code);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ArchiveCreationPolicyTests",
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
