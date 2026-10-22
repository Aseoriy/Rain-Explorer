using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using RainExplorer.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace RainExplorer.Tests;

/// <summary>
/// Points every XDG directory at a private temp root before any Rain service is touched,
/// so tests never read or write the real settings, recents or trash.
/// </summary>
internal static class TestEnvironment
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "rain-explorer-tests-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(Root, "config"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(Root, "data"));
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(Root, "cache"));
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", Path.Combine(Root, "runtime"));
        Directory.CreateDirectory(Path.Combine(Root, "config"));
        Directory.CreateDirectory(Path.Combine(Root, "data"));
        Directory.CreateDirectory(Path.Combine(Root, "runtime"));
        File.SetUnixFileMode(Path.Combine(Root, "runtime"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // Exercise SVG rendering without depending on the host's theme or settings.
        File.WriteAllText(Path.Combine(Root, "config", "kdeglobals"), "[Icons]\nTheme=RainTest\n");
        string theme = Path.Combine(Root, "data", "icons", "RainTest");
        string icons = Path.Combine(theme, "scalable", "places");
        Directory.CreateDirectory(icons);
        File.WriteAllText(Path.Combine(theme, "index.theme"),
            "[Icon Theme]\nName=Rain test\nDirectories=scalable/places\nInherits=hicolor\n" +
            "[scalable/places]\nSize=48\nType=Scalable\nMinSize=16\nMaxSize=256\n");
        foreach (string name in new[] { "folder", "text-x-generic" })
            File.WriteAllText(Path.Combine(icons, name + ".svg"),
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"48\" height=\"48\"><rect x=\"4\" y=\"8\" width=\"40\" height=\"32\" rx=\"4\" fill=\"#54a8e5\"/></svg>");
    }

    /// <summary>A fresh scratch folder on the same filesystem as the test trash.</summary>
    public static string NewFolder(string name)
    {
        string path = Path.Combine(Root, "work", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }
}

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
