using System.Text.Json;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

// Ported from the Linux MotionAndActivityTests (activity part), with Windows error texts.
public sealed class ActivityHistoryTests
{
    private static ActivityEntry Failed(string error, string title = "Move", string? items = "Project") => new()
    {
        Title = title,
        IconKey = "arrow-right",
        StartedAt = new DateTime(2026, 11, 16, 21, 21, 31),
        FinishedAt = new DateTime(2026, 11, 16, 21, 21, 32),
        BeginDetail = items,
        Detail = error,
        FullDetail = error,
        DurationText = "36 ms",
        Status = ActivityStatus.Failed,
    };

    [Theory]
    [InlineData("\"Rain-Explorer-v2\" is in use by Claude. Close it there and try again.", "Another program is using")]
    [InlineData("The process cannot access the file because it is being used by another process.", "Another program is using")]
    [InlineData("Access to the path 'C:\\Windows\\System32\\x' is denied.", "doesn't have permission")]
    [InlineData("There is not enough space on the disk.", "drive is full")]
    [InlineData("The media is write protected.", "read-only")]
    [InlineData("The cloud file provider is not running.", "cloud sync app")]
    [InlineData("Could not find a part of the path 'D:\\gone'.", "was gone")]
    [InlineData("The network path was not found.", "network location")]
    [InlineData("The filename, directory name, or volume label syntax is incorrect.", "doesn't accept")]
    public void FailuresGetAPlainExplanationAndAdvice(string error, string expected)
    {
        var explanation = ActivityExplainer.Explain(Failed(error));
        Assert.Contains(expected, explanation.Summary);
        Assert.NotNull(explanation.Advice);
    }

    [Fact]
    public void TheProgramNamedByRainStaysInTheDescription()
    {
        string text = ActivityExplainer.Describe(Failed("\"Rain-Explorer-v2\" is in use by Claude. Close it there and try again."));
        Assert.Contains("System message: \"Rain-Explorer-v2\" is in use by Claude.", text);
        Assert.Contains("Started: 2026-11-16 21:21:31", text);
        Assert.Contains("Items: Project", text);
    }

    [Fact]
    public void FolderOpenFailuresNameTheFolder()
    {
        var entry = Failed("Access to the path 'C:\\Secret' is denied.", "Open folder", @"C:\Secret");
        Assert.StartsWith(@"Rain couldn't open the folder C:\Secret.", ActivityExplainer.Explain(entry).Summary);
        Assert.Contains(@"Folder: C:\Secret", ActivityExplainer.Describe(entry));
    }

    [Fact]
    public void UnknownErrorsSayTheMessageIsVerbatim()
    {
        var explanation = ActivityExplainer.Explain(Failed("Something odd happened (0x8007ABCD)."));
        Assert.Contains("exactly what Windows reported", explanation.Summary);
    }

    [Fact]
    public void SuccessAndCancelAreDescribedWithoutAdvice()
    {
        var ok = new ActivityEntry { Title = "Copy", IconKey = "copy", Status = ActivityStatus.Success, DurationText = "2.0 s", BeginDetail = "3 items" };
        Assert.Equal("“Copy” (3 items) finished successfully in 2.0 s.", ActivityExplainer.Explain(ok).Summary);
        Assert.Null(ActivityExplainer.Explain(ok).Advice);
    }

    [Fact]
    public void RetentionDropsOldFinishedEntriesButNeverRunningOnes()
    {
        var now = new DateTime(2026, 11, 16, 12, 0, 0);
        var items = new List<ActivityEntry>
        {
            new() { Title = "Running", IconKey = "x", StartedAt = now.AddDays(-30), Status = ActivityStatus.Running },
            new() { Title = "New", IconKey = "x", StartedAt = now.AddDays(-1), Status = ActivityStatus.Success },
            new() { Title = "Old", IconKey = "x", StartedAt = now.AddDays(-8), Status = ActivityStatus.Failed },
        };

        ActivityService.PruneItems(items, detailed: true, retentionDays: 7, now);
        Assert.Equal(["Running", "New"], items.Select(i => i.Title));

        var keep = items.ToList();
        ActivityService.PruneItems(keep, detailed: true, retentionDays: 0, now.AddYears(5));
        Assert.Equal(2, keep.Count);   // "until I clear it"
    }

    [Fact]
    public void BasicHistoryKeepsTheOldCapAndDetailedHistoryKeepsMore()
    {
        var items = Enumerable.Range(0, 300).Select(i => new ActivityEntry
        {
            Title = $"#{i}", IconKey = "x", StartedAt = DateTime.Now, Status = ActivityStatus.Success,
        }).ToList();
        var detailed = items.ToList();

        ActivityService.PruneItems(items, detailed: false, retentionDays: 7, DateTime.Now);
        ActivityService.PruneItems(detailed, detailed: true, retentionDays: 7, DateTime.Now);

        Assert.Equal(ActivityService.BasicCap, items.Count);
        Assert.Equal(300, detailed.Count);
    }

    [Fact]
    public void NewFieldsSurviveSavingAndOldFilesStillLoad()
    {
        string json = JsonSerializer.Serialize(new List<ActivityEntry> { Failed("Access is denied.") });
        var back = JsonSerializer.Deserialize<List<ActivityEntry>>(json)!.Single();
        Assert.Equal("Access is denied.", back.FullDetail);
        Assert.Equal("Project", back.BeginDetail);
        Assert.NotNull(back.FinishedAt);

        var old = JsonSerializer.Deserialize<List<ActivityEntry>>(
            "[{\"Title\":\"Move\",\"IconKey\":\"x\",\"StartedAt\":\"2026-11-15T21:23:44\",\"Detail\":\"d\",\"Status\":2}]")!.Single();
        Assert.Null(old.FullDetail);
        Assert.True(old.IsFailed);
    }

    [Fact]
    public void SettingsDefaultsMatchLinux()
    {
        var settings = new AppSettings();
        Assert.False(settings.DetailedActivityHistory);
        Assert.Equal(7, settings.ActivityRetentionDays);
        Assert.False(settings.OpenActivityOnFailure);
        Assert.Contains(RainExplorer.Controls.SettingsView.RetentionChoices, c => c.Days == 7);
    }
}
