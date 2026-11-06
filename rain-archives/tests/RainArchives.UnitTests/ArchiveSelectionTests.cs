using System.Collections;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ArchiveSelectionTests
{
    [TestMethod]
    public void ArchiveSelection_IncludesFilesAndDescendantsButOmitsSyntheticDirectories()
    {
        var index = ArchiveIndex.Create(
        [
            FileEntry(0, "real/child.txt"),
            FileEntry(1, "synthetic/nested/file.txt"),
            FileEntry(2, "outside.txt"),
            DirectoryEntry(4, "real"),
        ]);
        var realDirectory = index.Entries.Single(entry => entry.Descriptor.RawPath == "real");
        var syntheticDirectory = index.Entries.Single(
            entry => entry.IsSynthetic && entry.Descriptor.RawPath == "synthetic");
        var outsideFile = index.Entries.Single(entry => entry.Descriptor.RawPath == "outside.txt");

        var resolved = index.ResolveSelection(
        [
            realDirectory.Descriptor.EntryId,
            syntheticDirectory.Descriptor.EntryId,
            outsideFile.Descriptor.EntryId,
        ]);

        CollectionAssert.AreEqual(
            new[] { "real/child.txt", "synthetic/nested/file.txt", "outside.txt", "real" },
            resolved.Select(entry => entry.Descriptor.RawPath).ToArray());
        Assert.IsFalse(resolved.Any(entry => entry.IsSynthetic));
    }

    [TestMethod]
    public void ArchiveSelection_EmptySelectionReturnsAllRealEntriesInProviderOrder()
    {
        var index = ArchiveIndex.Create(
        [
            FileEntry(2, "outside.txt"),
            FileEntry(0, "real/child.txt"),
            DirectoryEntry(4, "real"),
            FileEntry(1, "synthetic/file.txt"),
        ]);

        var resolved = index.ResolveSelection([]);

        CollectionAssert.AreEqual(
            new[] { "real/child.txt", "synthetic/file.txt", "outside.txt", "real" },
            resolved.Select(entry => entry.Descriptor.RawPath).ToArray());
        Assert.IsTrue(resolved.All(entry => !entry.IsSynthetic));
    }

    [TestMethod]
    public void ArchiveSelection_DeduplicatesIdsAndRejectsUnknownIds()
    {
        var index = ArchiveIndex.Create(
        [
            FileEntry(3, "folder/file.txt"),
            DirectoryEntry(5, "folder"),
        ]);
        var folder = index.Entries.Single(entry => entry.Descriptor.RawPath == "folder");

        var resolved = index.ResolveSelection(
        [
            folder.Descriptor.EntryId,
            folder.Descriptor.EntryId,
            folder.Descriptor.EntryId,
        ]);

        CollectionAssert.AreEqual(
            new[] { "folder/file.txt", "folder" },
            resolved.Select(entry => entry.Descriptor.RawPath).ToArray());

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            index.ResolveSelection([folder.Descriptor.EntryId, EntryId.New()]));

        Assert.AreEqual(ArchiveErrorCategory.InvalidRequest, exception.Category);
        Assert.AreEqual("entry.not_found", exception.Code);
    }

    [TestMethod]
    public void ArchiveSelection_TraversesManyDirectoriesWithoutPrefixScanning()
    {
        const int directoryCount = 10_000;
        var providerEntries = new List<ProviderArchiveEntry>(checked(directoryCount * 2));
        for (var index = 0; index < directoryCount; index++)
        {
            var directory = $"directory-{index:D5}";
            providerEntries.Add(DirectoryEntry(index * 2, directory));
            providerEntries.Add(FileEntry(index * 2 + 1, $"{directory}/file.txt"));
        }

        var archiveIndex = ArchiveIndex.Create(providerEntries);
        var selectedIds = archiveIndex.Entries
            .Where(entry => !entry.IsSynthetic && entry.Descriptor.Kind == ArchiveEntryKind.Directory)
            .Select(entry => entry.Descriptor.EntryId)
            .ToArray();

        var resolved = archiveIndex.ResolveSelection(selectedIds);

        Assert.AreEqual(directoryCount * 2, resolved.Count);
        Assert.AreEqual(0, resolved[0].ProviderEntry!.Ordinal);
        Assert.AreEqual(directoryCount * 2 - 1, resolved[^1].ProviderEntry!.Ordinal);
    }

    [TestMethod]
    public void ArchiveSelection_ObservesCancellationDuringSelectedIdEnumeration()
    {
        var index = ArchiveIndex.Create([FileEntry(0, "file.txt")]);
        var selectedFile = index.Entries.Single(entry => !entry.IsSynthetic);
        using var cancellation = new CancellationTokenSource();
        var selectedIds = new CancellingEntryIdCollection(
            [selectedFile.Descriptor.EntryId, EntryId.New()],
            cancellation);

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            index.ResolveSelection(selectedIds, cancellation.Token));
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

    private static ProviderArchiveEntry DirectoryEntry(int ordinal, string path) =>
        new(
            ordinal,
            path,
            ArchiveEntryKind.Directory,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            false,
            null);

    private sealed class CancellingEntryIdCollection(
        IReadOnlyList<EntryId> ids,
        CancellationTokenSource cancellation) : IReadOnlyCollection<EntryId>
    {
        public int Count => ids.Count;

        public IEnumerator<EntryId> GetEnumerator()
        {
            for (var index = 0; index < ids.Count; index++)
            {
                if (index == 1)
                {
                    cancellation.Cancel();
                }

                yield return ids[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
