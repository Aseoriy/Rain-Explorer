using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

/// <summary>FreeDesktop trash behaviour against a private XDG_DATA_HOME (see TestEnvironment).</summary>
public sealed class TrashServiceTests
{
    [Fact]
    public void TrashWritesSpecCompliantInfoAndRestoreBringsTheFileBack()
    {
        string dir = TestEnvironment.NewFolder("trash");
        string file = Path.Combine(dir, "report final.txt");
        File.WriteAllText(file, "hello");

        Assert.Null(TrashService.Trash(file));
        Assert.False(File.Exists(file));

        string payload = Path.Combine(TrashService.HomeTrash, "files", "report final.txt");
        string info = Path.Combine(TrashService.HomeTrash, "info", "report final.txt.trashinfo");
        Assert.True(File.Exists(payload));
        string[] lines = File.ReadAllLines(info);
        Assert.Equal("[Trash Info]", lines[0]);
        Assert.Equal("Path=" + string.Join('/', file.Split('/').Select(Uri.EscapeDataString)), lines[1]);
        Assert.StartsWith("DeletionDate=", lines[2]);
        Assert.Equal(file, TrashService.OriginalPathOf(payload));

        var (error, restored) = TrashService.Restore(file);
        Assert.Null(error);
        Assert.Equal(file, restored);
        Assert.Equal("hello", File.ReadAllText(file));
        Assert.False(File.Exists(info));
    }

    [Fact]
    public void SameNameTrashedTwiceGetsUniqueTrashNames()
    {
        string a = TestEnvironment.NewFolder("dupA");
        string b = TestEnvironment.NewFolder("dupB");
        File.WriteAllText(Path.Combine(a, "same.md"), "a");
        File.WriteAllText(Path.Combine(b, "same.md"), "b");

        Assert.Null(TrashService.Trash(Path.Combine(a, "same.md")));
        Assert.Null(TrashService.Trash(Path.Combine(b, "same.md")));

        // Each restores to its own origin with its own content.
        Assert.Null(TrashService.Restore(Path.Combine(b, "same.md")).error);
        Assert.Null(TrashService.Restore(Path.Combine(a, "same.md")).error);
        Assert.Equal("a", File.ReadAllText(Path.Combine(a, "same.md")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(b, "same.md")));
    }

    [Fact]
    public void TrashingAFolderKeepsItsContents()
    {
        string dir = TestEnvironment.NewFolder("folder");
        string folder = Path.Combine(dir, "Project");
        Directory.CreateDirectory(Path.Combine(folder, "src"));
        File.WriteAllText(Path.Combine(folder, "src", "main.c"), "int main(){}");

        Assert.Null(TrashService.Trash(folder));
        Assert.False(Directory.Exists(folder));
        Assert.Null(TrashService.Restore(folder).error);
        Assert.Equal("int main(){}", File.ReadAllText(Path.Combine(folder, "src", "main.c")));
    }

    [Fact]
    public void RestoreDoesNotOverwriteANewItemAtTheOriginalPath()
    {
        string dir = TestEnvironment.NewFolder("collide");
        string file = Path.Combine(dir, "notes.txt");
        File.WriteAllText(file, "old");
        Assert.Null(TrashService.Trash(file));
        File.WriteAllText(file, "new");

        var (error, restored) = TrashService.Restore(file);
        Assert.Null(error);
        Assert.NotEqual(file, restored);
        Assert.Equal("new", File.ReadAllText(file));
        Assert.Equal("old", File.ReadAllText(restored!));
    }

    [Fact]
    public void TrashingASymlinkMovesTheLinkNotItsTarget()
    {
        string dir = TestEnvironment.NewFolder("link");
        string target = Path.Combine(dir, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        string link = Path.Combine(dir, "shortcut");
        Directory.CreateSymbolicLink(link, target);

        Assert.Null(TrashService.Trash(link));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
        Assert.Null(TrashService.Restore(link).error);
        Assert.Equal(target, new DirectoryInfo(link).LinkTarget);
    }

    [Fact]
    public void TrashViewRestoreAndDeleteEntries()
    {
        string dir = TestEnvironment.NewFolder("view");
        string keep = Path.Combine(dir, "restore-me.txt");
        string drop = Path.Combine(dir, "drop-me.txt");
        File.WriteAllText(keep, "k");
        File.WriteAllText(drop, "d");
        Assert.Null(TrashService.Trash(keep));
        Assert.Null(TrashService.Trash(drop));

        string files = Path.Combine(TrashService.HomeTrash, "files");
        string keepPayload = Directory.GetFiles(files).Single(f => TrashService.OriginalPathOf(f) == keep);
        string dropPayload = Directory.GetFiles(files).Single(f => TrashService.OriginalPathOf(f) == drop);

        Assert.Null(TrashService.RestoreEntry(keepPayload).error);
        Assert.True(File.Exists(keep));
        Assert.Null(TrashService.DeleteEntry(dropPayload));
        Assert.False(File.Exists(dropPayload));
        Assert.False(File.Exists(Path.Combine(TrashService.HomeTrash, "info", Path.GetFileName(dropPayload) + ".trashinfo")));
    }
}
