using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class FilesystemObjectBudgetTests
{
    [TestMethod]
    public async Task FilesystemObjectBudgetRejectsManyEmptyFilesBeforeStagingCreation()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "output");
        const int fileCount = 2_048;
        var policy = NoReservePolicy();
        var available = checked(
            policy.DirectoryObjectChargeBytes
            + (fileCount * policy.FileObjectChargeBytes)
            - 1);
        var diskSpace = new FixedAvailableDiskSpace(available);
        await using var archive = new FakeArchiveHandle(
            Enumerable.Range(0, fileCount)
                .Select(index => ($"empty-{index:D5}.bin", Array.Empty<byte>()))
                .ToArray());
        var index = ArchiveIndex.Create(archive.Entries);
        var writer = new StagedExtractionWriter(
            resourceGuard: new ExtractionResourceGuard(policy, diskSpace));

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(() =>
            writer.ExtractAsync(
                archive,
                index.Entries,
                destination,
                _ => ValueTask.CompletedTask,
                CancellationToken.None));

        Assert.AreEqual(ArchiveErrorCategory.InsufficientSpace, exception.Category);
        Assert.AreEqual("resource.insufficient_space", exception.Code);
        Assert.AreEqual(0, archive.CopyCount);
        Assert.AreEqual(1, diskSpace.CallCount);
        Assert.IsFalse(Directory.Exists(destination));
        Assert.AreEqual(0, Directory.GetDirectories(root.Path, "*.staging", SearchOption.TopDirectoryOnly).Length);
    }

    [TestMethod]
    public void FilesystemObjectBudgetRejectsDirectoryOnlyPlan()
    {
        using var root = new TemporaryDirectory();
        const int directoryCount = 128;
        var policy = NoReservePolicy();
        var available = checked(
            ((long)directoryCount + 1) * policy.DirectoryObjectChargeBytes
            - 1);
        var diskSpace = new FixedAvailableDiskSpace(available);
        var index = ArchiveIndex.Create(
            Enumerable.Range(0, directoryCount)
                .Select(index => new ProviderArchiveEntry(
                    index,
                    $"folder-{index:D4}",
                    ArchiveEntryKind.Directory,
                    0,
                    0,
                    null,
                    null,
                    null,
                    null,
                    false,
                    false,
                    null))
                .ToArray());
        var plan = new ExtractionPathPolicy(policy).Plan(index.Entries);

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(
            () => new ExtractionResourceGuard(policy, diskSpace).Validate(plan, root.Path));

        Assert.AreEqual(ArchiveErrorCategory.InsufficientSpace, exception.Category);
        Assert.AreEqual("resource.insufficient_space", exception.Code);
        Assert.AreEqual(1, diskSpace.CallCount);
    }

    [TestMethod]
    public void FilesystemObjectBudgetReportsCheckedOverflow()
    {
        using var root = new TemporaryDirectory();
        var policy = NoReservePolicy() with
        {
            FileObjectChargeBytes = long.MaxValue,
        };
        var diskSpace = new FixedAvailableDiskSpace(long.MaxValue);
        var index = ArchiveIndex.Create(
        [
            new ProviderArchiveEntry(
                0,
                "empty.bin",
                ArchiveEntryKind.File,
                0,
                0,
                null,
                null,
                null,
                null,
                false,
                false,
                null),
        ]);
        var plan = new ExtractionPathPolicy(policy).Plan(index.Entries);

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(
            () => new ExtractionResourceGuard(policy, diskSpace).Validate(plan, root.Path));

        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, exception.Category);
        Assert.AreEqual("resource.metadata_overflow", exception.Code);
        Assert.AreEqual(0, diskSpace.CallCount);
    }

    [TestMethod]
    public void FilesystemObjectBudgetIncludesProvenanceStreamCharge()
    {
        using var root = new TemporaryDirectory();
        var policy = NoReservePolicy();
        var available = checked(policy.DirectoryObjectChargeBytes + policy.FileObjectChargeBytes);
        var diskSpace = new FixedAvailableDiskSpace(available);
        var index = ArchiveIndex.Create(
        [
            new ProviderArchiveEntry(
                0,
                "empty.bin",
                ArchiveEntryKind.File,
                0,
                0,
                null,
                null,
                null,
                null,
                false,
                false,
                null),
        ]);
        var plan = new ExtractionPathPolicy(policy).Plan(index.Entries);

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(
            () => new ExtractionResourceGuard(policy, diskSpace).Validate(
                plan,
                root.Path,
                includeProvenance: true));

        Assert.AreEqual(ArchiveErrorCategory.InsufficientSpace, exception.Category);
        Assert.AreEqual("resource.insufficient_space", exception.Code);
        Assert.AreEqual(1, diskSpace.CallCount);
    }

    [TestMethod]
    public void FilesystemObjectBudgetAllowsOrdinarySmallExtractionBudget()
    {
        using var root = new TemporaryDirectory();
        const long payloadBytes = 32;
        var policy = NoReservePolicy();
        var available = checked(
            payloadBytes
            + policy.DirectoryObjectChargeBytes
            + policy.FileObjectChargeBytes);
        var diskSpace = new FixedAvailableDiskSpace(available);
        var index = ArchiveIndex.Create(
        [
            new ProviderArchiveEntry(
                0,
                "small.bin",
                ArchiveEntryKind.File,
                payloadBytes,
                payloadBytes,
                null,
                null,
                "Store",
                null,
                false,
                false,
                null),
        ]);
        var plan = new ExtractionPathPolicy(policy).Plan(index.Entries);

        new ExtractionResourceGuard(policy, diskSpace).Validate(plan, root.Path);

        Assert.AreEqual(1, diskSpace.CallCount);
    }

    [TestMethod]
    public void FilesystemObjectBudgetRuntimeChecksZeroByteObjectsInBatches()
    {
        using var root = new TemporaryDirectory();
        var policy = NoReservePolicy() with
        {
            DiskSpaceCheckIntervalBytes = 16,
            FileObjectChargeBytes = 4,
            DirectoryObjectChargeBytes = 4,
            ProvenanceStreamChargeBytes = 4,
        };
        var diskSpace = new SequenceAvailableDiskSpace(long.MaxValue, 7);
        var runtime = new ExtractionResourceGuard(policy, diskSpace)
            .CreateRuntimeGuard(root.Path);

        runtime.ValidateObjectCreation(0, ExtractionObjectKind.Directory);
        runtime.ValidateObjectCreation(0, ExtractionObjectKind.File, "one.bin");
        runtime.ValidateObjectCreation(0, ExtractionObjectKind.File, "two.bin");
        runtime.ValidateObjectCreation(0, ExtractionObjectKind.File, "three.bin");

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            runtime.ValidateObjectCreation(0, ExtractionObjectKind.File, "four.bin"));

        Assert.AreEqual(ArchiveErrorCategory.InsufficientSpace, exception.Category);
        Assert.AreEqual("resource.insufficient_space_during_write", exception.Code);
        Assert.AreEqual(2, diskSpace.CallCount);
    }

    [TestMethod]
    public void FilesystemObjectBudgetRechecksAfterBoundedObjectCount()
    {
        using var root = new TemporaryDirectory();
        var policy = NoReservePolicy() with
        {
            DiskSpaceCheckIntervalBytes = long.MaxValue,
            DiskSpaceCheckIntervalObjects = 2,
            FileObjectChargeBytes = 4,
            DirectoryObjectChargeBytes = 4,
        };
        var diskSpace = new SequenceAvailableDiskSpace(long.MaxValue, 7);
        var runtime = new ExtractionResourceGuard(policy, diskSpace)
            .CreateRuntimeGuard(root.Path);

        runtime.ValidateObjectCreation(0, ExtractionObjectKind.Directory);
        runtime.ValidateObjectCreation(0, ExtractionObjectKind.File, "one.bin");

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            runtime.ValidateObjectCreation(0, ExtractionObjectKind.File, "two.bin"));

        Assert.AreEqual(ArchiveErrorCategory.InsufficientSpace, exception.Category);
        Assert.AreEqual("resource.insufficient_space_during_write", exception.Code);
        Assert.AreEqual(2, diskSpace.CallCount);
    }

    private static ExtractionSecurityPolicy NoReservePolicy() => new(
        MinimumDiskReserveBytes: 0,
        MinimumDiskReserveFraction: 0);

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

        public int CopyCount { get; private set; }
        public string Format => "fake";
        public string ProviderId => "fake";
        public ArchiveCapability Capabilities => ArchiveCapability.List | ArchiveCapability.Extract;
        public bool IsSolid => false;
        public ArchiveVolumeSetKind VolumeSetKind => ArchiveVolumeSetKind.Single;
        public IReadOnlyList<ProviderArchiveEntry> Entries { get; }

        public ValueTask<Stream> OpenEntryStreamAsync(
            int providerOrdinal,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream(_payloads[providerOrdinal], writable: false));

        public async ValueTask CopyEntryToAsync(
            int providerOrdinal,
            Stream destination,
            CancellationToken cancellationToken)
        {
            CopyCount++;
            await using var source = await OpenEntryStreamAsync(providerOrdinal, cancellationToken);
            await source.CopyToAsync(destination, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedAvailableDiskSpace(long availableBytes) : IAvailableDiskSpace
    {
        public int CallCount { get; private set; }

        public long GetAvailableFreeSpace(string driveRoot)
        {
            CallCount++;
            return availableBytes;
        }
    }

    private sealed class SequenceAvailableDiskSpace(params long[] values) : IAvailableDiskSpace
    {
        private int _index;

        public int CallCount { get; private set; }

        public long GetAvailableFreeSpace(string driveRoot)
        {
            CallCount++;
            return values[Math.Min(_index++, values.Length - 1)];
        }
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
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
