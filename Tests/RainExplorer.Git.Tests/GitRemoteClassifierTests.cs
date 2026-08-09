using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class GitRemoteClassifierTests
{
    [Theory]
    [InlineData("https://github.com/Aseoriy/Rain-Explorer.git", "https://github.com/Aseoriy/Rain-Explorer")]
    [InlineData("git@github.com:Aseoriy/Rain-Explorer.git", "https://github.com/Aseoriy/Rain-Explorer")]
    [InlineData("ssh://git@github.com/Aseoriy/Rain-Explorer.git", "https://github.com/Aseoriy/Rain-Explorer")]
    public void RecognizesSupportedGitHubRemoteForms(string remote, string expectedWebUrl)
    {
        Assert.True(GitRemoteClassifier.TryGetGitHubWebUrl(remote, out string? webUrl));
        Assert.Equal(expectedWebUrl, webUrl);
    }

    [Theory]
    [InlineData("https://gitlab.com/Aseoriy/Rain-Explorer.git")]
    [InlineData("git@example.test:owner/repository.git")]
    [InlineData("C:\\Repositories\\bare.git")]
    public void DoesNotClassifyOtherRemotesAsGitHub(string remote)
    {
        Assert.False(GitRemoteClassifier.TryGetGitHubWebUrl(remote, out _));
    }

    [Fact]
    public void RemovesUrlCredentialsFromDiagnostics()
    {
        const string secretUrl = "https://user:very-secret-token@github.com/owner/repository.git";

        string sanitized = GitRemoteClassifier.SanitizeUrl(secretUrl);
        string redacted = GitSecurity.Redact($"fatal: unable to access '{secretUrl}'");

        Assert.DoesNotContain("very-secret-token", sanitized);
        Assert.DoesNotContain("very-secret-token", redacted);
        Assert.Contains("github.com", sanitized);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("config/.env.production")]
    [InlineData("keys/id_rsa")]
    [InlineData("certificate.PFX")]
    [InlineData("private.key")]
    public void FlagsCommonSensitiveFileNames(string path)
    {
        Assert.True(GitPushPreflightService.IsSensitivePath(path));
    }

    [Theory]
    [InlineData("https://github.com/Aseoriy/Rain-Explorer", "Rain-Explorer")]
    [InlineData("https://github.com/Aseoriy/Rain-Explorer.git", "Rain-Explorer")]
    [InlineData("git@github.com:Aseoriy/Rain-Explorer.git", "Rain-Explorer")]
    public void GetsSafeCloneFolderName(string url, string expected)
    {
        Assert.Equal(expected, GitCloneService.RepositoryFolderName(url));
    }
}
