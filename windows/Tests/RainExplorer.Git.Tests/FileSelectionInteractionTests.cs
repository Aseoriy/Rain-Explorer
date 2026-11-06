using RainExplorer.Controls;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class FileSelectionInteractionTests
{
    [Theory]
    [InlineData(100, true)]
    [InlineData(300, true)]
    [InlineData(315, true)]
    [InlineData(340, false)]
    public void BlankTailAfterLastGridColumnIsNotPartOfFileRow(double clickX, bool expected)
    {
        Assert.Equal(expected, PaneView.IsWithinFileColumns(clickX, [100, 100, 100]));
    }
}
