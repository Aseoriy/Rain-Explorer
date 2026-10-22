using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class FileDropServiceTests
{
    [Fact]
    public void Incoming_AllowsDeeperDescendantToMoveToAncestor()
    {
        string root = NewTempDirectory();
        try
        {
            string destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
            string nested = Directory.CreateDirectory(Path.Combine(destination, "nested")).FullName;
            string file = Path.Combine(nested, "move-me.txt");
            File.WriteAllText(file, "test");

            Assert.Equal([file], FileDropService.Incoming([file], destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Incoming_RejectsItemAlreadyDirectlyInsideDestination()
    {
        string root = NewTempDirectory();
        try
        {
            string destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
            string file = Path.Combine(destination, "already-here.txt");
            File.WriteAllText(file, "test");

            Assert.Empty(FileDropService.Incoming([file], destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Incoming_RejectsDirectoryThatContainsDestination()
    {
        string root = NewTempDirectory();
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            string destination = Directory.CreateDirectory(Path.Combine(source, "destination")).FullName;

            Assert.Empty(FileDropService.Incoming([source], destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "RainExplorer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
