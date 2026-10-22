using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

public sealed class PathAndCrumbTests
{
    [Fact]
    public void PathsAreCaseSensitive()
    {
        Assert.False(PathUtil.Same("/tmp/Photos", "/tmp/photos"));
        Assert.True(PathUtil.Same("/tmp/photos/", "/tmp/photos"));
    }

    [Theory]
    [InlineData("/", "/a", true)]
    [InlineData("/a", "/a/b", true)]
    [InlineData("/a", "/ab", false)]
    [InlineData("/a/b", "/a", false)]
    [InlineData("/a", "/a", false)]
    public void StrictlyInside(string parent, string child, bool expected) =>
        Assert.Equal(expected, PathUtil.IsStrictlyInside(parent, child));

    [Theory]
    [InlineData("a/b", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    [InlineData("name with spaces & :colons*?", true)]
    [InlineData(".hidden", true)]
    public void FileNameValidation(string name, bool valid) =>
        Assert.Equal(valid, PathUtil.IsValidFileName(name));

    [Fact]
    public void TildeExpandsToHome()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, PathUtil.ExpandTilde("~"));
        Assert.Equal(home + "/Music", PathUtil.ExpandTilde("~/Music"));
        Assert.Equal("~user/x", PathUtil.ExpandTilde("~user/x"));
        Assert.Equal("~/Music", PathUtil.Tildify(home + "/Music"));
    }

}
