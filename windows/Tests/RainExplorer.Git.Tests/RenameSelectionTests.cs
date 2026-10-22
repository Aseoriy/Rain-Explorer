using RainExplorer.Helpers;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class RenameSelectionTests
{
    [Theory]
    [InlineData("name.html", false, true, 4)]
    [InlineData("archive.tar.gz", false, true, 11)]
    [InlineData("README", false, true, 6)]
    [InlineData(".gitignore", false, true, 10)]
    [InlineData("folder.name", true, true, 11)]
    [InlineData("name.html", false, false, 9)]
    public void SelectsTheExpectedPartOfTheName(
        string name,
        bool isDirectory,
        bool excludeFileExtension,
        int expectedLength)
    {
        Assert.Equal(
            expectedLength,
            RenameSelection.GetSelectionLength(name, isDirectory, excludeFileExtension));
    }
}
