using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ArchiveMetadataGuardTests
{
    [TestMethod]
    public void RejectsEntryCountBeforeAcceptingAnotherDescriptor()
    {
        var budget = new ArchiveMetadataBudget(new ArchiveMetadataLimits(MaximumEntries: 1));
        budget.Add(CreateEntry(0, "first.txt"));

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(
            () => budget.Add(CreateEntry(1, "second.txt")));

        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, exception.Category);
        Assert.AreEqual("resource.entry_count", exception.Code);
    }

    [TestMethod]
    public void RejectsDuplicateProviderOrdinal()
    {
        var budget = new ArchiveMetadataBudget();
        budget.Add(CreateEntry(4, "first.txt"));

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(
            () => budget.Add(CreateEntry(4, "second.txt")));

        Assert.AreEqual(ArchiveErrorCategory.InvalidArchive, exception.Category);
        Assert.AreEqual("archive.invalid_entry_ordinal", exception.Code);
    }

    [TestMethod]
    public void RejectsMetadataBudgetOverflow()
    {
        var budget = new ArchiveMetadataBudget(new ArchiveMetadataLimits(
            MaximumEntries: 10,
            MaximumRawPathLength: 100,
            MetadataBudgetBytes: 270));

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(
            () => budget.Add(CreateEntry(0, "long-name.txt")));

        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, exception.Category);
        Assert.AreEqual("resource.metadata_budget", exception.Code);
    }

    [TestMethod]
    public void RejectsEmptyEntryPathBeforeIndexing()
    {
        var budget = new ArchiveMetadataBudget();

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(
            () => budget.Add(CreateEntry(0, string.Empty)));

        Assert.AreEqual(ArchiveErrorCategory.InvalidArchive, exception.Category);
        Assert.AreEqual("archive.empty_entry_path", exception.Code);
    }

    private static ProviderArchiveEntry CreateEntry(int ordinal, string path) =>
        new(
            ordinal,
            path,
            ArchiveEntryKind.File,
            1,
            1,
            null,
            null,
            "Store",
            null,
            false,
            false,
            null);
}
