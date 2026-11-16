using Avalonia;
using Avalonia.Headless.XUnit;
using RainExplorer.Helpers;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

/// <summary>Smooth scrolling, always-smooth sidebar loading and detailed activity history.</summary>
public sealed class MotionAndActivityTests
{
    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(0, -2, true)]
    [InlineData(1, 0, true)]
    [InlineData(0, 0.25, false)]    // touchpad / hi-res wheel: left to Avalonia
    [InlineData(0, -1.5, false)]
    [InlineData(0, 0, false)]
    public void OnlyWholeWheelNotchesAreSmoothed(double x, double y, bool expected) =>
        Assert.Equal(expected, SmoothScroll.IsNotch(new Vector(x, y)));

    [Fact]
    public void ScrollSpeedsAreOrdered() =>
        Assert.True(SmoothScroll.StepFor(ScrollSpeed.Slow) < SmoothScroll.StepFor(ScrollSpeed.Normal)
                    && SmoothScroll.StepFor(ScrollSpeed.Normal) < SmoothScroll.StepFor(ScrollSpeed.Fast));

    [Fact]
    public void NewSettingsHaveSafeDefaults()
    {
        var s = new AppSettings();
        Assert.True(s.SmoothScrolling);
        Assert.Equal(ScrollSpeed.Normal, s.ScrollSpeed);
        Assert.False(s.AlwaysSmoothAnimations);
        Assert.False(s.DetailedActivityHistory);
        Assert.Equal(7, s.ActivityRetentionDays);
        Assert.False(s.OpenActivityOnFailure);
    }

    [Fact]
    public void NewSettingsRoundTripAsJson()
    {
        var s = new AppSettings
        {
            ScrollSpeed = ScrollSpeed.Fast, SmoothScrolling = false, AlwaysSmoothAnimations = true,
            DetailedActivityHistory = true, ActivityRetentionDays = 0, OpenActivityOnFailure = true,
        };
        string json = System.Text.Json.JsonSerializer.Serialize(s);
        Assert.Contains("\"ScrollSpeed\":\"Fast\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(ScrollSpeed.Fast, back.ScrollSpeed);
        Assert.False(back.SmoothScrolling);
        Assert.True(back.AlwaysSmoothAnimations && back.DetailedActivityHistory && back.OpenActivityOnFailure);
        Assert.Equal(0, back.ActivityRetentionDays);
    }

    [AvaloniaFact]
    public void PrefetchedSubfoldersAppearTheMomentTheFolderExpands()
    {
        string root = TestEnvironment.NewFolder("prefetch");
        foreach (string name in new[] { "b", "a", "c" }) Directory.CreateDirectory(Path.Combine(root, name));
        var settings = SettingsStore.Instance.Settings;
        settings.AlwaysSmoothAnimations = true;
        try
        {
            var node = SidebarNode.Folder("prefetch", root, "folder", NodeKind.Pinned);
            node.Prefetch();
            var until = DateTime.UtcNow.AddSeconds(5);
            // The read finishes on the thread pool and is handed back on the UI thread.
            while (DateTime.UtcNow < until)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                if (typeof(SidebarNode).GetField("_prefetched",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(node) is not null)
                    break;
                Thread.Sleep(10);
            }
            node.IsExpanded = true;
            // Synchronous: no await between expanding and the rows existing.
            Assert.Equal(["a", "b", "c"], node.Children.Select(c => c.Name));
        }
        finally
        {
            settings.AlwaysSmoothAnimations = false;
        }
    }

    [Theory]
    [InlineData("Access to the path '/root/x' is denied.", "permission")]
    [InlineData("No space left on device : '/media/usb/big.iso'", "full")]
    [InlineData("Read-only file system : '/media/cd'", "read-only")]
    [InlineData("Could not find a part of the path '/tmp/gone'.", "gone")]
    [InlineData("Input/output error : '/media/usb/a'", "read or write error")]
    public void FailuresAreExplainedInPlainWords(string error, string expectedWords)
    {
        var entry = new ActivityEntry
        {
            Title = "Copy", IconKey = "copy", StartedAt = DateTime.Now, Status = ActivityStatus.Failed,
            BeginDetail = "3 items", FullDetail = error,
        };
        var help = ActivityExplainer.Explain(entry);
        Assert.Contains(expectedWords, help.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("“Copy” (3 items)", help.Summary);
        Assert.NotNull(help.Advice);
        Assert.Contains(error, ActivityExplainer.Describe(entry));
    }

    [Fact]
    public void UnknownFailuresStillGetAnHonestSummary()
    {
        var entry = new ActivityEntry { Title = "Rename", IconKey = "pencil", Status = ActivityStatus.Failed, FullDetail = "fihsdiofhsiodf" };
        Assert.Contains("exactly what the system reported", ActivityExplainer.Explain(entry).Summary);
    }

    [AvaloniaFact]
    public void DetailedHistoryKeepsFullMessagesAndLogsFolderFailures()
    {
        var settings = SettingsStore.Instance.Settings;
        var service = ActivityService.Instance;
        service.Clear();
        string longError = "Access to the path '/srv/" + new string('x', 300) + "' is denied.";
        try
        {
            // Basic log: shortened text only, folder failures stay in the status bar.
            settings.DetailedActivityHistory = false;
            var basic = service.Begin("Copy", "1 item", "copy");
            service.Complete(basic, false, longError);
            Assert.Null(basic.FullDetail);
            Assert.True(basic.Detail.Length < longError.Length);
            service.LogFailure("Open folder", "/root", "Permission denied", "folder-open");
            Assert.DoesNotContain(service.Items, e => e.Title == "Open folder");
            Assert.False(service.DetailedHistory);

            settings.DetailedActivityHistory = true;
            Assert.True(service.DetailedHistory);
            var detailed = service.Begin("Copy", "1 item", "copy");
            service.Complete(detailed, false, longError);
            Assert.Equal(longError, detailed.FullDetail);
            Assert.Equal("1 item", detailed.BeginDetail);
            Assert.NotNull(detailed.FinishedAt);
            service.LogFailure("Open folder", "/root", "Permission denied", "folder-open");
            Assert.Contains(service.Items, e => e.Title == "Open folder" && e.IsFailed);
            Assert.Contains("Open folder", service.ExportText());
        }
        finally
        {
            settings.DetailedActivityHistory = false;
            service.Clear();
        }
    }

    [AvaloniaFact]
    public void OldEntriesArePrunedAfterTheRetentionPeriod()
    {
        var settings = SettingsStore.Instance.Settings;
        var service = ActivityService.Instance;
        service.Clear();
        try
        {
            settings.DetailedActivityHistory = true;
            settings.ActivityRetentionDays = 0;   // keep until cleared
            service.Items.Add(new ActivityEntry
            {
                Title = "Old", IconKey = "copy", StartedAt = DateTime.Now.AddDays(-10), Status = ActivityStatus.Success,
            });
            var fresh = service.Begin("New", "x", "copy");
            service.Complete(fresh, true);
            Assert.Contains(service.Items, e => e.Title == "Old");

            settings.ActivityRetentionDays = 7;
            Assert.DoesNotContain(service.Items, e => e.Title == "Old");
            Assert.Contains(service.Items, e => e.Title == "New");
        }
        finally
        {
            settings.ActivityRetentionDays = 7;
            settings.DetailedActivityHistory = false;
            service.Clear();
        }
    }
}
