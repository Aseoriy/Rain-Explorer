using System.Diagnostics;
using System.IO;
using System.Text;
using RainExplorer.Services;
using RainExplorer.Views;
using Xunit;

namespace RainExplorer.Git.Tests;

// Ported from the Linux GitHelpAndCommandTests; the command box runs cmd/PowerShell here instead of bash.
public sealed class GitHelpAndCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rain-cmd-" + Guid.NewGuid().ToString("N"));

    public GitHelpAndCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("Author identity unknown\n\n*** Please tell me who you are.\nfatal: unable to auto-detect email address", "SetIdentity")]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", "OpenTerminal")]
    [InlineData("git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", "OpenTerminal")]
    [InlineData(" ! [rejected]        dev -> dev (fetch first)\nerror: failed to push some refs", "OpenTerminal")]
    [InlineData("fatal: Unable to create 'C:/r/.git/index.lock': File exists.", "Refresh")]
    [InlineData("fatal: unable to access 'https://github.com/x/y/': Could not resolve host: github.com", "None")]
    public void KnownGitErrorsAreExplained(string raw, string fix)
    {
        var help = GitErrorExplainer.Explain(raw);
        Assert.NotNull(help);
        Assert.Equal(fix, help.Fix.ToString());
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

    [Theory]
    [InlineData("Ada", "ada@example.com", true)]
    [InlineData("", "ada@example.com", false)]
    [InlineData("Ada", "not-an-email", false)]
    [InlineData("Ada", "ada @example.com", false)]
    public void IdentityNeedsANameAndAPlausibleEmail(string name, string email, bool valid) =>
        Assert.Equal(valid, GitIdentityDialog.IsValid(name, email));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandBoxRunsInTheFolderAndReportsExitCodes(bool powerShell)
    {
        string command = powerShell
            ? "(Get-Location).Path; [Console]::Error.WriteLine('oops'); exit 3"
            : "cd & echo oops 1>&2 & exit /b 3";
        var output = new StringBuilder();
        int? code = await CommandRunner.RunAsync(command, _dir, t => { lock (output) output.Append(t); },
            powerShell, CancellationToken.None);
        Assert.Equal(3, code);
        Assert.Contains(Path.GetFileName(_dir), output.ToString());
        Assert.Contains("oops", output.ToString());
    }

    [Fact]
    public async Task PowerShellKeepsQuotesAndNonAsciiText()
    {
        var output = new StringBuilder();
        await CommandRunner.RunAsync("Write-Output 'a \"b\" ç ☂'", _dir, t => { lock (output) output.Append(t); },
            powerShell: true, CancellationToken.None);
        Assert.Contains("a \"b\" ç ☂", output.ToString());
    }

    [Fact]
    public async Task CmdPassesNonAsciiArgumentsToProgramsIntact()
    {
        // The command line reaches programs as Unicode, and programs started after chcp print UTF-8.
        var output = new StringBuilder();
        await CommandRunner.RunAsync("powershell -NoProfile -Command \"Write-Output 'ç ☂'\"", _dir,
            t => { lock (output) output.Append(t); }, powerShell: false, CancellationToken.None);
        Assert.Contains("ç ☂", output.ToString());
    }

    [Theory]
    [InlineData("echo one && echo two")]
    [InlineData("echo \"one & a\" & echo two")]
    [InlineData("(echo one) | findstr one && echo two")]
    public async Task CmdChainsAndPipesStillRunEveryPart(string command)
    {
        var output = new StringBuilder();
        int? code = await CommandRunner.RunAsync(command, _dir, t => { lock (output) output.Append(t); },
            powerShell: false, CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Contains("one", output.ToString());
        Assert.Contains("two", output.ToString());
    }

    [Fact]
    public async Task CommandBoxNeverWaitsForInput()
    {
        var watch = Stopwatch.StartNew();
        await CommandRunner.RunAsync("set /p line=Type:", _dir, _ => { }, powerShell: false, CancellationToken.None);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task StopKillsALongCommand()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CommandRunner.RunAsync("Start-Sleep -Seconds 30", _dir, _ => { }, powerShell: true, cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void CommandsNeverPromptForCredentials()
    {
        var start = CommandRunner.BuildStartInfo("git push", _dir, powerShell: false);
        Assert.Equal("0", start.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", start.Environment["GCM_INTERACTIVE"]);
        Assert.True(start.RedirectStandardInput);
    }
}
