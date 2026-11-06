using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ExtractionRecoveryStoreTests
{
    private const string TestDirectoryIdentity = "01234567:0123456789ABCDEF";

    [TestMethod]
    public void MissingRecoveryRootIsAnEmptyPathFreeInventory()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);

        var inventory = store.Inspect();

        Assert.AreEqual(0, inventory.ActiveOperationCount);
        Assert.AreEqual(0, inventory.PotentialRecoveryCount);
        Assert.AreEqual(0, inventory.SuspiciousOperationCount);
        Assert.AreEqual(0, inventory.IncompleteJournalCount);
        Assert.IsFalse(inventory.IsTruncated);
        Assert.IsFalse(inventory.InspectionIncomplete);
        Assert.HasCount(0, inventory.RecoveryRecords);
    }

    [TestMethod]
    public async Task UnknownLocalStateIsReportedAndPreserved()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        Directory.CreateDirectory(store.RootDirectory);
        var unknown = Path.Combine(store.RootDirectory, "do-not-trust-or-delete.txt");
        await File.WriteAllTextAsync(unknown, "preserve me");

        var inventory = store.Inspect();

        Assert.AreEqual(1, inventory.SuspiciousOperationCount);
        Assert.AreEqual("preserve me", await File.ReadAllTextAsync(unknown));
    }

    [TestMethod]
    public async Task NetworkRecoveryRootIsNeverUsedForJournalCreation()
    {
        using var root = new TemporaryDirectory();
        var store = new ExtractionRecoveryStore(
            @"\\203.0.113.1\RainArchivesExtractionRecovery\must-not-be-contacted");

        Assert.IsTrue(store.Inspect().InspectionIncomplete);
        var exception = await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await store.BeginAsync(
                OperationId.New(),
                Path.Combine(root.Path, "output"),
                FileSystemIdentityService.CaptureExistingDirectoryIdentity(root.Path),
                CancellationToken.None));
        StringAssert.Contains(exception.Message, "local fixed-drive");
    }

    [TestMethod]
    public async Task ExclusiveJournalReportsActiveThenBecomesAStaleAdmission()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var destination = Path.Combine(root.Path, "output");
        var operationId = OperationId.New();
        var transaction = await store.BeginAsync(
            operationId,
            destination,
            FileSystemIdentityService.CaptureExistingDirectoryIdentity(root.Path),
            CancellationToken.None);

        var active = store.Inspect();
        Assert.AreEqual(1, active.ActiveOperationCount);
        Assert.AreEqual(0, active.PotentialRecoveryCount);

        await transaction.DisposeAsync();
        var stale = store.Inspect();
        var record = stale.RecoveryRecords.Single();
        Assert.AreEqual(1, stale.PotentialRecoveryCount);
        Assert.AreEqual(operationId, record.OperationId);
        Assert.AreEqual(destination, record.DestinationPath);
        Assert.AreEqual(ExtractionRecoveryPhase.Admitted, record.Phase);
        Assert.AreEqual(1, record.Sequence);
        Assert.IsNull(record.StagingDirectoryIdentity);
        Assert.IsNull(record.TreeRevision);
        Assert.IsFalse(record.HasIncompleteTail);
        Assert.AreEqual(
            $".rainarchives-extract-{record.RecoveryId}.staging",
            record.StagingDirectoryName);
    }

    [TestMethod]
    public async Task ExplicitInspectionRefusesAnActiveJournalWithoutDestinationAuthority()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await store.InspectArtifactsAsync(transaction.Token));

        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, exception.Category);
        Assert.AreEqual("extraction_recovery.active", exception.Code);
        Assert.IsFalse(Directory.Exists(Path.Combine(root.Path, "output")));
        await transaction.DisposeAsync();
    }

    [TestMethod]
    public async Task AllFiveDurablePhasesRetainImmutableTreeEvidence()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        var evidence = new ExtractionTreeRecoveryEvidence(
            FileCount: 2,
            DirectoryCount: 1,
            TotalBytes: 13,
            NamedStreamCount: 0,
            Revision: new string('a', 64));
        await transaction.RecordWritingAsync(TestDirectoryIdentity, CancellationToken.None);
        await transaction.RecordFinalizedAsync(evidence, CancellationToken.None);
        await transaction.RecordCommitIntentAsync(CancellationToken.None);
        await transaction.RecordCommittedAsync(CancellationToken.None);
        await transaction.DisposeAsync();

        var record = store.Inspect().RecoveryRecords.Single();
        Assert.AreEqual(ExtractionRecoveryPhase.Committed, record.Phase);
        Assert.AreEqual(5, record.Sequence);
        Assert.AreEqual(TestDirectoryIdentity, record.StagingDirectoryIdentity);
        Assert.AreEqual(evidence.FileCount, record.FileCount);
        Assert.AreEqual(evidence.DirectoryCount, record.DirectoryCount);
        Assert.AreEqual(evidence.TotalBytes, record.TotalBytes);
        Assert.AreEqual(evidence.NamedStreamCount, record.NamedStreamCount);
        Assert.AreEqual(evidence.Revision, record.TreeRevision);
    }

    [TestMethod]
    public async Task FinalizedEvidenceRejectsMoreNamedStreamsThanFiles()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        await transaction.RecordWritingAsync(TestDirectoryIdentity, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await transaction.RecordFinalizedAsync(
                new ExtractionTreeRecoveryEvidence(
                    FileCount: 1,
                    DirectoryCount: 0,
                    TotalBytes: 13,
                    NamedStreamCount: 2,
                    Revision: new string('a', 64)),
                CancellationToken.None));

        await transaction.DisposeAsync();
        Assert.AreEqual(
            ExtractionRecoveryPhase.Writing,
            store.Inspect().RecoveryRecords.Single().Phase);
    }

    [TestMethod]
    public async Task WritingPhaseRejectsAZeroFilesystemIdentity()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await transaction.RecordWritingAsync(
                "01234567:0000000000000000",
                CancellationToken.None));

        await transaction.DisposeAsync();
        Assert.AreEqual(
            ExtractionRecoveryPhase.Admitted,
            store.Inspect().RecoveryRecords.Single().Phase);
    }

    [TestMethod]
    public async Task TornTailKeepsTheLastChecksummedPhaseAndNeverMutatesIt()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        await transaction.RecordWritingAsync(TestDirectoryIdentity, CancellationToken.None);
        var journal = Path.Combine(
            transaction.OperationDirectoryPath,
            ExtractionRecoveryStore.JournalFileName);
        await transaction.DisposeAsync();
        await using (var stream = new FileStream(
                         journal,
                         FileMode.Append,
                         FileAccess.Write,
                         FileShare.None))
        {
            await stream.WriteAsync("RAE"u8.ToArray());
            stream.Flush(flushToDisk: true);
        }

        var inventory = store.Inspect();

        Assert.AreEqual(1, inventory.PotentialRecoveryCount);
        Assert.AreEqual(1, inventory.IncompleteJournalCount);
        Assert.AreEqual(ExtractionRecoveryPhase.Writing, inventory.RecoveryRecords.Single().Phase);
        Assert.IsTrue(inventory.RecoveryRecords.Single().HasIncompleteTail);
        Assert.IsTrue(File.Exists(journal));
    }

    [TestMethod]
    public async Task TornFinalizedJournalForcesIncompleteReadOnlyAssessment()
    {
        using var root = new TemporaryDirectory();
        var fixture = await CreateFinalizedFixtureAsync(root.Path, commit: false);
        var journal = Path.Combine(
            fixture.Store.RootDirectory,
            ExtractionRecoveryStore.OperationPrefix + fixture.RecoveryId,
            ExtractionRecoveryStore.JournalFileName);
        await using (var stream = new FileStream(
                         journal,
                         FileMode.Append,
                         FileAccess.Write,
                         FileShare.None))
        {
            await stream.WriteAsync("RAE"u8.ToArray());
            stream.Flush(flushToDisk: true);
        }

        var before = SnapshotTree(fixture.StagingPath);
        var response = await fixture.Store.InspectArtifactsAsync(fixture.RecoveryId);

        Assert.IsTrue(response.Recovery.HasIncompleteTail);
        Assert.AreEqual(
            ExtractionRecoveryArtifactState.ExactMatch,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ExtractionRecoveryAssessment.InspectionIncomplete,
            response.Assessment);
        CollectionAssert.AreEqual(before, SnapshotTree(fixture.StagingPath));
        Assert.IsTrue(File.Exists(journal));
    }

    [TestMethod]
    public async Task CorruptJournalIsSuspiciousAndPreservedWithoutAuthority()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        var journal = Path.Combine(
            transaction.OperationDirectoryPath,
            ExtractionRecoveryStore.JournalFileName);
        await transaction.DisposeAsync();
        var bytes = await File.ReadAllBytesAsync(journal);
        bytes[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(journal, bytes);

        var inventory = store.Inspect();

        Assert.AreEqual(1, inventory.SuspiciousOperationCount);
        Assert.AreEqual(0, inventory.PotentialRecoveryCount);
        Assert.HasCount(0, inventory.RecoveryRecords);
        Assert.IsTrue(File.Exists(journal));
    }

    [TestMethod]
    public async Task ChecksummedJournalWithUnknownJsonMemberIsSuspiciousAndPreserved()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        var journal = Path.Combine(
            transaction.OperationDirectoryPath,
            ExtractionRecoveryStore.JournalFileName);
        await transaction.DisposeAsync();
        var original = await File.ReadAllBytesAsync(journal);
        const int headerBytes = 4 + sizeof(int) + 32;
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            original.AsSpan(4, sizeof(int)));
        var payload = Encoding.UTF8.GetString(original, headerBytes, payloadLength);
        var changedPayload = Encoding.UTF8.GetBytes(
            payload[..^1] + ",\"unexpected\":true}");
        var changed = new byte[headerBytes + changedPayload.Length];
        "RAEJ"u8.CopyTo(changed);
        BinaryPrimitives.WriteInt32LittleEndian(
            changed.AsSpan(4, sizeof(int)),
            changedPayload.Length);
        SHA256.HashData(changedPayload, changed.AsSpan(8, 32));
        changedPayload.CopyTo(changed.AsSpan(headerBytes));
        await File.WriteAllBytesAsync(journal, changed);

        var inventory = store.Inspect();

        Assert.AreEqual(1, inventory.SuspiciousOperationCount);
        Assert.AreEqual(0, inventory.PotentialRecoveryCount);
        Assert.IsTrue(File.Exists(journal));
    }

    [TestMethod]
    public async Task ExactJournalCleanupDeletesOnlyItsSingleProvenTree()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        var operationDirectory = transaction.OperationDirectoryPath;

        await transaction.DeleteAsync();

        Assert.IsFalse(Directory.Exists(operationDirectory));
        Assert.AreEqual(0, store.Inspect().PotentialRecoveryCount);
    }

    [TestMethod]
    public async Task ExplicitInspectionProvesExactFinalTreeWithoutChangingIt()
    {
        using var root = new TemporaryDirectory();
        var fixture = await CreateFinalizedFixtureAsync(root.Path, commit: true);
        var before = SnapshotTree(fixture.DestinationPath);

        var response = await fixture.Store.InspectArtifactsAsync(fixture.RecoveryId);

        Assert.AreEqual(ExtractionRecoveryParentState.MatchesJournal, response.DestinationParent);
        Assert.AreEqual(ExtractionRecoveryArtifactState.Absent, response.StagingArtifact.State);
        Assert.AreEqual(ExtractionRecoveryArtifactState.ExactMatch, response.FinalArtifact.State);
        Assert.AreEqual(1, response.FinalArtifact.ObservedFileCount);
        Assert.AreEqual(1, response.FinalArtifact.ObservedDirectoryCount);
        Assert.AreEqual(13L, response.FinalArtifact.ObservedTotalBytes);
        Assert.AreEqual(ExtractionRecoveryAssessment.OwnedFinalObserved, response.Assessment);
        Assert.AreEqual(fixture.DestinationPath, response.DestinationPath);
        Assert.IsFalse(response.SupportsMutation);
        Assert.IsFalse(response.SupportsRetry);
        Assert.IsFalse(response.SupportsCleanup);
        CollectionAssert.AreEqual(before, SnapshotTree(fixture.DestinationPath));
    }

    [TestMethod]
    public async Task SameLengthPayloadChangeInvalidatesTheFinalizedRevisionAndIsPreserved()
    {
        using var root = new TemporaryDirectory();
        var fixture = await CreateFinalizedFixtureAsync(root.Path, commit: false);
        var file = Path.Combine(fixture.StagingPath, "folder", "rain.txt");
        await File.WriteAllBytesAsync(file, "RAIN ARCHIVES"u8.ToArray());
        var changedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file)));

        var response = await fixture.Store.InspectArtifactsAsync(fixture.RecoveryId);

        Assert.AreEqual(
            ExtractionRecoveryArtifactState.RevisionMismatch,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ExtractionRecoveryAssessment.ChangedOrSuspicious,
            response.Assessment);
        Assert.IsTrue(File.Exists(file));
        Assert.AreEqual(
            changedHash,
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))));
    }

    [TestMethod]
    public async Task SameLengthProvenanceChangeInvalidatesRevisionAndCountsStreamBytes()
    {
        using var root = new TemporaryDirectory();
        var fixture = await CreateFinalizedFixtureAsync(
            root.Path,
            commit: false,
            includeProvenance: true);
        var file = Path.Combine(fixture.StagingPath, "folder", "rain.txt");
        var stream = file + ":Zone.Identifier";
        var replacement = DownloadProvenanceService.CreateCanonicalPayload(
            new DownloadProvenance(4));
        Assert.AreEqual(new FileInfo(stream).Length, replacement.LongLength);
        await File.WriteAllBytesAsync(stream, replacement);

        var response = await fixture.Store.InspectArtifactsAsync(fixture.RecoveryId);

        Assert.AreEqual(
            ExtractionRecoveryArtifactState.RevisionMismatch,
            response.StagingArtifact.State);
        Assert.AreEqual(1, response.StagingArtifact.ObservedNamedStreamCount);
        Assert.AreEqual(
            13L + replacement.LongLength,
            response.StagingArtifact.ObservedTotalBytes);
        Assert.AreEqual(
            ExtractionRecoveryAssessment.ChangedOrSuspicious,
            response.Assessment);
        CollectionAssert.AreEqual(replacement, await File.ReadAllBytesAsync(stream));
    }

    [TestMethod]
    public async Task ReparseInsertionIsReportedAndNeverFollowedOrDeleted()
    {
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var fixture = await CreateFinalizedFixtureAsync(root.Path, commit: false);
        var folder = Path.Combine(fixture.StagingPath, "folder");
        Directory.Delete(folder, recursive: true);
        Directory.CreateSymbolicLink(folder, outside.Path);
        var sentinel = Path.Combine(outside.Path, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "outside");

        var response = await fixture.Store.InspectArtifactsAsync(fixture.RecoveryId);

        Assert.AreEqual(
            ExtractionRecoveryArtifactState.ReparsePoint,
            response.StagingArtifact.State);
        Assert.AreEqual("outside", await File.ReadAllTextAsync(sentinel));
        Assert.IsTrue(Directory.Exists(folder));
    }

    [TestMethod]
    public async Task HardLinkInsertionIsReportedAndNeverDeleted()
    {
        using var root = new TemporaryDirectory();
        var fixture = await CreateFinalizedFixtureAsync(root.Path, commit: false);
        var file = Path.Combine(fixture.StagingPath, "folder", "rain.txt");
        var outsideLink = Path.Combine(root.Path, "outside-hardlink.txt");
        Assert.IsTrue(
            NativeMethods.CreateHardLink(outsideLink, file, IntPtr.Zero),
            $"CreateHardLink failed with Win32 error {Marshal.GetLastWin32Error()}.");

        var response = await fixture.Store.InspectArtifactsAsync(fixture.RecoveryId);

        Assert.AreEqual(
            ExtractionRecoveryArtifactState.UnexpectedHardLinks,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ExtractionRecoveryAssessment.ChangedOrSuspicious,
            response.Assessment);
        Assert.AreEqual("rain archives", await File.ReadAllTextAsync(outsideLink));
        Assert.IsTrue(File.Exists(file));
    }

    [TestMethod]
    public async Task UnexpectedNamedStreamIsReportedAndPreserved()
    {
        using var root = new TemporaryDirectory();
        var fixture = await CreateFinalizedFixtureAsync(root.Path, commit: false);
        var file = Path.Combine(fixture.StagingPath, "folder", "rain.txt");
        var stream = file + ":unexpected";
        await File.WriteAllTextAsync(stream, "preserve stream");

        var response = await fixture.Store.InspectArtifactsAsync(fixture.RecoveryId);

        Assert.AreEqual(
            ExtractionRecoveryArtifactState.UnexpectedDataStreams,
            response.StagingArtifact.State);
        Assert.AreEqual(
            ExtractionRecoveryAssessment.ChangedOrSuspicious,
            response.Assessment);
        Assert.AreEqual("preserve stream", await File.ReadAllTextAsync(stream));
        Assert.IsTrue(File.Exists(file));
    }

    [TestMethod]
    public async Task InventoryIsSortedBoundedAndDoesNotProbeRecordedDestinations()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        for (var index = 0; index < 3; index++)
        {
            var transaction = await store.BeginAsync(
                OperationId.New(),
                $@"\\203.0.113.{index + 1}\offline\output",
                TestDirectoryIdentity,
                CancellationToken.None);
            await transaction.DisposeAsync();
        }

        var all = store.Inspect();
        var identifiers = all.RecoveryRecords.Select(record => record.RecoveryId).ToArray();
        CollectionAssert.AreEqual(
            identifiers.Order(StringComparer.Ordinal).ToArray(),
            identifiers);

        var bounded = store.Inspect(maximumEntries: 1);
        Assert.AreEqual(1, bounded.PotentialRecoveryCount);
        Assert.IsTrue(bounded.IsTruncated);
    }

    [TestMethod]
    public async Task MissingJournalChildDoesNotHideOtherValidRecoveryRecords()
    {
        using var root = new TemporaryDirectory();
        var store = CreateStore(root.Path);
        var transaction = await BeginAsync(store, root.Path);
        var validRecoveryId = transaction.Token;
        await transaction.DisposeAsync();
        Directory.CreateDirectory(Path.Combine(
            store.RootDirectory,
            ExtractionRecoveryStore.OperationPrefix + new string('f', 64)));

        var inventory = store.Inspect();

        Assert.AreEqual(1, inventory.PotentialRecoveryCount);
        Assert.AreEqual(1, inventory.SuspiciousOperationCount);
        Assert.AreEqual(validRecoveryId, inventory.RecoveryRecords.Single().RecoveryId);
    }

    private static ExtractionRecoveryStore CreateStore(string parent) =>
        new(Path.Combine(parent, "extraction-recovery"));

    private static async Task<ExtractionRecoveryStore.ExtractionRecoveryTransaction> BeginAsync(
        ExtractionRecoveryStore store,
        string destinationParent) =>
        await store.BeginAsync(
            OperationId.New(),
            Path.Combine(destinationParent, "output"),
            FileSystemIdentityService.CaptureExistingDirectoryIdentity(destinationParent),
            CancellationToken.None);

    private static async Task<FinalizedFixture> CreateFinalizedFixtureAsync(
        string root,
        bool commit,
        bool includeProvenance = false)
    {
        var store = CreateStore(root);
        var destination = Path.Combine(root, "output");
        var transaction = await store.BeginAsync(
            OperationId.New(),
            destination,
            FileSystemIdentityService.CaptureExistingDirectoryIdentity(root),
            CancellationToken.None);
        var stagingPath = Path.Combine(root, transaction.StagingDirectoryName);
        using (var staging = HandleRelativeExtractionRoot.Create(
                   root,
                   transaction.StagingDirectoryName))
        {
            await transaction.RecordWritingAsync(
                staging.StagingIdentity,
                CancellationToken.None);
            staging.EnsureDirectory("folder");
            await using (var file = staging.CreateFile(@"folder\rain.txt"))
            {
                await file.Stream.WriteAsync("rain archives"u8.ToArray());
                await file.CloseDataAsync();
                if (includeProvenance)
                {
                    Assert.IsTrue(await file.TryApplyProvenanceAsync(
                        new DownloadProvenance(3),
                        CancellationToken.None));
                }
            }

            _ = staging.ValidateKnownTree();
            var evidence = staging.CaptureExtractionRecoveryEvidence(CancellationToken.None);
            await transaction.RecordFinalizedAsync(evidence, CancellationToken.None);
            await transaction.RecordCommitIntentAsync(CancellationToken.None);
            if (commit)
            {
                staging.CommitAs(Path.GetFileName(destination));
                await transaction.RecordCommittedAsync(CancellationToken.None);
            }
        }

        await transaction.DisposeAsync();
        var record = store.Inspect().RecoveryRecords.Single();
        return new FinalizedFixture(
            store,
            record.RecoveryId,
            destination,
            stagingPath);
    }

    private static byte[] SnapshotTree(string root)
    {
        var evidence = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .SelectMany(path =>
            {
                var relative = Path.GetRelativePath(root, path);
                if (Directory.Exists(path))
                {
                    return System.Text.Encoding.UTF8.GetBytes("D:" + relative + "\n");
                }

                return System.Text.Encoding.UTF8.GetBytes(
                    "F:" + relative + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) + "\n");
            })
            .ToArray();
        return SHA256.HashData(evidence);
    }

    private sealed record FinalizedFixture(
        ExtractionRecoveryStore Store,
        string RecoveryId,
        string DestinationPath,
        string StagingPath);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ExtractionRecoveryStoreTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 50 && Directory.Exists(Path); attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 49)
                {
                    Thread.Sleep(10);
                }
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLink(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);
    }
}
