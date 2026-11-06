using System.ComponentModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class OwnedNestedTemporaryWorkspaceTests
{
    [TestMethod]
    public async Task WorkspaceWithoutSourcesDisposesExactTree()
    {
        using var root = new TemporaryDirectory();
        var workspace = OwnedNestedTemporaryWorkspace.Create(root.Path);
        var workspacePath = workspace.DirectoryPath;

        await workspace.DisposeAsync();

        Assert.IsFalse(Directory.Exists(workspacePath));
    }

    [TestMethod]
    public async Task WorkspaceDisposesExactTree()
    {
        using var root = new TemporaryDirectory();
        var workspace = OwnedNestedTemporaryWorkspace.Create(root.Path);
        var workspacePath = workspace.DirectoryPath;
        var source = workspace.CreateSource();
        await source.Output.WriteAsync("nested archive"u8.ToArray());
        await source.CompleteAsync();

        await source.DisposeAsync();
        await workspace.DisposeAsync();

        Assert.IsFalse(Directory.Exists(workspacePath));
        Assert.AreEqual(
            new NestedTemporaryWorkspaceInventory(0, 0, 0, false, false),
            OwnedNestedTemporaryWorkspace.Inspect(root.Path));
    }

    [TestMethod]
    public async Task ExclusiveOwnerClaimIsClassifiedActiveWithoutLeakingProbeHandle()
    {
        using var root = new TemporaryDirectory();
        var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + new string('c', 64);
        var workspacePath = Path.Combine(root.Path, workspaceName);
        Directory.CreateDirectory(workspacePath);
        var ownerPath = Path.Combine(workspacePath, OwnedNestedTemporaryWorkspace.OwnerFileName);
        await File.WriteAllBytesAsync(
            ownerPath,
            OwnedNestedTemporaryWorkspace.CreateOwnerRecord(workspaceName));
        using (var owner = new FileStream(
                   ownerPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var active = OwnedNestedTemporaryWorkspace.Inspect(root.Path);
            Assert.AreEqual(1, active.ActiveWorkspaceCount);
            Assert.AreEqual(0, active.PotentialStaleWorkspaceCount);
            Assert.AreEqual(0, active.SuspiciousWorkspaceCount);
            Assert.IsFalse(active.RequiresAttention);
        }

        Directory.Delete(workspacePath, recursive: true);
    }

    [TestMethod]
    public async Task UnlockedValidOwnerClaimIsReportedButNeverDeleted()
    {
        using var root = new TemporaryDirectory();
        var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + new string('a', 64);
        var workspacePath = Path.Combine(root.Path, workspaceName);
        Directory.CreateDirectory(workspacePath);
        var ownerPath = Path.Combine(workspacePath, OwnedNestedTemporaryWorkspace.OwnerFileName);
        await File.WriteAllBytesAsync(
            ownerPath,
            OwnedNestedTemporaryWorkspace.CreateOwnerRecord(workspaceName));
        var preservedPath = Path.Combine(workspacePath, "rainarchives-nested-preserved");
        Directory.CreateDirectory(preservedPath);
        await File.WriteAllTextAsync(Path.Combine(preservedPath, "source.archive"), "preserve");

        var inventory = OwnedNestedTemporaryWorkspace.Inspect(root.Path);

        Assert.AreEqual(0, inventory.ActiveWorkspaceCount);
        Assert.AreEqual(1, inventory.PotentialStaleWorkspaceCount);
        Assert.AreEqual(0, inventory.SuspiciousWorkspaceCount);
        Assert.IsTrue(inventory.RequiresAttention);
        Assert.AreEqual("preserve", await File.ReadAllTextAsync(Path.Combine(preservedPath, "source.archive")));
    }

    [TestMethod]
    public async Task InvalidOwnerClaimIsSuspiciousAndPreserved()
    {
        using var root = new TemporaryDirectory();
        var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + new string('b', 64);
        var workspacePath = Path.Combine(root.Path, workspaceName);
        Directory.CreateDirectory(workspacePath);
        var ownerPath = Path.Combine(workspacePath, OwnedNestedTemporaryWorkspace.OwnerFileName);
        await File.WriteAllTextAsync(ownerPath, "not a Rain ownership claim");

        var inventory = OwnedNestedTemporaryWorkspace.Inspect(root.Path);

        Assert.AreEqual(0, inventory.ActiveWorkspaceCount);
        Assert.AreEqual(0, inventory.PotentialStaleWorkspaceCount);
        Assert.AreEqual(1, inventory.SuspiciousWorkspaceCount);
        Assert.IsTrue(inventory.RequiresAttention);
        Assert.AreEqual("not a Rain ownership claim", await File.ReadAllTextAsync(ownerPath));
    }

    [TestMethod]
    public async Task InventoryLimitIsBoundedAndReportsTruncation()
    {
        using var root = new TemporaryDirectory();
        for (var index = 0; index < 2; index++)
        {
            var token = index.ToString("x64");
            var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + token;
            var workspacePath = Path.Combine(root.Path, workspaceName);
            Directory.CreateDirectory(workspacePath);
            await File.WriteAllBytesAsync(
                Path.Combine(workspacePath, OwnedNestedTemporaryWorkspace.OwnerFileName),
                OwnedNestedTemporaryWorkspace.CreateOwnerRecord(workspaceName));
        }

        var inventory = OwnedNestedTemporaryWorkspace.Inspect(root.Path, maximumEntries: 1);

        Assert.AreEqual(1, inventory.PotentialStaleWorkspaceCount);
        Assert.IsTrue(inventory.IsTruncated);
        Assert.IsTrue(inventory.RequiresAttention);
    }

    [TestMethod]
    public async Task WorkspaceCleanupPreservesUnexpectedContentForLaterInventory()
    {
        using var root = new TemporaryDirectory();
        var workspace = OwnedNestedTemporaryWorkspace.Create(root.Path);
        var ownerPath = Path.Combine(
            workspace.DirectoryPath,
            OwnedNestedTemporaryWorkspace.OwnerFileName);
        var unexpectedPath = Path.Combine(workspace.DirectoryPath, "unexpected-user-data");
        Directory.CreateDirectory(unexpectedPath);
        await File.WriteAllTextAsync(Path.Combine(unexpectedPath, "keep.txt"), "preserve me");

        await Assert.ThrowsExactlyAsync<Win32Exception>(async () =>
            await workspace.DisposeAsync());

        Assert.IsFalse(File.Exists(ownerPath));
        Assert.AreEqual(
            "preserve me",
            await File.ReadAllTextAsync(Path.Combine(unexpectedPath, "keep.txt")));
        var inventory = OwnedNestedTemporaryWorkspace.Inspect(root.Path);
        Assert.AreEqual(1, inventory.SuspiciousWorkspaceCount);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.OwnedNestedTemporaryWorkspaceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 50 && Directory.Exists(Path); attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 49)
                {
                    Thread.Sleep(10);
                }
            }
        }
    }
}
