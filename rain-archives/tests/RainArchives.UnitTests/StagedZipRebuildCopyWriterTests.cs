using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.Zip;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class StagedZipRebuildCopyWriterTests
{
    [TestMethod]
    public async Task StreamsMixedEditsForEveryPresetAndLeavesOriginalByteIdentical()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var replacement = Path.Combine(root.Path, "replacement.txt");
        var addition = Path.Combine(root.Path, "addition.txt");
        await File.WriteAllBytesAsync(sourceArchive, "immutable source archive"u8.ToArray());
        await File.WriteAllBytesAsync(replacement, "new replacement"u8.ToArray());
        await File.WriteAllBytesAsync(addition, "雨 addition"u8.ToArray());
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive));
        var retainedPayload = "retained payload"u8.ToArray();
        var deletedPayload = "deleted payload"u8.ToArray();
        var oldReplacement = "old replacement"u8.ToArray();
        var timestamp = new DateTimeOffset(2025, 1, 2, 3, 4, 6, TimeSpan.Zero);
        await using var handle = new FakeArchiveHandle(
            [
                (Entry("empty/", ArchiveEntryKind.Directory, 0, timestamp), Array.Empty<byte>()),
                (Entry("keep.txt", ArchiveEntryKind.File, retainedPayload.Length, timestamp, (int)FileAttributes.ReadOnly), retainedPayload),
                (Entry("replace.txt", ArchiveEntryKind.File, oldReplacement.Length, timestamp, (int)FileAttributes.Hidden), oldReplacement),
                (Entry("delete.txt", ArchiveEntryKind.File, deletedPayload.Length, timestamp), deletedPayload),
                (Entry("zero.bin", ArchiveEntryKind.File, 0, timestamp), Array.Empty<byte>()),
            ]);
        var index = ArchiveIndex.Create(handle.Entries);
        var deleted = index.Entries.Single(entry => entry.Descriptor.RawPath == "delete.txt");
        var fingerprint = await ArchiveFingerprintService.CreateAsync(sourceArchive, CancellationToken.None);
        var snapshot = CaptureSnapshot(sourceArchive);

        foreach (var preset in Enum.GetValues<ArchiveCompressionPreset>())
        {
            var destination = Path.Combine(root.Path, $"rebuilt-{preset}.zip");
            var warnings = new List<ArchiveWarning>();
            var progress = new List<OperationProgress>();
            await CreateWriter(root.Path).RebuildAsync(
                new BuiltInZipArchiveWriterProvider(),
                handle,
                index,
                [new ArchiveEntryKey(deleted.Descriptor.NormalizedPath!, 0)],
                [
                    new ArchiveRebuildCopyFile(replacement, "REPLACE.TXT"),
                    new ArchiveRebuildCopyFile(addition, "Unicode/雨.txt"),
                ],
                sourceArchive,
                snapshot,
                destination,
                preset,
                value =>
                {
                    progress.Add(value);
                    return ValueTask.CompletedTask;
                },
                token => ArchiveFingerprintService.EnsureUnchangedAsync(
                    sourceArchive,
                    fingerprint,
                    token),
                CancellationToken.None,
                reportWarning: warning =>
                {
                    warnings.Add(warning);
                    return ValueTask.CompletedTask;
                });

            AssertZip(
                destination,
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["empty/"] = [],
                    ["keep.txt"] = retainedPayload,
                    ["replace.txt"] = await File.ReadAllBytesAsync(replacement),
                    ["Unicode/雨.txt"] = await File.ReadAllBytesAsync(addition),
                    ["zero.bin"] = [],
                });
            Assert.IsTrue(warnings.Any(warning => warning.Code == "zip.rebuild_metadata_limited"));
            Assert.IsTrue(progress.Any(value => value.CurrentEntryName == "keep.txt"));
            Assert.IsTrue(progress.All(value =>
                value.CurrentEntryName is null
                || !value.CurrentEntryName.Contains(root.Path, StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual(5, progress[^1].EntriesProcessed);
            Assert.AreEqual(5, progress[^1].TotalEntries);
            AssertNoResidue(root.Path);
        }

        CollectionAssert.AreEqual(
            originalHash,
            SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive)));
        Assert.AreEqual(8, handle.CopyCount);
    }

    [TestMethod]
    public async Task RebuildWithRenamesPublishesExactSwappedManifestAndEscapedProgress()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var destination = Path.Combine(root.Path, "renamed.zip");
        await File.WriteAllBytesAsync(sourceArchive, "immutable source archive"u8.ToArray());
        var firstPayload = "first payload"u8.ToArray();
        var secondPayload = "second payload"u8.ToArray();
        var timestamp = new DateTimeOffset(2025, 1, 2, 3, 4, 6, TimeSpan.Zero);
        await using var handle = new FakeArchiveHandle(
            [
                (Entry("first.txt", ArchiveEntryKind.File, firstPayload.Length, timestamp), firstPayload),
                (Entry("second.txt", ArchiveEntryKind.File, secondPayload.Length, timestamp), secondPayload),
            ]);
        var index = ArchiveIndex.Create(handle.Entries);
        var first = index.Entries.Single(entry => entry.Descriptor.RawPath == "first.txt");
        var second = index.Entries.Single(entry => entry.Descriptor.RawPath == "second.txt");
        var progress = new List<OperationProgress>();
        var fingerprint = await ArchiveFingerprintService.CreateAsync(sourceArchive, CancellationToken.None);

        await CreateWriter(root.Path).RebuildWithRenamesAsync(
            new BuiltInZipArchiveWriterProvider(),
            handle,
            index,
            [],
            [],
            [
                new ArchiveRebuildRename(
                    new ArchiveEntryKey(first.Descriptor.NormalizedPath!, first.Descriptor.DuplicateOrdinal),
                    "renamed/second.txt"),
                new ArchiveRebuildRename(
                    new ArchiveEntryKey(second.Descriptor.NormalizedPath!, second.Descriptor.DuplicateOrdinal),
                    "renamed/first.txt"),
            ],
            sourceArchive,
            CaptureSnapshot(sourceArchive),
            destination,
            ArchiveCompressionPreset.Balanced,
            value =>
            {
                progress.Add(value);
                return ValueTask.CompletedTask;
            },
            token => ArchiveFingerprintService.EnsureUnchangedAsync(sourceArchive, fingerprint, token),
            CancellationToken.None);

        AssertZip(
            destination,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["renamed/first.txt"] = secondPayload,
                ["renamed/second.txt"] = firstPayload,
            });
        Assert.IsTrue(progress.Any(value => value.CurrentEntryName == "renamed/first.txt"));
        Assert.IsTrue(progress.Any(value => value.CurrentEntryName == "renamed/second.txt"));
        Assert.IsTrue(progress.All(value =>
            value.CurrentEntryName is null
            || !value.CurrentEntryName.Contains(root.Path, StringComparison.OrdinalIgnoreCase)));
        AssertNoResidue(root.Path);
    }

    [TestMethod]
    public async Task CancellationDuringRetainedCopyPublishesNothingAndCleansStaging()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        await File.WriteAllBytesAsync(sourceArchive, "unchanged"u8.ToArray());
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive));
        var payload = Enumerable.Repeat((byte)0x52, 256 * 1024).ToArray();
        using var cancellation = new CancellationTokenSource();
        await using var handle = new FakeArchiveHandle(
            [(Entry("large.bin", ArchiveEntryKind.File, payload.Length), payload)],
            afterFirstCopyChunk: cancellation.Cancel);
        var destination = Path.Combine(root.Path, "cancelled.zip");

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await CreateWriter(root.Path).RebuildAsync(
                new BuiltInZipArchiveWriterProvider(),
                handle,
                ArchiveIndex.Create(handle.Entries),
                [],
                [],
                sourceArchive,
                CaptureSnapshot(sourceArchive),
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                _ => ValueTask.CompletedTask,
                cancellation.Token));

        Assert.IsFalse(File.Exists(destination));
        CollectionAssert.AreEqual(
            originalHash,
            SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive)));
        AssertNoResidue(root.Path);
    }

    [TestMethod]
    public async Task DestinationRacePreservesRacerAndRemovesOwnedStaging()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        await File.WriteAllBytesAsync(sourceArchive, "source"u8.ToArray());
        var payload = "retained"u8.ToArray();
        await using var handle = new FakeArchiveHandle(
            [(Entry("keep.txt", ArchiveEntryKind.File, payload.Length), payload)]);
        var destination = Path.Combine(root.Path, "raced.zip");
        var sentinel = new byte[] { 0x53, 0x41, 0x49, 0x4C };

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).RebuildAsync(
                new BuiltInZipArchiveWriterProvider(),
                handle,
                ArchiveIndex.Create(handle.Entries),
                [],
                [],
                sourceArchive,
                CaptureSnapshot(sourceArchive),
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                beforeCommit: async () => await File.WriteAllBytesAsync(destination, sentinel)));

        Assert.AreEqual("destination.appeared_before_commit", exception.Code);
        CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(destination));
        AssertNoResidue(root.Path);
    }

    [TestMethod]
    public async Task NewSourceReplacementAfterAdmissionFailsClosed()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var mapped = Path.Combine(root.Path, "mapped.txt");
        var admitted = Path.Combine(root.Path, "admitted.txt");
        await File.WriteAllBytesAsync(sourceArchive, "source"u8.ToArray());
        await File.WriteAllTextAsync(mapped, "admitted");
        await using var handle = new FakeArchiveHandle([]);
        var destination = Path.Combine(root.Path, "changed-source.zip");

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).RebuildAsync(
                new BuiltInZipArchiveWriterProvider(),
                handle,
                ArchiveIndex.Create(handle.Entries),
                [],
                [new ArchiveRebuildCopyFile(mapped, "mapped.txt")],
                sourceArchive,
                CaptureSnapshot(sourceArchive),
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                beforeWriting: async () =>
                {
                    File.Move(mapped, admitted);
                    await File.WriteAllTextAsync(mapped, "replacement");
                }));

        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, exception.Category);
        Assert.AreEqual("source.changed_during_creation", exception.Code);
        Assert.IsFalse(File.Exists(destination));
        AssertNoResidue(root.Path);
    }

    [TestMethod]
    public async Task RuntimeLowSpaceRefusalPublishesNothingAndCleansOwnedState()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var destination = Path.Combine(root.Path, "low-space.zip");
        await File.WriteAllBytesAsync(sourceArchive, "immutable source"u8.ToArray());
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive));
        var payload = new byte[128 * 1024];
        RandomNumberGenerator.Fill(payload);
        await using var handle = new FakeArchiveHandle(
            [(Entry("payload.bin", ArchiveEntryKind.File, payload.Length), payload)]);
        var diskSpace = new SequenceAvailableDiskSpace(
            10L * 1024 * 1024 * 1024,
            512L * 1024 * 1024);
        var writer = new StagedZipRebuildCopyWriter(
            new ArchiveCreationRecoveryStore(Path.Combine(root.Path, "creation-recovery")),
            creationResourceGuard: new ArchiveCreationResourceGuard(diskSpace));

        Exception? failure = null;
        try
        {
            await writer.RebuildAsync(
                new BuiltInZipArchiveWriterProvider(),
                handle,
                ArchiveIndex.Create(handle.Entries),
                [],
                [],
                sourceArchive,
                CaptureSnapshot(sourceArchive),
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Assert.IsNotNull(failure);
        var providerFailure = Assert.IsInstanceOfType<ArchiveProviderException>(
            failure,
            failure.ToString());
        Assert.AreEqual(ArchiveErrorCategory.InsufficientSpace, providerFailure.Category);
        Assert.AreEqual("creation.insufficient_space_during_write", providerFailure.Code);
        Assert.IsTrue(diskSpace.QueryCount >= 2);
        Assert.IsFalse(File.Exists(destination));
        CollectionAssert.AreEqual(
            sourceHash,
            SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive)));
        AssertNoResidue(root.Path);
    }

    [TestMethod]
    [DataRow(1, 2, "archive.entry_exceeded_declared_size")]
    [DataRow(2, 1, "archive.entry_size_mismatch")]
    public async Task MisleadingRetainedEntrySizesFailClosed(
        int declaredLength,
        int actualLength,
        string expectedCode)
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var destination = Path.Combine(root.Path, "misleading.zip");
        await File.WriteAllBytesAsync(sourceArchive, "immutable source"u8.ToArray());
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive));
        await using var handle = new FakeArchiveHandle(
            [(Entry("misleading.bin", ArchiveEntryKind.File, declaredLength), new byte[actualLength])]);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).RebuildAsync(
                new BuiltInZipArchiveWriterProvider(),
                handle,
                ArchiveIndex.Create(handle.Entries),
                [],
                [],
                sourceArchive,
                CaptureSnapshot(sourceArchive),
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                _ => ValueTask.CompletedTask,
                CancellationToken.None));

        Assert.AreEqual(ArchiveErrorCategory.CorruptArchive, exception.Category);
        Assert.AreEqual(expectedCode, exception.Code);
        Assert.IsFalse(File.Exists(destination));
        CollectionAssert.AreEqual(
            sourceHash,
            SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive)));
        AssertNoResidue(root.Path);
    }

    [TestMethod]
    public async Task CancellationAtFinalValidationPublishesNothingAndPreservesOriginal()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var destination = Path.Combine(root.Path, "final-cancel.zip");
        await File.WriteAllBytesAsync(sourceArchive, "immutable source"u8.ToArray());
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive));
        var payload = "retained"u8.ToArray();
        await using var handle = new FakeArchiveHandle(
            [(Entry("keep.txt", ArchiveEntryKind.File, payload.Length), payload)]);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await CreateWriter(root.Path).RebuildAsync(
                new BuiltInZipArchiveWriterProvider(),
                handle,
                ArchiveIndex.Create(handle.Entries),
                [],
                [],
                sourceArchive,
                CaptureSnapshot(sourceArchive),
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                _ => ValueTask.CompletedTask,
                cancellation.Token,
                beforeCommit: () =>
                {
                    cancellation.Cancel();
                    return ValueTask.CompletedTask;
                }));

        Assert.IsFalse(File.Exists(destination));
        CollectionAssert.AreEqual(
            sourceHash,
            SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive)));
        AssertNoResidue(root.Path);
    }

    [TestMethod]
    public async Task OpenArchiveMutationAtFinalValidationPublishesNothing()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var destination = Path.Combine(root.Path, "source-changed.zip");
        await File.WriteAllBytesAsync(sourceArchive, "admitted source"u8.ToArray());
        var fingerprint = await ArchiveFingerprintService.CreateAsync(
            sourceArchive,
            CancellationToken.None);
        var payload = "retained"u8.ToArray();
        await using var handle = new FakeArchiveHandle(
            [(Entry("keep.txt", ArchiveEntryKind.File, payload.Length), payload)]);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).RebuildAsync(
                new BuiltInZipArchiveWriterProvider(),
                handle,
                ArchiveIndex.Create(handle.Entries),
                [],
                [],
                sourceArchive,
                CaptureSnapshot(sourceArchive),
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                token => ArchiveFingerprintService.EnsureUnchangedAsync(
                    sourceArchive,
                    fingerprint,
                    token),
                CancellationToken.None,
                beforeCommit: async () =>
                    await File.WriteAllBytesAsync(sourceArchive, "changed source"u8.ToArray())));

        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, exception.Category);
        Assert.AreEqual("archive.source_changed", exception.Code);
        Assert.IsFalse(File.Exists(destination));
        AssertNoResidue(root.Path);
    }

    [TestMethod]
    public async Task MappedSourceLeaseBlocksWritesThroughFinalValidationAndCommit()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var mapped = Path.Combine(root.Path, "mapped.txt");
        var destination = Path.Combine(root.Path, "leased-source.zip");
        await File.WriteAllBytesAsync(sourceArchive, "immutable source"u8.ToArray());
        await File.WriteAllBytesAsync(mapped, "leased payload"u8.ToArray());
        await using var handle = new FakeArchiveHandle([]);
        var mutationBlocked = false;

        await CreateWriter(root.Path).RebuildAsync(
            new BuiltInZipArchiveWriterProvider(),
            handle,
            ArchiveIndex.Create(handle.Entries),
            [],
            [new ArchiveRebuildCopyFile(mapped, "mapped.txt")],
            sourceArchive,
            CaptureSnapshot(sourceArchive),
            destination,
            ArchiveCompressionPreset.Balanced,
            _ => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask,
            CancellationToken.None,
            beforeCommit: () =>
            {
                try
                {
                    using var mutation = new FileStream(
                        mapped,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.Read);
                    mutation.WriteByte(0x7F);
                    Assert.Fail("The mapped source accepted a write while its rebuild lease was active.");
                }
                catch (IOException)
                {
                    mutationBlocked = true;
                }

                return ValueTask.CompletedTask;
            });

        Assert.IsTrue(mutationBlocked);
        AssertZip(
            destination,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["mapped.txt"] = "leased payload"u8.ToArray(),
            });
        AssertNoResidue(root.Path);
    }

    [TestMethod]
    public async Task TreeMutationAtFinalValidationPublishesNothingAndPreservesOriginal()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var sourceTree = Path.Combine(root.Path, "source-tree");
        var destination = Path.Combine(root.Path, "tree-changed.zip");
        Directory.CreateDirectory(sourceTree);
        await File.WriteAllTextAsync(Path.Combine(sourceTree, "original.txt"), "original");
        await File.WriteAllBytesAsync(sourceArchive, "immutable source"u8.ToArray());
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive));
        var fingerprint = await ArchiveFingerprintService.CreateAsync(sourceArchive, CancellationToken.None);
        var payload = "retained"u8.ToArray();
        await using var handle = new FakeArchiveHandle(
            [(Entry("keep.txt", ArchiveEntryKind.File, payload.Length), payload)]);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).RebuildWithTreeEditsAsync(
                new BuiltInZipArchiveWriterProvider(),
                handle,
                ArchiveIndex.Create(handle.Entries),
                fingerprint,
                [],
                [],
                [],
                [new ArchiveCreationDirectory(sourceTree, "Imported")],
                [],
                sourceArchive,
                CaptureSnapshot(sourceArchive),
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                token => ArchiveFingerprintService.EnsureUnchangedAsync(
                    sourceArchive,
                    fingerprint,
                    token),
                CancellationToken.None,
                beforeCommit: async () =>
                    await File.WriteAllTextAsync(Path.Combine(sourceTree, "added.txt"), "added")));

        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, exception.Category);
        Assert.AreEqual("source.directory_tree_changed", exception.Code);
        Assert.IsFalse(File.Exists(destination));
        CollectionAssert.AreEqual(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive)));
        AssertNoResidue(root.Path);
    }

    [TestMethod]
    public async Task MetadataReadbackMismatchPublishesNothingAndPreservesOriginal()
    {
        using var root = new TemporaryDirectory();
        var sourceArchive = Path.Combine(root.Path, "source.zip");
        var destination = Path.Combine(root.Path, "metadata-mismatch.zip");
        await File.WriteAllBytesAsync(sourceArchive, "immutable source"u8.ToArray());
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive));
        var payload = "retained payload"u8.ToArray();
        await using var handle = new FakeArchiveHandle(
            [(Entry("keep.txt", ArchiveEntryKind.File, payload.Length), payload)]);
        var index = ArchiveIndex.Create(handle.Entries);
        var fingerprint = await ArchiveFingerprintService.CreateAsync(sourceArchive, CancellationToken.None);
        var retained = index.Entries.Single(entry => entry.Descriptor.RawPath == "keep.txt");
        var metadata = new ArchiveRebuildMetadataDirective(
            new ArchiveRebuildMetadataTarget(
                ArchiveRebuildMetadataTargetKind.RetainedEntryKey,
                new ArchiveEntryKey(retained.Descriptor.NormalizedPath!, retained.Descriptor.DuplicateOrdinal)),
            new ArchiveDosLocalTimestamp(2026, 8, 25, 12, 34, 56),
            AttributesLowWord: 0x20);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).RebuildWithMetadataAsync(
                new MetadataMutatingWriterProvider(new BuiltInZipArchiveWriterProvider()),
                handle,
                index,
                fingerprint,
                [],
                [],
                [],
                [],
                [],
                [metadata],
                sourceArchive,
                CaptureSnapshot(sourceArchive),
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                token => ArchiveFingerprintService.EnsureUnchangedAsync(sourceArchive, fingerprint, token),
                CancellationToken.None));

        Assert.AreEqual(ArchiveErrorCategory.ProviderFailure, exception.Category);
        Assert.AreEqual("zip.rebuild_output_manifest_mismatch", exception.Code);
        Assert.IsFalse(File.Exists(destination));
        CollectionAssert.AreEqual(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(sourceArchive)));
        AssertNoResidue(root.Path);
    }

    private static ProviderArchiveEntry Entry(
        string path,
        ArchiveEntryKind kind,
        long length,
        DateTimeOffset? timestamp = null,
        int? attributes = null) =>
        new(
            Ordinal: 0,
            path,
            kind,
            kind == ArchiveEntryKind.Directory ? null : length,
            kind == ArchiveEntryKind.Directory ? null : length,
            timestamp,
            attributes,
            CompressionMethod: "Store",
            IntegrityValue: "crc",
            IsEncrypted: false,
            IsSolid: false,
            LinkTarget: null);

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

    private static StagedZipRebuildCopyWriter CreateWriter(string root) =>
        new(new ArchiveCreationRecoveryStore(Path.Combine(root, "creation-recovery")));

    private static void AssertZip(
        string path,
        IReadOnlyDictionary<string, byte[]> expected)
    {
        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        CollectionAssert.AreEqual(
            expected.Keys.Order(StringComparer.Ordinal).ToArray(),
            archive.Entries.Select(entry => entry.FullName).ToArray());
        foreach (var entry in archive.Entries)
        {
            using var entryStream = entry.Open();
            using var payload = new MemoryStream();
            entryStream.CopyTo(payload);
            CollectionAssert.AreEqual(expected[entry.FullName], payload.ToArray(), entry.FullName);
        }
    }

    private static void AssertNoResidue(string root)
    {
        Assert.HasCount(
            0,
            Directory.GetFiles(root, ".rainarchives-*.zip.staging", SearchOption.TopDirectoryOnly));
        var recoveryRoot = Path.Combine(root, "creation-recovery");
        if (Directory.Exists(recoveryRoot))
        {
            Assert.HasCount(
                0,
                Directory.GetDirectories(
                    recoveryRoot,
                    ArchiveCreationRecoveryStore.OperationPrefix + "*",
                    SearchOption.TopDirectoryOnly));
        }
    }

    private sealed class FakeArchiveHandle : IArchiveHandle
    {
        private readonly IReadOnlyList<byte[]> _payloads;
        private readonly Action? _afterFirstCopyChunk;

        public FakeArchiveHandle(
            IReadOnlyList<(ProviderArchiveEntry Entry, byte[] Payload)> entries,
            Action? afterFirstCopyChunk = null)
        {
            Entries = entries
                .Select((entry, ordinal) => entry.Entry with { Ordinal = ordinal })
                .ToArray();
            _payloads = entries.Select(entry => entry.Payload).ToArray();
            _afterFirstCopyChunk = afterFirstCopyChunk;
        }

        public int CopyCount { get; private set; }
        public string Format => "zip";
        public string ProviderId => "fake/zip";
        public ArchiveCapability Capabilities => ArchiveCapability.List | ArchiveCapability.Extract;
        public bool IsSolid => false;
        public ArchiveVolumeSetKind VolumeSetKind => ArchiveVolumeSetKind.Single;
        public IReadOnlyList<ProviderArchiveEntry> Entries { get; }

        public ValueTask<Stream> OpenEntryStreamAsync(
            int providerOrdinal,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(
                new MemoryStream(_payloads[providerOrdinal], writable: false));

        public async ValueTask CopyEntryToAsync(
            int providerOrdinal,
            Stream destination,
            CancellationToken cancellationToken)
        {
            CopyCount++;
            var payload = _payloads[providerOrdinal];
            if (_afterFirstCopyChunk is null || payload.Length == 0)
            {
                await destination.WriteAsync(payload, cancellationToken);
                return;
            }

            var first = Math.Min(payload.Length, 64 * 1024);
            await destination.WriteAsync(payload.AsMemory(0, first), cancellationToken);
            _afterFirstCopyChunk();
            cancellationToken.ThrowIfCancellationRequested();
            await destination.WriteAsync(payload.AsMemory(first), cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MetadataMutatingWriterProvider(IArchiveWriterProvider inner) : IArchiveWriterProvider
    {
        public string Id => inner.Id + "/metadata-mutator";
        public string Format => inner.Format;
        public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets => inner.CompressionPresets;

        public async ValueTask<IArchiveWriterHandle> CreateAsync(
            Stream destination,
            ArchiveCompressionPreset compressionPreset,
            CancellationToken cancellationToken)
        {
            var handle = await inner.CreateAsync(destination, compressionPreset, cancellationToken);
            return new MetadataMutatingWriterHandle(handle);
        }
    }

    private sealed class MetadataMutatingWriterHandle(IArchiveWriterHandle inner) : IArchiveWriterHandle
    {
        public ValueTask AddDirectoryAsync(
            ArchiveWriteDirectory entry,
            CancellationToken cancellationToken) =>
            inner.AddDirectoryAsync(entry, cancellationToken);

        public ValueTask AddFileAsync(
            ArchiveWriteEntry entry,
            Stream source,
            Func<long, ValueTask> reportEntryBytes,
            CancellationToken cancellationToken) =>
            inner.AddFileAsync(
                entry with { LastWriteTime = entry.LastWriteTime.AddSeconds(2) },
                source,
                reportEntryBytes,
                cancellationToken);

        public ValueTask AddFileFromProducerAsync(
            ArchiveWriteEntry entry,
            Func<Stream, CancellationToken, ValueTask> writeSource,
            Func<long, ValueTask> reportEntryBytes,
            CancellationToken cancellationToken) =>
            inner.AddFileFromProducerAsync(
                entry with { LastWriteTime = entry.LastWriteTime.AddSeconds(2) },
                writeSource,
                reportEntryBytes,
                cancellationToken);

        public ValueTask CompleteAsync(CancellationToken cancellationToken) =>
            inner.CompleteAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class SequenceAvailableDiskSpace(params long[] values) : IAvailableDiskSpace
    {
        private int _index;

        public int QueryCount => _index;

        public long GetAvailableFreeSpace(string driveRoot)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(driveRoot));
            Assert.IsTrue(values.Length > 0);
            var index = Interlocked.Increment(ref _index) - 1;
            return values[Math.Min(index, values.Length - 1)];
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.StagedZipRebuildCopyWriterTests",
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
