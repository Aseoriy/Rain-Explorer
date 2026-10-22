using System.Diagnostics;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class GitProcessRunnerTests
{
    [Fact]
    public async Task CancellationTerminatesTheProcessTree()
    {
        var runner = new GitProcessRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var watch = Stopwatch.StartNew();

        GitProcessResult result = await runner.RunAsync(
            new GitProcessRequest(
                "cmd.exe",
                Path.GetTempPath(),
                ["/d", "/c", "ping", "-n", "30", "127.0.0.1"]),
            cancellation.Token);

        Assert.True(result.WasCanceled);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5),
            $"Cancellation took {watch.Elapsed}.");
    }
}
