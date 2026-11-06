using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Infrastructure;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class AppThemeTests
{
    private static readonly string[] SemanticBrushKeys =
    [
        "BackgroundBrush",
        "SurfaceBrush",
        "SurfaceMutedBrush",
        "SurfaceRaisedBrush",
        "BorderBrush",
        "BorderStrongBrush",
        "TextBrush",
        "MutedTextBrush",
        "SubtleTextBrush",
        "AccentBrush",
        "AccentHoverBrush",
        "AccentPressedBrush",
        "AccentForegroundBrush",
        "AccentWashBrush",
        "FocusBrush",
        "SuccessBrush",
        "WarningBrush",
        "DangerBrush",
        "SelectionBrush",
        "DisabledTextBrush",
        "BackgroundForegroundBrush",
        "BackgroundSecondaryForegroundBrush",
        "SurfaceForegroundBrush",
        "SurfaceSecondaryForegroundBrush",
        "SurfaceAccentForegroundBrush",
        "SurfaceMutedForegroundBrush",
        "SurfaceMutedSecondaryForegroundBrush",
        "SurfaceMutedAccentForegroundBrush",
        "SurfaceMutedSuccessForegroundBrush",
        "SurfaceRaisedForegroundBrush",
        "SurfaceRaisedSecondaryForegroundBrush",
        "AccentWashForegroundBrush",
        "SelectionForegroundBrush",
        "SelectionBorderBrush",
    ];

    [TestMethod]
    public void ThemeResolverFollowsPreferenceButKeepsHighContrastAuthoritative()
    {
        Assert.AreEqual(
            EffectiveAppTheme.Light,
            AppThemeManager.ResolveEffectiveTheme(
                AppThemePreference.System,
                highContrast: false,
                systemUsesLightTheme: true));
        Assert.AreEqual(
            EffectiveAppTheme.Dark,
            AppThemeManager.ResolveEffectiveTheme(
                AppThemePreference.System,
                highContrast: false,
                systemUsesLightTheme: false));
        Assert.AreEqual(
            EffectiveAppTheme.Light,
            AppThemeManager.ResolveEffectiveTheme(
                AppThemePreference.Light,
                highContrast: false,
                systemUsesLightTheme: false));
        Assert.AreEqual(
            EffectiveAppTheme.Dark,
            AppThemeManager.ResolveEffectiveTheme(
                AppThemePreference.Dark,
                highContrast: false,
                systemUsesLightTheme: true));

        foreach (var preference in Enum.GetValues<AppThemePreference>())
        {
            Assert.AreEqual(
                EffectiveAppTheme.HighContrast,
                AppThemeManager.ResolveEffectiveTheme(
                    preference,
                    highContrast: true,
                    systemUsesLightTheme: preference == AppThemePreference.Dark));
        }
    }

    [TestMethod]
    public void ThemePalettesExposeTheSameSemanticBrushContractOnSta()
    {
        RunOnStaThread(() =>
        {
            var light = LoadDictionary("Light");
            var dark = LoadDictionary("Dark");
            var highContrast = LoadDictionary("HighContrast");

            foreach (var key in SemanticBrushKeys)
            {
                Assert.IsInstanceOfType<SolidColorBrush>(light[key]);
                Assert.IsInstanceOfType<SolidColorBrush>(dark[key]);
                Assert.IsInstanceOfType<SolidColorBrush>(highContrast[key]);
            }

            Assert.AreNotEqual(
                ((SolidColorBrush)light["BackgroundBrush"]).Color,
                ((SolidColorBrush)dark["BackgroundBrush"]).Color);
            Assert.IsTrue(light.Contains(SystemColors.WindowBrushKey));
            Assert.IsTrue(dark.Contains(SystemColors.WindowBrushKey));
            Assert.IsFalse(
                highContrast.Contains(SystemColors.WindowBrushKey),
                "High Contrast must fall through to the user's authoritative SystemColors.");
        });
    }

    [TestMethod]
    public void ThemeManagerSwapsAnExactPaletteAndRaisesOneChangeOnSta()
    {
        RunOnStaThread(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(LoadDictionary("Light"));
            resources.MergedDictionaries.Add(LoadDesignResources());
            using var manager = new AppThemeManager(
                resources,
                Dispatcher.CurrentDispatcher,
                readSystemUsesLightTheme: () => false,
                readHighContrast: () => false,
                loadPalette: theme => LoadDictionary(theme.ToString()));
            var changeCount = 0;
            manager.ThemeChanged += (_, _) => changeCount++;

            manager.Initialize();
            Assert.AreEqual(EffectiveAppTheme.Dark, manager.EffectiveTheme);
            Assert.AreEqual(
                ((SolidColorBrush)LoadDictionary("Dark")["AccentBrush"]).Color,
                ((SolidColorBrush)resources.MergedDictionaries[0]["AccentBrush"]).Color);
            Assert.AreEqual(1, changeCount);

            manager.SetPreference(AppThemePreference.Light);
            Assert.AreEqual(AppThemePreference.Light, manager.Preference);
            Assert.AreEqual(EffectiveAppTheme.Light, manager.EffectiveTheme);
            Assert.AreEqual(
                ((SolidColorBrush)LoadDictionary("Light")["AccentBrush"]).Color,
                ((SolidColorBrush)resources.MergedDictionaries[0]["AccentBrush"]).Color);
            Assert.AreEqual(2, changeCount);

            manager.SetPreference(AppThemePreference.Dark);
            Assert.AreEqual(EffectiveAppTheme.Dark, manager.EffectiveTheme);
            Assert.AreEqual(3, changeCount);
            Assert.AreEqual(2, resources.MergedDictionaries.Count);
        });
    }

    [TestMethod]
    public void WindowChromeAttachmentIsIdempotentThemeAwareAndDetachedOnCloseOnSta()
    {
        RunOnStaThread(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(LoadDictionary("Light"));
            resources.MergedDictionaries.Add(LoadDesignResources());
            var chromeApplications = new List<(IntPtr Handle, bool IsDark)>();
            using var manager = new AppThemeManager(
                resources,
                Dispatcher.CurrentDispatcher,
                readSystemUsesLightTheme: () => true,
                readHighContrast: () => false,
                loadPalette: theme => LoadDictionary(theme.ToString()),
                applyWindowChrome: (handle, isDark) =>
                    chromeApplications.Add((handle, isDark)));
            manager.Initialize();

            var window = new Window
            {
                Width = 240,
                Height = 140,
                ShowInTaskbar = false,
                Content = new TextBlock { Text = "Caption probe" },
            };
            var firstAttachment = manager.AttachWindowChrome(window);
            var repeatedAttachment = manager.AttachWindowChrome(window);
            Assert.AreSame(firstAttachment, repeatedAttachment);
            Assert.AreEqual(1, manager.AttachedWindowCount);

            window.Show();
            window.UpdateLayout();
            Assert.AreEqual(1, chromeApplications.Count);
            Assert.IsFalse(chromeApplications[0].IsDark);
            Assert.AreNotEqual(IntPtr.Zero, chromeApplications[0].Handle);

            manager.SetPreference(AppThemePreference.Dark);
            Assert.AreEqual(2, chromeApplications.Count);
            Assert.IsTrue(chromeApplications[1].IsDark);

            manager.SetPreference(AppThemePreference.Light);
            Assert.AreEqual(3, chromeApplications.Count);
            Assert.IsFalse(chromeApplications[2].IsDark);

            window.Close();
            Assert.AreEqual(0, manager.AttachedWindowCount);
            manager.SetPreference(AppThemePreference.Dark);
            Assert.AreEqual(
                3,
                chromeApplications.Count,
                "A closed window remained subscribed to theme-driven caption updates.");
            firstAttachment.Dispose();
            repeatedAttachment.Dispose();

            var detachedWindow = CreateDetachedWindowReference(manager);
            Assert.AreEqual(0, manager.AttachedWindowCount);
            for (var attempt = 0; attempt < 3 && detachedWindow.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.IsFalse(
                detachedWindow.IsAlive,
                "A disposed caption attachment retained its Window target.");
        });
    }

    [TestMethod]
    public void InspectorWidthModelIsBoundedAndProportional()
    {
        Assert.AreEqual(320, RainArchives.App.MainWindow.CalculateInspectorWidth(double.NaN));
        Assert.AreEqual(320, RainArchives.App.MainWindow.CalculateInspectorWidth(760));
        Assert.AreEqual(360, RainArchives.App.MainWindow.CalculateInspectorWidth(1000));
        Assert.AreEqual(380, RainArchives.App.MainWindow.CalculateInspectorWidth(1400));
        Assert.IsTrue(RainArchives.App.MainWindow.CalculateInspectorWidth(1000) <= 1000 * 0.4);
    }

    [TestMethod]
    public void HighContrastPaletteAndMainShellDeclarePairedSurfaceRoles()
    {
        var highContrast = XDocument.Load(GetThemePath("HighContrast"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var brushColors = highContrast
            .Descendants(presentation + "SolidColorBrush")
            .ToDictionary(
                brush => (string?)brush.Attribute(x + "Key") ?? string.Empty,
                brush => (string?)brush.Attribute("Color") ?? string.Empty,
                StringComparer.Ordinal);

        AssertSystemColor(brushColors, "BackgroundBrush", "WindowColorKey");
        AssertSystemColor(brushColors, "BackgroundForegroundBrush", "WindowTextColorKey");
        AssertSystemColor(brushColors, "BackgroundSecondaryForegroundBrush", "WindowTextColorKey");
        AssertSystemColor(brushColors, "SurfaceBrush", "WindowColorKey");
        AssertSystemColor(brushColors, "SurfaceForegroundBrush", "WindowTextColorKey");
        AssertSystemColor(brushColors, "SurfaceSecondaryForegroundBrush", "WindowTextColorKey");
        AssertSystemColor(brushColors, "SurfaceAccentForegroundBrush", "WindowTextColorKey");
        AssertSystemColor(brushColors, "SurfaceMutedBrush", "ControlColorKey");
        AssertSystemColor(brushColors, "SurfaceMutedForegroundBrush", "ControlTextColorKey");
        AssertSystemColor(brushColors, "SurfaceMutedSecondaryForegroundBrush", "ControlTextColorKey");
        AssertSystemColor(brushColors, "SurfaceMutedAccentForegroundBrush", "ControlTextColorKey");
        AssertSystemColor(brushColors, "SurfaceMutedSuccessForegroundBrush", "ControlTextColorKey");
        AssertSystemColor(brushColors, "SurfaceRaisedBrush", "WindowColorKey");
        AssertSystemColor(brushColors, "SurfaceRaisedForegroundBrush", "WindowTextColorKey");
        AssertSystemColor(brushColors, "SurfaceRaisedSecondaryForegroundBrush", "WindowTextColorKey");
        AssertSystemColor(brushColors, "AccentBrush", "HighlightColorKey");
        AssertSystemColor(brushColors, "AccentForegroundBrush", "HighlightTextColorKey");
        AssertSystemColor(brushColors, "AccentWashBrush", "ControlColorKey");
        AssertSystemColor(brushColors, "AccentWashForegroundBrush", "ControlTextColorKey");
        AssertSystemColor(brushColors, "SelectionBrush", "HighlightColorKey");
        AssertSystemColor(brushColors, "SelectionForegroundBrush", "HighlightTextColorKey");
        AssertSystemColor(brushColors, "SelectionBorderBrush", "HighlightTextColorKey");

        var mainWindow = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RainArchives.App",
            "MainWindow.xaml"));
        AssertMainShellSurfacePair(mainWindow, "SurfaceBrush", "SurfaceForegroundBrush");
        AssertMainShellSurfacePair(mainWindow, "SurfaceMutedBrush", "SurfaceMutedForegroundBrush");
        AssertMainShellSurfacePair(mainWindow, "SurfaceRaisedBrush", "SurfaceRaisedForegroundBrush");

        var accentWashConsumers = mainWindow
            .Descendants()
            .Where(element => string.Equals(
                (string?)element.Attribute("Background"),
                "{DynamicResource AccentWashBrush}",
                StringComparison.Ordinal))
            .ToArray();
        Assert.IsTrue(accentWashConsumers.Length > 0, "No AccentWash surface consumers were found.");
        foreach (var consumer in accentWashConsumers)
        {
            Assert.AreEqual(
                "{DynamicResource AccentWashForegroundBrush}",
                (string?)consumer.Attribute("BorderBrush"));
            foreach (var icon in consumer.Descendants(presentation + "Path"))
            {
                Assert.AreEqual(
                    "{DynamicResource AccentWashForegroundBrush}",
                    (string?)icon.Attribute("Stroke"));
            }
        }
    }

    [TestMethod]
    public void SurfaceStylesResolveSyntheticHighContrastPairsOnSta()
    {
        RunOnStaThread(() =>
        {
            var surfaceMuted = Color.FromRgb(17, 43, 71);
            var surfaceMutedForeground = Color.FromRgb(239, 213, 187);
            var surfaceMutedSecondary = Color.FromRgb(222, 196, 170);
            var surfaceMutedAccent = Color.FromRgb(205, 179, 153);
            var selection = Color.FromRgb(53, 79, 107);
            var selectionForeground = Color.FromRgb(255, 231, 94);
            var surface = Color.FromRgb(8, 24, 40);
            var surfaceForeground = Color.FromRgb(244, 248, 252);
            var surfaceSecondary = Color.FromRgb(210, 218, 226);

            var palette = LoadDictionary("Light");
            palette["SurfaceBrush"] = new SolidColorBrush(surface);
            palette["SurfaceForegroundBrush"] = new SolidColorBrush(surfaceForeground);
            palette["SurfaceSecondaryForegroundBrush"] = new SolidColorBrush(surfaceSecondary);
            palette["SurfaceMutedBrush"] = new SolidColorBrush(surfaceMuted);
            palette["SurfaceMutedForegroundBrush"] = new SolidColorBrush(surfaceMutedForeground);
            palette["SurfaceMutedSecondaryForegroundBrush"] = new SolidColorBrush(surfaceMutedSecondary);
            palette["SurfaceMutedAccentForegroundBrush"] = new SolidColorBrush(surfaceMutedAccent);
            palette["SelectionBrush"] = new SolidColorBrush(selection);
            palette["SelectionForegroundBrush"] = new SolidColorBrush(selectionForeground);
            palette["SelectionBorderBrush"] = new SolidColorBrush(selectionForeground);

            var window = new Window
            {
                Width = 520,
                Height = 420,
                ShowInTaskbar = false,
                Content = new StackPanel(),
            };
            window.Resources.MergedDictionaries.Add(palette);
            window.Resources.MergedDictionaries.Add(LoadDesignResources());
            var panel = (StackPanel)window.Content;

            var browserButton = new Button
            {
                Content = "Browser action",
                Style = Assert.IsInstanceOfType<Style>(window.FindResource("BrowserButtonStyle")),
            };
            var columnHeader = new DataGridColumnHeader
            {
                Content = "Name",
                Style = Assert.IsInstanceOfType<Style>(window.FindResource("ArchiveColumnHeaderStyle")),
            };
            var statusText = new TextBlock { Text = "Status" };
            var statusChip = new Border
            {
                Child = statusText,
                Style = Assert.IsInstanceOfType<Style>(window.FindResource("StatusChipStyle")),
            };
            var entryIconTemplate = Assert.IsInstanceOfType<DataTemplate>(XamlReader.Parse(
                """
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <Path Style="{DynamicResource ArchiveFolderIconStyle}" />
                </DataTemplate>
                """));
            var entriesGrid = new DataGrid
            {
                AlternationCount = 2,
                AutoGenerateColumns = false,
                Height = 130,
                ItemsSource = new[] { "Selected", "Alternating" },
                SelectedIndex = 0,
                Style = Assert.IsInstanceOfType<Style>(window.FindResource("ArchiveDataGridStyle")),
            };
            entriesGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Entry",
                CellTemplate = entryIconTemplate,
            });

            panel.Children.Add(browserButton);
            panel.Children.Add(columnHeader);
            panel.Children.Add(statusChip);
            panel.Children.Add(entriesGrid);

            try
            {
                window.Show();
                window.UpdateLayout();
                entriesGrid.UpdateLayout();

                var selectedRow = Assert.IsInstanceOfType<DataGridRow>(
                    entriesGrid.ItemContainerGenerator.ContainerFromIndex(0));
                var alternatingRow = Assert.IsInstanceOfType<DataGridRow>(
                    entriesGrid.ItemContainerGenerator.ContainerFromIndex(1));
                var selectedIcon = FindVisualDescendant<System.Windows.Shapes.Path>(selectedRow);
                Assert.IsNotNull(selectedIcon);
                var alternatingIcon = FindVisualDescendant<System.Windows.Shapes.Path>(alternatingRow);
                Assert.IsNotNull(alternatingIcon);

                AssertBrushColor(browserButton.Background, surfaceMuted, "BrowserButton background");
                AssertBrushColor(browserButton.Foreground, surfaceMutedForeground, "BrowserButton foreground");
                AssertBrushColor(columnHeader.Background, surfaceMuted, "column-header background");
                AssertBrushColor(columnHeader.Foreground, surfaceMutedSecondary, "column-header foreground");
                AssertBrushColor(statusChip.Background, surfaceMuted, "muted shell background");
                AssertBrushColor(statusText.Foreground, surfaceMutedForeground, "muted shell foreground");
                AssertBrushColor(alternatingRow.Background, surfaceMuted, "alternating-row background");
                AssertBrushColor(alternatingRow.Foreground, surfaceMutedForeground, "alternating-row foreground");
                AssertBrushColor(alternatingIcon.Stroke, surfaceMutedAccent, "alternating-row icon");
                AssertBrushColor(selectedRow.Background, selection, "selected-row background");
                AssertBrushColor(selectedRow.Foreground, selectionForeground, "selected-row foreground");
                AssertBrushColor(selectedIcon.Stroke, selectionForeground, "selected-row icon");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static ResourceDictionary LoadDictionary(string name)
    {
        using var stream = File.OpenRead(GetThemePath(name));
        return Assert.IsInstanceOfType<ResourceDictionary>(XamlReader.Load(stream));
    }

    private static ResourceDictionary LoadDesignResources()
    {
        var controls = XDocument.Load(GetThemePath("Controls"));
        var designTokens = XDocument.Load(GetThemePath("DesignTokens"));
        var combined = new XDocument(new XElement(
            controls.Root!.Name,
            controls.Root.Attributes(),
            controls.Root.Elements().Select(element => new XElement(element)),
            designTokens.Root!.Elements().Select(element => new XElement(element))));
        using var reader = combined.CreateReader();
        return Assert.IsInstanceOfType<ResourceDictionary>(XamlReader.Load(reader));
    }

    private static string GetThemePath(string name) =>
        Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RainArchives.App",
            "Themes",
            $"{name}.xaml");

    private static void AssertSystemColor(
        IReadOnlyDictionary<string, string> brushColors,
        string brushKey,
        string systemColorKey)
    {
        Assert.IsTrue(brushColors.TryGetValue(brushKey, out var actual), $"Missing {brushKey}.");
        Assert.AreEqual(
            $"{{DynamicResource {{x:Static SystemColors.{systemColorKey}}}}}",
            actual,
            $"{brushKey} does not use the expected authoritative system color.");
    }

    private static void AssertMainShellSurfacePair(
        XDocument document,
        string backgroundKey,
        string foregroundKey)
    {
        var expectedBackground = $"{{DynamicResource {backgroundKey}}}";
        var expectedForeground = $"{{DynamicResource {foregroundKey}}}";
        var consumers = document
            .Descendants()
            .Where(element => string.Equals(
                (string?)element.Attribute("Background"),
                expectedBackground,
                StringComparison.Ordinal))
            .ToArray();
        Assert.IsTrue(consumers.Length > 0, $"No {backgroundKey} main-shell consumers were found.");

        foreach (var consumer in consumers)
        {
            var actualForeground =
                (string?)consumer.Attribute("Foreground") ??
                (string?)consumer.Attribute("TextElement.Foreground");
            Assert.AreEqual(
                expectedForeground,
                actualForeground,
                $"{consumer.Name.LocalName} with {backgroundKey} lacks its paired foreground.");
        }
    }

    private static void AssertBrushColor(Brush brush, Color expected, string role)
    {
        Assert.AreEqual(
            expected,
            Assert.IsInstanceOfType<SolidColorBrush>(brush, $"{role} is not a solid brush.").Color,
            $"{role} did not resolve its paired semantic brush.");
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate)
            {
                return candidate;
            }

            var descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RainArchives.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The Rain Archives repository root was not found.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDetachedWindowReference(AppThemeManager manager)
    {
        var window = new Window();
        var attachment = manager.AttachWindowChrome(window);
        Assert.AreEqual(1, manager.AttachedWindowCount);
        attachment.Dispose();
        return new WeakReference(window);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = "RainArchives.AppThemeTests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "The theme test STA thread did not terminate.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
