using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ArchiveIndexTests
{
    [TestMethod]
    public void ReconstructsMissingDirectoriesAndKeepsDuplicateFilesDistinct()
    {
        var index = ArchiveIndex.Create(
        [
            FileEntry(0, "folder/child.txt"),
            FileEntry(1, "folder/child.txt"),
        ]);

        var directory = index.Entries.Single(entry => entry.IsSynthetic);
        var files = index.Entries.Where(entry => !entry.IsSynthetic).ToArray();

        Assert.AreEqual("folder", directory.Descriptor.RawPath);
        Assert.AreEqual(2, files.Length);
        Assert.AreNotEqual(files[0].Descriptor.EntryId, files[1].Descriptor.EntryId);
        Assert.AreEqual(directory.Descriptor.EntryId, files[0].Descriptor.ParentEntryId);
        Assert.AreEqual(directory.Descriptor.EntryId, files[1].Descriptor.ParentEntryId);
        CollectionAssert.AreEqual(
            new[] { 0, 1 },
            files.Select(entry => entry.Descriptor.DuplicateOrdinal).ToArray());
        Assert.IsTrue(files.All(entry => entry.Descriptor.NormalizedPath == "folder/child.txt"));

        var reopened = ArchiveIndex.Create(
        [
            FileEntry(0, "folder/child.txt"),
            FileEntry(1, "folder/child.txt"),
        ]);
        var reopenedFiles = reopened.Entries.Where(entry => !entry.IsSynthetic).ToArray();
        CollectionAssert.AreEqual(
            files.Select(entry => (entry.Descriptor.NormalizedPath, entry.Descriptor.DuplicateOrdinal)).ToArray(),
            reopenedFiles.Select(entry => (entry.Descriptor.NormalizedPath, entry.Descriptor.DuplicateOrdinal)).ToArray());
        Assert.IsTrue(files.Zip(reopenedFiles).All(pair => pair.First.Descriptor.EntryId != pair.Second.Descriptor.EntryId));
    }

    [TestMethod]
    public void SelectingDirectoryResolvesItsRealDescendants()
    {
        var index = ArchiveIndex.Create(
        [
            FileEntry(0, "folder/a.txt"),
            FileEntry(1, "folder/nested/b.txt"),
            FileEntry(2, "outside.txt"),
        ]);
        var folder = index.Entries.Single(entry => entry.Descriptor.RawPath == "folder");

        var resolved = index.ResolveSelection([folder.Descriptor.EntryId]);

        CollectionAssert.AreEqual(
            new[] { "folder/a.txt", "folder/nested/b.txt" },
            resolved.Select(entry => entry.Descriptor.RawPath).ToArray());
    }

    [TestMethod]
    public void RejectsHierarchyBeyondConfiguredDepthBeforeSyntheticExpansion()
    {
        var exception = Assert.ThrowsExactly<ArchiveProviderException>(() => ArchiveIndex.Create(
            [FileEntry(0, "a/b/c/file.txt")],
            new ArchiveMetadataLimits(MaximumHierarchyDepth: 3)));

        Assert.AreEqual("resource.hierarchy_depth", exception.Code);
    }

    [TestMethod]
    public void RejectsSyntheticDirectoryAmplificationBeyondBudget()
    {
        var exception = Assert.ThrowsExactly<ArchiveProviderException>(() => ArchiveIndex.Create(
            [FileEntry(0, "a/b/file.txt")],
            new ArchiveMetadataLimits(MaximumSyntheticDirectories: 1)));

        Assert.AreEqual("resource.synthetic_directory_count", exception.Code);
    }

    private static ProviderArchiveEntry FileEntry(int ordinal, string path) =>
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
