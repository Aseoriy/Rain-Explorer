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
}
