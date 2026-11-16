using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

public sealed class GitHelpAndCommandTests
{
    [Theory]
    [InlineData("Author identity unknown\n\n*** Please tell me who you are.\nfatal: unable to auto-detect email address", GitErrorFix.SetIdentity)]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", GitErrorFix.OpenTerminal)]
    [InlineData("git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", GitErrorFix.OpenTerminal)]
    [InlineData(" ! [rejected]        dev -> dev (fetch first)\nerror: failed to push some refs", GitErrorFix.OpenTerminal)]
    [InlineData("fatal: Unable to create '/r/.git/index.lock': File exists.", GitErrorFix.Refresh)]
    [InlineData("fatal: unable to access 'https://github.com/x/y/': Could not resolve host: github.com", GitErrorFix.None)]
    public void KnownGitErrorsAreExplained(string raw, GitErrorFix fix)
    {
        var help = GitErrorExplainer.Explain(raw);
        Assert.NotNull(help);
        Assert.Equal(fix, help.Fix);
        Assert.False(string.IsNullOrWhiteSpace(help.Title));
        Assert.Equal(raw.Trim(), help.Details);
    }

    [Fact]
    public void PublicKeyBeatsTheGenericRemoteError()
    {
        var help = GitErrorExplainer.Explain("Permission denied (publickey).\nfatal: Could not read from remote repository.");
        Assert.Equal("Your SSH key wasn't accepted", help?.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Committed locally. Nothing was pushed.")]
    [InlineData("3 changed entries · 1 staged")]
    public void OrdinaryMessagesAreLeftAlone(string? message) => Assert.Null(GitErrorExplainer.Explain(message));

    [Fact]
    public async Task CommandBoxRunsInTheFolderAndReportsExitCodes()
    {
        string dir = TestEnvironment.NewFolder("command-box");
        var output = new System.Text.StringBuilder();
        int? code = await CommandRunner.RunAsync("pwd; echo oops >&2; exit 3", dir, t => { lock (output) output.Append(t); },
            CancellationToken.None);
        Assert.Equal(3, code);
        Assert.Contains(dir, output.ToString());
        Assert.Contains("oops", output.ToString());
    }

    [Fact]
    public async Task CommandBoxNeverWaitsForInput()
    {
        string dir = TestEnvironment.NewFolder("command-box-stdin");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int? code = await CommandRunner.RunAsync("read line", dir, _ => { }, CancellationToken.None);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(1, code);   // read hits end of input immediately
    }

    [Fact]
    public async Task StopKillsALongCommand()
    {
        string dir = TestEnvironment.NewFolder("command-box-stop");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CommandRunner.RunAsync("sleep 30", dir, _ => { }, cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }
}
