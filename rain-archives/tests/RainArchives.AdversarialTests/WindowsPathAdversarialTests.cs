using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.AdversarialTests;

[TestClass]
public sealed class WindowsPathAdversarialTests
{
    [TestMethod]
    [DataRow("/rooted")]
    [DataRow("\\rooted")]
    [DataRow("\\??\\C:\\file")]
    [DataRow("\\\\.\\PhysicalDrive0")]
    [DataRow("\\\\server\\share\\file")]
    [DataRow("C:drive-relative")]
    [DataRow("file.txt:stream")]
    [DataRow("AUX")]
    [DataRow("nul.txt")]
    [DataRow("COM1.log")]
    [DataRow("CON .txt")]
    [DataRow("LPT\u00b9.txt")]
    [DataRow("name.")]
    [DataRow("name ")]
    [DataRow("one\\..\\two")]
    [DataRow("one/\u0001/two")]
    public void UnsafeNameNeverProducesExtractionPath(string rawPath)
    {
        var policy = new ExtractionPathPolicy();
        Assert.IsFalse(policy.TryNormalize(rawPath, out _, out _, out _), rawPath);
    }

    [TestMethod]
    public void FileDirectoryPrefixCollisionIsRejectedInEitherOrder()
    {
        var policy = new ExtractionPathPolicy();
        foreach (var entries in new[]
                 {
                     new[] { Entry(0, "a"), Entry(1, "a/b.txt") },
                     new[] { Entry(0, "a/b.txt"), Entry(1, "a") },
                 })
        {
            var index = ArchiveIndex.Create(entries);
            var plan = policy.Plan(index.Entries.Where(entry => !entry.IsSynthetic).ToArray());
            Assert.IsFalse(plan.IsSafe);
            Assert.IsTrue(plan.Issues.Any(issue => issue.Code == "path.file_directory_collision"));
        }
    }

    [TestMethod]
    public void LinkEntryIsNeverPlannedForMaterialization()
    {
        var link = new ProviderArchiveEntry(
            0,
            "link",
            ArchiveEntryKind.SymbolicLink,
            0,
            0,
            null,
            null,
            null,
            null,
            false,
            false,
            "../../outside");
        var plan = new ExtractionPathPolicy().Plan(ArchiveIndex.Create([link]).Entries);

        Assert.IsFalse(plan.IsSafe);
        Assert.AreEqual(0, plan.Entries.Count);
        Assert.AreEqual("path.link_not_materialized", plan.Issues.Single().Code);
    }

    [TestMethod]
    public void ExistingDestinationReparsePointIsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows reparse points are required for this test.");
        }

        var root = Path.Combine(Path.GetTempPath(), "RainArchives.AdversarialTests", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                Assert.Inconclusive($"This Windows test context cannot create a directory reparse point: {exception.Message}");
            }

            Assert.ThrowsExactly<IOException>(() =>
                ReparsePointGuard.EnsureExistingPathHasNoReparsePoints(Path.Combine(link, "child")));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ProviderArchiveEntry Entry(int ordinal, string path) =>
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
