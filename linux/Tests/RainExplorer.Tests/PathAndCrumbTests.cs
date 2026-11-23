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

    [Fact]
    public async Task FolderNamesWithEdgeSpacesStayReal()
    {
        string root = Path.Combine(Path.GetTempPath(), "rain-space-" + Guid.NewGuid().ToString("N"));
        string spaced = Path.Combine(root, "Season 01 ");
        string leading = Path.Combine(root, " Extras");
        Directory.CreateDirectory(spaced);
        Directory.CreateDirectory(leading);
        File.WriteAllText(Path.Combine(spaced, "ep1.mkv"), "x");
        try
        {
            Assert.Equal(spaced, PathUtil.Normalize(spaced));
            Assert.Equal(leading, PathUtil.Normalize(leading));
            Assert.False(PathUtil.Same(spaced, Path.Combine(root, "Season 01")));
            var items = await new FileSystemService().ReadDirectoryAsync(PathUtil.Normalize(spaced));
            Assert.Contains(items, i => i.Name == "ep1.mkv");
        }
        finally { Directory.Delete(root, true); }
    }

}
