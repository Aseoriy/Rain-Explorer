using System.Text.Json.Nodes;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

public sealed class SettingsBackupTests
{
    private static AppSettings Sample()
    {
        var s = new AppSettings { Theme = "Ocean", ShowHiddenFiles = true };
        s.Pinned.Add(new PinnedItem { Name = "Projects", Path = "/home/me/Projects" });
        s.Pinned.Add(new PinnedItem { Name = "Notes", Path = "/home/me/Notes" });
        s.CustomGroups.Add(new SidebarGroup { Name = "Work", Items = [new PinnedItem { Name = "Docs", Path = "/srv/docs" }] });
        s.TabProjects.Add(new TabProject { Name = "Site", Tabs = [new TabProjectTab { Target = "/srv/site" }] });
        s.ShortcutOverrides["tab.new"] = "Ctrl+K";
        return s;
    }

    [Fact]
    public void BackupRoundTripsEverything()
    {
        string config = TestEnvironment.NewFolder("backup-config");
        File.WriteAllText(Path.Combine(config, "sorts.json"), """{ "/srv": { "Key": "Size", "Dir": -1 } }""");
        string json = SettingsBackupService.Create(Sample(), config);

        var (settingsJson, sortsJson, summary) = SettingsBackupService.Read(json);
        Assert.Equal((2, 1, 1, 1), (summary.Pins, summary.Lists, summary.Projects, summary.Shortcuts));
        Assert.Contains("2 pinned places, 1 sidebar list, 1 tab project and 1 custom shortcut", SettingsBackupService.Describe(summary));
        Assert.Contains("Size", sortsJson);

        string target = TestEnvironment.NewFolder("backup-restore");
        File.WriteAllText(Path.Combine(target, "settings.json"), """{ "Theme": "Violet" }""");
        SettingsBackupService.WriteRestore(target, settingsJson, sortsJson);
        Assert.Contains("Violet", File.ReadAllText(Path.Combine(target, "settings.json" + SettingsBackupService.BeforeRestoreSuffix)));
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            File.ReadAllText(Path.Combine(target, "settings.json")), SettingsStore.JsonOptions)!;
        Assert.Equal("Ocean", restored.Theme);
        Assert.True(restored.ShowHiddenFiles);
        Assert.Equal(["Projects", "Notes"], restored.Pinned.Select(p => p.Name));
        Assert.Equal("/srv/docs", restored.CustomGroups.Single().Items.Single().Path);
        Assert.Equal("/srv/site", restored.TabProjects.Single().Tabs.Single().Target);
        Assert.Equal("Ctrl+K", restored.ShortcutOverrides["tab.new"]);
        Assert.Contains("Size", File.ReadAllText(Path.Combine(target, "sorts.json")));
        Assert.Empty(Directory.GetFiles(target, "*.tmp"));
    }

    [Theory]
    [InlineData("not json at all", "isn't valid JSON")]
    [InlineData("""{ "Theme": "Violet" }""", "isn't a Rain Explorer backup")]
    [InlineData("""{ "format": "rain-explorer-backup", "formatVersion": 99, "settings": {} }""", "newer Rain Explorer")]
    [InlineData("""{ "format": "rain-explorer-backup", "formatVersion": 1, "platform": "windows", "settings": {} }""", "for Windows")]
    [InlineData("""{ "format": "rain-explorer-backup", "formatVersion": 1 }""", "no settings")]
    public void BadFilesAreExplained(string json, string reason)
    {
        var ex = Assert.Throws<InvalidDataException>(() => SettingsBackupService.Read(json));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void UnknownSettingsFromNewerBuildsAreKept()
    {
        var root = JsonNode.Parse(SettingsBackupService.Create(Sample(), TestEnvironment.NewFolder("backup-unknown")))!;
        root["settings"]!["SomethingFromTheFuture"] = 42;
        var (settingsJson, _, _) = SettingsBackupService.Read(root.ToJsonString());
        Assert.Contains("SomethingFromTheFuture", settingsJson);
    }
}
