using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ZipRebuildSourceFolderReviewPolicyTests
{
    [TestMethod]
    public void CountsPinnedTreeChildrenAndMaterialFilesWithoutReadingPayloads()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Pinned source-folder review is Windows-only.");
        }

        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "雨 source");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllText(Path.Combine(source, "root.txt"), "root");
        File.WriteAllText(Path.Combine(source, "nested", "child.txt"), "child");
        using (var ads = new FileStream(
                   Path.Combine(source, "root.txt") + ":review-provenance",
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.Read))
        {
            ads.WriteByte(1);
        }

        var response = new ZipRebuildSourceFolderReviewPolicy().BuildReview(
            [new ArchiveCreationDirectory(source, "Imported/")]);

        var review = response.Directories.Single();
        Assert.AreEqual(Path.GetFullPath(source), review.SourcePath);
        Assert.AreEqual("Imported", review.ArchiveRoot);
        Assert.AreEqual(4, review.DescendantCount);
        Assert.AreEqual(2, review.FileCount);
    }

    [TestMethod]
    public void RejectsOverlappingRootsBeforeReviewingTheSecondRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Pinned source-folder review is Windows-only.");
        }

        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source");
        var nested = Path.Combine(source, "nested");
        Directory.CreateDirectory(nested);

        ArchiveProviderException? exception = null;
        try
        {
            _ = new ZipRebuildSourceFolderReviewPolicy().BuildReview(
                [
                    new ArchiveCreationDirectory(source, "one"),
                    new ArchiveCreationDirectory(nested, "two"),
                ]);
        }
        catch (ArchiveProviderException caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception);
        Assert.AreEqual("zip.rebuild_source_folder_review_overlap", exception!.Code);
    }

    [TestMethod]
    public void RejectsOverlappingArchiveRootsAcrossIndependentSourceFolders()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Pinned source-folder review is Windows-only.");
        }

        using var root = new TemporaryDirectory();
        var first = Path.Combine(root.Path, "first");
        var second = Path.Combine(root.Path, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        ArchiveProviderException? exception = null;
        try
        {
            _ = new ZipRebuildSourceFolderReviewPolicy().BuildReview(
                [
                    new ArchiveCreationDirectory(first, "Imported"),
                    new ArchiveCreationDirectory(second, "Imported/nested"),
                ]);
        }
        catch (ArchiveProviderException caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception);
        Assert.AreEqual("zip.rebuild_source_folder_review_archive_collision", exception!.Code);
    }

    [TestMethod]
    public void EnforcesOneGlobalEntryBudgetAcrossAllReviewedRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Pinned source-folder review is Windows-only.");
        }

        using var root = new TemporaryDirectory();
        var first = Path.Combine(root.Path, "first");
        var second = Path.Combine(root.Path, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        for (var index = 0; index < ProtocolConstants.MaximumZipCreationEntries - 1; index++)
        {
            using var file = new FileStream(
                Path.Combine(first, $"file-{index:D4}.txt"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
        }

        ArchiveProviderException? exception = null;
        try
        {
            _ = new ZipRebuildSourceFolderReviewPolicy().BuildReview(
                [
                    new ArchiveCreationDirectory(first, "first"),
                    new ArchiveCreationDirectory(second, "second"),
                ]);
        }
        catch (ArchiveProviderException caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception);
        Assert.AreEqual("zip.rebuild_source_folder_review_limit", exception!.Code);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.SourceFolderReviewTests",
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
