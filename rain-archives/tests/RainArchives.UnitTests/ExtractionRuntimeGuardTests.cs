using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ExtractionRuntimeGuardTests
{
    [TestMethod]
    public void RejectsActualBytesBeyondDeclaredEntrySize()
    {
        var guard = new ExtractionResourceGuard().CreateRuntimeGuard();

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(
            () => guard.ValidateNextChunk(8, 8, 4, 10));

        Assert.AreEqual(ArchiveErrorCategory.CorruptArchive, exception.Category);
        Assert.AreEqual("archive.entry_exceeded_declared_size", exception.Code);
    }

    [TestMethod]
    public void RejectsUnknownEntryBeyondRuntimeLimit()
    {
        var guard = new ExtractionResourceGuard(new ExtractionSecurityPolicy(
            DefaultMaximumOutputBytes: 100,
            MaximumUnknownEntryOutputBytes: 10,
            MinimumDiskReserveBytes: 0,
            MinimumDiskReserveFraction: 0)).CreateRuntimeGuard();

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(
            () => guard.ValidateNextChunk(8, 8, 4, null));

        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, exception.Category);
        Assert.AreEqual("resource.unknown_entry_limit", exception.Code);
    }

    [TestMethod]
    public void RejectsAggregateDecodedOutputBeyondRuntimeLimit()
    {
        var guard = new ExtractionResourceGuard(new ExtractionSecurityPolicy(
            DefaultMaximumOutputBytes: 10,
            MaximumUnknownEntryOutputBytes: 10,
            MinimumDiskReserveBytes: 0,
            MinimumDiskReserveFraction: 0)).CreateRuntimeGuard();

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(
            () => guard.ValidateNextChunk(8, 2, 4, 20));

        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, exception.Category);
        Assert.AreEqual("resource.output_limit", exception.Code);
    }

    [TestMethod]
    public void RejectsCompletedEntryShorterThanDeclaredSize()
    {
        var guard = new ExtractionResourceGuard().CreateRuntimeGuard();

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(
            () => guard.ValidateCompletedEntry(8, 10));

        Assert.AreEqual(ArchiveErrorCategory.CorruptArchive, exception.Category);
        Assert.AreEqual("archive.entry_size_mismatch", exception.Code);
    }

    [TestMethod]
    public void RejectsActualExpansionRatioWhenOutputMetadataWasUnknown()
    {
        var guard = new ExtractionResourceGuard(new ExtractionSecurityPolicy(
            DefaultMaximumOutputBytes: 1_000,
            ExpansionRatioWarning: 2,
            ExpansionRatioMinimumOutputBytes: 10,
            MinimumDiskReserveBytes: 0,
            MinimumDiskReserveFraction: 0)).CreateRuntimeGuard(
                declaredCompressedBytes: 4);

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(
            () => guard.ValidateNextChunk(8, 8, 4, null));

        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, exception.Category);
        Assert.AreEqual("resource.expansion_ratio", exception.Code);
    }

    [TestMethod]
    public void RejectsWriteWhenInjectedAvailableSpaceFallsBelowReserve()
    {
        var guard = new ExtractionResourceGuard(
            new ExtractionSecurityPolicy(
                DefaultMaximumOutputBytes: 1_000,
                MaximumUnknownEntryOutputBytes: 1_000,
                MinimumDiskReserveBytes: 4,
                MinimumDiskReserveFraction: 0),
            new FixedAvailableDiskSpace(9)).CreateRuntimeGuard(
                destinationParent: Path.GetTempPath());

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(
            () => guard.ValidateNextChunk(0, 0, 6, null));

        Assert.AreEqual(ArchiveErrorCategory.InsufficientSpace, exception.Category);
        Assert.AreEqual("resource.insufficient_space_during_write", exception.Code);
    }

    private sealed class FixedAvailableDiskSpace(long availableBytes) : IAvailableDiskSpace
    {
        public long GetAvailableFreeSpace(string driveRoot) => availableBytes;
    }
}
