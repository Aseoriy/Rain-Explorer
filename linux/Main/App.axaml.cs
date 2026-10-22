using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;

namespace RainExplorer;

public partial class App : Application
{
    /// <summary>An existing folder passed on the command line (or the folder of a passed file).</summary>
    public static string? LaunchFolder { get; private set; }

    /// <summary>A file to highlight once <see cref="LaunchFolder"/> opens (e.g. "Show in folder").</summary>
    public static string? SelectPath { get; set; }

    /// <summary>How many main windows are currently open (session saving only writes for one).</summary>
    public static int OpenMainWindowCount =>
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
        .OfType<MainWindow>().Count(w => w.IsVisible) ?? 0;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            SingleInstance.StartListening();
            SingleInstance.ArgumentsReceived += args => Dispatcher.UIThread.Post(() => OpenForwarded(args));

            // Last-resort safety net: log unhandled UI-thread exceptions and keep the app
            // alive rather than hard-crashing on a stray binding/render fault.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                LogCrash(e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) LogCrash(ex);
            };

            ResolveLaunchTarget(desktop.Args ?? []);

            var settings = SettingsStore.Instance.Settings;
            // Migrate legacy/unknown theme names to the brand default.
            if (!ThemeService.Names.Contains(settings.Theme))
                settings.Theme = "Violet";

            ThemeService.ApplyTheme(settings.Theme);
            ThemeService.ApplyDensity(settings.Density);
            ThemeService.ApplyFont(settings.FontFamily);
            settings.PropertyChanged += OnSettingsChanged;

            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            desktop.Exit += (_, _) =>
            {
                RenderPerformance.Shutdown();
                FileManagerIntegration.Instance.Stop();
                SingleInstance.Stop();
                SettingsStore.Instance.Flush();
            };
            desktop.MainWindow = new MainWindow();
            FileManagerIntegration.Instance.Start(ShowFileManagerRequestAsync);
            if (desktop.Args?.Contains("--select", StringComparer.Ordinal) == true)
                desktop.MainWindow.Opened += async (_, _) => await ShowArgumentSelectionAsync(desktop.Args);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        var s = SettingsStore.Instance.Settings;
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Theme): ThemeService.ApplyTheme(s.Theme); break;
            case nameof(AppSettings.Density): ThemeService.ApplyDensity(s.Density); break;
            case nameof(AppSettings.FontFamily): ThemeService.ApplyFont(s.FontFamily); break;
        }
    }

    /// <summary>Interpret command-line arguments: a folder opens directly; a file opens its
    /// folder with that file selected.</summary>
    private static void ResolveLaunchTarget(string[] args)
    {
        bool reveal = args.Contains("--select", StringComparer.Ordinal);
        foreach (string arg in args)
        {
            if (SingleInstance.ToLocalPath(arg) is not { } path) continue;
            if (Directory.Exists(path))
            {
                LaunchFolder = reveal ? Path.GetDirectoryName(path) ?? path : path;
                if (reveal) SelectPath = path;
                return;
            }
            if (File.Exists(path))
            {
                LaunchFolder = Path.GetDirectoryName(path);
                SelectPath = path;
                return;
            }
        }
    }

    /// <summary>A second launch forwarded its arguments: open them in a new tab of the
    /// most recently active window and bring it to the front.</summary>
    private static async void OpenForwarded(string[] args)
    {
        if (args.Contains("--select", StringComparer.Ordinal))
        {
            await ShowArgumentSelectionAsync(args);
            return;
        }
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var window = desktop.Windows.OfType<MainWindow>().LastOrDefault(w => w.IsActive)
                     ?? desktop.Windows.OfType<MainWindow>().LastOrDefault();
        if (window is null) return;

        string? target = null;
        string? select = null;
        foreach (string arg in args)
        {
            if (SingleInstance.ToLocalPath(arg) is not { } path) continue;
            if (Directory.Exists(path)) { target = path; break; }
            if (File.Exists(path)) { target = Path.GetDirectoryName(path); select = path; break; }
        }

        window.OpenForwardedTarget(target, select);
    }

    private static async Task ShowArgumentSelectionAsync(string[] args)
    {
        try
        {
            var request = await Task.Run(() =>
            {
                string[] uris = args.Select(SingleInstance.ToLocalPath).OfType<string>()
                    .Select(PathUtil.ToFileUri).ToArray();
                return FileManagerRequest.Resolve(FileManagerAction.ShowItems, uris, "");
            });
            await ShowFileManagerRequestAsync(request);
        }
        catch (Exception ex)
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.Windows.OfType<MainWindow>().LastOrDefault()?.DataContext is MainViewModel vm
                && vm.ActivePane.SelectedTab is { } tab) tab.Status = $"⚠️ {ex.Message}";
        }
    }

    private static async Task ShowFileManagerRequestAsync(FileManagerRequest request)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                throw new InvalidOperationException("Rain's desktop window is not available.");
            var window = desktop.Windows.OfType<MainWindow>().LastOrDefault(window => window.IsActive)
                ?? desktop.Windows.OfType<MainWindow>().LastOrDefault()
                ?? desktop.MainWindow as MainWindow;
            if (window is null) throw new InvalidOperationException("Rain is closing.");
            if (!window.IsVisible) window.Show();
            await window.ShowFileManagerRequestAsync(request);
        });
    }

    public static void LogCrash(Exception ex)
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
}
