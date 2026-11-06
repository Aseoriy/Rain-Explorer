using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class NetworkPathTests
{
    [Theory]
    [InlineData(@"\\server", "server")]
    [InlineData(@"\\server\Public", "Public")]
    [InlineData(@"\\server\Public\Folder\", "Folder")]
    [InlineData(@"C:\Folder", "Folder")]
    public void DisplayNamesIncludeUncShareRoots(string path, string expected) =>
        Assert.Equal(expected, NetworkPath.GetDisplayName(path));

    [Theory]
    [InlineData(@"\\\192.168.1.89", @"\\192.168.1.89")]
    [InlineData(@"//server/Share/", @"\\server\Share")]
    [InlineData(@"  \\HOST\Public\Folder  ", @"\\HOST\Public\Folder")]
    public void Normalize_CanonicalizesUncSeparatorsAndTrailingSlashes(string input, string expected) =>
        Assert.Equal(expected, NetworkPath.Normalize(input));

    [Theory]
    [InlineData(@"\\192.168.1.89")]
    [InlineData(@"\\server")]
    [InlineData(@"\\HOST\Public")]
    public void IsNetworkPath_RecognizesServerAndSharePaths(string path) =>
        Assert.True(NetworkPath.IsNetworkPath(path));

    [Theory]
    [InlineData(@"C:\folder")]
    [InlineData(@"\\?\C:\folder")]
    [InlineData(@"\\.\pipe\name")]
    [InlineData(@"\\server\share\..\escape")]
    public void IsNetworkPath_RejectsLocalDeviceAndInvalidTraversalPaths(string path) =>
        Assert.False(NetworkPath.IsNetworkPath(path));

    [Fact]
    public void IsNetworkPath_NormalizesForwardSlashesInsideUncPaths() =>
        Assert.True(NetworkPath.IsNetworkPath(@"//server/share/child/name"));

    [Fact]
    public void ServerAndShareRoots_ReturnCanonicalHierarchy()
    {
        Assert.True(NetworkPath.TryGetServerRoot(@"\\\192.168.1.89\", out string server));
        Assert.Equal(@"\\192.168.1.89", server);
        Assert.True(NetworkPath.TryGetShareRoot(@"\\192.168.1.89\Public\", out string share));
        Assert.Equal(@"\\192.168.1.89\Public", share);

        Assert.Null(NetworkPath.GetParent(server));
        Assert.Equal(server, NetworkPath.GetParent(share));
        Assert.Equal(share, NetworkPath.GetParent(share + @"\Folder"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("bad/name")]
    [InlineData("bad\\name")]
    public void InvalidShareNames_AreRejected(string? name) =>
        Assert.False(NetworkPath.IsValidShareName(name));

}
