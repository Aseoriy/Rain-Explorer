using System.Windows;
using RainArchives.App.Infrastructure;

namespace RainArchives.App;

public partial class App : Application
{
    private AppThemeManager? _themeManager;

    internal AppThemeManager ThemeManager => _themeManager ??= new AppThemeManager(this);

    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Initialize();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _themeManager?.Dispose();
        base.OnExit(e);
    }
}
