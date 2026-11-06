using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class StagedArchiveCreationWriterTests
{
    [TestMethod]
    public async Task SourceReplacementAfterPlanningFailsClosedAndCleansStaging()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var original = Path.Combine(root.Path, "original.txt");
        var destination = Path.Combine(root.Path, "created.zip");
        await File.WriteAllTextAsync(source, "original");
        var provider = new CopyingWriterProvider();

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).CreateZipAsync(
                provider,
                [new ArchiveCreationFile(source, "source.txt")],
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                beforeWriting: async () =>
                {
                    File.Move(source, original);
                    await File.WriteAllTextAsync(source, "replacement");
                }));

        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, exception.Category);
        Assert.AreEqual("source.changed_during_creation", exception.Code);
        Assert.AreEqual(0, provider.AddCount);
        Assert.IsFalse(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task DestinationAppearingBeforeCommitIsPreservedAndStagingIsRemoved()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "created.zip");
        var sentinel = new byte[] { 0x53, 0x41, 0x49, 0x4C };
        await File.WriteAllTextAsync(source, "payload");

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).CreateZipAsync(
                new CopyingWriterProvider(),
                [new ArchiveCreationFile(source, "source.txt")],
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                beforeCommit: async () => await File.WriteAllBytesAsync(destination, sentinel)));

        Assert.AreEqual(ArchiveErrorCategory.DestinationConflict, exception.Category);
        Assert.AreEqual("destination.appeared_before_commit", exception.Code);
        CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task ProviderFailureLeavesNeitherDestinationNorStagingFile()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "created.zip");
        await File.WriteAllTextAsync(source, "payload");

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).CreateZipAsync(
                new ThrowingWriterProvider(),
                [new ArchiveCreationFile(source, "source.txt")],
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None));

        Assert.AreEqual("writer.injected_failure", exception.Code);
        Assert.IsFalse(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task DirectoryProviderFailureLeavesNeitherDestinationNorStagingFile()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "empty-tree");
        Directory.CreateDirectory(tree);
        var destination = Path.Combine(root.Path, "created.zip");

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).CreateZipTreeAsync(
                new ThrowingWriterProvider(),
                [],
                [new ArchiveCreationDirectory(tree, "tree")],
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None));

        Assert.AreEqual("writer.injected_directory_failure", exception.Code);
        Assert.IsFalse(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task RuntimeLowSpaceRefusalLeavesNeitherDestinationNorStagingFile()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.bin");
        var destination = Path.Combine(root.Path, "created.zip");
        await File.WriteAllBytesAsync(source, new byte[128 * 1024]);
        var diskSpace = new SequenceAvailableDiskSpace(
            10L * 1024 * 1024 * 1024,
            512L * 1024 * 1024);
        var writer = new StagedArchiveCreationWriter(
            CreateRecoveryStore(root.Path),
            resourceGuard: new ArchiveCreationResourceGuard(diskSpace));

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await writer.CreateZipAsync(
                new CopyingWriterProvider(),
                [new ArchiveCreationFile(source, "source.bin")],
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None));

        Assert.AreEqual(ArchiveErrorCategory.InsufficientSpace, exception.Category);
        Assert.AreEqual("creation.insufficient_space_during_write", exception.Code);
        Assert.IsTrue(diskSpace.QueryCount >= 2);
        Assert.IsFalse(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task DirectoryMutationDuringCreationFailsClosedAndCleansStaging()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "tree");
        var destination = Path.Combine(root.Path, "created.zip");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(tree, "original.txt"), "original");

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).CreateZipTreeAsync(
                new CopyingWriterProvider(),
                [],
                [new ArchiveCreationDirectory(tree, "tree")],
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                beforeCommit: async () =>
                    await File.WriteAllTextAsync(Path.Combine(tree, "added.txt"), "added")));

        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, exception.Category);
        Assert.AreEqual("source.directory_tree_changed", exception.Code);
        Assert.IsFalse(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task MixedTreeProgressCountsDirectoriesWithoutAddingSourceBytes()
    {
        using var root = new TemporaryDirectory();
        var tree = Path.Combine(root.Path, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "empty"));
        var source = Path.Combine(tree, "source.txt");
        await File.WriteAllTextAsync(source, "payload");
        var destination = Path.Combine(root.Path, "created.zip");
        var provider = new CopyingWriterProvider();
        var progress = new List<OperationProgress>();

        await CreateWriter(root.Path).CreateZipTreeAsync(
            provider,
            [],
            [new ArchiveCreationDirectory(tree, "tree")],
            destination,
            ArchiveCompressionPreset.Balanced,
            update =>
            {
                progress.Add(update);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.AreEqual(1, provider.AddCount);
        Assert.AreEqual(2, provider.DirectoryAddCount);
        var terminalProgress = progress[^1];
        Assert.AreEqual(new FileInfo(source).Length, terminalProgress.BytesProcessed);
        Assert.AreEqual(new FileInfo(source).Length, terminalProgress.TotalBytes);
        Assert.AreEqual(3, terminalProgress.EntriesProcessed);
        Assert.AreEqual(3, terminalProgress.TotalEntries);
        Assert.IsNull(terminalProgress.CurrentEntryName);
        Assert.IsTrue(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task UnanimousSourceProvenanceIsAppliedToCommittedArchive()
    {
        using var root = new TemporaryDirectory();
        var first = Path.Combine(root.Path, "first.txt");
        var second = Path.Combine(root.Path, "second.txt");
        var destination = Path.Combine(root.Path, "created.zip");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        await File.WriteAllTextAsync(
            first + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://one.invalid/private\r\n");
        await File.WriteAllTextAsync(
            second + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://two.invalid/private\r\n");
        var warnings = new List<ArchiveWarning>();

        await CreateWriter(root.Path).CreateZipAsync(
            new CopyingWriterProvider(),
            [
                new ArchiveCreationFile(first, "first.txt"),
                new ArchiveCreationFile(second, "second.txt"),
            ],
            destination,
            ArchiveCompressionPreset.Balanced,
            _ => ValueTask.CompletedTask,
            CancellationToken.None,
            reportWarning: warning =>
            {
                warnings.Add(warning);
                return ValueTask.CompletedTask;
            });

        Assert.IsEmpty(warnings);
        Assert.AreEqual(
            "[ZoneTransfer]\r\nZoneId=3\r\n",
            await File.ReadAllTextAsync(destination + ":Zone.Identifier"));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task MixedSourceProvenanceIsOmittedWithOneSanitizedWarning()
    {
        using var root = new TemporaryDirectory();
        var marked = Path.Combine(root.Path, "marked.txt");
        var unmarked = Path.Combine(root.Path, "unmarked.txt");
        var destination = Path.Combine(root.Path, "created.zip");
        await File.WriteAllTextAsync(marked, "marked");
        await File.WriteAllTextAsync(unmarked, "unmarked");
        await File.WriteAllTextAsync(
            marked + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://secret.invalid/source\r\n");
        var warnings = new List<ArchiveWarning>();

        await CreateWriter(root.Path).CreateZipAsync(
            new CopyingWriterProvider(),
            [
                new ArchiveCreationFile(marked, "marked.txt"),
                new ArchiveCreationFile(unmarked, "unmarked.txt"),
            ],
            destination,
            ArchiveCompressionPreset.Balanced,
            _ => ValueTask.CompletedTask,
            CancellationToken.None,
            reportWarning: warning =>
            {
                warnings.Add(warning);
                return ValueTask.CompletedTask;
            });

        Assert.HasCount(1, warnings);
        var warning = warnings[0];
        Assert.AreEqual("provenance.source_ambiguous", warning.Code);
        Assert.IsFalse(warning.Message.Contains("secret.invalid", StringComparison.Ordinal));
        Assert.IsNull(await DownloadProvenanceService.TryReadAsync(
            destination,
            CancellationToken.None));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task ProvenanceWarningCanPrecedeAuthoritativeSourceFailureWithoutPublishingOutput()
    {
        using var root = new TemporaryDirectory();
        var marked = Path.Combine(root.Path, "marked.txt");
        var unmarked = Path.Combine(root.Path, "unmarked.txt");
        var original = Path.Combine(root.Path, "original.txt");
        var destination = Path.Combine(root.Path, "created.zip");
        await File.WriteAllTextAsync(marked, "marked");
        await File.WriteAllTextAsync(unmarked, "unmarked");
        await File.WriteAllTextAsync(
            marked + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\n");
        var warnings = new List<ArchiveWarning>();

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).CreateZipAsync(
                new CopyingWriterProvider(),
                [
                    new ArchiveCreationFile(marked, "marked.txt"),
                    new ArchiveCreationFile(unmarked, "unmarked.txt"),
                ],
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                beforeCommit: async () =>
                {
                    File.Move(unmarked, original);
                    await File.WriteAllTextAsync(unmarked, "replacement");
                },
                reportWarning: warning =>
                {
                    warnings.Add(warning);
                    return ValueTask.CompletedTask;
                }));

        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, exception.Category);
        Assert.HasCount(1, warnings);
        Assert.AreEqual("provenance.source_ambiguous", warnings[0].Code);
        Assert.IsFalse(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task MalformedSourceProvenanceIsOmittedWithOneWarning()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "created.zip");
        await File.WriteAllTextAsync(source, "payload");
        await File.WriteAllTextAsync(
            source + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=99\r\nHostUrl=https://secret.invalid/source\r\n");
        var warnings = new List<ArchiveWarning>();

        await CreateWriter(root.Path).CreateZipAsync(
            new CopyingWriterProvider(),
            [new ArchiveCreationFile(source, "source.txt")],
            destination,
            ArchiveCompressionPreset.Balanced,
            _ => ValueTask.CompletedTask,
            CancellationToken.None,
            reportWarning: warning =>
            {
                warnings.Add(warning);
                return ValueTask.CompletedTask;
            });

        Assert.HasCount(1, warnings);
        Assert.AreEqual("provenance.source_ambiguous", warnings[0].Code);
        Assert.IsFalse(warnings[0].Message.Contains("secret.invalid", StringComparison.Ordinal));
        Assert.IsNull(await DownloadProvenanceService.TryReadAsync(
            destination,
            CancellationToken.None));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task ProvenancePayloadMutationBeforeCommitFailsClosed()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "created.zip");
        await File.WriteAllTextAsync(source, "payload");
        await File.WriteAllTextAsync(
            source + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://before.invalid/\r\n");
        var sourceLastWriteTimeUtc = File.GetLastWriteTimeUtc(source);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).CreateZipAsync(
                new CopyingWriterProvider(),
                [new ArchiveCreationFile(source, "source.txt")],
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                beforeCommit: async () =>
                {
                    await File.WriteAllTextAsync(
                        source + ":Zone.Identifier",
                        "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://after.invalid//\r\n");
                    File.SetLastWriteTimeUtc(source, sourceLastWriteTimeUtc);
                }));

        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, exception.Category);
        Assert.AreEqual("source.provenance_changed", exception.Code);
        Assert.IsFalse(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task OversizedProvenancePrefixMutationBeforeCommitFailsClosed()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "created.zip");
        await File.WriteAllTextAsync(source, "payload");
        var initialPayload = Enumerable.Repeat((byte)0x41, 5000).ToArray();
        await File.WriteAllBytesAsync(source + ":Zone.Identifier", initialPayload);
        var sourceLastWriteTimeUtc = File.GetLastWriteTimeUtc(source);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).CreateZipAsync(
                new CopyingWriterProvider(),
                [new ArchiveCreationFile(source, "source.txt")],
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                beforeCommit: async () =>
                {
                    var changedPayload = initialPayload.ToArray();
                    changedPayload[0] = 0x42;
                    await File.WriteAllBytesAsync(
                        source + ":Zone.Identifier",
                        changedPayload);
                    File.SetLastWriteTimeUtc(source, sourceLastWriteTimeUtc);
                }));

        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, exception.Category);
        Assert.AreEqual("source.provenance_changed", exception.Code);
        Assert.IsFalse(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task ExplicitSourceReplacementAfterWritingFailsBeforeCommit()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var original = Path.Combine(root.Path, "original.txt");
        var destination = Path.Combine(root.Path, "created.zip");
        await File.WriteAllTextAsync(source, "original");

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await CreateWriter(root.Path).CreateZipAsync(
                new CopyingWriterProvider(),
                [new ArchiveCreationFile(source, "source.txt")],
                destination,
                ArchiveCompressionPreset.Balanced,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                beforeCommit: async () =>
                {
                    File.Move(source, original);
                    await File.WriteAllTextAsync(source, "replacement");
                }));

        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, exception.Category);
        Assert.AreEqual("source.changed_during_creation", exception.Code);
        Assert.IsFalse(File.Exists(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task NativeNoReplaceRenamePreservesDestinationAndRestoresStagingDeletion()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "created.zip");
        var sentinel = new byte[] { 0x52, 0x41, 0x49, 0x4E };
        await using (var staged = HandleRelativeExtractionRoot.CreateStagedFile(
                         root.Path,
                         ".rainarchives-native-race.zip.staging"))
        {
            await staged.Stream.WriteAsync(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
            await staged.CompleteWritingAsync(CancellationToken.None);
            await File.WriteAllBytesAsync(destination, sentinel);

            var exception = Assert.ThrowsExactly<ArchiveProviderException>(
                () => staged.CommitAs(Path.GetFileName(destination)));

            Assert.AreEqual(ArchiveErrorCategory.DestinationConflict, exception.Category);
            Assert.AreEqual("destination.appeared_before_commit", exception.Code);
            CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(destination));
        }

        CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(destination));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task PreparedProvenanceSurvivesDeletePendingWriteVerificationAndRename()
    {
        using var root = new TemporaryDirectory();
        var stagingName = ".rainarchives-held-provenance.zip.staging";
        var destination = Path.Combine(root.Path, "created.zip");
        var archiveBytes = new byte[] { 0x50, 0x4B, 0x05, 0x06 };
        await using (var staged = HandleRelativeExtractionRoot.CreateStagedFile(
                         root.Path,
                         stagingName,
                         prepareProvenanceStream: true))
        {
            await staged.Stream.WriteAsync(archiveBytes);
            await staged.CompleteWritingAsync(CancellationToken.None);
            await staged.ApplyPreparedProvenanceAsync(
                new DownloadProvenance(3),
                CancellationToken.None);
            staged.CommitAs(Path.GetFileName(destination));
        }

        CollectionAssert.AreEqual(archiveBytes, await File.ReadAllBytesAsync(destination));
        Assert.AreEqual(
            "[ZoneTransfer]\r\nZoneId=3\r\n",
            await File.ReadAllTextAsync(destination + ":Zone.Identifier"));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task PreparedProvenanceIsRemovedWhenStagedArchiveIsNotCommitted()
    {
        using var root = new TemporaryDirectory();
        var stagingName = ".rainarchives-held-provenance-cancelled.zip.staging";
        var stagingPath = Path.Combine(root.Path, stagingName);
        await using (var staged = HandleRelativeExtractionRoot.CreateStagedFile(
                         root.Path,
                         stagingName,
                         prepareProvenanceStream: true))
        {
            await staged.Stream.WriteAsync(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
            await staged.CompleteWritingAsync(CancellationToken.None);
            await staged.ApplyPreparedProvenanceAsync(
                new DownloadProvenance(3),
                CancellationToken.None);
        }

        Assert.IsFalse(File.Exists(stagingPath));
        Assert.IsFalse(File.Exists(stagingPath + ":Zone.Identifier"));
        AssertNoStagingFiles(root.Path);
    }

    [TestMethod]
    public async Task PreparedProvenanceCancellationBeforeApplicationRemovesStaging()
    {
        using var root = new TemporaryDirectory();
        var stagingName = ".rainarchives-held-provenance-cancel.zip.staging";
        var stagingPath = Path.Combine(root.Path, stagingName);
        await using (var staged = HandleRelativeExtractionRoot.CreateStagedFile(
                         root.Path,
                         stagingName,
                         prepareProvenanceStream: true))
        {
            await staged.Stream.WriteAsync(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
            await staged.CompleteWritingAsync(CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await staged.ApplyPreparedProvenanceAsync(
                    new DownloadProvenance(3),
                    cancellation.Token));
        }

        Assert.IsFalse(File.Exists(stagingPath));
        Assert.IsFalse(File.Exists(stagingPath + ":Zone.Identifier"));
        AssertNoStagingFiles(root.Path);
    }

    private static void AssertNoStagingFiles(string parent)
    {
        Assert.HasCount(
            0,
            Directory.GetFiles(
                parent,
                ".rainarchives-*.zip.staging",
                SearchOption.TopDirectoryOnly));
        var recoveryRoot = Path.Combine(parent, "creation-recovery");
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

    private static StagedArchiveCreationWriter CreateWriter(string testRoot) =>
        new(CreateRecoveryStore(testRoot));

    private static ArchiveCreationRecoveryStore CreateRecoveryStore(string testRoot) =>
        new(Path.Combine(testRoot, "creation-recovery"));

    private sealed class CopyingWriterProvider : IArchiveWriterProvider
    {
        private static readonly IReadOnlySet<ArchiveCompressionPreset> Presets =
            new HashSet<ArchiveCompressionPreset> { ArchiveCompressionPreset.Balanced };

        public int AddCount { get; private set; }
        public int DirectoryAddCount { get; private set; }
        public string Id => "copying-writer-test";
        public string Format => "zip";
        public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets => Presets;

        public ValueTask<IArchiveWriterHandle> CreateAsync(
            Stream destination,
            ArchiveCompressionPreset compressionPreset,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IArchiveWriterHandle>(new CopyingWriterHandle(destination, this));

        private sealed class CopyingWriterHandle(
            Stream destination,
            CopyingWriterProvider owner) : IArchiveWriterHandle
        {
            public ValueTask AddDirectoryAsync(
                ArchiveWriteDirectory entry,
                CancellationToken cancellationToken)
            {
                owner.DirectoryAddCount++;
                return ValueTask.CompletedTask;
            }

            public async ValueTask AddFileAsync(
                ArchiveWriteEntry entry,
                Stream source,
                Func<long, ValueTask> reportEntryBytes,
                CancellationToken cancellationToken)
            {
                owner.AddCount++;
                await source.CopyToAsync(destination, cancellationToken);
                await reportEntryBytes(entry.Length);
            }

            public ValueTask CompleteAsync(CancellationToken cancellationToken) =>
                ValueTask.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingWriterProvider : IArchiveWriterProvider
    {
        private static readonly IReadOnlySet<ArchiveCompressionPreset> Presets =
            new HashSet<ArchiveCompressionPreset> { ArchiveCompressionPreset.Balanced };

        public string Id => "throwing-writer-test";
        public string Format => "zip";
        public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets => Presets;

        public ValueTask<IArchiveWriterHandle> CreateAsync(
            Stream destination,
            ArchiveCompressionPreset compressionPreset,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IArchiveWriterHandle>(new ThrowingWriterHandle(destination));

        private sealed class ThrowingWriterHandle(Stream destination) : IArchiveWriterHandle
        {
            public async ValueTask AddDirectoryAsync(
                ArchiveWriteDirectory entry,
                CancellationToken cancellationToken)
            {
                await destination.WriteAsync(new byte[] { 0x01 }, cancellationToken);
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ProviderFailure,
                    "writer.injected_directory_failure",
                    "Injected directory-writer failure.");
            }

            public async ValueTask AddFileAsync(
                ArchiveWriteEntry entry,
                Stream source,
                Func<long, ValueTask> reportEntryBytes,
                CancellationToken cancellationToken)
            {
                await destination.WriteAsync(new byte[] { 0x01 }, cancellationToken);
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ProviderFailure,
                    "writer.injected_failure",
                    "Injected writer failure.");
            }

            public ValueTask CompleteAsync(CancellationToken cancellationToken) =>
                ValueTask.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class SequenceAvailableDiskSpace : IAvailableDiskSpace
    {
        private readonly long[] _values;
        private int _index;

        public SequenceAvailableDiskSpace(params long[] values)
        {
            Assert.IsTrue(values.Length > 0);
            _values = values;
        }

        public int QueryCount => _index;

        public long GetAvailableFreeSpace(string driveRoot)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(driveRoot));
            var index = Interlocked.Increment(ref _index) - 1;
            return _values[Math.Min(index, _values.Length - 1)];
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.StagedArchiveCreationWriterTests",
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
