using System.ComponentModel;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class NetworkConnectionServiceTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(86)]
    [InlineData(1326)]
    public void RequiresSignIn_RecognizesCredentialAndAccessErrors(int code) =>
        Assert.True(NetworkConnectionService.RequiresSignIn(new Win32Exception(code)));

    [Theory]
    [InlineData(53)]
    [InlineData(1219)]
    [InlineData(1223)]
    public void RequiresSignIn_DoesNotTreatOtherNetworkResultsAsCredentialPrompts(int code) =>
        Assert.False(NetworkConnectionService.RequiresSignIn(new Win32Exception(code)));

    [Fact]
    public void RequiresSignIn_TreatsUnauthorizedAccessAsCredentialPrompt() =>
        Assert.True(NetworkConnectionService.RequiresSignIn(new UnauthorizedAccessException()));
}
