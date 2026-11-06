using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ExtractionPathPolicyTests
{
    private readonly ExtractionPathPolicy _policy = new();

    [TestMethod]
    public void AcceptsOrdinaryRelativeWindowsPath()
    {
        Assert.IsTrue(_policy.TryNormalize(
            "folder/sub/file.txt",
            out var relative,
            out var collision,
            out _));
        Assert.AreEqual(Path.Combine("folder", "sub", "file.txt"), relative);
        Assert.AreEqual("FOLDER/SUB/FILE.TXT", collision);
    }

    [TestMethod]
    [DataRow("../escape.txt")]
    [DataRow("..\\escape.txt")]
    [DataRow("C:\\absolute.txt")]
    [DataRow("C:drive-relative.txt")]
    [DataRow("\\\\server\\share\\file.txt")]
    [DataRow("\\\\?\\C:\\device.txt")]
    [DataRow("file.txt:stream")]
    [DataRow("folder//file.txt")]
    [DataRow("folder/./file.txt")]
    [DataRow("folder/CON.txt")]
    [DataRow("folder/CON .txt")]
    [DataRow("folder/name. ")]
    public void RejectsUnsafeWindowsPaths(string rawPath)
    {
        Assert.IsFalse(_policy.TryNormalize(rawPath, out _, out _, out _), rawPath);
    }

    [TestMethod]
    public void TreatsCaseAndUnicodeNormalizationAsCollisions()
    {
        var entries = ArchiveIndex.Create(
        [
            FileEntry(0, "Folder/Caf\u00e9.txt"),
            FileEntry(1, "folder/Cafe\u0301.TXT"),
        ]).Entries.Where(entry => !entry.IsSynthetic).ToArray();

        var plan = _policy.Plan(entries);

        Assert.IsFalse(plan.IsSafe);
        Assert.IsTrue(plan.Issues.Any(issue => issue.Code == "path.collision"));
    }

    [TestMethod]
    public void RejectsImplicitParentDirectoriesWithCollidingSpellings()
    {
        var entries = ArchiveIndex.Create(
        [
            FileEntry(0, "Folder/one.txt"),
            FileEntry(1, "folder/two.txt"),
            FileEntry(2, "Caf\u00e9/three.txt"),
            FileEntry(3, "Cafe\u0301/four.txt"),
        ]).Entries.Where(entry => !entry.IsSynthetic).ToArray();

        var plan = _policy.Plan(entries);

        Assert.IsFalse(plan.IsSafe);
        Assert.AreEqual(4, plan.Issues.Count(issue => issue.Code == "path.parent_spelling_collision"));
    }

    [TestMethod]
    public void AllowsOneDirectoryMarkerSeparator()
    {
        var directory = ArchiveIndex.Create(
        [
            new ProviderArchiveEntry(
                0,
                "folder/",
                ArchiveEntryKind.Directory,
                0,
                0,
                null,
                null,
                null,
                null,
                false,
                false,
                null),
        ]).Entries;

        Assert.IsTrue(_policy.Plan(directory).IsSafe);
    }

    [TestMethod]
    public void ReportsLargeDuplicateSetsWithoutDuplicateDiagnostics()
    {
        var entries = ArchiveIndex.Create(
            Enumerable.Range(0, 2_000)
                .Select(index => FileEntry(index, "same.txt"))
                .ToArray()).Entries;

        var plan = _policy.Plan(entries);

        Assert.IsFalse(plan.IsSafe);
        Assert.AreEqual(2_000, plan.Issues.Count(issue => issue.Code == "path.collision"));
    }

    [TestMethod]
    public void RefusesUnclassifiedEntryKinds()
    {
        var index = ArchiveIndex.Create(
        [
            new ProviderArchiveEntry(
                0,
                "mystery.bin",
                ArchiveEntryKind.Unknown,
                1,
                1,
                null,
                null,
                null,
                null,
                false,
                false,
                null),
        ]);

        var plan = _policy.Plan(index.Entries);

        Assert.IsFalse(plan.IsSafe);
        Assert.AreEqual("path.unsupported_entry_kind", plan.Issues.Single().Code);
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
