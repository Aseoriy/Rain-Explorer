using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using Xunit;

namespace RainExplorer.Git.Tests;

// The window shrank by the display-scale ratio on every launch when it was last closed on a monitor with
// a different scale than the main one (125% main, 100% side: 1521 -> 1217 -> 974 px). Restoring must put
// the window at exactly the saved pixel size, wherever it is.
public sealed class WindowPlacementTests
{
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out R rect);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorProc proc, nint data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(P point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    private delegate bool MonitorProc(nint monitor, nint hdc, ref R rect, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct R { public int L, T, Rt, B; }
    [StructLayout(LayoutKind.Sequential)] private struct P { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public R Monitor; public R Work; public uint Flags; }

    [Fact]
    public void RestoredWindowKeepsItsExactPixelSizeOnEveryMonitor()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Per-monitor v2 like the app manifest, for this test thread only: a process-wide switch
                // would change how every later WPF test in this run lays out.
                SetThreadDpiAwarenessContext(-4);
                if (GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) != 2) return;
                var monitors = new List<(R Work, uint Dpi)>();
                EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref R _, nint _) =>
                {
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    GetMonitorInfoW(monitor, ref info);
                    GetDpiForMonitor(monitor, 0, out uint dpi, out _);
                    monitors.Add((info.Work, dpi));
                    return true;
                }, 0);
                GetDpiForMonitor(MonitorFromPoint(new P(), 1), 0, out uint primaryDpi, out _);

                foreach (var (work, dpi) in monitors)
                {
                    int width = Math.Min(1200, work.Rt - work.L - 40), height = Math.Min(800, work.B - work.T - 40);
                    int left = work.L + 20, top = work.T + 20;
                    for (int launch = 0; launch < 3; launch++)
                    {
                        var window = new Window
                        {
                            Width = 1140, Height = 720, MinWidth = 400, MinHeight = 300,
                            WindowStyle = WindowStyle.None, WindowStartupLocation = WindowStartupLocation.Manual,
                            ShowActivated = false, ShowInTaskbar = false,
                        };
                        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 40, GlassFrameThickness = new Thickness(0) });
                        window.SourceInitialized += (_, _) =>
                            MainWindow.PlaceAtPixelBounds(new WindowInteropHelper(window).Handle, left, top, width, height);
                        window.Show();
                        Pump();
                        GetWindowRect(new WindowInteropHelper(window).Handle, out var r);
                        window.Close();
                        Assert.True(r.Rt - r.L == width && r.B - r.T == height,
                            $"Launch {launch} on a {dpi * 100 / 96}% monitor (main {primaryDpi * 100 / 96}%): " +
                            $"expected {width}x{height}, got {r.Rt - r.L}x{r.B - r.T}.");
                        Assert.Equal((left, top), (r.L, r.T));
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
