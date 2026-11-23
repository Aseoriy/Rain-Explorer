using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Input;
using RainExplorer.Controls;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

/// <summary>
/// The 2.0 stable features ported from Linux: batch rename, settings backups, customizable shortcuts and the
/// command box's "cd". None of these tests read or write the real settings file.
/// </summary>
public sealed class BatchRenameTests
{
    private static string Name(string original, BatchRenameOptions o, bool dir = false, int n = 1) =>
        BatchRenameService.Apply(original, dir, o, o.UseRegex && o.Find.Length > 0
            ? new Regex(o.Find, o.MatchCase ? default : RegexOptions.IgnoreCase)
            : null, n);

    [Theory]
    [InlineData("IMG_001.JPG", "IMG_", "Holiday ", false, "Holiday 001.JPG")]
    [InlineData("img_001.jpg", "IMG_", "", false, "001.jpg")]          // ignore case by default
    [InlineData("img_001.jpg", "IMG_", "", true, "img_001.jpg")]       // match case
    [InlineData("report.pdf.pdf", ".pdf", "", false, "report.pdf")]    // the extension itself is kept
    public void FindAndReplace(string original, string find, string replace, bool matchCase, string expected) =>
        Assert.Equal(expected, Name(original, new() { Find = find, Replace = replace, MatchCase = matchCase }));

    [Fact]
    public void RegexCaseNumbersAndExtension()
    {
        Assert.Equal("2024-05 trip.png", Name("trip 2024-05.png", new() { Find = @"^(.*) (\d{4}-\d{2})$", Replace = "$2 $1", UseRegex = true }));
        Assert.Equal("my holiday.jpg", Name("My HOLIDAY.JPG", new() { Case = NameCase.Lower }));
        Assert.Equal("My Holiday.jpg", Name("my HOLIDAY.jpg", new() { Case = NameCase.Title }));
        Assert.Equal("007 - song.mp3", Name("song.mp3", new() { Numbering = Numbering.Before, StartAt = 7, Digits = 3, Separator = " - " }, n: 7));
        Assert.Equal("song_2.mp3", Name("song.mp3", new() { Numbering = Numbering.After, Separator = "_" }, n: 2));
        Assert.Equal("notes.md", Name("notes.txt", new() { Extension = "md" }));
        Assert.Equal("notes", Name("notes.txt", new() { Extension = "" }));
        Assert.Equal("Folder.v2", Name("Folder.v2", new() { Extension = "md" }, dir: true));   // folders keep their name
    }

    [Theory]
    [InlineData("a:b.txt")]
    [InlineData("what?.txt")]
    [InlineData("CON.txt")]
    [InlineData("nul")]
    [InlineData("ends with a dot.")]
    public void WindowsOnlyBadNamesAreFlagged(string name) => Assert.NotNull(BatchRenameService.Check(name));

    [Fact]
    public void ClashesIgnoreCaseLikeWindows()
    {
        using var temp = new TemporaryDirectory();
        foreach (string f in new[] { "a.txt", "b.txt", "Keep.txt" }) File.WriteAllText(Path.Combine(temp.Path, f), f);
        var items = new[] { "a.txt", "b.txt" }.Select(f => (Path.Combine(temp.Path, f), false)).ToList();

        var same = BatchRenameService.Plan(items, new() { Find = "a", Replace = "B" });   // a.txt -> B.txt while b.txt stays
        Assert.Contains("Two items", same[0].Problem);

        var taken = BatchRenameService.Plan(items.Take(1).ToList(), new() { Find = "a", Replace = "keep" });
        Assert.Contains("already exists", taken[0].Problem);

        var bad = BatchRenameService.Plan(items, new() { Find = "a", Replace = "x/y" });
        Assert.NotNull(bad[0].Problem);
        Assert.Null(bad[1].Problem);

        var regex = BatchRenameService.Plan(items, new() { Find = "(", UseRegex = true });
        Assert.All(regex, p => Assert.Contains("pattern", p.Problem));
        var refused = BatchRenameService.Execute(regex);
        Assert.NotNull(refused.Error);
        Assert.Empty(refused.Done);
    }

    [Fact]
    public void CaseOnlyRenamesWork()
    {
        using var temp = new TemporaryDirectory();
        string file = Path.Combine(temp.Path, "photo.jpg");
        File.WriteAllText(file, "x");
        var plan = BatchRenameService.Plan([(file, false)], new() { Case = NameCase.Upper });
        Assert.Null(plan[0].Problem);
        Assert.Equal("PHOTO.JPG", plan[0].NewName);

        var (done, error) = BatchRenameService.Execute(plan);
        Assert.Null(error);
        Assert.Single(done);
        Assert.Equal("PHOTO.JPG", Path.GetFileName(Directory.GetFiles(temp.Path).Single()));
    }

    [Fact]
    public void SwappingNamesWorksAndUndoPutsThemBack()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "A");
        File.WriteAllText(Path.Combine(temp.Path, "b.txt"), "B");
        var plan = new List<RenamePlanItem>
        {
            new(Path.Combine(temp.Path, "a.txt"), "a.txt", "b.txt", false, null),
            new(Path.Combine(temp.Path, "b.txt"), "b.txt", "a.txt", false, null),
        };

        var (done, error) = BatchRenameService.Execute(plan);
        Assert.Null(error);
        Assert.Equal(2, done.Count);
        Assert.Equal("B", File.ReadAllText(Path.Combine(temp.Path, "a.txt")));
        Assert.Equal("A", File.ReadAllText(Path.Combine(temp.Path, "b.txt")));
        Assert.DoesNotContain(Directory.GetFiles(temp.Path), f => Path.GetFileName(f).StartsWith(".rain-rename-"));

        var (undoError, redo) = new BatchRenameAction(done).Invoke();
        Assert.Null(undoError);
        Assert.Equal("A", File.ReadAllText(Path.Combine(temp.Path, "a.txt")));
        Assert.NotNull(redo);
        Assert.Null(redo!.Invoke().error);
        Assert.Equal("B", File.ReadAllText(Path.Combine(temp.Path, "a.txt")));
    }

    [Fact]
    public void UndoRefusesWhenSomethingTookAnOriginalName()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "one.txt"), "1");
        var plan = BatchRenameService.Plan([(Path.Combine(temp.Path, "one.txt"), false)], new() { Find = "one", Replace = "uno" });
        var (done, _) = BatchRenameService.Execute(plan);
        File.WriteAllText(Path.Combine(temp.Path, "one.txt"), "new file");

        var (error, _) = new BatchRenameAction(done).Invoke();
        Assert.NotNull(error);
        Assert.Equal("1", File.ReadAllText(Path.Combine(temp.Path, "uno.txt")));
        Assert.Equal("new file", File.ReadAllText(Path.Combine(temp.Path, "one.txt")));
    }
}

public sealed class SettingsBackupTests
{
    private static AppSettings Sample()
    {
        var s = new AppSettings { Theme = "Ocean", ShowHiddenFiles = true };
        s.Pinned.Add(new PinnedItem { Name = "Projects", Path = @"C:\Users\me\Projects" });
        s.Pinned.Add(new PinnedItem { Name = "Notes", Path = @"C:\Users\me\Notes" });
        s.CustomGroups.Add(new SidebarGroup { Name = "Work", Items = [new PinnedItem { Name = "Docs", Path = @"D:\Docs" }] });
        s.TabProjects.Add(new TabProject { Name = "Site", Tabs = [new TabProjectTab { Target = @"D:\Site" }] });
        s.ShortcutOverrides["tab.new"] = "Ctrl+K";
        return s;
    }

    [Fact]
    public void BackupRoundTripsEverything()
    {
        using var config = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(config.Path, "sorts.json"), """{ "D:\\Site": { "Key": "Size", "Dir": -1 } }""");
        string json = SettingsBackupService.Create(Sample(), config.Path);

        var (settingsJson, sortsJson, summary) = SettingsBackupService.Read(json);
        Assert.Equal((2, 1, 1, 1), (summary.Pins, summary.Lists, summary.Projects, summary.Shortcuts));
        Assert.Contains("2 pinned places, 1 sidebar list, 1 tab project and 1 custom shortcut", SettingsBackupService.Describe(summary));
        Assert.Contains("Size", sortsJson);

        using var target = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(target.Path, "settings.json"), """{ "Theme": "Violet" }""");
        SettingsBackupService.WriteRestore(target.Path, settingsJson, sortsJson);
        Assert.Contains("Violet", File.ReadAllText(Path.Combine(target.Path, "settings.json" + SettingsBackupService.BeforeRestoreSuffix)));
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            File.ReadAllText(Path.Combine(target.Path, "settings.json")), SettingsJson.Options)!;
        Assert.Equal("Ocean", restored.Theme);
        Assert.True(restored.ShowHiddenFiles);
        Assert.Equal(["Projects", "Notes"], restored.Pinned.Select(p => p.Name));
        Assert.Equal(@"D:\Docs", restored.CustomGroups.Single().Items.Single().Path);
        Assert.Equal(@"D:\Site", restored.TabProjects.Single().Tabs.Single().Target);
        Assert.Equal("Ctrl+K", restored.ShortcutOverrides["tab.new"]);
        Assert.Contains("Size", File.ReadAllText(Path.Combine(target.Path, "sorts.json")));
        Assert.Empty(Directory.GetFiles(target.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("not json at all", "isn't valid JSON")]
    [InlineData("""{ "Theme": "Violet" }""", "isn't a Rain Explorer backup")]
    [InlineData("""{ "format": "rain-explorer-backup", "formatVersion": 99, "settings": {} }""", "newer Rain Explorer")]
    [InlineData("""{ "format": "rain-explorer-backup", "formatVersion": 1, "platform": "linux", "settings": {} }""", "for Linux")]
    [InlineData("""{ "format": "rain-explorer-backup", "formatVersion": 1 }""", "no settings")]
    public void BadFilesAreExplained(string json, string reason)
    {
        var ex = Assert.Throws<InvalidDataException>(() => SettingsBackupService.Read(json));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void UnknownSettingsFromNewerBuildsAreKept()
    {
        using var config = new TemporaryDirectory();
        var root = JsonNode.Parse(SettingsBackupService.Create(Sample(), config.Path))!;
        root["settings"]!["SomethingFromTheFuture"] = 42;
        var (settingsJson, _, _) = SettingsBackupService.Read(root.ToJsonString());
        Assert.Contains("SomethingFromTheFuture", settingsJson);
    }
}

public sealed class ShortcutServiceTests
{
    [Fact]
    public void ActionIdsAreUniqueAndDefaultsParse()
    {
        Assert.Equal(ShortcutService.Actions.Count, ShortcutService.Actions.Select(a => a.Id).Distinct().Count());
        foreach (var action in ShortcutService.Actions)
            foreach (string text in action.Defaults)
                Assert.True(ShortcutService.TryParse(text, out _), $"{action.Id}: {text}");
    }

    [Fact]
    public void NoTwoDefaultsShareAKey()
    {
        var seen = new Dictionary<Shortcut, string>();
        foreach (var action in ShortcutService.Actions)
        {
            foreach (string text in action.Defaults)
            {
                Assert.True(ShortcutService.TryParse(text, out var gesture));
                Assert.False(seen.TryGetValue(gesture, out string? other), $"{text} is used by {other} and {action.Id}");
                seen[gesture] = action.Id;
            }
        }
    }

    [Theory]
    [InlineData("Ctrl+Shift+T", Key.T, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+T")]
    [InlineData("Ctrl+,", Key.OemComma, ModifierKeys.Control, "Ctrl+,")]
    [InlineData("Ctrl+`", Key.Oem3, ModifierKeys.Control, "Ctrl+`")]
    [InlineData("Shift+/", Key.Oem2, ModifierKeys.Shift, "Shift+/")]
    [InlineData("Shift+Delete", Key.Delete, ModifierKeys.Shift, "Shift+Delete")]
    [InlineData("Alt+Enter", Key.Enter, ModifierKeys.Alt, "Alt+Enter")]
    [InlineData("Ctrl+PageDown", Key.PageDown, ModifierKeys.Control, "Ctrl+PageDown")]
    [InlineData("F1", Key.F1, ModifierKeys.None, "F1")]
    [InlineData("Ctrl+1", Key.D1, ModifierKeys.Control, "Ctrl+1")]
    public void KeysParseAndDescribeTheSameWay(string text, Key key, ModifierKeys modifiers, string described)
    {
        Assert.True(ShortcutService.TryParse(text, out var gesture));
        Assert.Equal(new Shortcut(key, modifiers), gesture);
        Assert.Equal(described, ShortcutService.Describe(gesture));
        Assert.True(ShortcutService.TryParse(ShortcutService.Format(gesture), out var again));
        Assert.Equal(gesture, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+T")]
    [InlineData("Ctrl+NotAKey")]
    public void NonsenseDoesNotParse(string text) => Assert.False(ShortcutService.TryParse(text, out _));

    [Fact]
    public void TypingOnlyAllowsSafeShortcuts()
    {
        Assert.True(ShortcutService.SafeWhileTyping(new Shortcut(Key.T, ModifierKeys.Control)));
        Assert.True(ShortcutService.SafeWhileTyping(new Shortcut(Key.F5, ModifierKeys.None)));
        Assert.False(ShortcutService.SafeWhileTyping(new Shortcut(Key.Back, ModifierKeys.None)));
        Assert.False(ShortcutService.SafeWhileTyping(new Shortcut(Key.Space, ModifierKeys.None)));
        Assert.False(ShortcutService.SafeWhileTyping(new Shortcut(Key.Oem2, ModifierKeys.Shift)));   // typing "?"
    }

    [Fact]
    public void MatchingIgnoresKeysThatArentModifiers()
    {
        var gesture = new Shortcut(Key.W, ModifierKeys.Control);
        Assert.True(ShortcutService.Matches(gesture, Key.W, ModifierKeys.Control));
        Assert.False(ShortcutService.Matches(gesture, Key.W, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.False(ShortcutService.Matches(gesture, Key.Q, ModifierKeys.Control));
    }

    [Fact]
    public void BlockedKeysAreExplained()
    {
        Assert.NotNull(ShortcutService.Validate(new Shortcut(Key.A, ModifierKeys.None)));
        Assert.NotNull(ShortcutService.Validate(new Shortcut(Key.D5, ModifierKeys.Shift)));
        Assert.NotNull(ShortcutService.Validate(new Shortcut(Key.Escape, ModifierKeys.None)));
        Assert.NotNull(ShortcutService.Validate(new Shortcut(Key.Tab, ModifierKeys.None)));
        Assert.NotNull(ShortcutService.Validate(new Shortcut(Key.E, ModifierKeys.Windows)));
        Assert.Null(ShortcutService.Validate(new Shortcut(Key.K, ModifierKeys.Control)));
        Assert.Null(ShortcutService.Validate(new Shortcut(Key.F9, ModifierKeys.None)));
    }
}

public sealed class CommandBoxTests
{
    [Theory]
    [InlineData("cd", true, "")]
    [InlineData("cd ..", true, "..")]
    [InlineData("cd..", true, "..")]
    [InlineData(@"cd\", true, @"\")]
    [InlineData(@"cd /d D:\Work", true, @"D:\Work")]
    [InlineData(@"chdir C:\Temp", true, @"C:\Temp")]
    [InlineData(@"Set-Location ""C:\My Files""", true, @"""C:\My Files""")]
    [InlineData("sl src", true, "src")]
    [InlineData("cd src && dir", false, "")]       // chained: runs in the shell
    [InlineData("cdrecord --help", false, "")]
    [InlineData("dir", false, "")]
    public void ChangeFolderCommandsAreRecognised(string command, bool isCd, string argument)
    {
        Assert.Equal(isCd, CommandBoxView.IsChangeFolder(command, out string actual));
        if (isCd) Assert.Equal(argument, actual);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rain-stable-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
