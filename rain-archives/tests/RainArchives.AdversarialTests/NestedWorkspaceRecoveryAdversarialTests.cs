using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.AdversarialTests;

[TestClass]
public sealed class NestedWorkspaceRecoveryAdversarialTests
{
    private const string WorkspacePrefix = "rainarchives-worker-";
    private const string SourcePrefix = "rainarchives-nested-";
    private const string OwnerFileName = "owner.lock";
    private const string SourceFileName = "source.archive";

    [TestMethod]
    public void WorkspaceReparsePointIsNeverTraversedOrDeleted()
    {
        using var root = new TemporaryDirectory();
        var recoveryId = new string('1', 64);
        var target = Path.Combine(root.Path, "outside-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "preserve.txt"), "preserve me");
        var workspaceLink = Path.Combine(root.Path, WorkspacePrefix + recoveryId);
        try
        {
            Directory.CreateSymbolicLink(workspaceLink, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive(
                $"This Windows test context cannot create a directory reparse point: {exception.Message}");
        }

        var store = new NestedTemporaryWorkspaceRecoveryStore(root.Path);
        var inspection = store.Inspect(recoveryId);

        Assert.AreEqual(
            NestedWorkspaceRecoveryAssessment.UnexpectedContents,
            inspection.Assessment);
        Assert.AreEqual("preserve me", File.ReadAllText(Path.Combine(target, "preserve.txt")));
        Directory.Delete(workspaceLink, recursive: false);
    }

    [TestMethod]
    public void NestedSourceReparsePointIsNeverTraversedOrDeleted()
    {
        using var root = new TemporaryDirectory();
        var recoveryId = new string('2', 64);
        var workspace = CreateWorkspace(root.Path, recoveryId);
        var target = Path.Combine(root.Path, "outside-source");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, SourceFileName), "preserve me");
        var sourceLink = Path.Combine(workspace, SourcePrefix + new string('a', 64));
        try
        {
            Directory.CreateSymbolicLink(sourceLink, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Inconclusive(
                $"This Windows test context cannot create a directory reparse point: {exception.Message}");
        }

        var store = new NestedTemporaryWorkspaceRecoveryStore(root.Path);
        var inspection = store.Inspect(recoveryId);

        Assert.AreEqual(
            NestedWorkspaceRecoveryAssessment.UnexpectedContents,
            inspection.Assessment);
        Assert.AreEqual("preserve me", File.ReadAllText(Path.Combine(target, SourceFileName)));
        Directory.Delete(sourceLink, recursive: false);
    }

    [TestMethod]
    public void HardLinkedNestedSourceIsNeverDispositionEligible()
    {
        using var root = new TemporaryDirectory();
        var recoveryId = new string('3', 64);
        var workspace = CreateWorkspace(root.Path, recoveryId);
        var sourceDirectory = AddSource(workspace, new string('b', 64), 4);
        var sourcePath = Path.Combine(sourceDirectory, SourceFileName);
        var secondLink = Path.Combine(root.Path, "preserved-hard-link.bin");
        if (!NativeMethods.CreateHardLink(secondLink, sourcePath, IntPtr.Zero))
        {
            Assert.Inconclusive(
                $"This test volume cannot create hard links: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        var store = new NestedTemporaryWorkspaceRecoveryStore(root.Path);
        var inspection = store.Inspect(recoveryId);

        Assert.AreEqual(
            NestedWorkspaceRecoveryAssessment.UnexpectedContents,
            inspection.Assessment);
        Assert.IsTrue(File.Exists(sourcePath));
        Assert.IsTrue(File.Exists(secondLink));
    }

    [TestMethod]
    public void ExcessiveSourceCountAndSparseLengthArePreservedAsUnexpected()
    {
        using var root = new TemporaryDirectory();
        var excessiveCountId = new string('4', 64);
        var excessiveCountWorkspace = CreateWorkspace(root.Path, excessiveCountId);
        for (var index = 0; index <= ProtocolConstants.MaximumOpenSessions; index++)
        {
            _ = AddSource(excessiveCountWorkspace, index.ToString("x64"), 0);
        }

        var excessiveLengthId = new string('5', 64);
        var excessiveLengthWorkspace = CreateWorkspace(root.Path, excessiveLengthId);
        var sourceDirectory = AddSource(excessiveLengthWorkspace, new string('c', 64), 0);
        var sourcePath = Path.Combine(sourceDirectory, SourceFileName);
        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            source.SetLength(ProtocolConstants.MaximumNestedArchiveBytes + 1);
        }

        var store = new NestedTemporaryWorkspaceRecoveryStore(root.Path);

        Assert.AreEqual(
            NestedWorkspaceRecoveryAssessment.UnexpectedContents,
            store.Inspect(excessiveCountId).Assessment);
        Assert.AreEqual(
            NestedWorkspaceRecoveryAssessment.UnexpectedContents,
            store.Inspect(excessiveLengthId).Assessment);
        Assert.IsTrue(Directory.Exists(excessiveCountWorkspace));
        Assert.AreEqual(
            ProtocolConstants.MaximumNestedArchiveBytes + 1,
            new FileInfo(sourcePath).Length);
    }

    private static string CreateWorkspace(string rootPath, string recoveryId)
    {
        var workspaceName = WorkspacePrefix + recoveryId;
        var workspace = Path.Combine(rootPath, workspaceName);
        Directory.CreateDirectory(workspace);
        File.WriteAllBytes(
            Path.Combine(workspace, OwnerFileName),
            Encoding.ASCII.GetBytes($"RainArchives nested workspace v1\n{recoveryId}\n"));
        return workspace;
    }

    private static string AddSource(
        string workspace,
        string sourceId,
        int length)
    {
        var sourceDirectory = Path.Combine(workspace, SourcePrefix + sourceId);
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllBytes(Path.Combine(sourceDirectory, SourceFileName), new byte[length]);
        return sourceDirectory;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.NestedWorkspaceRecoveryAdversarialTests",
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

    private static class NativeMethods
    {
        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateHardLinkW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLink(
            string newFileName,
            string existingFileName,
            IntPtr securityAttributes);
    }
}
