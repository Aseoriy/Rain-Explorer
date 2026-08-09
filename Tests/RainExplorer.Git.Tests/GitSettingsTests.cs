using System.Text.Json;
using RainExplorer.Models;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class GitSettingsTests
{
    [Fact]
    public void AutomaticFolderRefreshesDefaultToEnabledAndRoundTrip()
    {
        var defaults = new AppSettings();
        Assert.True(defaults.AutoRefreshFolders);

        AppSettings disabled = Assert.IsType<AppSettings>(
            JsonSerializer.Deserialize<AppSettings>("{\"AutoRefreshFolders\":false}"));
        Assert.False(disabled.AutoRefreshFolders);
    }

    [Fact]
    public void PreservesUnknownSettingsWithoutPersistingTokens()
    {
        const string json =
            """
            {
              "GitIntegrationEnabled": true,
              "FutureGitOption": { "Mode": "careful" },
              "GitHubAccounts": [
                {
                  "AccountId": 42,
                  "Login": "example",
                  "AccessCredentialTarget": "RainExplorer:GitHub:github.com:42:access"
                }
              ]
            }
            """;

        AppSettings settings = Assert.IsType<AppSettings>(
            JsonSerializer.Deserialize<AppSettings>(json));
        string saved = JsonSerializer.Serialize(settings);

        Assert.Contains("FutureGitOption", saved);
        Assert.Contains("AccessCredentialTarget", saved);
        Assert.DoesNotContain("\"AccessToken\":", saved);
        Assert.DoesNotContain("\"RefreshToken\":", saved);
    }
}
