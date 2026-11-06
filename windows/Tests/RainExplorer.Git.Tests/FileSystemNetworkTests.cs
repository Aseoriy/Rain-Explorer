using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class FileSystemNetworkTests
{
    [Fact]
    public async Task ReadDirectory_ServerRootUsesInjectedShareProvider()
    {
        int calls = 0;
        var expected = new FileItem
        {
            Name = "Public",
            FullPath = @"\\server\Public",
            IsDirectory = true,
            IsNetworkShare = true,
        };
        var fs = new FileSystemService((path, showHidden, ct) =>
        {
            calls++;
            Assert.Equal(@"\\server", path);
            return [expected];
        });

        IReadOnlyList<FileItem> actual = await fs.ReadDirectoryAsync(@"\\\server\");

        Assert.Equal(1, calls);
        Assert.Same(expected, Assert.Single(actual));
    }

    [Fact]
    public async Task ReadDirectory_CancelledNetworkWaitReturnsBeforeProviderUnblocks()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fs = new FileSystemService((path, showHidden, ct) =>
        {
            started.SetResult(true);
            release.Task.GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();
            return [];
        });
        using var cancellation = new CancellationTokenSource();
        Task<IReadOnlyList<FileItem>> read = fs.ReadDirectoryAsync(@"\\server", cancellation.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                read.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            release.TrySetResult(true);
        }
    }
}
