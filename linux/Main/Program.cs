using Avalonia;
using RainExplorer.Services;

namespace RainExplorer;

internal static class Program
{
    // Avalonia configuration; don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static int Main(string[] args)
    {
        if (SingleInstance.TryForwardToRunningInstance(args))
            return 0;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = "rain-explorer" })
            .AfterPlatformServicesSetup(_ => RenderPerformance.Install())
            .LogToTrace();
}
