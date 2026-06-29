using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer;

public partial class App : Application
{
    /// <summary>A folder passed on the command line (e.g. from the shell verb), if it exists.</summary>
    public static string? LaunchFolder { get; private set; }

    /// <summary>A file to highlight once <see cref="LaunchFolder"/> opens — set when we're
    /// launched via "Show in folder" / "Reveal in File Explorer" (the /select form).</summary>
    public static string? SelectPath { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Last-resort safety net: log unhandled UI-thread exceptions and keep the
        // app alive rather than hard-crashing on a stray binding/render fault.
        DispatcherUnhandledException += (_, ex) =>
        {
            LogCrash(ex.Exception);
            MessageBox.Show("Rain Explorer hit an unexpected error but will keep running.\n\n"
                + ex.Exception.Message, "Rain Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };

        // Interpret what the shell handed us. If it's a virtual location we can't
        // render (This PC, Recycle Bin, Control Panel, …) this forwards to Windows
        // Explorer and asks us to bow out.
        if (!ResolveLaunchTarget(e.Args))
        {
            Shutdown();
            return;
        }

        var settings = SettingsStore.Instance.Settings;

        // Migrate legacy theme names ("Dark"/anything unknown) to the brand default
        // so the Settings picker reflects the active theme.
        if (!ThemeService.Names.Contains(settings.Theme))
            settings.Theme = "Violet";

        ThemeService.ApplyTheme(settings.Theme);
        ThemeService.ApplyDensity(settings.Density);
        ThemeService.ApplyFont(settings.FontFamily);

        // Re-register the default-handler keys on launch so they always point at the
        // current exe location (e.g. after the app is moved or updated).
        if (settings.SetAsDefaultExplorer) ShellIntegrationService.ApplyDefaultHandler(true);

        // Re-apply theme/density/font whenever they change in the settings panel.
        settings.PropertyChanged += OnSettingsChanged;

        // Create the main window explicitly (no StartupUri) — see App.xaml.
        new MainWindow().Show();
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainExplorer");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:u}  {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        var settings = SettingsStore.Instance.Settings;
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Theme): ThemeService.ApplyTheme(settings.Theme); break;
            case nameof(AppSettings.Density): ThemeService.ApplyDensity(settings.Density); break;
            case nameof(AppSettings.FontFamily): ThemeService.ApplyFont(settings.FontFamily); break;
            case nameof(AppSettings.RegisterShellIntegration):
                ShellIntegrationService.Apply(settings.RegisterShellIntegration); break;
            case nameof(AppSettings.SetAsDefaultExplorer):
                ShellIntegrationService.ApplyDefaultHandler(settings.SetAsDefaultExplorer); break;
        }
    }

    /// <summary>
    /// Work out which folder (and optional file to highlight) the command line points at.
    /// Handles a plain folder path, a file path (opens its parent and remembers the file),
    /// and the "/select,&lt;path&gt;" form that "Show in folder" / "Reveal in File Explorer"
    /// produce via SHOpenFolderAndSelectItems. Returns <c>true</c> to keep launching.
    /// Returns <c>false</c> when the argument is a virtual shell location we can't display —
    /// in that case it relaunches the real Explorer and the caller should shut down.
    /// </summary>
    private static bool ResolveLaunchTarget(string[] args)
    {
        foreach (string raw in args)
        {
            string p = StripSelectSwitch(raw).Trim().Trim('"');
            if (p.Length == 0) continue;

            if (Directory.Exists(p)) { LaunchFolder = p; return true; }

            if (File.Exists(p))
            {
                LaunchFolder = Path.GetDirectoryName(p);
                SelectPath = p;
                return true;
            }

            // A virtual shell location (::{CLSID}, shell:Foo) — This PC, Recycle Bin,
            // Control Panel, etc. We can't render those, so hand the whole invocation
            // back to Windows Explorer rather than opening to nothing.
            if (LooksLikeShellPath(p)) { ForwardToExplorer(args); return false; }
        }

        return true;   // nothing usable on the command line — fall through to Home
    }

    // "Show in folder" callers pass e.g. /select,"C:\path\file.ext". Peel the switch
    // off so what's left is a bare path. Covers the handful of explorer-style switches.
    private static string StripSelectSwitch(string arg)
    {
        foreach (string sw in new[] { "/select,", "-select,", "/select", "/n,", "/e,", "/root," })
            if (arg.StartsWith(sw, StringComparison.OrdinalIgnoreCase))
                return arg[sw.Length..];
        return arg;
    }

    private static bool LooksLikeShellPath(string p) =>
        p.StartsWith("::") || p.StartsWith("{") || p.Contains("::{")
        || p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);

    private static void ForwardToExplorer(string[] args)
    {
        try
        {
            string explorer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var psi = new ProcessStartInfo { FileName = explorer, UseShellExecute = false };
            foreach (string a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch { /* best-effort; don't crash on a virtual location we can't forward */ }
    }
}
