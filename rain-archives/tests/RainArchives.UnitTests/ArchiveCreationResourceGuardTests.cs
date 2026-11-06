using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ArchiveCreationResourceGuardTests
{
    [TestMethod]
    public void PreflightPreservesMinimumDiskReserve()
    {
        var guard = new ArchiveCreationResourceGuard(
            new FixedAvailableDiskSpace(600L * 1024 * 1024));

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            guard.CreateOutputGuard(Path.GetTempPath(), 100L * 1024 * 1024));

        Assert.AreEqual(ArchiveErrorCategory.InsufficientSpace, exception.Category);
        Assert.AreEqual("creation.insufficient_space", exception.Code);
    }

    [TestMethod]
    public void RuntimeRejectsOutputPastTheBoundedEstimate()
    {
        var guard = new ArchiveCreationResourceGuard(
                new FixedAvailableDiskSpace(10L * 1024 * 1024 * 1024))
            .CreateOutputGuard(Path.GetTempPath(), maximumExpectedOutputBytes: 10);

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            guard.ValidateNextChunk(bytesWritten: 0, nextChunkBytes: 11));

        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, exception.Category);
        Assert.AreEqual("creation.output_limit", exception.Code);
    }

    private sealed class FixedAvailableDiskSpace(long availableBytes) : IAvailableDiskSpace
    {
        public long GetAvailableFreeSpace(string driveRoot) => availableBytes;
    }
}
