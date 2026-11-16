using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RainExplorer.Helpers;
using RainExplorer.Models;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class SmoothScrollTests
{
    [Theory]
    [InlineData("Slow", 40)]
    [InlineData("Normal", 60)]
    [InlineData("Fast", 100)]
    public void EachSpeedHasItsStep(string speed, double pixels) =>
        Assert.Equal(pixels, SmoothScroll.StepFor(Enum.Parse<ScrollSpeed>(speed)));

    [Theory]
    [InlineData(120, true)]
    [InlineData(-240, true)]
    [InlineData(40, false)]    // precision touchpad / hi-res wheel: already smooth
    [InlineData(0, false)]
    public void OnlyWholeNotchesGlide(int delta, bool notch) => Assert.Equal(notch, SmoothScroll.IsNotch(delta));

    [Fact]
    public void SettingsKeepTheirValuesAndDefaults()
    {
        Assert.True(new AppSettings().SmoothScrolling);
        string json = JsonSerializer.Serialize(new AppSettings { ScrollSpeed = ScrollSpeed.Fast, SmoothScrolling = false });
        Assert.Contains("\"Fast\"", json);
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(ScrollSpeed.Fast, back.ScrollSpeed);
        Assert.False(back.SmoothScrolling);
    }

    [Fact]
    public void ItemScrollingListsAreLeftToWpfAndPixelListsGlide()
    {
        RunSta(() =>
        {
            var itemList = new ListBox { ItemsSource = Enumerable.Range(0, 200).Select(i => $"Item {i}").ToList() };
            var pixelList = new ListBox { ItemsSource = Enumerable.Range(0, 200).Select(i => $"Item {i}").ToList() };
            VirtualizingPanel.SetScrollUnit(pixelList, ScrollUnit.Pixel);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(itemList);
            panel.Children.Add(pixelList);
            itemList.Height = pixelList.Height = 200;
            var window = Host(panel);
            try
            {
                Assert.False(SmoothScroll.ScrollsByPixel(Viewer(itemList)));
                Assert.True(SmoothScroll.ScrollsByPixel(Viewer(pixelList)));
                Assert.True(SmoothScroll.ScrollsByPixel(new ScrollViewer { CanContentScroll = false }));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AWheelNotchGlidesToItsTarget()
    {
        RunSta(() =>
        {
            Func<AppSettings> original = SmoothScroll.SettingsSource;
            SmoothScroll.SettingsSource = () => new AppSettings { SmoothScrolling = true, ScrollSpeed = ScrollSpeed.Normal };
            SmoothScroll.Install();
            var content = new Border { Height = 5000 };
            var viewer = new ScrollViewer { Height = 300, Content = content };
            var window = Host(viewer);
            try
            {
                var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                    Source = content,
                };
                content.RaiseEvent(args);
                Assert.True(args.Handled);
                Assert.True(viewer.VerticalOffset < 60);   // not there in one jump

                var until = DateTime.UtcNow.AddSeconds(2);
                while (viewer.VerticalOffset < 60 && DateTime.UtcNow < until) Pump(TimeSpan.FromMilliseconds(30));
                Assert.Equal(60, viewer.VerticalOffset, 1);
            }
            finally
            {
                window.Close();
                SmoothScroll.SettingsSource = original;
            }
        });
    }

    private static ScrollViewer Viewer(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (d is ScrollViewer v) return v;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
        }
        throw new InvalidOperationException("No ScrollViewer");
    }

    private static Window Host(UIElement content)
    {
        var window = new Window
        {
            Content = content, Width = 600, Height = 400, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None,
        };
        window.Show();
        window.UpdateLayout();
        Pump(TimeSpan.FromMilliseconds(50));
        return window;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
