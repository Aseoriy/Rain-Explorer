using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class NestedTemporaryWorkspaceRecoveryStoreTests
{
    [TestMethod]
    public void VerifiedWorkspaceCanBeInspectedDiscardedAndReplayedWithoutPaths()
    {
        using var root = new TemporaryDirectory();
        var recoveryId = new string('a', 64);
        var workspacePath = CreateWorkspace(root.Path, recoveryId, [3, 5]);
        var store = new NestedTemporaryWorkspaceRecoveryStore(root.Path);

        var inventory = store.List();
        Assert.HasCount(1, inventory.Recoveries);
        Assert.AreEqual(recoveryId, inventory.Recoveries[0].RecoveryId);
        Assert.AreEqual(0, inventory.ActiveWorkspaceCount);
        Assert.AreEqual(0, inventory.SuspiciousWorkspaceCount);
        Assert.IsFalse(inventory.IsTruncated);
        Assert.IsFalse(inventory.InspectionIncomplete);

        var inspection = store.Inspect(recoveryId);
        Assert.AreEqual(
            NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
            inspection.Assessment);
        Assert.AreEqual(2, inspection.SourceCount);
        Assert.AreEqual(8L, inspection.TotalSourceBytes);
        Assert.IsTrue(IsLowercaseSha256(inspection.Revision));

        var discarded = store.Discard(
            recoveryId,
            inspection.Revision!,
            inspection.SourceCount!.Value,
            inspection.TotalSourceBytes!.Value);
        Assert.AreEqual(NestedWorkspaceRecoveryDiscardResult.Discarded, discarded.Result);
        Assert.IsFalse(Directory.Exists(workspacePath));

        var replay = store.Discard(
            recoveryId,
            inspection.Revision!,
            inspection.SourceCount.Value,
            inspection.TotalSourceBytes.Value);
        Assert.AreEqual(NestedWorkspaceRecoveryDiscardResult.AlreadyAbsent, replay.Result);
    }

    [TestMethod]
    public void EmptyOwnerlessWorkspaceRequiresInspectionRevisionBeforeExactDeletion()
    {
        using var root = new TemporaryDirectory();
        var recoveryId = new string('b', 64);
        var workspacePath = Path.Combine(
            root.Path,
            OwnedNestedTemporaryWorkspace.WorkspacePrefix + recoveryId);
        Directory.CreateDirectory(workspacePath);
        var store = new NestedTemporaryWorkspaceRecoveryStore(root.Path);

        var inspection = store.Inspect(recoveryId);

        Assert.AreEqual(
            NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
            inspection.Assessment);
        Assert.AreEqual(0, inspection.SourceCount);
        Assert.AreEqual(0L, inspection.TotalSourceBytes);
        var result = store.Discard(
            recoveryId,
            inspection.Revision!,
            inspection.SourceCount!.Value,
            inspection.TotalSourceBytes!.Value);
        Assert.AreEqual(NestedWorkspaceRecoveryDiscardResult.Discarded, result.Result);
        Assert.IsFalse(Directory.Exists(workspacePath));
    }

    [TestMethod]
    public void ActiveOwnerClaimIsReportedAndPreserved()
    {
        using var root = new TemporaryDirectory();
        var recoveryId = new string('c', 64);
        var workspacePath = CreateWorkspace(root.Path, recoveryId, [4]);
        var ownerPath = Path.Combine(
            workspacePath,
            OwnedNestedTemporaryWorkspace.OwnerFileName);
        using var activeOwner = new FileStream(
            ownerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        var store = new NestedTemporaryWorkspaceRecoveryStore(root.Path);

        var inventory = store.List();
        var inspection = store.Inspect(recoveryId);

        Assert.HasCount(0, inventory.Recoveries);
        Assert.AreEqual(1, inventory.ActiveWorkspaceCount);
        Assert.AreEqual(0, inventory.SuspiciousWorkspaceCount);
        Assert.AreEqual(NestedWorkspaceRecoveryAssessment.Active, inspection.Assessment);
        Assert.IsTrue(Directory.Exists(workspacePath));
    }

    [TestMethod]
    public void ChangedSourceRejectsStaleRevisionAndPreservesTree()
    {
        using var root = new TemporaryDirectory();
        var recoveryId = new string('d', 64);
        var workspacePath = CreateWorkspace(root.Path, recoveryId, [7]);
        var sourcePath = Directory
            .EnumerateFiles(workspacePath, OwnedTemporaryArchiveSource.SourceFileName, SearchOption.AllDirectories)
            .Single();
        var store = new NestedTemporaryWorkspaceRecoveryStore(root.Path);
        var inspection = store.Inspect(recoveryId);
        File.AppendAllBytes(sourcePath, [1]);

        var exception = Assert.ThrowsExactly<ArchiveProviderException>(() => store.Discard(
            recoveryId,
            inspection.Revision!,
            inspection.SourceCount!.Value,
            inspection.TotalSourceBytes!.Value));

        Assert.AreEqual("nested_recovery.changed", exception.Code);
        Assert.IsTrue(Directory.Exists(workspacePath));
        Assert.AreEqual(8L, new FileInfo(sourcePath).Length);
    }

    [TestMethod]
    public void UnexpectedContentIsNeverOfferedForDisposition()
    {
        using var root = new TemporaryDirectory();
        var recoveryId = new string('e', 64);
        var workspacePath = CreateWorkspace(root.Path, recoveryId, [2]);
        var unexpectedPath = Path.Combine(workspacePath, "keep.txt");
        File.WriteAllText(unexpectedPath, "preserve me");
        var store = new NestedTemporaryWorkspaceRecoveryStore(root.Path);

        var inventory = store.List();
        var inspection = store.Inspect(recoveryId);

        Assert.HasCount(0, inventory.Recoveries);
        Assert.AreEqual(1, inventory.SuspiciousWorkspaceCount);
        Assert.AreEqual(
            NestedWorkspaceRecoveryAssessment.UnexpectedContents,
            inspection.Assessment);
        Assert.AreEqual("preserve me", File.ReadAllText(unexpectedPath));
    }

    [TestMethod]
    public void NamedDataStreamMakesWorkspaceSuspiciousAndPreservesIt()
    {
        using var root = new TemporaryDirectory();
        var recoveryId = new string('f', 64);
        var workspacePath = CreateWorkspace(root.Path, recoveryId, [2]);
        var sourcePath = Directory
            .EnumerateFiles(workspacePath, OwnedTemporaryArchiveSource.SourceFileName, SearchOption.AllDirectories)
            .Single();
        try
        {
            File.WriteAllText(sourcePath + ":untrusted", "preserve this stream");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Assert.Inconclusive("Windows named data streams are unavailable for this test volume.");
        }

        var store = new NestedTemporaryWorkspaceRecoveryStore(root.Path);
        var inspection = store.Inspect(recoveryId);

        Assert.AreEqual(
            NestedWorkspaceRecoveryAssessment.UnexpectedContents,
            inspection.Assessment);
        Assert.AreEqual("preserve this stream", File.ReadAllText(sourcePath + ":untrusted"));
        Assert.IsTrue(Directory.Exists(workspacePath));
    }

    private static string CreateWorkspace(
        string rootPath,
        string recoveryId,
        IReadOnlyList<int> sourceLengths)
    {
        var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + recoveryId;
        var workspacePath = Path.Combine(rootPath, workspaceName);
        Directory.CreateDirectory(workspacePath);
        File.WriteAllBytes(
            Path.Combine(workspacePath, OwnedNestedTemporaryWorkspace.OwnerFileName),
            OwnedNestedTemporaryWorkspace.CreateOwnerRecord(workspaceName));
        for (var index = 0; index < sourceLengths.Count; index++)
        {
            var sourceName = OwnedTemporaryArchiveSource.SourceDirectoryPrefix
                + index.ToString("x64");
            var sourceDirectory = Path.Combine(workspacePath, sourceName);
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllBytes(
                Path.Combine(sourceDirectory, OwnedTemporaryArchiveSource.SourceFileName),
                new byte[sourceLengths[index]]);
        }

        return workspacePath;
    }

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.NestedTemporaryWorkspaceRecoveryStoreTests",
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
