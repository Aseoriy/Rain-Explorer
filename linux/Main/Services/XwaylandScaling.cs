using System.Diagnostics;
using System.Text.Json;

namespace RainExplorer.Services;

/// <summary>
/// Avalonia only runs through XWayland on Linux. A compositor with fractional scaling normally stretches the
/// whole X11 window, which looks blurry. Hyprland can instead hand XWayland apps the real pixels
/// (<c>xwayland:force_zero_scaling</c>), but then nothing scales the app. When that is the setup, take the
/// monitor scale from Hyprland and give it to Avalonia so the interface is both sharp and the right size.
/// </summary>
internal static class XwaylandScaling
{
    private const string ScaleVariable = "AVALONIA_GLOBAL_SCALE_FACTOR";

    /// <summary>Call before Avalonia starts. Does nothing when the user already chose a scale.</summary>
    public static void ApplyIfNeeded()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ScaleVariable))) return;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE"))) return;

        if (!IsZeroScaling(RunHyprctl("getoption", "xwayland:force_zero_scaling", "-j"))) return;
        double? scale = FocusedMonitorScale(RunHyprctl("monitors", "-j"));
        if (scale is null or <= 1.0) return;

        Environment.SetEnvironmentVariable(ScaleVariable, scale.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>True when <c>hyprctl getoption xwayland:force_zero_scaling -j</c> reports it enabled.</summary>
    public static bool IsZeroScaling(string? optionJson)
    {
        if (string.IsNullOrWhiteSpace(optionJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(optionJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("int", out var value)
                   && value.ValueKind == JsonValueKind.Number
                   && value.GetInt32() == 1;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Scale of the focused monitor from <c>hyprctl monitors -j</c>, or the first one when none is focused.</summary>
    public static double? FocusedMonitorScale(string? monitorsJson)
    {
        if (string.IsNullOrWhiteSpace(monitorsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(monitorsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            double? first = null;
            foreach (var monitor in doc.RootElement.EnumerateArray())
            {
                if (monitor.ValueKind != JsonValueKind.Object
                    || !monitor.TryGetProperty("scale", out var scaleElement)
                    || scaleElement.ValueKind != JsonValueKind.Number)
                    continue;

                double scale = scaleElement.GetDouble();
                first ??= scale;
                if (monitor.TryGetProperty("focused", out var focused) && focused.ValueKind == JsonValueKind.True)
                    return scale;
            }
            return first;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? RunHyprctl(params string[] args)
    {
        string? hyprctl = ProcessLauncher.FindExecutable("hyprctl");
        if (hyprctl is null) return null;
        try
        {
            var start = new ProcessStartInfo(hyprctl)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args) start.ArgumentList.Add(arg);

            using var process = Process.Start(start);
            if (process is null) return null;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(1000))
            {
                process.Kill();
                return null;
            }
            return output.Wait(500) ? output.Result : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
