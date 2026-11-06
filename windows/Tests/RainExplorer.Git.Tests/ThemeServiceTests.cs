using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

[CollectionDefinition("WPF application resources", DisableParallelization = true)]
public sealed class WpfApplicationResourceCollection;

[Collection("WPF application resources")]
public sealed class ThemeServiceTests
{
    private static readonly string[] ButtonStyleKeys =
    [
        "IconButton",
        "CloseButton",
        "DialogButton",
        "GhostButton",
        "PrimaryButton",
    ];

    private static readonly string[] MainWindowDynamicPaletteKeys =
    [
        "MenuBg",
        "Line1",
        "Line2",
        "Glass2",
        "Glass3",
        "Glass4",
        "Text",
        "TextDim",
        "TextMuted",
        "TextFaint",
        "AccentBright",
        "AccentLine",
        "AccentWash",
    ];

    [Fact]
    public void SharedMenusAndButtonsUseSelectedThemeResourcesOnTheExistingSta()
    {
        RunOnApplicationSta(() =>
        {
            EnsureApplicationResources();

            ThemeService.ApplyTheme("Violet");

            var menu = CreateContextMenu();
            menu.Items.Add(new MenuItem { Header = "_Open" });
            var dialogButton = CreateDialogButton();
            dialogButton.ContextMenu = menu;
            var root = new Grid();
            root.Children.Add(dialogButton);
            var window = new Window
            {
                Width = 320,
                Height = 180,
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
                ShowInTaskbar = false,
                Opacity = 0,
                WindowStyle = WindowStyle.ToolWindow,
                Content = root,
            };
            bool shown = false;
            try
            {
                window.Show();
                shown = true;
                window.UpdateLayout();
                menu.PlacementTarget = dialogButton;
                menu.IsOpen = true;
                PumpDispatcher();

                Assert.Equal(Color.FromArgb(0xFF, 0x2D, 0x1F, 0x3C), GetBrush(menu.Background).Color);
                Assert.Equal(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), GetBrush(dialogButton.Background).Color);
                Assert.True(FindVisual<ContentPresenter>(dialogButton).RecognizesAccessKey);

                ThemeService.ApplyTheme("Light");
                PumpDispatcher();
                Assert.Equal(Color.FromArgb(0xFF, 0xF7, 0xF5, 0xFB), GetBrush(menu.Background).Color);
                Assert.Equal(Color.FromArgb(0x1E, 0x00, 0x00, 0x00), GetBrush(dialogButton.Background).Color);
                Assert.Equal(Color.FromArgb(0xFF, 0x0B, 0x0A, 0x10), GetBrush(dialogButton.Foreground).Color);

                // Mono is one of the supported custom-background themes. This
                // also exercises replacement of frozen XAML Freezables while
                // the popup and its placement target are already presented.
                ThemeService.ApplyTheme("Mono");
                PumpDispatcher();
                Assert.Equal(Color.FromArgb(0xFF, 0x25, 0x25, 0x27), GetBrush(menu.Background).Color);
                Assert.Equal(Color.FromArgb(0xFF, 0xF5, 0xF4, 0xFB), GetBrush(dialogButton.Foreground).Color);
            }
            finally
            {
                if (menu.IsOpen)
                    menu.IsOpen = false;
                if (shown)
                    window.Close();
                ThemeService.ApplyTheme("Violet");
            }
        });
    }

    [Fact]
    public void DarkThemesDeriveOpaqueDistinctSecondarySurfacePalettes()
    {
        RunOnApplicationSta(() =>
        {
            EnsureApplicationResources();
            var resources = Application.Current!.Resources;

            SurfacePalette violet = ApplyAndCapture(resources, "Violet");
            SurfacePalette mono = ApplyAndCapture(resources, "Mono");
            SurfacePalette ocean = ApplyAndCapture(resources, "Ocean");
            SurfacePalette midnight = ApplyAndCapture(resources, "Midnight");

            AssertOpaque(violet);
            AssertOpaque(mono);
            AssertOpaque(ocean);
            AssertOpaque(midnight);

            AssertDifferent(violet, mono);
            AssertDifferent(violet, ocean);
            AssertDifferent(violet, midnight);
            AssertDifferent(mono, ocean);
            AssertDifferent(mono, midnight);
            AssertDifferent(ocean, midnight);

            foreach (Color color in SurfaceColors(mono))
            {
                Assert.InRange(Math.Abs((int)color.R - color.G), 0, 2);
                Assert.InRange(Math.Abs((int)color.G - color.B), 0, 2);
            }

            ThemeService.ApplyTheme("Violet");
        });
    }

    [Fact]
    public void EverySharedButtonTemplateRecognizesAccessKeysWithoutChangingContentObjects()
    {
        RunOnApplicationSta(() =>
        {
            EnsureApplicationResources();
            ThemeService.ApplyTheme("Violet");

            foreach (string key in ButtonStyleKeys)
            {
                var content = new TextBlock { Text = "already-rendered" };
                var button = new Button
                {
                    Content = content,
                    Style = (Style)Application.Current!.FindResource(key),
                };

                ContentPresenter presenter = FindVisual<ContentPresenter>(button);
                Assert.True(presenter.RecognizesAccessKey, key);
                Assert.Same(content, button.Content);
            }
        });
    }

    [Fact]
    public void MainWindowRuntimePaletteReferencesAreDynamic()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Main", "MainWindow.xaml"));

        foreach (string key in MainWindowDynamicPaletteKeys)
        {
            Assert.DoesNotContain($"{{StaticResource {key}}}", source, StringComparison.Ordinal);
        }
    }

    private static SolidColorBrush GetBrush(Brush? brush) =>
        Assert.IsType<SolidColorBrush>(brush);

    private static SurfacePalette ApplyAndCapture(ResourceDictionary resources, string theme)
    {
        ThemeService.ApplyTheme(theme);
        return new SurfacePalette(
            GetColor(resources, "MenuBg"),
            GetColor(resources, "BgRaised"),
            GetColor(resources, "BgChrome"),
            GetColor(resources, "BgAlt"),
            GetColor(resources, "BgSide"));
    }

    private static Color GetColor(ResourceDictionary resources, string key) =>
        Assert.IsType<SolidColorBrush>(resources[key]).Color;

    private static ContextMenu CreateContextMenu() =>
        new()
        {
            Style = (Style)Application.Current!.FindResource(typeof(ContextMenu)),
        };

    private static Button CreateDialogButton() =>
        new()
        {
            Content = "_Cancel",
            Style = (Style)Application.Current!.FindResource("DialogButton"),
        };

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

    private static void AssertOpaque(SurfacePalette palette) =>
        Assert.All(SurfaceColors(palette), color => Assert.Equal(0xFF, color.A));

    private static void AssertDifferent(SurfacePalette first, SurfacePalette second)
    {
        Assert.NotEqual(first.MenuBg, second.MenuBg);
        Assert.NotEqual(first.BgRaised, second.BgRaised);
        Assert.NotEqual(first.BgChrome, second.BgChrome);
        Assert.NotEqual(first.BgAlt, second.BgAlt);
        Assert.NotEqual(first.BgSide, second.BgSide);
    }

    private static IEnumerable<Color> SurfaceColors(SurfacePalette palette)
    {
        yield return palette.MenuBg;
        yield return palette.BgRaised;
        yield return palette.BgChrome;
        yield return palette.BgAlt;
        yield return palette.BgSide;
    }

    private static T FindVisual<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is FrameworkElement element)
            element.ApplyTemplate();
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            try { return FindVisual<T>(child); }
            catch (InvalidOperationException) { }
        }

        throw new InvalidOperationException($"A {typeof(T).Name} was not found in the template.");
    }

    private static void EnsureApplicationResources()
    {
        if (Application.Current is null)
            throw new InvalidOperationException("The shared WPF STA did not initialize Application.Current.");

        if (Application.Current.TryFindResource("Text") is null)
        {
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/RainExplorer;component/Themes/Dark.xaml",
                    UriKind.Absolute),
            });
        }
    }

    private static void RunOnApplicationSta(Action action) =>
        ArchivePasswordDialogTests.RunSta(action);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Main", "MainWindow.xaml")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Rain Explorer repository root was not found.");
    }

    private readonly record struct SurfacePalette(
        Color MenuBg,
        Color BgRaised,
        Color BgChrome,
        Color BgAlt,
        Color BgSide);
}
