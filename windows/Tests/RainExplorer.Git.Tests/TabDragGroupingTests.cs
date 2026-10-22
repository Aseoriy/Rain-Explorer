using RainExplorer.Controls;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class TabDragGroupingTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData("", "", false)]
    [InlineData("group-a", null, false)]
    [InlineData("group-a", "group-b", false)]
    [InlineData("group-a", "group-a", true)]
    public void TabsOnlyShareAGroupWhenBothHaveTheSameNonEmptyId(
        string? firstGroupId, string? secondGroupId, bool expected)
    {
        Assert.Equal(expected, PaneView.TabsShareGroup(firstGroupId, secondGroupId));
    }

    [Theory]
    [InlineData(100, 100, 200, 100, false)]
    [InlineData(129, 100, 200, 100, false)]
    [InlineData(130, 100, 200, 100, true)]
    [InlineData(148, 100, 200, 100, true)]
    [InlineData(149, 100, 200, 100, false)]
    [InlineData(70, 100, 0, 100, true)]
    [InlineData(52, 100, 0, 100, true)]
    [InlineData(51, 100, 0, 100, false)]
    [InlineData(50, 100, 0, 100, false)]
    [InlineData(135, 100, 200, 200, true)]
    public void GroupHoverOnlyUsesTheShallowBandBetweenFacingEdges(
        double draggedLeft, double draggedWidth,
        double targetLeft, double targetWidth, bool expected)
    {
        Assert.Equal(expected, PaneView.IsInsideFacingTabEdgeBand(
            draggedLeft, draggedWidth, targetLeft, targetWidth));
    }
}
