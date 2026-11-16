using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class ArchiveContextMenuServiceTests
{
    private const string Exe = @"C:\Program Files\Rain Explorer\RainExplorer.exe";

    [Fact]
    public void RegisterWritesOneOwnedCascadingMenuPerTarget()
    {
        var registry = new FakeRegistry();

        Assert.True(ArchiveContextMenuService.Register(registry, Exe));

        string zip = ArchiveContextMenuService.ArchiveMenuPath(".zip");
        Assert.Equal("Rain Archives", registry.GetString(zip, "MUIVerb"));
        Assert.Equal(string.Empty, registry.GetString(zip, "SubCommands"));
        Assert.Equal(ArchiveContextMenuService.OwnerValue,
            registry.GetString(zip, ArchiveContextMenuService.OwnerValueName));
        Assert.Equal($"\"{Exe}\" --rain-archives test \"%1\"",
            registry.GetString($@"{zip}\shell\40test\command", null));
        Assert.Equal($"\"{Exe}\" --rain-archives extract-new \"%1\"",
            registry.GetString($@"{zip}\shell\30extractnew\command", null));
        Assert.True(ArchiveContextMenuService.IsRegistered(registry));
    }

    [Fact]
    public void NoExtractHereEntryExists()
    {
        var registry = new FakeRegistry();
        ArchiveContextMenuService.Register(registry, Exe);
        Assert.DoesNotContain(registry.Values.Keys, key =>
            key.Path.Contains("here", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OtherFilesAndFoldersOnlyGetAddAndSkipArchives()
    {
        var registry = new FakeRegistry();
        ArchiveContextMenuService.Register(registry, Exe);

        string files = ArchiveContextMenuService.OtherFilesMenuPath;
        Assert.Equal($"\"{Exe}\" --rain-archives add \"%1\"",
            registry.GetString($@"{files}\shell\50add\command", null));
        Assert.False(registry.KeyExists($@"{files}\shell\40test"));
        Assert.Contains("System.FileExtension:=\".zip\"", registry.GetString(files, "AppliesTo"));
        Assert.StartsWith("NOT (", registry.GetString(files, "AppliesTo"));
        Assert.True(registry.KeyExists($@"{ArchiveContextMenuService.FolderMenuPath}\shell\50add\command"));
    }

    [Fact]
    public void ForeignKeyWithTheSameNameIsNeverOverwrittenOrRemoved()
    {
        var registry = new FakeRegistry();
        string zip = ArchiveContextMenuService.ArchiveMenuPath(".zip");
        registry.SetString(zip, "MUIVerb", "Someone else");

        Assert.False(ArchiveContextMenuService.Register(registry, Exe));
        Assert.Equal("Someone else", registry.GetString(zip, "MUIVerb"));

        Assert.False(ArchiveContextMenuService.Unregister(registry));
        Assert.Equal("Someone else", registry.GetString(zip, "MUIVerb"));
        Assert.False(registry.KeyExists(ArchiveContextMenuService.ArchiveMenuPath(".7z")));
    }

    [Fact]
    public void UnregisterRemovesEverythingRainWrote()
    {
        var registry = new FakeRegistry();
        ArchiveContextMenuService.Register(registry, Exe);

        Assert.True(ArchiveContextMenuService.Unregister(registry));
        Assert.Empty(registry.Values);
    }

    [Fact]
    public void ReRegisteringAfterTheExeMovedLeavesNoStaleCommand()
    {
        var registry = new FakeRegistry();
        ArchiveContextMenuService.Register(registry, @"C:\Old\RainExplorer.exe");
        ArchiveContextMenuService.Register(registry, Exe);

        Assert.DoesNotContain(registry.Values.Values, value => value.Contains(@"C:\Old\"));
    }

    [Theory]
    [InlineData("open", "Open")]
    [InlineData("ADD", "Add")]
    [InlineData("extract-new", "ExtractToNewFolder")]
    [InlineData("extract-to", "ExtractTo")]
    [InlineData("test", "Test")]
    public void CommandLineRoundTrips(string verbText, string expected)
    {
        Assert.True(ArchiveContextMenuService.TryParseCommandLine(
            new[] { "--rain-archives", verbText, @"C:\Data\photos.zip" }, out var verb, out string path));
        Assert.Equal(expected, verb.ToString());
        Assert.Equal(@"C:\Data\photos.zip", path);
    }

    [Theory]
    [InlineData("--rain-archives", "explode", @"C:\a.zip")]
    [InlineData("--rain-archives", "open", @"relative\a.zip")]
    [InlineData(@"C:\a.zip", "", "")]
    public void BadCommandLinesAreNotMenuCommands(string a, string b, string c) =>
        Assert.False(ArchiveContextMenuService.TryParseCommandLine(new[] { a, b, c }, out _, out _));

    [Fact]
    public void PerFileLaunchesAreGroupedPerVerbWithoutDuplicates()
    {
        var batches = App.ArchiveMenuBatches(new[]
        {
            (ArchiveMenuVerb.Add, @"C:\x\a.txt"),
            (ArchiveMenuVerb.Add, @"C:\x\b.txt"),
            (ArchiveMenuVerb.Test, @"C:\x\c.zip"),
            (ArchiveMenuVerb.Add, @"C:\X\A.TXT"),
        });

        Assert.Equal(2, batches.Count);
        Assert.Equal(ArchiveMenuVerb.Add, batches[0].Verb);
        Assert.Equal(new[] { @"C:\x\a.txt", @"C:\x\b.txt" }, batches[0].Paths);
        Assert.Equal(new[] { @"C:\x\c.zip" }, batches[1].Paths);
    }

    private sealed class FakeRegistry : IContextMenuRegistry
    {
        public Dictionary<(string Path, string Name), string> Values { get; } = new();
        private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

        public bool KeyExists(string path) => _keys.Contains(path);

        public string? GetString(string path, string? name) =>
            Values.TryGetValue((path.ToLowerInvariant(), name ?? string.Empty), out string? value) ? value : null;

        public void SetString(string path, string? name, string value)
        {
            for (string? key = path; !string.IsNullOrEmpty(key); key = Parent(key)) _keys.Add(key);
            Values[(path.ToLowerInvariant(), name ?? string.Empty)] = value;
        }

        public void DeleteTree(string path)
        {
            string prefix = path.ToLowerInvariant();
            foreach (var key in Values.Keys.Where(k => k.Path == prefix || k.Path.StartsWith(prefix + @"\")).ToList())
                Values.Remove(key);
            _keys.RemoveWhere(k => k.Equals(path, StringComparison.OrdinalIgnoreCase)
                || k.StartsWith(path + @"\", StringComparison.OrdinalIgnoreCase));
        }

        private static string? Parent(string path)
        {
            int slash = path.LastIndexOf('\\');
            return slash < 0 ? null : path[..slash];
        }
    }
}
