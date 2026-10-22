using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

public sealed class FileOperationsTests
{
    private readonly FileOperationsService _ops = new();

    [Fact]
    public void CopyFolderPreservesSymlinksAsLinks()
    {
        string src = TestEnvironment.NewFolder("src");
        string dst = TestEnvironment.NewFolder("dst");
        string tree = Path.Combine(src, "tree");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "a.txt"), "a");
        // A link back to an ancestor would recurse forever if it were followed.
        Directory.CreateSymbolicLink(Path.Combine(tree, "loop"), "..");
        File.CreateSymbolicLink(Path.Combine(tree, "a-link"), "a.txt");

        var result = _ops.CopyIntoResult(new[] { tree }, dst, _ => TransferConflictChoice.Cancel);

        Assert.True(result.Ok, result.Error);
        string copied = Path.Combine(dst, "tree");
        Assert.Equal("a", File.ReadAllText(Path.Combine(copied, "a.txt")));
        Assert.Equal("..", new DirectoryInfo(Path.Combine(copied, "loop")).LinkTarget);
        Assert.Equal("a.txt", new FileInfo(Path.Combine(copied, "a-link")).LinkTarget);
    }

    [Fact]
    public void CopyKeepsExecutableBit()
    {
        string src = TestEnvironment.NewFolder("exec");
        string dst = TestEnvironment.NewFolder("execDst");
        string script = Path.Combine(src, "run.sh");
        File.WriteAllText(script, "#!/bin/sh\necho hi\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.True(_ops.CopyIntoResult(new[] { script }, dst, _ => TransferConflictChoice.Cancel).Ok);
        Assert.True(File.GetUnixFileMode(Path.Combine(dst, "run.sh")).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void MoveWithinFilesystemIsARename()
    {
        string src = TestEnvironment.NewFolder("mv");
        string dst = TestEnvironment.NewFolder("mvDst");
        string file = Path.Combine(src, "big.bin");
        File.WriteAllBytes(file, new byte[1024]);
        var before = UnixStat.TryGetIdentity(file);

        var result = _ops.MoveIntoResult(new[] { file }, dst, _ => TransferConflictChoice.Cancel);

        Assert.True(result.Ok, result.Error);
        Assert.False(File.Exists(file));
        Assert.Equal(before, UnixStat.TryGetIdentity(Path.Combine(dst, "big.bin")));
    }

    [Fact]
    public void ConflictsHonourSkipAndReplace()
    {
        string src = TestEnvironment.NewFolder("cs");
        string dst = TestEnvironment.NewFolder("cd");
        File.WriteAllText(Path.Combine(src, "x.txt"), "new");
        File.WriteAllText(Path.Combine(dst, "x.txt"), "old");

        Assert.True(_ops.CopyIntoResult(new[] { Path.Combine(src, "x.txt") }, dst, _ => TransferConflictChoice.SkipAll).Ok);
        Assert.Equal("old", File.ReadAllText(Path.Combine(dst, "x.txt")));

        Assert.True(_ops.CopyIntoResult(new[] { Path.Combine(src, "x.txt") }, dst, _ => TransferConflictChoice.ReplaceAll).Ok);
        Assert.Equal("new", File.ReadAllText(Path.Combine(dst, "x.txt")));

        var cancelled = _ops.CopyIntoResult(new[] { Path.Combine(src, "x.txt") }, dst, _ => TransferConflictChoice.Cancel);
        Assert.True(cancelled.Canceled);
    }

    [Fact]
    public void UndoMoveWithoutResolverNeverOverwrites()
    {
        string src = TestEnvironment.NewFolder("u");
        string dst = TestEnvironment.NewFolder("uDst");
        File.WriteAllText(Path.Combine(src, "keep.txt"), "src");
        File.WriteAllText(Path.Combine(dst, "keep.txt"), "dst");

        string? error = _ops.MoveInto(new[] { Path.Combine(src, "keep.txt") }, dst);

        Assert.NotNull(error);
        Assert.Equal("src", File.ReadAllText(Path.Combine(src, "keep.txt")));
        Assert.Equal("dst", File.ReadAllText(Path.Combine(dst, "keep.txt")));
    }

    [Fact]
    public void FolderCannotBeMovedIntoItself()
    {
        string root = TestEnvironment.NewFolder("self");
        string folder = Path.Combine(root, "A");
        Directory.CreateDirectory(Path.Combine(folder, "B"));

        var result = _ops.MoveIntoResult(new[] { folder }, Path.Combine(folder, "B"), _ => TransferConflictChoice.Cancel);

        Assert.False(result.Ok);
        Assert.True(Directory.Exists(Path.Combine(folder, "B")));
    }

    [Fact]
    public void RenameIsCaseSensitiveAndRejectsSlashes()
    {
        string dir = TestEnvironment.NewFolder("rename");
        string file = Path.Combine(dir, "readme");
        File.WriteAllText(file, "x");

        Assert.Null(_ops.Rename(file, "README"));
        Assert.True(File.Exists(Path.Combine(dir, "README")));
        Assert.NotNull(_ops.Rename(Path.Combine(dir, "README"), "a/b"));
    }

    [Fact]
    public void CreateFolderAndFileAreUniquified()
    {
        string dir = TestEnvironment.NewFolder("create");
        var (e1, f1) = _ops.CreateFolder(dir, "New folder");
        var (e2, f2) = _ops.CreateFolder(dir, "New folder");
        var (e3, n1) = _ops.CreateFile(dir, "notes.txt");
        var (e4, n2) = _ops.CreateFile(dir, "notes.txt");
        Assert.Null(e1 ?? e2 ?? e3 ?? e4);
        Assert.Equal(Path.Combine(dir, "New folder (2)"), f2);
        Assert.Equal(Path.Combine(dir, "notes (2).txt"), n2);
        Assert.True(Directory.Exists(f1) && File.Exists(n1));
    }

    [Fact]
    public void DeleteGoesToTrashAndPermanentDeleteSparesLinkTargets()
    {
        string dir = TestEnvironment.NewFolder("del");
        string target = Path.Combine(dir, "real");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "data.txt"), "precious");
        string link = Path.Combine(dir, "link-to-real");
        Directory.CreateSymbolicLink(link, target);

        Assert.True(_ops.DeletePermanent(new[] { link }).Ok);
        Assert.True(File.Exists(Path.Combine(target, "data.txt")));

        var trashed = _ops.Delete(new[] { target });
        Assert.True(trashed.Ok, trashed.Error);
        Assert.Single(trashed.Completed);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void LinksToFoldersCanBeRenamedAndMovedWithoutTouchingTheTarget()
    {
        string dir = TestEnvironment.NewFolder("linkmove");
        string target = Path.Combine(dir, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "t.txt"), "t");
        string link = Path.Combine(dir, "link");
        Directory.CreateSymbolicLink(link, target);
        string dangling = Path.Combine(dir, "dangling");
        File.CreateSymbolicLink(dangling, "/nonexistent/rain-test");

        Assert.Null(_ops.Rename(link, "renamed-link"));
        Assert.Equal(target, new DirectoryInfo(Path.Combine(dir, "renamed-link")).LinkTarget);

        string sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        var moved = _ops.MoveIntoResult(new[] { Path.Combine(dir, "renamed-link"), dangling }, sub,
            _ => TransferConflictChoice.Cancel);
        Assert.True(moved.Ok, moved.Error);
        Assert.Equal(target, new DirectoryInfo(Path.Combine(sub, "renamed-link")).LinkTarget);
        Assert.Equal("/nonexistent/rain-test", new FileInfo(Path.Combine(sub, "dangling")).LinkTarget);
        Assert.True(File.Exists(Path.Combine(target, "t.txt")));
    }

    [Fact]
    public void MountPointFallsBackToRoot()
    {
        Assert.Equal("/", MountService.GetMountPoint("/definitely/not/mounted/here"));
        Assert.Contains(MountService.GetDrives(), d => d.IsRoot);
    }

    [Fact]
    public void StatxReportsIdentityAndDevice()
    {
        string dir = TestEnvironment.NewFolder("stat");
        string file = Path.Combine(dir, "f");
        File.WriteAllText(file, "x");
        var info = UnixStat.TryGet(file);
        Assert.NotNull(info);
        Assert.True(info!.Value.Inode > 0);
        Assert.True(UnixStat.SameDevice(dir, file));
        Assert.Equal(1UL, info.Value.LinkCount);
    }
}
