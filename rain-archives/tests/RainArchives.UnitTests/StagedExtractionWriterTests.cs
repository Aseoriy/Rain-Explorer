using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class StagedExtractionWriterTests
{
    [TestMethod]
    public async Task TrackedCommitStaysActiveUntilTerminalCleanupThenConverges()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "tracked-output");
        var recoveryStore = new ExtractionRecoveryStore(
            Path.Combine(root.Path, "extraction-recovery"));
        await using var archive = new FakeArchiveHandle(
        [
            ("safe.txt", "safe"u8.ToArray()),
        ]);
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter(
            resourceGuard: new ExtractionResourceGuard(new ExtractionSecurityPolicy(
                MinimumDiskReserveBytes: 0,
                MinimumDiskReserveFraction: 0)),
            recoveryStore: recoveryStore);

        await using var completion = await writer.ExtractTrackedAsync(
            OperationId.New(),
            archive,
            index.Entries.Where(entry => !entry.IsSynthetic).ToArray(),
            destination,
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.AreEqual(1, recoveryStore.Inspect().ActiveOperationCount);
        Assert.AreEqual("safe", await File.ReadAllTextAsync(Path.Combine(destination, "safe.txt")));
        await completion.DeleteAsync();
        Assert.AreEqual(0, recoveryStore.Inspect().PotentialRecoveryCount);
        Assert.AreEqual(0, recoveryStore.Inspect().ActiveOperationCount);
    }

    [TestMethod]
    public async Task TransportLossAfterCommitPreservesCommittedReadOnlyEvidence()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "tracked-output");
        var recoveryStore = new ExtractionRecoveryStore(
            Path.Combine(root.Path, "extraction-recovery"));
        await using var archive = new FakeArchiveHandle(
        [
            ("safe.txt", "safe"u8.ToArray()),
        ]);
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter(
            resourceGuard: new ExtractionResourceGuard(new ExtractionSecurityPolicy(
                MinimumDiskReserveBytes: 0,
                MinimumDiskReserveFraction: 0)),
            recoveryStore: recoveryStore);

        var completion = await writer.ExtractTrackedAsync(
            OperationId.New(),
            archive,
            index.Entries.Where(entry => !entry.IsSynthetic).ToArray(),
            destination,
            _ => ValueTask.CompletedTask,
            CancellationToken.None);
        await completion.DisposeAsync();

        var record = recoveryStore.Inspect().RecoveryRecords.Single();
        Assert.AreEqual(ExtractionRecoveryPhase.Committed, record.Phase);
        var inspection = await recoveryStore.InspectArtifactsAsync(record.RecoveryId);
        Assert.AreEqual(ExtractionRecoveryArtifactState.ExactMatch, inspection.FinalArtifact.State);
        Assert.AreEqual(ExtractionRecoveryAssessment.OwnedFinalObserved, inspection.Assessment);
        Assert.AreEqual("safe", await File.ReadAllTextAsync(Path.Combine(destination, "safe.txt")));
    }

    [TestMethod]
    public async Task SafeTrackedFailureRemovesOwnedStagingAndJournal()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "tracked-output");
        var recoveryStore = new ExtractionRecoveryStore(
            Path.Combine(root.Path, "extraction-recovery"));
        await using var archive = new FakeArchiveHandle(
        [
            ("safe.txt", "safe"u8.ToArray()),
        ]);
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter(
            resourceGuard: new ExtractionResourceGuard(new ExtractionSecurityPolicy(
                MinimumDiskReserveBytes: 0,
                MinimumDiskReserveFraction: 0)),
            recoveryStore: recoveryStore);

        await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await writer.ExtractTrackedAsync(
                OperationId.New(),
                archive,
                index.Entries.Where(entry => !entry.IsSynthetic).ToArray(),
                destination,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                _ => ValueTask.FromException(new ArchiveProviderException(
                    ArchiveErrorCategory.SourceChanged,
                    "archive.source_changed",
                    "The source changed."))));

        Assert.IsFalse(Directory.Exists(destination));
        Assert.AreEqual(0, recoveryStore.Inspect().PotentialRecoveryCount);
        Assert.AreEqual(0, recoveryStore.Inspect().SuspiciousOperationCount);
        Assert.AreEqual(0, Directory.GetDirectories(root.Path, "*.staging").Length);
    }

    [TestMethod]
    public async Task SuspiciousTrackedFailurePreservesUnexpectedContentAndJournal()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "tracked-output");
        var recoveryStore = new ExtractionRecoveryStore(
            Path.Combine(root.Path, "extraction-recovery"));
        await using var archive = new FakeArchiveHandle(
        [
            ("safe.txt", "safe"u8.ToArray()),
        ]);
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter(
            resourceGuard: new ExtractionResourceGuard(new ExtractionSecurityPolicy(
                MinimumDiskReserveBytes: 0,
                MinimumDiskReserveFraction: 0)),
            recoveryStore: recoveryStore);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await writer.ExtractTrackedAsync(
                OperationId.New(),
                archive,
                index.Entries.Where(entry => !entry.IsSynthetic).ToArray(),
                destination,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                _ =>
                {
                    var staging = Directory.GetDirectories(
                        root.Path,
                        "*.staging",
                        SearchOption.TopDirectoryOnly).Single();
                    File.WriteAllText(
                        Path.Combine(staging, "unexpected.txt"),
                        "preserve unexpected content");
                    return ValueTask.CompletedTask;
                }));

        Assert.AreEqual("destination.unexpected_path_appeared", exception.Code);
        Assert.IsFalse(Directory.Exists(destination));
        var preservedStaging = Directory.GetDirectories(
            root.Path,
            "*.staging",
            SearchOption.TopDirectoryOnly).Single();
        Assert.AreEqual(
            "preserve unexpected content",
            await File.ReadAllTextAsync(Path.Combine(preservedStaging, "unexpected.txt")));
        var recovery = recoveryStore.Inspect().RecoveryRecords.Single();
        Assert.AreEqual(ExtractionRecoveryPhase.Writing, recovery.Phase);
    }

    [TestMethod]
    public async Task SuspiciousTrackedFailurePreservesUnexpectedNamedStreamAndJournal()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "tracked-output");
        var recoveryStore = new ExtractionRecoveryStore(
            Path.Combine(root.Path, "extraction-recovery"));
        await using var archive = new FakeArchiveHandle(
        [
            ("safe.txt", "safe"u8.ToArray()),
        ]);
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter(
            resourceGuard: new ExtractionResourceGuard(new ExtractionSecurityPolicy(
                MinimumDiskReserveBytes: 0,
                MinimumDiskReserveFraction: 0)),
            recoveryStore: recoveryStore);

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await writer.ExtractTrackedAsync(
                OperationId.New(),
                archive,
                index.Entries.Where(entry => !entry.IsSynthetic).ToArray(),
                destination,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                _ =>
                {
                    var staging = Directory.GetDirectories(
                        root.Path,
                        "*.staging",
                        SearchOption.TopDirectoryOnly).Single();
                    File.WriteAllText(
                        Path.Combine(staging, "safe.txt") + ":unexpected",
                        "preserve unexpected stream");
                    return ValueTask.CompletedTask;
                }));

        Assert.AreEqual("destination.staging_shape_changed", exception.Code);
        Assert.IsFalse(Directory.Exists(destination));
        var preservedStaging = Directory.GetDirectories(
            root.Path,
            "*.staging",
            SearchOption.TopDirectoryOnly).Single();
        Assert.AreEqual(
            "preserve unexpected stream",
            await File.ReadAllTextAsync(
                Path.Combine(preservedStaging, "safe.txt") + ":unexpected"));
        Assert.AreEqual(
            ExtractionRecoveryPhase.Writing,
            recoveryStore.Inspect().RecoveryRecords.Single().Phase);
    }

    [TestMethod]
    public async Task ExtractsThroughStagingAndCommitsCompleteTree()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "output");
        await using var archive = new FakeArchiveHandle(
        [
            ("folder/a.txt", "alpha"u8.ToArray()),
            ("folder/b.txt", "beta"u8.ToArray()),
        ]);
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter(
            resourceGuard: new ExtractionResourceGuard(new ExtractionSecurityPolicy(
                MinimumDiskReserveBytes: 0,
                MinimumDiskReserveFraction: 0)));

        await writer.ExtractAsync(
            archive,
            index.Entries.Where(entry => !entry.IsSynthetic).ToArray(),
            destination,
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.AreEqual("alpha", await File.ReadAllTextAsync(Path.Combine(destination, "folder", "a.txt")));
        Assert.AreEqual("beta", await File.ReadAllTextAsync(Path.Combine(destination, "folder", "b.txt")));
        Assert.AreEqual(0, Directory.GetDirectories(root.Path, "*.staging", SearchOption.TopDirectoryOnly).Length);
    }

    [TestMethod]
    public async Task FailureBeforeCommitLeavesNoDestination()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "output");
        await using var archive = new FakeArchiveHandle(
        [
            ("safe.txt", "safe"u8.ToArray()),
            ("../escape.txt", "bad"u8.ToArray()),
        ]);
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter();

        await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
        {
            await writer.ExtractAsync(
                archive,
                index.Entries.Where(entry => !entry.IsSynthetic).ToArray(),
                destination,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
        });

        Assert.IsFalse(Directory.Exists(destination));
        Assert.IsFalse(File.Exists(Path.Combine(root.Path, "escape.txt")));
    }

    [TestMethod]
    public async Task CommitValidationFailureRemovesOnlyOwnedStaging()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "output");
        await using var archive = new FakeArchiveHandle(
        [
            ("safe.txt", "safe"u8.ToArray()),
        ]);
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter(
            resourceGuard: new ExtractionResourceGuard(new ExtractionSecurityPolicy(
                MinimumDiskReserveBytes: 0,
                MinimumDiskReserveFraction: 0)));

        await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
        {
            await writer.ExtractAsync(
                archive,
                index.Entries.Where(entry => !entry.IsSynthetic).ToArray(),
                destination,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                _ => ValueTask.FromException(new ArchiveProviderException(
                    ArchiveErrorCategory.SourceChanged,
                    "archive.source_changed",
                    "The source changed.")));
        });

        Assert.IsFalse(Directory.Exists(destination));
        Assert.AreEqual(0, Directory.GetDirectories(root.Path, "*.staging", SearchOption.TopDirectoryOnly).Length);
    }

    [TestMethod]
    public async Task DestinationAppearingBeforeCommitIsNotOverwritten()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "output");
        await using var archive = new FakeArchiveHandle(
        [
            ("safe.txt", "safe"u8.ToArray()),
        ]);
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter(
            resourceGuard: new ExtractionResourceGuard(new ExtractionSecurityPolicy(
                MinimumDiskReserveBytes: 0,
                MinimumDiskReserveFraction: 0)));

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
        {
            await writer.ExtractAsync(
                archive,
                index.Entries.Where(entry => !entry.IsSynthetic).ToArray(),
                destination,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                _ =>
                {
                    Directory.CreateDirectory(destination);
                    return ValueTask.CompletedTask;
                });
        });

        Assert.AreEqual(ArchiveErrorCategory.DestinationConflict, exception.Category);
        Assert.IsTrue(Directory.Exists(destination));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "safe.txt")));
        Assert.AreEqual(0, Directory.GetDirectories(root.Path, "*.staging", SearchOption.TopDirectoryOnly).Length);
    }

    [TestMethod]
    public async Task HandleBoundCommitRefusesAnExistingRelativeDestination()
    {
        using var root = new TemporaryDirectory();
        var stagingName = $".output.rainarchives-{Guid.NewGuid():N}.staging";
        var staging = Path.Combine(root.Path, stagingName);
        var destination = Path.Combine(root.Path, "output");
        using var extractionRoot = HandleRelativeExtractionRoot.Create(root.Path, stagingName);
        await using (var output = extractionRoot.CreateFile("safe.txt"))
        {
            await output.Stream.WriteAsync("safe"u8.ToArray());
            await output.CloseDataAsync();
        }

        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "existing.txt"), "existing");

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(
            () => extractionRoot.CommitAs("output"));

        Assert.AreEqual(ArchiveErrorCategory.DestinationConflict, exception.Category);
        Assert.AreEqual("destination.appeared_before_commit", exception.Code);
        Assert.AreEqual("existing", await File.ReadAllTextAsync(Path.Combine(destination, "existing.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(destination, "safe.txt")));
        Assert.AreEqual("safe", await File.ReadAllTextAsync(Path.Combine(staging, "safe.txt")));
    }

    [TestMethod]
    public async Task ReplacingOwnedDirectoryWithSymlinkCannotRedirectNextFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows symbolic-link behavior is required for this test.");
        }

        using var root = new TemporaryDirectory();
        var outside = Path.Combine(root.Path, "outside");
        var destination = Path.Combine(root.Path, "output");
        Directory.CreateDirectory(outside);
        await using var archive = new FakeArchiveHandle(
        [
            ("folder/first.txt", "first"u8.ToArray()),
            ("folder/second.txt", "second"u8.ToArray()),
        ]);
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter(
            resourceGuard: new ExtractionResourceGuard(new ExtractionSecurityPolicy(
                MinimumDiskReserveBytes: 0,
                MinimumDiskReserveFraction: 0)));
        var replacementAttempted = false;
        string? plantedStaging = null;
        string? plantedLink = null;

        try
        {
            var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            {
                await writer.ExtractAsync(
                    archive,
                    index.Entries.Where(entry => !entry.IsSynthetic).ToArray(),
                    destination,
                    progress =>
                    {
                        if (replacementAttempted || progress.EntriesProcessed != 1)
                        {
                            return ValueTask.CompletedTask;
                        }

                        var staging = Directory.GetDirectories(
                            root.Path,
                            "*.staging",
                            SearchOption.TopDirectoryOnly).Single();
                        var folder = Path.Combine(staging, "folder");
                        Directory.Delete(folder, recursive: true);
                        try
                        {
                            Directory.CreateSymbolicLink(folder, outside);
                        }
                        catch (Exception linkException) when (
                            linkException is UnauthorizedAccessException or IOException)
                        {
                            Assert.Inconclusive(
                                $"This Windows test context cannot create a directory symbolic link: {linkException.Message}");
                        }

                        replacementAttempted = true;
                        plantedStaging = staging;
                        plantedLink = folder;
                        return ValueTask.CompletedTask;
                    },
                    CancellationToken.None);
            });

            Assert.IsTrue(replacementAttempted);
            Assert.AreEqual(ArchiveErrorCategory.DestinationConflict, exception.Category);
            Assert.AreEqual("destination.reparse_point_encountered", exception.Code);
            Assert.IsTrue(Directory.Exists(outside));
            Assert.IsFalse(File.Exists(Path.Combine(outside, "second.txt")));
            Assert.IsFalse(Directory.Exists(destination));
        }
        finally
        {
            if (plantedStaging is not null && plantedLink is not null)
            {
                try
                {
                    if ((File.GetAttributes(plantedLink) & FileAttributes.ReparsePoint) != 0)
                    {
                        DeleteDirectoryEntryWithRetry(plantedLink);
                        await WaitForPathRemovalAsync(plantedLink);
                    }
                }
                catch (Exception cleanupException) when (
                    cleanupException is FileNotFoundException or DirectoryNotFoundException)
                {
                    // The writer may already have removed a clean staging tree.
                }

                if (Directory.Exists(plantedStaging))
                {
                    Directory.Delete(plantedStaging, recursive: false);
                    await WaitForPathRemovalAsync(plantedStaging);
                }
            }
        }
    }

    [TestMethod]
    public async Task UnexpectedFileAddedBeforeCommitFailsClosed()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "output");
        await using var archive = new FakeArchiveHandle(
        [
            ("safe.txt", "safe"u8.ToArray()),
        ]);
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter(
            resourceGuard: new ExtractionResourceGuard(new ExtractionSecurityPolicy(
                MinimumDiskReserveBytes: 0,
                MinimumDiskReserveFraction: 0)));

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await writer.ExtractAsync(
                archive,
                index.Entries.Where(entry => !entry.IsSynthetic).ToArray(),
                destination,
                _ => ValueTask.CompletedTask,
                CancellationToken.None,
                _ =>
                {
                    var staging = Directory.GetDirectories(
                        root.Path,
                        "*.staging",
                        SearchOption.TopDirectoryOnly).Single();
                    File.WriteAllText(Path.Combine(staging, "unexpected.txt"), "not owned by the operation");
                    return ValueTask.CompletedTask;
                }));

        Assert.AreEqual(ArchiveErrorCategory.DestinationConflict, exception.Category);
        Assert.AreEqual("destination.unexpected_path_appeared", exception.Code);
        Assert.IsFalse(Directory.Exists(destination));
        var preservedStaging = Directory.GetDirectories(
            root.Path,
            "*.staging",
            SearchOption.TopDirectoryOnly).Single();
        Assert.AreEqual(
            "not owned by the operation",
            await File.ReadAllTextAsync(Path.Combine(preservedStaging, "unexpected.txt")));
    }

    [TestMethod]
    public async Task HardLinkAddedToClosedStagedFileByAnotherProcessIsTolerated()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows hard-link behavior is required for this test.");
        }

        using var root = new TemporaryDirectory();
        var probeSource = Path.Combine(root.Path, "hard-link-probe-source");
        var probeLink = Path.Combine(root.Path, "hard-link-probe-link");
        await File.WriteAllTextAsync(probeSource, "probe");
        if (!NativeMethods.CreateHardLink(probeLink, probeSource, IntPtr.Zero))
        {
            Assert.Inconclusive(
                $"This test filesystem cannot create hard links (Win32 {Marshal.GetLastPInvokeError()}).");
        }

        File.Delete(probeLink);
        File.Delete(probeSource);

        var destination = Path.Combine(root.Path, "output");
        var syncClientLink = Path.Combine(root.Path, "sync-client-link.txt");
        await using var archive = new FakeArchiveHandle(
        [
            ("safe.txt", "safe"u8.ToArray()),
        ]);
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter(
            resourceGuard: new ExtractionResourceGuard(new ExtractionSecurityPolicy(
                MinimumDiskReserveBytes: 0,
                MinimumDiskReserveFraction: 0)));

        await writer.ExtractAsync(
            archive,
            index.Entries.Where(entry => !entry.IsSynthetic).ToArray(),
            destination,
            _ => ValueTask.CompletedTask,
            CancellationToken.None,
            _ =>
            {
                var staging = Directory.GetDirectories(
                    root.Path,
                    "*.staging",
                    SearchOption.TopDirectoryOnly).Single();
                if (!NativeMethods.CreateHardLink(
                        syncClientLink,
                        Path.Combine(staging, "safe.txt"),
                        IntPtr.Zero))
                {
                    throw new IOException(
                        $"Could not create the hard-link race fixture (Win32 {Marshal.GetLastPInvokeError()}).");
                }

                return ValueTask.CompletedTask;
            });

        Assert.AreEqual("safe", await File.ReadAllTextAsync(Path.Combine(destination, "safe.txt")));
        Assert.AreEqual("safe", await File.ReadAllTextAsync(syncClientLink));
        Assert.AreEqual(
            0,
            Directory.GetDirectories(root.Path, "*.staging", SearchOption.TopDirectoryOnly).Length);
    }

    [TestMethod]
    public async Task ExtractsPathLongerThanLegacyMaxPathThroughPinnedHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The production extraction writer is Windows-specific.");
        }

        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "output");
        var first = new string('a', 180);
        var second = new string('b', 100);
        var relativePath = $"{first}/{second}/payload.txt";
        await using var archive = new FakeArchiveHandle(
        [
            (relativePath, "long-path"u8.ToArray()),
        ]);
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter(
            resourceGuard: new ExtractionResourceGuard(new ExtractionSecurityPolicy(
                MinimumDiskReserveBytes: 0,
                MinimumDiskReserveFraction: 0)));

        await writer.ExtractAsync(
            archive,
            index.Entries.Where(entry => !entry.IsSynthetic).ToArray(),
            destination,
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        var extracted = Path.Combine(destination, first, second, "payload.txt");
        Assert.IsTrue(extracted.Length > 260);
        Assert.AreEqual("long-path", await File.ReadAllTextAsync(extracted));
    }

    [TestMethod]
    public async Task ExtractsWhenTheStagingRootExceedsLegacyMaxPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The production extraction writer is Windows-specific.");
        }

        using var root = new TemporaryDirectory();
        var longParent = Path.Combine(root.Path, "long-parent");
        Directory.CreateDirectory(longParent);
        var segment = 0;
        while (longParent.Length < 220)
        {
            longParent = Path.Combine(longParent, $"segment-{++segment:D2}-abcdefgh");
            Directory.CreateDirectory(longParent);
        }

        var destination = Path.Combine(longParent, "output");
        var representativeStaging = Path.Combine(
            longParent,
            $".output.rainarchives-{Guid.Empty:N}.staging");
        Assert.IsTrue(representativeStaging.Length > 260);
        await using var archive = new FakeArchiveHandle(
        [
            ("safe.txt", "long-staging-root"u8.ToArray()),
        ]);
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter(
            resourceGuard: new ExtractionResourceGuard(new ExtractionSecurityPolicy(
                MinimumDiskReserveBytes: 0,
                MinimumDiskReserveFraction: 0)));

        await writer.ExtractAsync(
            archive,
            index.Entries.Where(entry => !entry.IsSynthetic).ToArray(),
            destination,
            _ => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.AreEqual(
            "long-staging-root",
            await File.ReadAllTextAsync(Path.Combine(destination, "safe.txt")));
        Assert.HasCount(0, Directory.GetDirectories(longParent, "*.staging", SearchOption.TopDirectoryOnly));
    }

    private static async Task WaitForPathRemovalAsync(string path)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new IOException("A test-owned filesystem object must have a parent.");
        var name = Path.GetFileName(path);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var stillEnumerated = Directory
                    .EnumerateFileSystemEntries(parent)
                    .Any(entry => string.Equals(
                        Path.GetFileName(entry),
                        name,
                        StringComparison.OrdinalIgnoreCase));
                if (!stillEnumerated)
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new IOException("A test-owned filesystem object remained deletion-pending.");
    }

    private static void DeleteDirectoryEntryWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: false);
                return;
            }
            catch (IOException) when (attempt < 100)
            {
                Thread.Sleep(10);
            }
        }
    }

    private sealed class FakeArchiveHandle : IArchiveHandle
    {
        private readonly IReadOnlyList<byte[]> _payloads;

        public FakeArchiveHandle(IReadOnlyList<(string Path, byte[] Payload)> files)
        {
            _payloads = files.Select(file => file.Payload).ToArray();
            Entries = files.Select((file, ordinal) => new ProviderArchiveEntry(
                ordinal,
                file.Path,
                ArchiveEntryKind.File,
                file.Payload.Length,
                file.Payload.Length,
                null,
                null,
                "Store",
                null,
                false,
                false,
                null)).ToArray();
        }

        public string Format => "fake";
        public string ProviderId => "fake";
        public ArchiveCapability Capabilities => ArchiveCapability.List | ArchiveCapability.Extract;
        public bool IsSolid => false;
        public ArchiveVolumeSetKind VolumeSetKind => ArchiveVolumeSetKind.Single;
        public IReadOnlyList<ProviderArchiveEntry> Entries { get; }

        public ValueTask<Stream> OpenEntryStreamAsync(int providerOrdinal, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream(_payloads[providerOrdinal], writable: false));

        public async ValueTask CopyEntryToAsync(
            int providerOrdinal,
            Stream destination,
            CancellationToken cancellationToken)
        {
            await using var source = await OpenEntryStreamAsync(providerOrdinal, cancellationToken);
            await source.CopyToAsync(destination, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                DeleteTreeWithoutFollowingReparsePoints(Path);
            }
        }

        private static void DeleteTreeWithoutFollowingReparsePoints(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).ToArray())
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (
                    exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        DeleteDirectoryEntryWithRetry(entry);
                    }
                    else
                    {
                        File.Delete(entry);
                    }

                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    DeleteTreeWithoutFollowingReparsePoints(entry);
                }
                else
                {
                    File.Delete(entry);
                }
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: false);
                    return;
                }
                catch (IOException) when (attempt < 100)
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
