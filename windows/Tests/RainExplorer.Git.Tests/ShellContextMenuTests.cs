using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class ShellContextMenuTests
{
    [Fact]
    public void RainOwnedOpenWithIsRemovedEvenWhenWindowsExposesItAsASubmenu()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.False(ShellContextMenu.ShouldIncludeTopLevelItem("Open with…", null, seen));
        Assert.False(ShellContextMenu.ShouldIncludeTopLevelItem("Open with", "openas", seen));
        Assert.Empty(seen);
    }

    [Fact]
    public void DuplicateThirdPartyTopLevelLabelsKeepOnlyTheFirstRegistration()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(ShellContextMenu.ShouldIncludeTopLevelItem("WinRAR", null, seen));
        Assert.False(ShellContextMenu.ShouldIncludeTopLevelItem("winrar", null, seen));
        Assert.True(ShellContextMenu.ShouldIncludeTopLevelItem("Send to", null, seen));
    }

    [Fact]
    public void SameLabelFromUnrelatedCommandsIsNotGloballyDeduplicated()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(ShellContextMenu.ShouldIncludeTopLevelItem("Provider command", "provider.one", seen));
        Assert.True(ShellContextMenu.ShouldIncludeTopLevelItem("Provider command", "provider.two", seen));
        Assert.Empty(seen);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("copy")]
    [InlineData("properties")]
    public void CanonicalRainOwnedVerbsDoNotReappearInShowMoreOptions(string verb)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.False(ShellContextMenu.ShouldIncludeTopLevelItem("Localized label", verb, seen));
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("explore")]
    [InlineData("find")]
    [InlineData("copyto")]
    [InlineData("cutto")]
    public void ShellCommandsWithoutRainOwnedReplacementRemainVisible(string verb)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(ShellContextMenu.ShouldIncludeTopLevelItem("Shell command", verb, seen));
    }
}
