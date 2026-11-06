using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class ShellIconServiceTests
{
    [Fact]
    public void StockFolderIconCanBeResolvedOutsideShellImageList()
    {
        var icon = ShellIconService.ResolveStockFolderIcon();

        Assert.NotNull(icon);
        Assert.True(icon.PixelWidth > 0);
        Assert.True(icon.PixelHeight > 0);
    }

    [Theory]
    [InlineData("payload.exe", false)]
    [InlineData("README", false)]
    [InlineData("folder", true)]
    public void VirtualArchiveRowsResolveWindowsFileTypeIconsWithoutPhysicalPaths(
        string fileName,
        bool isDirectory)
    {
        var icon = ShellIconService.ResolveFileTypeIcon(fileName, isDirectory);

        Assert.NotNull(icon);
        Assert.True(icon.PixelWidth > 0);
        Assert.True(icon.PixelHeight > 0);
    }
}
