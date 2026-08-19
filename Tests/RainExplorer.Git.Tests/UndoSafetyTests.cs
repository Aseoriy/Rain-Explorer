using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class UndoSafetyTests
{
    [Fact]
    public void RecycleUndoRefusesAReplacementAtTheSamePath()
    {
        using var temp = new TemporaryDirectory();
        string target = Path.Combine(temp.Path, "copied.txt");
        File.WriteAllText(target, "original");
        var action = new RecycleAction([target], "Copy");

        ReplaceWithDifferentFile(target, "replacement");
        (string? error, UndoAction? redo) = action.Invoke();

        Assert.NotNull(error);
        Assert.Null(redo);
        Assert.Equal("replacement", File.ReadAllText(target));
    }

    [Fact]
    public void MoveUndoRefusesAReplacementAtTheDestination()
    {
        using var temp = new TemporaryDirectory();
        string current = Path.Combine(temp.Path, "destination.txt");
        string home = Path.Combine(temp.Path, "original.txt");
        File.WriteAllText(current, "moved");
        var action = new MoveAction([(current, home)]);

        ReplaceWithDifferentFile(current, "replacement");
        (string? error, UndoAction? redo) = action.Invoke();

        Assert.NotNull(error);
        Assert.Null(redo);
        Assert.Equal("replacement", File.ReadAllText(current));
        Assert.False(File.Exists(home));
    }

    private static void ReplaceWithDifferentFile(string target, string contents)
    {
        string replacement = target + ".replacement";
        File.WriteAllText(replacement, contents);
        File.Move(replacement, target, overwrite: true);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "RainExplorerTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
