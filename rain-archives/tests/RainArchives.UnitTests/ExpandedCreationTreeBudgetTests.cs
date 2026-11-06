using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ExpandedCreationTreeBudgetTests
{
    [TestMethod]
    public void ExpandedBudgetIsAdditiveAndLeavesEveryLegacyBoundIntact()
    {
        CollectionAssert.AreEqual(
            ProtocolFeatures.SupportedForMinorVersion(23)
                .Append(ProtocolFeatures.ExpandedCreationTreeBudget).ToArray(),
            ProtocolFeatures.SupportedForMinorVersion(24).ToArray());
        CollectionAssert.AreEqual(new[] { 4096, 4096, 4096, 4096, 4096, 4096, 4096, 4096, 1, 1024 * 1024 },
            new[] { ProtocolConstants.MaximumZipCreationFiles, ProtocolConstants.MaximumZipCreationEntries,
                ProtocolConstants.MaximumTarCreationFiles, ProtocolConstants.MaximumTarCreationEntries,
                ProtocolConstants.MaximumRarCreationFiles, ProtocolConstants.MaximumRarCreationEntries,
                ProtocolConstants.MaximumZipRebuildDirectives, ProtocolConstants.MaximumPageSize,
                ProtocolConstants.MaximumRawCreationEntries, ProtocolConstants.MaximumControlFrameBytes });
    }

    [TestMethod]
    public void PlannerCapacityAccepts65536thEntryAndRefusesTheNext()
    {
        var maximum = ProtocolConstants.MaximumExpandedCreationTreeEntries;
        Assert.AreEqual(65_536, maximum);
        // The same pre-add seam is used for both files and directories during planning/revalidation.
        ArchiveCreationPolicy.EnsureEntryCapacity(maximum - 1, maximum);
        var error = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            ArchiveCreationPolicy.EnsureEntryCapacity(maximum, maximum));
        Assert.AreEqual("creation.entry_count_limit", error.Code);
        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, error.Category);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ArchiveCreationPolicy(maximumTreeEntries: maximum + 1));
    }

    [TestMethod]
    public void IncrementalMetadataAccountingKeepsExact64MiBBoundWithoutOverflow()
    {
        long bytes = 0;
        ArchiveCreationPolicy.AddMetadataBytes(ref bytes, 64L * 1024 * 1024);
        Assert.AreEqual(64L * 1024 * 1024, bytes);
        var error = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            ArchiveCreationPolicy.AddMetadataBytes(ref bytes, 1));
        Assert.AreEqual("creation.metadata_budget", error.Code);
        Assert.ThrowsExactly<ArchiveProviderException>(() =>
            ArchiveCreationPolicy.AddMetadataBytes(ref bytes, long.MaxValue));
    }

    [TestMethod]
    [DataRow(ArchiveCreationOutputFormat.GZip)]
    [DataRow(ArchiveCreationOutputFormat.BZip2)]
    public async Task ExpandedPolicyStillRejectsRawTrees(ArchiveCreationOutputFormat format)
    {
        var policy = new ArchiveCreationPolicy(maximumTreeEntries: 65_536);
        var error = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.PlanTreeAsync([], [new ArchiveCreationDirectory(@"Z:\absent", "root")],
                @"Z:\output", format, CancellationToken.None));
        Assert.AreEqual("creation.raw_tree_unsupported", error.Code);
    }

    [TestMethod]
    public async Task ExpandedPolicyStillRejectsOversizedExplicitMappingsBeforeFilesystemAccess()
    {
        var policy = new ArchiveCreationPolicy(maximumTreeEntries: 65_536);
        var files = Enumerable.Repeat(new ArchiveCreationFile(@"Z:\absent", "file"), 4096).ToArray();
        foreach (var format in new[] { ArchiveCreationOutputFormat.Zip, ArchiveCreationOutputFormat.Tar,
                     ArchiveCreationOutputFormat.TarGZip, ArchiveCreationOutputFormat.TarBZip2,
                     ArchiveCreationOutputFormat.Rar })
        {
            var error = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
                await policy.PlanTreeAsync(files, [new ArchiveCreationDirectory(@"Z:\absent", "root")],
                    @"Z:\output", format, CancellationToken.None));
            Assert.AreEqual("creation.entry_count_limit", error.Code);
        }
    }
}
