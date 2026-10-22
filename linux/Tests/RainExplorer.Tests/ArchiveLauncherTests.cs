using System.Text;
using System.Text.Json;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

internal sealed class ArchiveTestEnvironment : IDisposable
{
    private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
    private readonly string? _desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
    private readonly string? _capture = Environment.GetEnvironmentVariable("RAIN_TEST_ARCHIVE_CAPTURE");
    private readonly string _choice = SettingsStore.Instance.Settings.ArchiveManager;
    public string Root { get; } = TestEnvironment.NewFolder("archive-apps");
    public string Capture => Path.Combine(Root, "arguments");

    public ArchiveTestEnvironment(params string[] apps)
    {
        foreach (string app in apps)
        {
            string exe = Path.Combine(Root, app);
            File.WriteAllText(exe, "#!/bin/sh\nprintf '%s\\0' \"$@\" > \"$RAIN_TEST_ARCHIVE_CAPTURE\"\n");
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Environment.SetEnvironmentVariable("PATH", Root);
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "KDE");
        Environment.SetEnvironmentVariable("RAIN_TEST_ARCHIVE_CAPTURE", Capture);
        SettingsStore.Instance.Settings.ArchiveManager = "";
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _path);
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", _desktop);
        Environment.SetEnvironmentVariable("RAIN_TEST_ARCHIVE_CAPTURE", _capture);
        SettingsStore.Instance.Settings.ArchiveManager = _choice;
    }
}

public sealed class ArchiveLauncherTests
{
    [Theory]
    [InlineData("KDE", "ark")]
    [InlineData("GNOME", "file-roller")]
    [InlineData("X-Cinnamon", "file-roller")]
    [InlineData("MATE", "engrampa")]
    [InlineData("XFCE", "xarchiver")]
    [InlineData("LXQt", "xarchiver")]
    public void AutomaticChoiceUsesInstalledDesktopApp(string desktop, string expected)
    {
        using var env = new ArchiveTestEnvironment("ark", "file-roller", "engrampa", "xarchiver");
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", desktop);
        Assert.Equal(expected, ArchiveLauncher.Resolve(out string? error)!.Id);
        Assert.Null(error);
    }

    [Fact]
    public void AutomaticChoiceFallsBackButMissingExplicitChoiceIsPreserved()
    {
        using var env = new ArchiveTestEnvironment("file-roller");
        Assert.Equal("file-roller", ArchiveLauncher.Resolve(out _)!.Id);
        SettingsStore.Instance.Settings.ArchiveManager = "ark";
        Assert.Null(ArchiveLauncher.Resolve(out string? error));
        Assert.Contains("Ark is no longer installed", error);
        Assert.Equal("ark", SettingsStore.Instance.Settings.ArchiveManager);
        SettingsStore.Instance.Settings.ArchiveManager = "arbitrary-command";
        Assert.Null(ArchiveLauncher.Resolve(out error));
        Assert.Contains("supported archive app", error);
    }

    [Fact]
    public void MissingAppsExplainHowToEnableArchivesWithoutRequiringArk()
    {
        using var env = new ArchiveTestEnvironment();
        Assert.Empty(ArchiveLauncher.Installed());
        Assert.False(ArchiveLauncher.TryLaunch(ArchiveAction.Compress, [], out _, out string? error));
        Assert.Contains("File Roller", error);
        Assert.Contains("software manager", error);
        Assert.False(File.Exists(env.Capture));
    }

    [Theory]
    [InlineData("ark")]
    [InlineData("file-roller")]
    [InlineData("engrampa")]
    [InlineData("xarchiver")]
    public void EveryAdapterPassesLiteralPathsAndInteractiveOptions(string id)
    {
        using var env = new ArchiveTestEnvironment(id);
        SettingsStore.Instance.Settings.ArchiveManager = id;
        string first = Path.Combine(env.Root, "-one with spaces;$(literal)'é.zip");
        string second = Path.Combine(env.Root, "two # literal\nname.zip");
        File.WriteAllText(first, "one"); File.WriteAllText(second, "two");
        var app = Assert.Single(ArchiveLauncher.Installed());
        foreach (var action in Enum.GetValues<ArchiveAction>())
        {
            string[] paths = action == ArchiveAction.Open ? [first] : [first, second];
            if (id == "xarchiver" && action == ArchiveAction.ExtractHere)
            {
                Assert.False(app.CanExtractHere);
                Assert.Throws<NotSupportedException>(() => ArchiveLauncher.BuildStartInfo(app, action, paths));
                continue;
            }
            var start = ArchiveLauncher.BuildStartInfo(app, action, paths);
            Assert.False(start.UseShellExecute);
            Assert.Equal(env.Root, start.WorkingDirectory);
            Assert.Contains("--", start.ArgumentList);
            Assert.Contains(first, start.ArgumentList);
            if (action != ArchiveAction.Open) Assert.Contains(second, start.ArgumentList);
            Assert.DoesNotContain("--force", start.ArgumentList);
            Assert.DoesNotContain("--extract-to", start.ArgumentList);
            if (id == "ark" && action is ArchiveAction.ExtractTo or ArchiveAction.Compress)
                Assert.Contains("--dialog", start.ArgumentList);
            if (id == "xarchiver" && action == ArchiveAction.Compress)
                Assert.Equal(new[] { "--compress", first, "--", second }, start.ArgumentList);
            if (id == "xarchiver" && action == ArchiveAction.ExtractTo)
                Assert.Equal("--multi-extract", start.ArgumentList[0]);
            if (File.Exists(env.Capture)) File.Delete(env.Capture);
            Assert.True(ArchiveLauncher.TryLaunch(action, paths, out string? label, out string? error), error);
            Assert.Equal(app.Label, label);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(env.Capture) && new FileInfo(env.Capture).Length > 0, 3000));
            var captured = Encoding.UTF8.GetString(File.ReadAllBytes(env.Capture)).Split('\0')[..^1];
            Assert.Equal(start.ArgumentList, captured);
            Assert.Equal("one", File.ReadAllText(first)); Assert.Equal("two", File.ReadAllText(second));
        }
    }

    [Fact]
    public void LinksMissingInputsAndAmbiguousHereDestinationsDoNotLaunch()
    {
        using var env = new ArchiveTestEnvironment("ark", "xarchiver");
        string archive = Path.Combine(env.Root, "source.zip"); File.WriteAllText(archive, "source");
        string link = Path.Combine(env.Root, "link.zip"); File.CreateSymbolicLink(link, archive);
        Assert.False(ArchiveLauncher.TryLaunch(ArchiveAction.ExtractHere, [link], out _, out string? error));
        Assert.Contains("symbolic links", error);
        Assert.False(ArchiveLauncher.TryLaunch(ArchiveAction.Open, [archive + ".missing"], out _, out error));
        Assert.Contains("no longer exists", error);
        string other = TestEnvironment.NewFolder("other-archives");
        string second = Path.Combine(other, "second.zip"); File.WriteAllText(second, "second");
        Assert.False(ArchiveLauncher.TryLaunch(ArchiveAction.ExtractHere, [archive, second], out _, out error));
        Assert.Contains("one folder", error);
        SettingsStore.Instance.Settings.ArchiveManager = "xarchiver";
        Assert.False(ArchiveLauncher.TryLaunch(ArchiveAction.Compress, [archive, second], out _, out error));
        Assert.Contains("one folder", error);
        Assert.False(File.Exists(env.Capture));
    }

    [Fact]
    public void FolderCompressionRejectsNestedFileDirectoryBrokenAndCyclicLinks()
    {
        using var env = new ArchiveTestEnvironment("ark");
        string outside = TestEnvironment.NewFolder("archive-outside");
        File.WriteAllText(Path.Combine(outside, "private.txt"), "outside data");
        foreach (string target in new[] { Path.Combine(outside, "private.txt"), outside, outside + ".missing", "." })
        {
            string folder = TestEnvironment.NewFolder("archive-tree");
            string nested = Path.Combine(folder, "child"); Directory.CreateDirectory(nested);
            File.CreateSymbolicLink(Path.Combine(nested, "link"), target);
            Assert.False(ArchiveLauncher.TryLaunch(ArchiveAction.Compress, [folder], out _, out string? error));
            Assert.Contains("contains symbolic links", error);
            Assert.False(File.Exists(env.Capture));
        }
        Assert.Equal("outside data", File.ReadAllText(Path.Combine(outside, "private.txt")));
    }

    [Fact]
    public void ArchiveChoiceRoundTripsAlongsideUnknownSettings()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"Theme":"Ocean","ArchiveManager":"file-roller","FutureOption":{"value":42}}
            """)!;
        string json = JsonSerializer.Serialize(settings);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("file-roller", document.RootElement.GetProperty("ArchiveManager").GetString());
        Assert.Equal(42, document.RootElement.GetProperty("FutureOption").GetProperty("value").GetInt32());
        Assert.Equal("", new AppSettings().ArchiveManager);
    }
}
