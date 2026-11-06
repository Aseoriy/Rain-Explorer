using System.Text;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class GitStatusParserTests
{
    [Fact]
    public void ParsesBranchTrackedRenameConflictAndSpecialEntries()
    {
        string data = string.Join('\0',
            "# branch.oid 0123456789abcdef",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +2 -1",
            "# stash 3",
            "1 M. N... 100644 100644 100644 aaaaaaa bbbbbbb src/space name.txt",
            "1 .M N... 100644 100644 100644 aaaaaaa bbbbbbb src/line\nbreak.txt",
            "2 R. N... 100644 100644 100644 aaaaaaa bbbbbbb R100 new-name.txt",
            "old-name.txt",
            "u UU N... 100644 100644 100644 100644 aaaaaaa bbbbbbb ccccccc conflict.txt",
            "? -leading-dash.txt",
            "! obj/",
            "");

        GitStatusParseResult result = GitStatusParser.Parse(Encoding.UTF8.GetBytes(data));

        Assert.Equal("main", result.Branch.Name);
        Assert.Equal("origin/main", result.Branch.Upstream);
        Assert.Equal(2, result.Branch.Ahead);
        Assert.Equal(1, result.Branch.Behind);
        Assert.Equal(3, result.StashCount);
        Assert.Contains(result.Files, file => file.Path == "src/space name.txt" && file.IsStaged);
        Assert.Contains(result.Files, file => file.Path == "src/line\nbreak.txt" && file.IsUnstaged);
        Assert.Contains(result.Files, file =>
            file.Path == "new-name.txt" && file.OriginalPath == "old-name.txt");
        Assert.Contains(result.Files, file => file.Path == "conflict.txt" && file.IsConflict);
        Assert.Contains(result.Files, file => file.Path == "-leading-dash.txt" && file.IsUntracked);
        Assert.Contains(result.Files, file => file.Path == "obj/" && file.IsIgnored);
    }

    [Fact]
    public void ParsesUnbornAndDetachedBranches()
    {
        GitStatusParseResult unborn = Parse("# branch.oid (initial)", "# branch.head main");
        GitStatusParseResult detached = Parse("# branch.oid abcdef", "# branch.head (detached)");

        Assert.True(unborn.Branch.IsUnborn);
        Assert.Equal("main", unborn.Branch.Name);
        Assert.Null(unborn.Branch.ObjectId);
        Assert.True(detached.Branch.IsDetached);
        Assert.Null(detached.Branch.Name);
    }

    [Fact]
    public void CapsRenderedRowsWithoutLosingTruncationSignal()
    {
        GitStatusParseResult result = Parse("? one", "? two", "? three", maxRows: 2);

        Assert.Equal(2, result.Files.Count);
        Assert.True(result.IsTruncated);
    }

    private static GitStatusParseResult Parse(
        string first, string second, string? third = null, int maxRows = 100)
    {
        string data = string.Join('\0', new[] { first, second, third, "" }
            .Where(value => value is not null));
        return GitStatusParser.Parse(Encoding.UTF8.GetBytes(data), maxRows);
    }
}
