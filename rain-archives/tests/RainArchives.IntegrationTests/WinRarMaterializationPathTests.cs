using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.WinRar;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class WinRarMaterializationPathTests
{
    [TestMethod]
    [DataRow(@"C:\workspace\child", @"\\?\C:\workspace\child")]
    [DataRow(@"C:\workspace\first\..\child", @"\\?\C:\workspace\child")]
    [DataRow(@"\\server\share\workspace", @"\\?\UNC\server\share\workspace")]
    [DataRow(@"\\?\C:\workspace\child", @"\\?\C:\workspace\child")]
    public void NativeWorkspacePathsUseExtendedLengthSyntax(string path, string expected)
    {
        Assert.AreEqual(expected, WinRarEncoderWorkspace.ToExtendedLengthPath(path));
    }

    [TestMethod]
    public void UnrepresentableNameReportsFixedFailureAndReleasesPins()
    {
        using var fixture = new WorkspaceFixture();
        var count = 1;
        var parent = Path.Combine(fixture.Workspace.DirectoryPath, "input");
        var exception = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            fixture.Workspace.PrepareMaterializationDirectory(
                Path.Combine(parent, new string('x', 256)), ref count, 10, CancellationToken.None));
        Assert.IsTrue(exception.Code is "rar.workspace_path_too_long" or "rar.workspace_io_failure");
        Assert.AreEqual(ArchiveErrorCategory.ProviderFailure, exception.Category);
        Assert.IsFalse(exception.Message.Contains(parent, StringComparison.Ordinal));
        Directory.Move(parent, parent + "-moved");
    }

    [TestMethod]
    [DataRow(206, "rar.workspace_path_too_long", "A RAR preparation path or name exceeds the filesystem limit.")]
    [DataRow(32, "rar.workspace_io_failure", "RAR preparation could not create or pin a private workspace directory.")]
    public void NativeMaterializationFailureUsesOnlyFixedCodesAndText(int code, string expectedCode, string expectedText)
    {
        var exception = WinRarEncoderWorkspace.DescribeMaterializationFailure(
            new System.ComponentModel.Win32Exception(code, "private-native-message-and-path"));
        Assert.AreEqual(expectedCode, exception.Code);
        Assert.AreEqual(expectedText, exception.Message);
        Assert.AreEqual(ArchiveErrorCategory.ProviderFailure, exception.Category);
    }

    [TestMethod]
    public void PinConflictReportsFixedWorkspaceFailureWithoutNativeText()
    {
        using var fixture = new WorkspaceFixture();
        var count = 1;
        var parent = Path.Combine(fixture.Workspace.DirectoryPath, "input");
        using (fixture.Workspace.PrepareMaterializationDirectory(parent, ref count, 10, CancellationToken.None))
        {
            var exception = Assert.ThrowsExactly<ArchiveProviderException>(() =>
                fixture.Workspace.PrepareMaterializationDirectory(parent, ref count, 10, CancellationToken.None));
            Assert.AreEqual("rar.workspace_io_failure", exception.Code);
            Assert.AreEqual("RAR preparation could not create or pin a private workspace directory.", exception.Message);
        }
        Directory.Move(parent, parent + "-moved");
    }

    [TestMethod]
    [DataRow(274)]
    [DataRow(520)]
    public void LongMaterializationPathsRemainPinnedAndCleanUp(int minimumLength)
    {
        using var fixture = new WorkspaceFixture();
        var count = 1;
        var nested = Path.Combine(fixture.Workspace.DirectoryPath, "directories");
        var components = 1;
        while (nested.Length < minimumLength)
        {
            nested = Path.Combine(nested, "capture-空-0123456789abcdef0123456789abcdef");
            components++;
        }

        using (fixture.Workspace.PrepareMaterializationDirectory(
                   nested, ref count, components + 1, CancellationToken.None))
        {
            Assert.AreEqual(components + 1, count);
            Assert.IsTrue(Directory.Exists(nested));
            Assert.ThrowsExactly<IOException>(() => Directory.Move(nested, nested + "-moved"));
            File.WriteAllText(Path.Combine(nested, "payload.txt"), "long path payload");
        }

        Assert.AreEqual("long path payload", File.ReadAllText(Path.Combine(nested, "payload.txt")));
        Directory.Move(nested, nested + "-moved");
    }

    [TestMethod]
    public void RepeatedDirectoryPreparationCountsEachComponentOnceAndReleasesPins()
    {
        using var fixture = new WorkspaceFixture();
        var count = 1; // owner.lock
        var nested = Path.Combine(fixture.Workspace.DirectoryPath, "input", "Unicode-空", "leaf");
        for (var index = 0; index < 1_000; index++)
        {
            using var lease = fixture.Workspace.PrepareMaterializationDirectory(
                nested, ref count, 4, CancellationToken.None);
            Assert.AreEqual(4, count);
        }

        Directory.Move(nested, nested + "-moved");
        Assert.IsTrue(Directory.Exists(nested + "-moved"));
    }

    [TestMethod]
    public void DirectoryBudgetRefusesBeforeCreatingTheExcessComponent()
    {
        using var fixture = new WorkspaceFixture();
        var count = 1;
        var parent = Path.Combine(fixture.Workspace.DirectoryPath, "input");
        var excess = Path.Combine(parent, "excess");
        var exception = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            fixture.Workspace.PrepareMaterializationDirectory(excess, ref count, 2, CancellationToken.None));
        Assert.AreEqual("rar.workspace_object_limit", exception.Code);
        Assert.AreEqual(2, count);
        Assert.IsFalse(Directory.Exists(excess));
        // Failed preparation must release the already-pinned prefix.
        Directory.Move(parent, parent + "-moved");
    }

    [TestMethod]
    public void MaterializationPinsEveryDirectoryUntilTheOutputOwnerFinishes()
    {
        using var fixture = new WorkspaceFixture();
        var count = 1;
        var parent = Path.Combine(fixture.Workspace.DirectoryPath, "input");
        var nested = Path.Combine(parent, "child");
        using (fixture.Workspace.PrepareMaterializationDirectory(nested, ref count, 3, CancellationToken.None))
        {
            Assert.ThrowsExactly<IOException>(() => Directory.Move(parent, parent + "-moved"));
            Assert.ThrowsExactly<IOException>(() => Directory.Move(nested, nested + "-moved"));
        }
        Directory.Move(parent, parent + "-moved");
    }

    [TestMethod]
    public void PreCancelledPreparationCreatesNothingAndFilePrefixDoesNotGetTraversed()
    {
        using var fixture = new WorkspaceFixture();
        var count = 1;
        var path = Path.Combine(fixture.Workspace.DirectoryPath, "input");
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            fixture.Workspace.PrepareMaterializationDirectory(path, ref count, 10, new CancellationToken(true)));
        Assert.IsFalse(Directory.Exists(path));
        File.WriteAllText(path, "ordinary file");
        var exception = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            fixture.Workspace.PrepareMaterializationDirectory(
                Path.Combine(path, "child"), ref count, 10, CancellationToken.None));
        Assert.AreEqual("rar.workspace_ancestry_unsafe", exception.Code);
        Assert.AreEqual("ordinary file", File.ReadAllText(path));
    }

    [TestMethod]
    public void MaterializationRefusesPathsOutsideTheOwnedWorkspace()
    {
        using var fixture = new WorkspaceFixture();
        var count = 1;
        var outside = Path.Combine(fixture.Root, "sibling");
        var exception = Assert.ThrowsExactly<ArchiveProviderException>(() =>
            fixture.Workspace.PrepareMaterializationDirectory(outside, ref count, 10, CancellationToken.None));
        Assert.AreEqual("rar.materialization_path_escape", exception.Code);
        Assert.IsFalse(Directory.Exists(outside));
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    [TestCategory("ExternalQualification")]
    public async Task CancellationAfterManyFilesReleasesMaterializationAndLeavesDestinationUntouched()
    {
        var executable = Environment.GetEnvironmentVariable("RAINARCHIVES_TEST_WINRAR_723");
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(),
            "RainArchives-RarMaterializationCancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.IsTrue(WinRarArchiveWriterProvider.TryCreateVerifiedExecutable(
                executable, root, out var provider, out var availability));
            Assert.AreEqual(RarEncoderAvailability.Available, availability);
            Assert.IsNotNull(provider);
            using var destination = new MemoryStream();
            using var cancellation = new CancellationTokenSource();
            var payload = new byte[128 * 1024];
            var timestamp = DateTimeOffset.UnixEpoch.AddYears(50);
            await using (var writer = await provider.CreateAsync(
                destination, ArchiveCompressionPreset.Balanced, CancellationToken.None))
            {
                for (var index = 0; index < 256; index++)
                {
                    using var source = new MemoryStream(payload, 0, 1024, writable: false);
                    await writer.AddFileAsync(
                        new ArchiveWriteEntry($"group/f{index:D4}.bin", source.Length, timestamp),
                        source, _ => ValueTask.CompletedTask, CancellationToken.None);
                }

                using var partial = new MemoryStream(payload, writable: false);
                var exception = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                    await writer.AddFileAsync(
                        new ArchiveWriteEntry("group/partial.bin", partial.Length, timestamp),
                        partial,
                        _ => { cancellation.Cancel(); return ValueTask.CompletedTask; },
                        cancellation.Token));
                Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            }

            Assert.AreEqual(0L, destination.Length);
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(root));
            Assert.IsTrue(payload.All(value => value == 0));
        }
        finally
        {
            // Non-recursive removal deliberately preserves unexpected residue.
            Directory.Delete(root, recursive: false);
        }
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(),
            "RainArchives-RarMaterialization-" + Guid.NewGuid().ToString("N"));
        internal WinRarEncoderWorkspace Workspace { get; }

        internal WorkspaceFixture() => Workspace = WinRarEncoderWorkspace.Create(Root);

        public void Dispose()
        {
            Workspace.Cleanup();
            Workspace.Dispose();
            Directory.Delete(Root, recursive: false);
        }
    }
}
