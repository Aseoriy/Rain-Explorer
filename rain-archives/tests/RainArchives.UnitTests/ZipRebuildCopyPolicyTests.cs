using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ZipRebuildCopyPolicyTests
{
    [TestMethod]
    public async Task MixedPlanDeletesReplacesAddsAndPreservesMaterialDirectoriesDeterministically()
    {
        using var root = new TemporaryDirectory();
        var replacement = Path.Combine(root.Path, "replacement.txt");
        var addition = Path.Combine(root.Path, "addition.txt");
        await File.WriteAllTextAsync(replacement, "replacement payload");
        await File.WriteAllTextAsync(addition, "雨 payload");
        var timestamp = new DateTimeOffset(2024, 5, 6, 7, 8, 10, TimeSpan.Zero);
        await using var handle = new FakeArchiveHandle(
            [
                Entry("Folder/", ArchiveEntryKind.Directory, 0, timestamp, (int)FileAttributes.Directory),
                Entry("Folder/keep.txt", ArchiveEntryKind.File, 4, timestamp, (int)FileAttributes.ReadOnly),
                Entry("replace.txt", ArchiveEntryKind.File, 7, timestamp, (int)FileAttributes.Hidden),
                Entry("delete.txt", ArchiveEntryKind.File, 6, timestamp),
                Entry("zero.bin", ArchiveEntryKind.File, 0, timestamp),
            ]);
        var index = ArchiveIndex.Create(handle.Entries);
        var delete = index.Entries.Single(entry => entry.Descriptor.RawPath == "delete.txt");

        var plan = await new ZipRebuildCopyPolicy().PlanAsync(
            handle,
            index,
            [new ArchiveEntryKey(delete.Descriptor.NormalizedPath!, delete.Descriptor.DuplicateOrdinal)],
            [
                new ArchiveRebuildCopyFile(replacement, "REPLACE.TXT"),
                new ArchiveRebuildCopyFile(addition, "Unicode/雨.txt"),
            ],
            Path.Combine(root.Path, "source.zip"),
            Path.Combine(root.Path, "rebuilt.zip"),
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                "Folder",
                "Folder/keep.txt",
                "Unicode/雨.txt",
                "replace.txt",
                "zero.bin",
            },
            plan.Entries.Select(entry => entry.ArchivePath).ToArray(),
            string.Join(", ", plan.Entries.Select(entry => entry.ArchivePath)));
        Assert.IsFalse(plan.Entries.Any(entry => entry.ArchivePath == "delete.txt"));
        var replaced = plan.Entries.Single(entry => entry.ArchivePath == "replace.txt");
        Assert.AreEqual(ZipRebuildCopyEntrySource.NewFile, replaced.Source);
        Assert.AreEqual((int)FileAttributes.Hidden, replaced.WindowsAttributes);
        Assert.AreEqual(new FileInfo(replacement).Length, replaced.Length);
        var retainedDirectory = plan.Entries.Single(entry => entry.ArchivePath == "Folder");
        Assert.AreEqual(ArchiveEntryKind.Directory, retainedDirectory.Kind);
        Assert.AreEqual(ZipRebuildCopyEntrySource.RetainedArchive, retainedDirectory.Source);
        Assert.AreEqual((int)FileAttributes.Directory, retainedDirectory.WindowsAttributes);
        Assert.AreEqual(
            4 + new FileInfo(replacement).Length + new FileInfo(addition).Length,
            plan.TotalSourceBytes);
        Assert.AreEqual(5, plan.TotalEntries);
    }

    [TestMethod]
    public async Task RenamesResolveSimultaneousSwapAndRetainExactMetadata()
    {
        using var root = new TemporaryDirectory();
        var firstTime = new DateTimeOffset(2024, 5, 6, 7, 8, 10, TimeSpan.Zero);
        var secondTime = new DateTimeOffset(2025, 6, 7, 8, 9, 12, TimeSpan.Zero);
        await using var handle = new FakeArchiveHandle(
            [
                Entry("first.txt", ArchiveEntryKind.File, 3, firstTime, (int)FileAttributes.ReadOnly),
                Entry("second.txt", ArchiveEntryKind.File, 4, secondTime, (int)FileAttributes.Hidden),
            ]);
        var index = ArchiveIndex.Create(handle.Entries);
        var first = index.Entries.Single(entry => entry.Descriptor.RawPath == "first.txt");
        var second = index.Entries.Single(entry => entry.Descriptor.RawPath == "second.txt");

        var plan = await new ZipRebuildCopyPolicy().PlanWithRenamesAsync(
            handle,
            index,
            [],
            [],
            [
                new ArchiveRebuildRename(
                    new ArchiveEntryKey(first.Descriptor.NormalizedPath!, first.Descriptor.DuplicateOrdinal),
                    "second.txt"),
                new ArchiveRebuildRename(
                    new ArchiveEntryKey(second.Descriptor.NormalizedPath!, second.Descriptor.DuplicateOrdinal),
                    "first.txt"),
            ],
            Path.Combine(root.Path, "source.zip"),
            Path.Combine(root.Path, "swapped.zip"),
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "first.txt", "second.txt" },
            plan.Entries.Select(entry => entry.ArchivePath).ToArray());
        Assert.AreEqual(second.Descriptor.EntryId, plan.Entries[0].RetainedEntry!.Descriptor.EntryId);
        Assert.AreEqual(first.Descriptor.EntryId, plan.Entries[1].RetainedEntry!.Descriptor.EntryId);
        Assert.AreEqual((int)FileAttributes.Hidden, plan.Entries[0].WindowsAttributes);
        Assert.AreEqual((int)FileAttributes.ReadOnly, plan.Entries[1].WindowsAttributes);
        Assert.AreEqual(secondTime, plan.Entries[0].ZipLastWriteTime);
        Assert.AreEqual(firstTime, plan.Entries[1].ZipLastWriteTime);
    }

    [TestMethod]
    public async Task RenamesRejectDuplicateActionsUnsafeTargetsAndDirectoryTargetsBeforeStaging()
    {
        using var root = new TemporaryDirectory();
        await using var handle = new FakeArchiveHandle(
            [
                Entry("file.txt", ArchiveEntryKind.File, 1),
                Entry("folder/", ArchiveEntryKind.Directory, 0),
            ]);
        var index = ArchiveIndex.Create(handle.Entries);
        var file = index.Entries.Single(entry => entry.Descriptor.RawPath == "file.txt");
        var key = new ArchiveEntryKey(file.Descriptor.NormalizedPath!, file.Descriptor.DuplicateOrdinal);

        var duplicate = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new ZipRebuildCopyPolicy().PlanWithRenamesAsync(
                handle,
                index,
                [],
                [],
                [
                    new ArchiveRebuildRename(key, "one.txt"),
                    new ArchiveRebuildRename(key, "two.txt"),
                ],
                Path.Combine(root.Path, "source.zip"),
                Path.Combine(root.Path, "duplicate.zip"),
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.InvalidRequest, duplicate.Category);
        Assert.AreEqual("zip.rebuild_rename_key_invalid", duplicate.Code);

        var unsafeTarget = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new ZipRebuildCopyPolicy().PlanWithRenamesAsync(
                handle,
                index,
                [],
                [],
                [new ArchiveRebuildRename(key, "../escape.txt")],
                Path.Combine(root.Path, "source.zip"),
                Path.Combine(root.Path, "unsafe.zip"),
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, unsafeTarget.Category);

        var directoryTarget = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new ZipRebuildCopyPolicy().PlanWithRenamesAsync(
                handle,
                index,
                [],
                [],
                [new ArchiveRebuildRename(key, "folder")],
                Path.Combine(root.Path, "source.zip"),
                Path.Combine(root.Path, "directory.zip"),
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.PathCollision, directoryTarget.Category);
        Assert.AreEqual("zip.rebuild_rename_directory_target_refused", directoryTarget.Code);
    }

    [TestMethod]
    public async Task DeletingTheOnlyEntryProducesAValidEmptyPlan()
    {
        using var root = new TemporaryDirectory();
        await using var handle = new FakeArchiveHandle([Entry("only.txt", ArchiveEntryKind.File, 4)]);
        var index = ArchiveIndex.Create(handle.Entries);
        var only = index.Entries.Single(entry => !entry.IsSynthetic);

        var plan = await new ZipRebuildCopyPolicy().PlanAsync(
            handle,
            index,
            [new ArchiveEntryKey(only.Descriptor.NormalizedPath!, 0)],
            [],
            Path.Combine(root.Path, "source.zip"),
            Path.Combine(root.Path, "empty.zip"),
            CancellationToken.None);

        Assert.HasCount(0, plan.Entries);
        Assert.AreEqual(0, plan.TotalSourceBytes);
        Assert.AreEqual(0, plan.DeclaredInputBytesForRatio);
    }

    [TestMethod]
    public async Task RefusesEncryptedMultipartUnknownDuplicateAndSpecialEntrySources()
    {
        using var root = new TemporaryDirectory();
        var policy = new ZipRebuildCopyPolicy();
        var cases = new[]
        {
            new FakeArchiveHandle([Entry("encrypted.txt", ArchiveEntryKind.File, 1, encrypted: true)]),
            new FakeArchiveHandle([Entry("file.txt", ArchiveEntryKind.File, 1)], ArchiveVolumeSetKind.ConfirmedMultipart),
            new FakeArchiveHandle([Entry("file.txt", ArchiveEntryKind.File, 1)], ArchiveVolumeSetKind.Unknown),
            new FakeArchiveHandle([Entry("same.txt", ArchiveEntryKind.File, 1), Entry("same.txt", ArchiveEntryKind.File, 1)]),
            new FakeArchiveHandle([Entry("link", ArchiveEntryKind.SymbolicLink, 0)]),
            new FakeArchiveHandle([Entry("unknown", ArchiveEntryKind.Unknown, 0)]),
        };

        foreach (var handle in cases)
        {
            await using (handle)
            {
                var index = ArchiveIndex.Create(handle.Entries);
                Assert.IsFalse(policy.CanRebuild(handle, index));
                var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                    await policy.PlanAsync(
                        handle,
                        index,
                        [],
                        [],
                        Path.Combine(root.Path, "source.zip"),
                        Path.Combine(root.Path, $"{Guid.NewGuid():N}.zip"),
                        CancellationToken.None));
                Assert.AreEqual("zip.rebuild_source_not_eligible", exception.Code);
            }
        }
    }

    [TestMethod]
    public async Task RefusesUnsafeSourcePathsAndClosedPlanCollisionsBeforeStaging()
    {
        using var root = new TemporaryDirectory();
        var mapped = Path.Combine(root.Path, "mapped.txt");
        await File.WriteAllTextAsync(mapped, "mapped");
        var policy = new ZipRebuildCopyPolicy();
        await using var colliding = new FakeArchiveHandle(
            [Entry("Café.txt", ArchiveEntryKind.File, 1), Entry("Café.txt", ArchiveEntryKind.File, 1)]);
        var collision = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.PlanAsync(
                colliding,
                ArchiveIndex.Create(colliding.Entries),
                [],
                [],
                Path.Combine(root.Path, "source.zip"),
                Path.Combine(root.Path, "collision.zip"),
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.PathCollision, collision.Category);
        Assert.AreEqual("zip.rebuild_source_paths_unsafe", collision.Code);

        await using var directory = new FakeArchiveHandle([Entry("folder/", ArchiveEntryKind.Directory, 0)]);
        var directoryReplacement = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.PlanAsync(
                directory,
                ArchiveIndex.Create(directory.Entries),
                [],
                [new ArchiveRebuildCopyFile(mapped, "FOLDER")],
                Path.Combine(root.Path, "source-directory.zip"),
                Path.Combine(root.Path, "directory-replacement.zip"),
                CancellationToken.None));
        Assert.AreEqual("zip.rebuild_directory_replacement_refused", directoryReplacement.Code);

        await using var unsafePath = new FakeArchiveHandle([Entry("../escape.txt", ArchiveEntryKind.File, 1)]);
        Assert.IsFalse(policy.CanRebuild(unsafePath, ArchiveIndex.Create(unsafePath.Entries)));
    }

    [TestMethod]
    public async Task RefusesSourceAndMappedFileDestinationAliases()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var mapped = Path.Combine(root.Path, "mapped.txt");
        await File.WriteAllTextAsync(sourceArchive, "source placeholder");
        await File.WriteAllTextAsync(mapped, "mapped");
        await using var handle = new FakeArchiveHandle([Entry("keep.txt", ArchiveEntryKind.File, 1)]);
        var index = ArchiveIndex.Create(handle.Entries);
        var policy = new ZipRebuildCopyPolicy();

        var sourceAlias = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.PlanAsync(
                handle,
                index,
                [],
                [],
                sourceArchive,
                sourceArchive,
                CancellationToken.None));
        Assert.AreEqual("zip.rebuild_source_is_destination", sourceAlias.Code);

        var mappedAlias = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.PlanAsync(
                handle,
                index,
                [],
                [new ArchiveRebuildCopyFile(mapped, "mapped.txt")],
                sourceArchive,
                mapped,
                CancellationToken.None));
        Assert.AreEqual("creation.source_is_destination", mappedAlias.Code);
    }

    [TestMethod]
    public void EligibilityEnforcesGlobalEntryAndDecodedByteLimits()
    {
        var tooMany = Enumerable.Range(0, ProtocolConstants.MaximumZipCreationEntries + 1)
            .Select(index => Entry($"{index:D4}.txt", ArchiveEntryKind.File, 0))
            .ToArray();
        using var many = new FakeArchiveHandle(tooMany);
        Assert.IsFalse(new ZipRebuildCopyPolicy().CanRebuild(many, ArchiveIndex.Create(many.Entries)));

        using var large = new FakeArchiveHandle(
            [Entry("large.bin", ArchiveEntryKind.File, ProtocolConstants.MaximumZipCreationSourceBytes + 1)]);
        Assert.IsFalse(new ZipRebuildCopyPolicy().CanRebuild(large, ArchiveIndex.Create(large.Entries)));
    }

    private static ProviderArchiveEntry Entry(
        string path,
        ArchiveEntryKind kind,
        long length,
        DateTimeOffset? timestamp = null,
        int? attributes = null,
        bool encrypted = false) =>
        new(
            Ordinal: 0,
            path,
            kind,
            kind == ArchiveEntryKind.Directory ? null : length,
            kind == ArchiveEntryKind.Directory ? null : Math.Max(0, length / 2),
            timestamp,
            attributes,
            CompressionMethod: "Deflate",
            IntegrityValue: "crc",
            encrypted,
            IsSolid: false,
            LinkTarget: kind is ArchiveEntryKind.SymbolicLink ? "target" : null);

    private sealed class FakeArchiveHandle : IArchiveHandle, IDisposable
    {
        public FakeArchiveHandle(
            IReadOnlyList<ProviderArchiveEntry> entries,
            ArchiveVolumeSetKind volumeSetKind = ArchiveVolumeSetKind.Single)
        {
            Entries = entries.Select((entry, ordinal) => entry with { Ordinal = ordinal }).ToArray();
            VolumeSetKind = volumeSetKind;
        }

        public string Format => "zip";
        public string ProviderId => "fake/zip";
        public ArchiveCapability Capabilities => ArchiveCapability.List | ArchiveCapability.Extract;
        public bool IsSolid => false;
        public ArchiveVolumeSetKind VolumeSetKind { get; }
        public IReadOnlyList<ProviderArchiveEntry> Entries { get; }

        public ValueTask<Stream> OpenEntryStreamAsync(
            int providerOrdinal,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(Stream.Null);

        public ValueTask CopyEntryToAsync(
            int providerOrdinal,
            Stream destination,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose()
        {
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ZipRebuildCopyPolicyTests",
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
