using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class FileOperationsServiceTests
{
    [Fact]
    public void MissingSourceFailsWithoutReportingSuccess()
    {
        using var temp = new TemporaryDirectory();
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        string missing = Path.Combine(temp.Path, "missing.txt");

        OpResult result = new FileOperationsService().MoveIntoResult([missing], destination);

        Assert.Equal(OpOutcome.Failed, result.Outcome);
        Assert.Empty(result.Completed);
        Assert.Empty(result.Created);
        Assert.Contains("no longer exists", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateDestinationNamesFailBeforeAnythingMoves()
    {
        using var temp = new TemporaryDirectory();
        string firstDir = Directory.CreateDirectory(Path.Combine(temp.Path, "first")).FullName;
        string secondDir = Directory.CreateDirectory(Path.Combine(temp.Path, "second")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        string first = Path.Combine(firstDir, "same.txt");
        string second = Path.Combine(secondDir, "same.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");

        OpResult result = new FileOperationsService().MoveIntoResult([first, second], destination);

        Assert.Equal(OpOutcome.Failed, result.Outcome);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.False(File.Exists(Path.Combine(destination, "same.txt")));
    }

    [Fact]
    public void CopyReportsBothCompletedSourceAndNewOutput()
    {
        using var temp = new TemporaryDirectory();
        string source = Path.Combine(temp.Path, "source.txt");
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        File.WriteAllText(source, "rain");

        OpResult result = new FileOperationsService().CopyIntoResult([source], destination);
        string output = Path.Combine(destination, "source.txt");

        Assert.Equal(OpOutcome.Ok, result.Outcome);
        Assert.Contains(source, result.Completed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(output, result.Created, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("rain", File.ReadAllText(output));
    }

    [Fact]
    public void CopyConflictUsesOneReplaceAllDecision()
    {
        using var temp = new TemporaryDirectory();
        string source = Path.Combine(temp.Path, "same.txt");
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        string output = Path.Combine(destination, "same.txt");
        File.WriteAllText(source, "new");
        File.WriteAllText(output, "old");
        int decisions = 0;

        OpResult result = new FileOperationsService().CopyIntoResult([source], destination, conflicts =>
        {
            decisions++;
            Assert.Single(conflicts);
            return TransferConflictChoice.ReplaceAll;
        });

        Assert.Equal(OpOutcome.Ok, result.Outcome);
        Assert.Equal(1, decisions);
        Assert.Equal("new", File.ReadAllText(output));
        Assert.Empty(result.Created);
    }

    [Fact]
    public void CopyConflictSkipAllSkipsOnlyExistingPaths()
    {
        using var temp = new TemporaryDirectory();
        string sourceDir = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        string existingSource = Path.Combine(sourceDir, "existing.txt");
        string newSource = Path.Combine(sourceDir, "new.txt");
        string destinationDir = Path.Combine(destination, "source");
        Directory.CreateDirectory(destinationDir);
        File.WriteAllText(existingSource, "new");
        File.WriteAllText(newSource, "copied");
        File.WriteAllText(Path.Combine(destinationDir, "existing.txt"), "old");

        OpResult result = new FileOperationsService().CopyIntoResult([sourceDir], destination,
            _ => TransferConflictChoice.SkipAll);

        Assert.Equal(OpOutcome.Ok, result.Outcome);
        Assert.Equal("old", File.ReadAllText(Path.Combine(destinationDir, "existing.txt")));
        Assert.Equal("copied", File.ReadAllText(Path.Combine(destinationDir, "new.txt")));
    }

    [Fact]
    public void MoveConflictReplaceAllMergesFoldersWithoutShellPrompts()
    {
        using var temp = new TemporaryDirectory();
        string source = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(temp.Path, "destination")).FullName;
        string sourceFolder = Directory.CreateDirectory(Path.Combine(source, "project")).FullName;
        string destinationFolder = Directory.CreateDirectory(Path.Combine(destination, "project")).FullName;
        File.WriteAllText(Path.Combine(sourceFolder, "same.txt"), "new");
        File.WriteAllText(Path.Combine(sourceFolder, "new.txt"), "moved");
        File.WriteAllText(Path.Combine(destinationFolder, "same.txt"), "old");
        File.WriteAllText(Path.Combine(destinationFolder, "keep.txt"), "keep");

        OpResult result = new FileOperationsService().MoveIntoResult([sourceFolder], destination,
            _ => TransferConflictChoice.ReplaceAll);

        Assert.Equal(OpOutcome.Ok, result.Outcome);
        Assert.False(Directory.Exists(sourceFolder));
        Assert.Equal("new", File.ReadAllText(Path.Combine(destinationFolder, "same.txt")));
        Assert.Equal("moved", File.ReadAllText(Path.Combine(destinationFolder, "new.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(destinationFolder, "keep.txt")));
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
