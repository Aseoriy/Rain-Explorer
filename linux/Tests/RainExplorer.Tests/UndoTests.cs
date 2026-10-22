using Avalonia.Headless.XUnit;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

/// <summary>Undo/redo round trips (run on the Avalonia dispatcher because the activity log is UI-bound).</summary>
public sealed class UndoTests
{
    [AvaloniaFact]
    public void DeleteUndoRestoresFromTrashAndRedoTrashesAgain()
    {
        string dir = TestEnvironment.NewFolder("undoDelete");
        string file = Path.Combine(dir, "draft.txt");
        File.WriteAllText(file, "v1");
        var result = new FileOperationsService().Delete(new[] { file });
        Assert.True(result.Ok, result.Error);

        var undo = UndoService.Instance;
        undo.Clear();
        undo.Push(new RestoreFromTrashAction(result.Completed, "Delete"));

        Assert.Null(undo.Undo());
        Assert.Equal("v1", File.ReadAllText(file));
        Assert.True(undo.CanRedo);

        Assert.Null(undo.Redo());
        Assert.False(File.Exists(file));
        undo.Clear();
    }

    [AvaloniaFact]
    public void RenameUndoIsSkippedWhenTheFileWasReplaced()
    {
        string dir = TestEnvironment.NewFolder("undoRename");
        string before = Path.Combine(dir, "a.txt");
        string after = Path.Combine(dir, "b.txt");
        File.WriteAllText(before, "x");
        Assert.Null(new FileOperationsService().Rename(before, "b.txt"));
        var action = new RenameAction(after, before);

        // Another program replaces b.txt with a different file (new inode).
        File.Delete(after);
        File.WriteAllText(after, "someone else's");

        var (error, _) = action.Invoke();
        Assert.NotNull(error);
        Assert.Equal("someone else's", File.ReadAllText(after));
        Assert.False(File.Exists(before));
    }

    [AvaloniaFact]
    public void MoveUndoPutsItemsBack()
    {
        string src = TestEnvironment.NewFolder("undoMoveSrc");
        string dst = TestEnvironment.NewFolder("undoMoveDst");
        File.WriteAllText(Path.Combine(src, "m.txt"), "m");
        Assert.Null(new FileOperationsService().MoveInto(new[] { Path.Combine(src, "m.txt") }, dst));
        var action = new MoveAction(new List<(string Cur, string Home)> { (Path.Combine(dst, "m.txt"), Path.Combine(src, "m.txt")) });

        var (error, redo) = action.Invoke();
        Assert.Null(error);
        Assert.True(File.Exists(Path.Combine(src, "m.txt")));
        Assert.NotNull(redo);
    }
}
