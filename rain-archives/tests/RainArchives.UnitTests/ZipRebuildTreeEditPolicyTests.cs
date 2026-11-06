using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ZipRebuildTreeEditPolicyTests
{
    [TestMethod]
    public async Task PlanIsDeterministicAndMovesExplicitEmptyDirectories()
    {
        await using var handle = new TestArchiveHandle(
        [
            Entry("tree/", ArchiveEntryKind.Directory, 0),
            Entry("tree/keep.txt", ArchiveEntryKind.File, 4),
            Entry("tree/empty/", ArchiveEntryKind.Directory, 0),
            Entry("outside.txt", ArchiveEntryKind.File, 7),
        ]);
        var index = ArchiveIndex.Create(handle.Entries);
        var fingerprint = new ArchiveFingerprint(123, DateTimeOffset.UnixEpoch, "id", "ABCDEF");
        var policy = new ZipRebuildTreeEditPolicy();
        var response = policy.BuildPlan(index, fingerprint);
        var rebuiltIndex = ArchiveIndex.Create(handle.Entries);
        var rebuiltResponse = policy.BuildPlan(rebuiltIndex, fingerprint);
        var tree = response.Directories.Single(node => node.NormalizedPath == "tree");
        Assert.AreEqual(
            tree.SubtreeRevision,
            rebuiltResponse.Directories.Single(node => node.NormalizedPath == "tree").SubtreeRevision,
            "Tree revisions must not depend on session-random entry identifiers.");
        Assert.AreEqual(2, tree.DescendantCount);
        Assert.AreEqual(2, tree.MaterialEntryCount);
        Assert.AreEqual(64, tree.SubtreeRevision.Length);
        Assert.IsTrue(tree.SubtreeRevision.All(Uri.IsHexDigit));

        var action = new ArchiveRebuildTreeAction(
            "tree",
            tree.SubtreeRevision,
            ArchiveRebuildTreeActionKind.Move,
            "moved");
        var plan = await policy.PlanAsync(
            handle,
            index,
            fingerprint,
            [],
            [],
            [],
            [],
            [action],
            Path.Combine(Path.GetTempPath(), "source.zip"),
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "moved", "moved/empty", "moved/keep.txt", "outside.txt" },
            plan.Entries.Select(entry => entry.ArchivePath).ToArray());
        Assert.IsTrue(plan.Entries.Single(entry => entry.ArchivePath == "moved/empty").Kind == ArchiveEntryKind.Directory);
    }

    [TestMethod]
    public void PlanRejectsStaleOverlappingAndSelfDescendantActions()
    {
        using var handle = new TestArchiveHandle(
        [
            Entry("tree/", ArchiveEntryKind.Directory, 0),
            Entry("tree/nested/", ArchiveEntryKind.Directory, 0),
            Entry("tree/file.txt", ArchiveEntryKind.File, 1),
            Entry("tree-bridge/", ArchiveEntryKind.Directory, 0),
            Entry("tree-bridge/file.txt", ArchiveEntryKind.File, 1),
        ]);
        var index = ArchiveIndex.Create(handle.Entries);
        var fingerprint = new ArchiveFingerprint(1, DateTimeOffset.UnixEpoch, "id", "prefix");
        var policy = new ZipRebuildTreeEditPolicy();
        var nodes = policy.BuildPlan(index, fingerprint).Directories;
        var tree = nodes.Single(node => node.NormalizedPath == "tree");
        var nested = nodes.Single(node => node.NormalizedPath == "tree/nested");
        var bridge = nodes.Single(node => node.NormalizedPath == "tree-bridge");

        var stale = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            policy.PlanAsync(
                handle,
                index,
                fingerprint,
                [], [], [], [],
                [new ArchiveRebuildTreeAction("tree", new string('0', 64), ArchiveRebuildTreeActionKind.Delete)],
                Path.Combine(Path.GetTempPath(), "source.zip"),
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
                CancellationToken.None).AsTask().GetAwaiter().GetResult());
        Assert.AreEqual("zip.rebuild_tree_action_invalid", stale.Code);

        var overlap = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            policy.PlanAsync(
                handle,
                index,
                fingerprint,
                [], [], [], [],
                [
                    new ArchiveRebuildTreeAction("tree", tree.SubtreeRevision, ArchiveRebuildTreeActionKind.Delete),
                    new ArchiveRebuildTreeAction("tree-bridge", bridge.SubtreeRevision, ArchiveRebuildTreeActionKind.Delete),
                    new ArchiveRebuildTreeAction("tree/nested", nested.SubtreeRevision, ArchiveRebuildTreeActionKind.Delete),
                ],
                Path.Combine(Path.GetTempPath(), "source.zip"),
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
                CancellationToken.None).AsTask().GetAwaiter().GetResult());
        Assert.AreEqual("zip.rebuild_tree_overlap", overlap.Code);

        var selfDescendant = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            policy.PlanAsync(
                handle,
                index,
                fingerprint,
                [], [], [], [],
                [new ArchiveRebuildTreeAction(
                    "tree",
                    tree.SubtreeRevision,
                    ArchiveRebuildTreeActionKind.Move,
                    "tree/child")],
                Path.Combine(Path.GetTempPath(), "source.zip"),
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
                CancellationToken.None).AsTask().GetAwaiter().GetResult());
        Assert.AreEqual("zip.rebuild_tree_self_descendant", selfDescendant.Code);

        var cycle = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            policy.PlanAsync(
                handle,
                index,
                fingerprint,
                [], [], [], [],
                [
                    new ArchiveRebuildTreeAction(
                        "tree",
                        tree.SubtreeRevision,
                        ArchiveRebuildTreeActionKind.Move,
                        "tree-bridge"),
                    new ArchiveRebuildTreeAction(
                        "tree-bridge",
                        bridge.SubtreeRevision,
                        ArchiveRebuildTreeActionKind.Move,
                        "tree"),
                ],
                Path.Combine(Path.GetTempPath(), "source.zip"),
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
                CancellationToken.None).AsTask().GetAwaiter().GetResult());
        Assert.AreEqual("zip.rebuild_tree_cycle", cycle.Code);
    }

    [TestMethod]
    public void TreePlanCountsSyntheticDescendantsButNotSyntheticMaterial()
    {
        using var handle = new TestArchiveHandle(
        [
            Entry("root/", ArchiveEntryKind.Directory, 0),
            Entry("root/synthetic/leaf.txt", ArchiveEntryKind.File, 1),
        ]);
        var plan = new ZipRebuildTreeEditPolicy().BuildPlan(
            ArchiveIndex.Create(handle.Entries),
            new ArchiveFingerprint(1, DateTimeOffset.UnixEpoch, "id", "prefix"));

        var root = plan.Directories.Single(node => node.NormalizedPath == "root");
        Assert.AreEqual(2, root.DescendantCount);
        Assert.AreEqual(1, root.MaterialEntryCount);
        var synthetic = plan.Directories.Single(node => node.NormalizedPath == "root/synthetic");
        Assert.IsFalse(synthetic.HasExplicitDirectoryEntry);
        Assert.AreEqual(1, synthetic.DescendantCount);
        Assert.AreEqual(1, synthetic.MaterialEntryCount);
    }

    [TestMethod]
    public void TreePlanRefusesBeforeGrowingPastBoundedNodeBudget()
    {
        using var handle = new TestArchiveHandle(
            Enumerable.Range(0, ProtocolConstants.MaximumZipRebuildTreeActions + 1)
                .Select(index => Entry($"node-{index:D4}/", ArchiveEntryKind.Directory, 0))
                .ToArray());
        var index = ArchiveIndex.Create(handle.Entries);

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            new ZipRebuildTreeEditPolicy().BuildPlan(
                index,
                new ArchiveFingerprint(1, DateTimeOffset.UnixEpoch, "id", "prefix")));

        Assert.AreEqual("zip.rebuild_tree_plan_limit", exception.Code);
    }

    [TestMethod]
    public void TreePlanRefusesIncrementallyAtPathPayloadBudget()
    {
        var suffix = new string('x', 120);
        using var handle = new TestArchiveHandle(
            Enumerable.Range(0, ProtocolConstants.MaximumZipRebuildTreeActions)
                .Select(index => Entry($"node-{index:D4}-{suffix}/", ArchiveEntryKind.Directory, 0))
                .ToArray());

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            new ZipRebuildTreeEditPolicy().BuildPlan(
                ArchiveIndex.Create(handle.Entries),
                new ArchiveFingerprint(1, DateTimeOffset.UnixEpoch, "id", "prefix")));

        Assert.AreEqual("zip.rebuild_tree_plan_limit", exception.Code);
    }

    [TestMethod]
    public void TreePlanUsesOrdinalPathOrder()
    {
        using var handle = new TestArchiveHandle(
        [
            Entry("zeta/file.txt", ArchiveEntryKind.File, 1),
            Entry("Alpha/file.txt", ArchiveEntryKind.File, 1),
            Entry("omega/file.txt", ArchiveEntryKind.File, 1),
            Entry("Beta/file.txt", ArchiveEntryKind.File, 1),
        ]);

        var plan = new ZipRebuildTreeEditPolicy().BuildPlan(
            ArchiveIndex.Create(handle.Entries),
            new ArchiveFingerprint(1, DateTimeOffset.UnixEpoch, "id", "prefix"));

        CollectionAssert.AreEqual(
            new[] { "Alpha", "Beta", "omega", "zeta" },
            plan.Directories.Select(node => node.NormalizedPath).ToArray());
    }

    [TestMethod]
    public async Task MappedTreeKeepsSourceManifestAndEmptyRoot()
    {
        using var root = new TemporaryDirectory();
        var sourceTree = Path.Combine(root.Path, "source");
        Directory.CreateDirectory(Path.Combine(sourceTree, "empty"));
        await File.WriteAllTextAsync(Path.Combine(sourceTree, "file.txt"), "mapped tree");
        await using var handle = new TestArchiveHandle([Entry("existing.txt", ArchiveEntryKind.File, 1)]);
        var index = ArchiveIndex.Create(handle.Entries);
        var plan = await new ZipRebuildTreeEditPolicy().PlanAsync(
            handle,
            index,
            new ArchiveFingerprint(1, DateTimeOffset.UnixEpoch, "id", "prefix"),
            [], [], [], [new ArchiveCreationDirectory(sourceTree, "added")], [],
            Path.Combine(root.Path, "source.zip"),
            Path.Combine(root.Path, "out.zip"),
            CancellationToken.None);

        Assert.HasCount(1, plan.NewSourceTrees);
        CollectionAssert.Contains(plan.Entries.Select(entry => entry.ArchivePath).ToArray(), "added");
        CollectionAssert.Contains(plan.Entries.Select(entry => entry.ArchivePath).ToArray(), "added/empty");
        CollectionAssert.Contains(plan.Entries.Select(entry => entry.ArchivePath).ToArray(), "added/file.txt");
    }

    [TestMethod]
    public async Task ExplicitReplacementUnderMoveUsesFinalPathAndDeletedSubtreeFailsClosed()
    {
        using var root = new TemporaryDirectory();
        var replacement = Path.Combine(root.Path, "replacement.txt");
        await File.WriteAllTextAsync(replacement, "replacement");
        await using var handle = new TestArchiveHandle(
        [
            Entry("tree/", ArchiveEntryKind.Directory, 0),
            Entry("tree/file.txt", ArchiveEntryKind.File, 4),
        ]);
        var index = ArchiveIndex.Create(handle.Entries);
        var fingerprint = new ArchiveFingerprint(1, DateTimeOffset.UnixEpoch, "id", "prefix");
        var policy = new ZipRebuildTreeEditPolicy();
        var revision = policy.BuildPlan(index, fingerprint).Directories.Single().SubtreeRevision;
        var move = new ArchiveRebuildTreeAction("tree", revision, ArchiveRebuildTreeActionKind.Move, "moved");
        var plan = await policy.PlanAsync(
            handle, index, fingerprint, [],
            [new ArchiveRebuildCopyFile(replacement, "tree/file.txt")], [], [], [move],
            Path.Combine(root.Path, "source.zip"), Path.Combine(root.Path, "move.zip"), CancellationToken.None);
        var replaced = plan.Entries.Single(entry => entry.ArchivePath == "moved/file.txt");
        Assert.AreEqual(ZipRebuildCopyEntrySource.NewFile, replaced.Source);

        var delete = new ArchiveRebuildTreeAction("tree", revision, ArchiveRebuildTreeActionKind.Delete);
        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.PlanAsync(
                handle, index, fingerprint, [],
                [new ArchiveRebuildCopyFile(replacement, "tree/file.txt")], [], [], [delete],
                Path.Combine(root.Path, "source.zip"), Path.Combine(root.Path, "delete.zip"), CancellationToken.None));
        Assert.AreEqual("zip.rebuild_tree_edit_conflict", exception.Code);
    }

    [TestMethod]
    public async Task FinalNamespaceRejectsParentFileClaimedAfterItsChild()
    {
        await using var handle = new TestArchiveHandle(
        [
            Entry("child-source.txt", ArchiveEntryKind.File, 1),
            Entry("parent-source.txt", ArchiveEntryKind.File, 1),
        ]);
        var index = ArchiveIndex.Create(handle.Entries);
        var byPath = index.Entries.ToDictionary(
            entry => entry.Descriptor.NormalizedPath!,
            entry => new ArchiveEntryKey(
                entry.Descriptor.NormalizedPath!,
                entry.Descriptor.DuplicateOrdinal));

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await new ZipRebuildTreeEditPolicy().PlanAsync(
                handle,
                index,
                new ArchiveFingerprint(1, DateTimeOffset.UnixEpoch, "id", "prefix"),
                [],
                [],
                [
                    new ArchiveRebuildRename(byPath["child-source.txt"], "parent/child.txt"),
                    new ArchiveRebuildRename(byPath["parent-source.txt"], "parent"),
                ],
                [],
                [],
                Path.Combine(Path.GetTempPath(), "source.zip"),
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip"),
                CancellationToken.None));

        Assert.AreEqual("zip.rebuild_tree_path_collision", exception.Code);
    }

    private static ProviderArchiveEntry Entry(string path, ArchiveEntryKind kind, long length) =>
        new(0, path, kind, kind == ArchiveEntryKind.File ? length : null,
            kind == ArchiveEntryKind.File ? length : null, DateTimeOffset.UnixEpoch, null,
            "Deflate", "crc", false, false, null);

    private sealed class TestArchiveHandle(IReadOnlyList<ProviderArchiveEntry> source) : IArchiveHandle, IDisposable
    {
        public IReadOnlyList<ProviderArchiveEntry> Entries { get; } = source.Select((entry, index) => entry with { Ordinal = index }).ToArray();
        public string Format => "zip";
        public string ProviderId => "test";
        public ArchiveCapability Capabilities => ArchiveCapability.List | ArchiveCapability.Extract;
        public bool IsSolid => false;
        public ArchiveVolumeSetKind VolumeSetKind => ArchiveVolumeSetKind.Single;
        public ValueTask<Stream> OpenEntryStreamAsync(int providerOrdinal, CancellationToken cancellationToken) => ValueTask.FromResult<Stream>(Stream.Null);
        public ValueTask CopyEntryToAsync(int providerOrdinal, Stream destination, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RainArchives.TreePolicyTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
