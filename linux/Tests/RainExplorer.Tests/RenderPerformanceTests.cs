using System.Collections.Concurrent;
using System.Text.Json;
using RainExplorer.Models;
using RainExplorer.Controls;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

public sealed class RenderPerformanceTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(240)]
    [InlineData(280)]
    [InlineData(360)]
    public void TabDragResponseDependsOnElapsedTimeRatherThanFrameCount(int rate)
    {
        // Compare 250 ms at each cadence against five 50 ms updates,
        // before the UI's final pixel snapping.
        double atRate = 0, reference = 0;
        for (int i = 0; i < rate / 4; i++) atRate += (1000 - atRate) * PaneView.TabPreviewBlend(1d / rate);
        for (int i = 0; i < 5; i++) reference += (1000 - reference) * PaneView.TabPreviewBlend(0.05);
        Assert.Equal(reference, atRate, precision: 7);
    }

    [Theory]
    [InlineData("DP-1 connected\n   2560x1440 239.88*+ 144.00\nDP-2 connected\n   2496x1404 279.56*+", 280)]
    [InlineData("DP-1 connected\n   1920x1080 144.00+ 59.94*", 60)]
    [InlineData("DP-1 connected\n   1920x1080 144.00*+", 144)]
    [InlineData("DP-1 connected\n   1920x1080 500.00*+", 360)]
    [InlineData("DP-1 disconnected\n   1920x1080 144.00+", 120)]
    [InlineData("   mode NaN* Infinity* bad*\n", 120)]
    [InlineData("", 120)]
    public void RefreshDetectionUsesCurrentModesAndBoundsTheCadence(string output, int expected) =>
        Assert.Equal(expected, RenderPerformance.ParseRefreshRate(output));

    [Fact]
    public void PerformancePreferenceDefaultsOffAndRoundTripsOlderSettings()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("""
            {"Theme":"Light","ShowAmbientBackground":true,"FuturePreference":{"value":42}}
            """)!;
        Assert.False(old.PerformanceMode);
        var changed = new List<string>();
        old.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);
        old.PerformanceMode = true;
        old.PerformanceMode = true;
        Assert.Equal(new[] { nameof(AppSettings.PerformanceMode) }, changed);
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(old))!;
        Assert.True(restored.PerformanceMode);
        Assert.True(restored.ShowAmbientBackground);
        Assert.Equal("Light", restored.Theme);
        Assert.Equal(42, restored.ExtensionData!["FuturePreference"].GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task RenderClockChangesLiveSleepsWithoutSubscribersAndDisposesSafely()
    {
        using var timer = new AdaptiveRenderTimer(240);
        var times = new ConcurrentQueue<TimeSpan>();
        void Tick(TimeSpan time) => times.Enqueue(time);
        timer.Tick += Tick;
        await Task.Delay(150);
        Assert.NotEmpty(times);
        timer.FramesPerSecond = 60;
        Assert.Equal(60, timer.FramesPerSecond);
        int before = times.Count;
        await Task.Delay(150);
        Assert.True(times.Count > before);
        timer.Tick -= Tick;
        await Task.Delay(30); // Allow a callback already in progress to finish.
        before = times.Count;
        await Task.Delay(80);
        Assert.Equal(before, times.Count);
        timer.FramesPerSecond = 280;
        timer.Tick += Tick;
        await Task.Delay(100);
        Assert.True(times.Count > before);
        var sequence = times.ToArray();
        Assert.True(sequence.Zip(sequence.Skip(1)).All(pair => pair.First < pair.Second));
        timer.Dispose();
        timer.Dispose();
        await Task.Delay(30);
        before = times.Count;
        await Task.Delay(80);
        Assert.Equal(before, times.Count);
        Assert.Throws<ObjectDisposedException>(() => timer.Tick += Tick);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveRenderTimer(0));
    }
}
