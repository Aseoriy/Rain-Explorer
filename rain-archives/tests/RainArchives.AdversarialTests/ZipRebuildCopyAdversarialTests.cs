using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.AdversarialTests;

[TestClass]
public sealed class ZipRebuildCopyAdversarialTests
{
    [TestMethod]
    public async Task MappedArchivePathsRejectTraversalAbsoluteDeviceAdsReservedAndTrailingAliases()
    {
        using var root = new TemporaryDirectory();
        var sourceFile = Path.Combine(root.Path, "source.txt");
        await File.WriteAllTextAsync(sourceFile, "payload");
        await using var handle = new FakeArchiveHandle([Entry("keep.txt", 1, 1)]);
        var index = ArchiveIndex.Create(handle.Entries);
        var unsafePaths = new[]
        {
            "../escape.txt",
            "/absolute.txt",
            @"C:\absolute.txt",
            @"\\server\share\escape.txt",
            @"\\?\C:\device.txt",
            "C:drive-relative.txt",
            "file.txt:stream",
            "CON.txt",
            "name. ",
            "folder//file.txt",
            string.Join('/', Enumerable.Repeat("deep", 300)) + "/file.txt",
        };

        foreach (var archivePath in unsafePaths)
        {
            var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await new ZipRebuildCopyPolicy().PlanAsync(
                    handle,
                    index,
                    [],
                    [new ArchiveRebuildCopyFile(sourceFile, archivePath)],
                    Path.Combine(root.Path, "source.zip"),
                    Path.Combine(root.Path, $"{Guid.NewGuid():N}.zip"),
                    CancellationToken.None));

            Assert.IsTrue(
                exception.Category is ArchiveErrorCategory.UnsafePath
                    or ArchiveErrorCategory.PathCollision
                    or ArchiveErrorCategory.ResourceLimit,
                $"{archivePath}: {exception.Code} ({exception.Category})");
        }
    }

    [TestMethod]
    public async Task ClosedPlanRejectsCaseNfcDuplicateAndFileDirectoryPrefixCollisions()
    {
        using var root = new TemporaryDirectory();
        var first = Path.Combine(root.Path, "first.txt");
        var second = Path.Combine(root.Path, "second.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        await using var handle = new FakeArchiveHandle([Entry("keep.txt", 1, 1)]);
        var index = ArchiveIndex.Create(handle.Entries);
        IReadOnlyList<(string First, string Second)> collisions =
        [
            ("same.txt", "SAME.txt"),
            ("café.txt", "café.txt"),
            ("node", "node/child.txt"),
            ("branch/child.txt", "branch"),
        ];

        foreach (var pair in collisions)
        {
            var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await new ZipRebuildCopyPolicy().PlanAsync(
                    handle,
                    index,
                    [],
                    [
                        new ArchiveRebuildCopyFile(first, pair.First),
                        new ArchiveRebuildCopyFile(second, pair.Second),
                    ],
                    Path.Combine(root.Path, "source.zip"),
                    Path.Combine(root.Path, $"{Guid.NewGuid():N}.zip"),
                    CancellationToken.None));

            Assert.AreEqual(ArchiveErrorCategory.PathCollision, exception.Category, pair.ToString());
        }
    }

    [TestMethod]
    public async Task RenameTargetsRejectTraversalCaseNfcAndPrefixCollisionsBeforeStaging()
    {
        using var root = new TemporaryDirectory();
        await using var handle = new FakeArchiveHandle(
            [Entry("keep.txt", 1, 1), Entry("other.txt", 1, 1)]);
        var index = ArchiveIndex.Create(handle.Entries);
        var keep = index.Entries.Single(entry => entry.Descriptor.RawPath == "keep.txt");
        var other = index.Entries.Single(entry => entry.Descriptor.RawPath == "other.txt");
        var keepKey = new ArchiveEntryKey(keep.Descriptor.NormalizedPath!, keep.Descriptor.DuplicateOrdinal);
        var otherKey = new ArchiveEntryKey(other.Descriptor.NormalizedPath!, other.Descriptor.DuplicateOrdinal);

        var traversal = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new ZipRebuildCopyPolicy().PlanWithRenamesAsync(
                handle,
                index,
                [],
                [],
                [new ArchiveRebuildRename(keepKey, "../escape.txt")],
                Path.Combine(root.Path, "source.zip"),
                Path.Combine(root.Path, "traversal.zip"),
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, traversal.Category);

        var duplicateTarget = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new ZipRebuildCopyPolicy().PlanWithRenamesAsync(
                handle,
                index,
                [],
                [],
                [
                    new ArchiveRebuildRename(keepKey, "same.txt"),
                    new ArchiveRebuildRename(otherKey, "SAME.txt"),
                ],
                Path.Combine(root.Path, "source.zip"),
                Path.Combine(root.Path, "duplicate-target.zip"),
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.PathCollision, duplicateTarget.Category);
        Assert.AreEqual("zip.rebuild_rename_target_collision", duplicateTarget.Code);

        var nfcTarget = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new ZipRebuildCopyPolicy().PlanWithRenamesAsync(
                handle,
                index,
                [],
                [],
                [
                    new ArchiveRebuildRename(keepKey, "Unicode/é.txt"),
                    new ArchiveRebuildRename(otherKey, "Unicode/e\u0301.txt"),
                ],
                Path.Combine(root.Path, "source.zip"),
                Path.Combine(root.Path, "nfc-target.zip"),
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.PathCollision, nfcTarget.Category);
        Assert.AreEqual("zip.rebuild_rename_target_collision", nfcTarget.Code);

        var prefix = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new ZipRebuildCopyPolicy().PlanWithRenamesAsync(
                handle,
                index,
                [],
                [],
                [new ArchiveRebuildRename(keepKey, "other.txt/child.txt")],
                Path.Combine(root.Path, "source.zip"),
                Path.Combine(root.Path, "prefix.zip"),
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.PathCollision, prefix.Category);
    }

    [TestMethod]
    public async Task ReparseAndHardLinkedMappedSourcesAreRefused()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var hardLink = Path.Combine(root.Path, "hard-link.txt");
        await File.WriteAllTextAsync(source, "payload");
        if (!NativeMethods.CreateHardLink(hardLink, source, IntPtr.Zero))
        {
            throw new AssertInconclusiveException(
                $"This test volume cannot create hard links: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        await using var handle = new FakeArchiveHandle([Entry("keep.txt", 1, 1)]);
        var index = ArchiveIndex.Create(handle.Entries);
        var hardLinkFailure = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new ZipRebuildCopyPolicy().PlanAsync(
                handle,
                index,
                [],
                [new ArchiveRebuildCopyFile(hardLink, "hard-link.txt")],
                Path.Combine(root.Path, "source.zip"),
                Path.Combine(root.Path, "hard-link-output.zip"),
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, hardLinkFailure.Category);
        Assert.AreEqual("source.hard_link_refused", hardLinkFailure.Code);

        var tree = Path.Combine(root.Path, "source-tree");
        Directory.CreateDirectory(tree);
        var treeHardLink = Path.Combine(tree, "hard-link.txt");
        if (!NativeMethods.CreateHardLink(treeHardLink, source, IntPtr.Zero))
        {
            throw new AssertInconclusiveException(
                $"This test volume cannot create a tree hard link: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        var treeHardLinkFailure = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new ZipRebuildTreeEditPolicy().PlanAsync(
                handle,
                index,
                new ArchiveFingerprint(1, DateTimeOffset.UnixEpoch, "id", "prefix"),
                [],
                [],
                [],
                [new ArchiveCreationDirectory(tree, "Imported")],
                [],
                Path.Combine(root.Path, "source.zip"),
                Path.Combine(root.Path, "tree-hard-link-output.zip"),
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, treeHardLinkFailure.Category);
        Assert.AreEqual("source.hard_link_refused", treeHardLinkFailure.Code);

        var symbolicLink = Path.Combine(root.Path, "symbolic-link.txt");
        try
        {
            _ = File.CreateSymbolicLink(symbolicLink, source);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException)
        {
            return;
        }

        var reparseFailure = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new ZipRebuildCopyPolicy().PlanAsync(
                handle,
                index,
                [],
                [new ArchiveRebuildCopyFile(symbolicLink, "symbolic-link.txt")],
                Path.Combine(root.Path, "source.zip"),
                Path.Combine(root.Path, "symbolic-link-output.zip"),
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, reparseFailure.Category);
        Assert.AreEqual("source.reparse_point", reparseFailure.Code);

        var targetDirectory = Path.Combine(root.Path, "target-directory");
        var linkedDirectory = Path.Combine(root.Path, "linked-directory");
        Directory.CreateDirectory(targetDirectory);
        try
        {
            _ = Directory.CreateSymbolicLink(linkedDirectory, targetDirectory);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or PlatformNotSupportedException)
        {
            return;
        }

        var treeReparseFailure = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new ZipRebuildTreeEditPolicy().PlanAsync(
                handle,
                index,
                new ArchiveFingerprint(1, DateTimeOffset.UnixEpoch, "id", "prefix"),
                [],
                [],
                [],
                [new ArchiveCreationDirectory(linkedDirectory, "Imported")],
                [],
                Path.Combine(root.Path, "source.zip"),
                Path.Combine(root.Path, "tree-reparse-output.zip"),
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.UnsafePath, treeReparseFailure.Category);
        Assert.AreEqual("source.reparse_point", treeReparseFailure.Code);
    }

    [TestMethod]
    public async Task MisleadingExpansionOverflowAndAdmissionCancellationFailClosed()
    {
        var policy = new ZipRebuildCopyPolicy();
        await using var expansion = new FakeArchiveHandle(
            [Entry("bomb.bin", 128L * 1024 * 1024, 1)]);
        var expansionIndex = ArchiveIndex.Create(expansion.Entries);
        var expansionPlan = await policy.PlanAsync(
            expansion,
            expansionIndex,
            [],
            [],
            @"C:\source.zip",
            @"C:\output.zip",
            CancellationToken.None);
        var expansionFailure = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            new ExtractionResourceGuard().ValidateRead(
                expansionPlan.Entries
                    .Where(entry => entry.RetainedEntry is not null)
                    .Select(entry => entry.RetainedEntry!)
                    .ToArray()));
        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, expansionFailure.Category);
        Assert.AreEqual("resource.expansion_ratio", expansionFailure.Code);

        await using var overflow = new FakeArchiveHandle(
            [Entry("one.bin", 1, long.MaxValue), Entry("two.bin", 1, long.MaxValue)]);
        var overflowFailure = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.PlanAsync(
                overflow,
                ArchiveIndex.Create(overflow.Entries),
                [],
                [],
                @"C:\source.zip",
                @"C:\output.zip",
                CancellationToken.None));
        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, overflowFailure.Category);
        Assert.AreEqual("zip.rebuild_accounting_overflow", overflowFailure.Code);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var ordinary = new FakeArchiveHandle([Entry("file.txt", 1, 1)]);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await policy.PlanAsync(
                ordinary,
                ArchiveIndex.Create(ordinary.Entries),
                [],
                [],
                @"C:\source.zip",
                @"C:\output.zip",
                cancelled.Token));
    }

    [TestMethod]
    public async Task WriterOutputManifestMismatchIsRejectedBeforePublication()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var mappedSource = Path.Combine(root.Path, "mapped.txt");
        var destination = Path.Combine(root.Path, "rebuilt.zip");
        await File.WriteAllBytesAsync(sourceArchive, "immutable source"u8.ToArray());
        await File.WriteAllBytesAsync(mappedSource, "mapped payload"u8.ToArray());
        await using var handle = new FakeArchiveHandle([]);

        var failure = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new StagedZipRebuildCopyWriter(
                    new ArchiveCreationRecoveryStore(Path.Combine(root.Path, "creation-recovery")))
                .RebuildAsync(
                    new MismatchingZipWriterProvider(),
                    handle,
                    ArchiveIndex.Create(handle.Entries),
                    [],
                    [new ArchiveRebuildCopyFile(mappedSource, "expected.txt")],
                    sourceArchive,
                    CaptureSnapshot(sourceArchive),
                    destination,
                    ArchiveCompressionPreset.Balanced,
                    _ => ValueTask.CompletedTask,
                    _ => ValueTask.CompletedTask,
                    CancellationToken.None));

        Assert.AreEqual(ArchiveErrorCategory.ProviderFailure, failure.Category);
        Assert.AreEqual("zip.rebuild_output_manifest_mismatch", failure.Code);
        Assert.IsFalse(File.Exists(destination));
        Assert.HasCount(
            0,
            Directory.GetFiles(root.Path, ".rainarchives-*.zip.staging"));
        var recoveryRoot = Path.Combine(root.Path, "creation-recovery");
        if (Directory.Exists(recoveryRoot))
        {
            Assert.HasCount(
                0,
                Directory.GetDirectories(recoveryRoot));
        }
    }

    private static OpenFileSnapshot CaptureSnapshot(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.RandomAccess);
        return FileSystemIdentityService.CaptureOpenFile(stream, path);
    }

    private static ProviderArchiveEntry Entry(
        string path,
        long originalSize,
        long compressedSize) =>
        new(
            Ordinal: 0,
            path,
            ArchiveEntryKind.File,
            originalSize,
            compressedSize,
            ModifiedTimeUtc: DateTimeOffset.UtcNow,
            WindowsAttributes: null,
            CompressionMethod: "Deflate",
            IntegrityValue: "crc",
            IsEncrypted: false,
            IsSolid: false,
            LinkTarget: null);

    private sealed class FakeArchiveHandle : IArchiveHandle
    {
        public FakeArchiveHandle(IReadOnlyList<ProviderArchiveEntry> entries)
        {
            Entries = entries.Select((entry, ordinal) => entry with { Ordinal = ordinal }).ToArray();
        }

        public string Format => "zip";
        public string ProviderId => "fake/zip";
        public ArchiveCapability Capabilities => ArchiveCapability.List | ArchiveCapability.Extract;
        public bool IsSolid => false;
        public ArchiveVolumeSetKind VolumeSetKind => ArchiveVolumeSetKind.Single;
        public IReadOnlyList<ProviderArchiveEntry> Entries { get; }
        public ValueTask<Stream> OpenEntryStreamAsync(int providerOrdinal, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(Stream.Null);
        public ValueTask CopyEntryToAsync(
            int providerOrdinal,
            Stream destination,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MismatchingZipWriterProvider : IArchiveWriterProvider
    {
        private static readonly IReadOnlySet<ArchiveCompressionPreset> Presets =
            new HashSet<ArchiveCompressionPreset>(Enum.GetValues<ArchiveCompressionPreset>());

        public string Id => "adversarial/mismatching-zip";
        public string Format => "zip";
        public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets => Presets;

        public ValueTask<IArchiveWriterHandle> CreateAsync(
            Stream destination,
            ArchiveCompressionPreset compressionPreset,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IArchiveWriterHandle>(
                new MismatchingZipWriterHandle(
                    new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true)));
        }
    }

    private sealed class MismatchingZipWriterHandle(ZipArchive archive) : IArchiveWriterHandle
    {
        private ZipArchive? _archive = archive;

        public ValueTask AddDirectoryAsync(
            ArchiveWriteDirectory entry,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _archive!.CreateEntry("unexpected-directory/");
            return ValueTask.CompletedTask;
        }

        public ValueTask AddFileAsync(
            ArchiveWriteEntry entry,
            Stream source,
            Func<long, ValueTask> reportEntryBytes,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new NotSupportedException());

        public async ValueTask AddFileFromProducerAsync(
            ArchiveWriteEntry entry,
            Func<Stream, CancellationToken, ValueTask> writeSource,
            Func<long, ValueTask> reportEntryBytes,
            CancellationToken cancellationToken)
        {
            var mismatched = _archive!.CreateEntry("unexpected.txt");
            await using var destination = mismatched.Open();
            await writeSource(destination, cancellationToken);
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _archive, null)!.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _archive, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ZipRebuildCopyAdversarialTests",
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

    private static class NativeMethods
    {
        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateHardLinkW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateHardLink(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);
    }
}
