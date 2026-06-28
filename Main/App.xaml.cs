using System.ComponentModel;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LaunchFolder = e.Args.FirstOrDefault(a => Directory.Exists(a));

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
}
