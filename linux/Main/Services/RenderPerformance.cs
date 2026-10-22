using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Rendering;
using Avalonia.Threading;
using RainExplorer.Models;

namespace RainExplorer.Services;

internal static class RenderPerformance
{
    public const int FallbackFrameRate = 120;
    private static AdaptiveRenderTimer? _timer;
    private static int _smoothFrameRate = FallbackFrameRate;
    private static CancellationTokenSource? _detection;
    internal static int FramesPerSecond => _timer?.FramesPerSecond ?? 60;

    /// <summary>Install after X11 creates its compositor, before the application opens any windows.</summary>
    public static void Install()
    {
        var timer = new AdaptiveRenderTimer(SettingsStore.Instance.Settings.PerformanceMode ? 60 : _smoothFrameRate);
        try
        {
            ReplaceX11Timer(timer);
            _timer = timer;
            SettingsStore.Instance.Settings.PropertyChanged += SettingsChanged;
            _detection = new CancellationTokenSource();
            _ = DetectRefreshRate(_detection.Token);
        }
        catch
        {
            timer.Dispose();
            throw;
        }
    }

    private static void ReplaceX11Timer(IRenderTimer timer)
    {
        // Avalonia 11.3.22 hardcodes SleepLoopRenderTimer(60) and captures it in
        // RenderLoop during X11 setup. Rebinding IRenderTimer alone does NOT change
        // rendering. Keep this version-specific bridge here until X11 exposes an option.
        var loopType = typeof(IRenderTimer).Assembly.GetType("Avalonia.Rendering.RenderLoop", throwOnError: true)!;
        var loopInterface = typeof(IRenderTimer).Assembly.GetType("Avalonia.Rendering.IRenderLoop", throwOnError: true)!;
        var loop = AvaloniaLocator.Current.GetService(loopInterface)
            ?? throw new InvalidOperationException("Avalonia's X11 render loop was not initialized.");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var timerField = loopType.GetField("_timer", flags)!;
        var items = (System.Collections.IList)loopType.GetField("_items", flags)!.GetValue(loop)!;
        var onTick = loopType.GetMethod("TimerTick", flags)!.CreateDelegate<Action<TimeSpan>>(loop);
        lock (items)
        {
            // The compositor already subscribes during platform setup. Move that
            // subscription too, otherwise both clocks run or the new one never starts.
            if (items.Count > 0 && timerField.GetValue(loop) is IRenderTimer old) old.Tick -= onTick;
            timerField.SetValue(loop, timer);
            AvaloniaLocator.CurrentMutable.Bind<IRenderTimer>().ToConstant(timer);
            if (items.Count > 0) timer.Tick += onTick;
        }
    }

    private static void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.PerformanceMode) && _timer is { } timer)
            timer.FramesPerSecond = SettingsStore.Instance.Settings.PerformanceMode ? 60 : _smoothFrameRate;
    }

    private static async Task DetectRefreshRate(CancellationToken shutdown)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("xrandr", "--current")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.Environment["LC_ALL"] = "C";
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string text = await output.ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            if (process.ExitCode != 0 || shutdown.IsCancellationRequested) return;
            int rate = ParseRefreshRate(text);
            Dispatcher.UIThread.Post(() =>
            {
                if (shutdown.IsCancellationRequested) return;
                _smoothFrameRate = rate;
                if (_timer is { } timer && !SettingsStore.Instance.Settings.PerformanceMode)
                    timer.FramesPerSecond = rate;
            });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or OperationCanceledException)
        {
            // xrandr is optional. Unknown displays still get the 120 Hz fallback.
        }
        finally
        {
            try { if (process.Id != 0 && !process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
    }

    internal static int ParseRefreshRate(string output)
    {
        double highest = 0;
        foreach (string line in output.Split('\n'))
        {
            // Only '*' marks the current mode; '+' alone marks a preferred mode.
            if (line.Length == 0 || !char.IsWhiteSpace(line[0])) continue;
            foreach (string token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (token.Contains('*') && double.TryParse(token.TrimEnd('*', '+'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double rate) && double.IsFinite(rate) && rate > 0)
                    highest = Math.Max(highest, rate);
        }
        return highest > 0 ? (int)Math.Clamp(Math.Round(highest), 60, 360) : FallbackFrameRate;
    }

    public static void Shutdown()
    {
        SettingsStore.Instance.Settings.PropertyChanged -= SettingsChanged;
        _detection?.Cancel();
        _detection?.Dispose();
        _detection = null;
        _timer?.Dispose();
        _timer = null;
    }
}
