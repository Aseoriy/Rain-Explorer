using System.Windows;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class DetachedWindowPlacementTests
{
    [Fact]
    public void DropPointIsPreservedOnASecondaryMonitor()
    {
        var workArea = new Rect(-1920, 0, 1920, 1040);

        Rect bounds = MainWindow.CalculateDetachedWindowBounds(
            workArea,
            new Size(1140, 720),
            new Point(-960, 300),
            new Point(360, 64));

        Assert.Equal(-1320, bounds.Left);
        Assert.Equal(236, bounds.Top);
    }

    [Fact]
    public void WindowIsClampedToTheDestinationMonitorWorkArea()
    {
        var workArea = new Rect(-1920, 0, 1920, 1040);

        Rect topLeft = MainWindow.CalculateDetachedWindowBounds(
            workArea,
            new Size(1140, 720),
            new Point(-1900, 20),
            new Point(360, 64));
        Rect bottomRight = MainWindow.CalculateDetachedWindowBounds(
            workArea,
            new Size(1140, 720),
            new Point(-10, 1030),
            new Point(100, 20));

        Assert.Equal(-1920, topLeft.Left);
        Assert.Equal(0, topLeft.Top);
        Assert.Equal(-1140, bottomRight.Left);
        Assert.Equal(320, bottomRight.Top);
    }
}
