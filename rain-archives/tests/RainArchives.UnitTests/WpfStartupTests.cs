using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Infrastructure;
using RainArchives.App.Services;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class WpfStartupTests
{
    [TestMethod]
    public void AppHostRunsPerMonitorDpiAware()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The WPF application host is Windows-specific.");
        }

        var executable = Path.Combine(AppContext.BaseDirectory, "RainArchives.exe");
        Assert.IsTrue(File.Exists(executable), "The unit-test output does not contain RainArchives.exe.");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        Assert.IsNotNull(process);

        try
        {
            Assert.IsTrue(process.WaitForInputIdle(5_000), "The WPF app did not create its window in time.");
            var result = GetProcessDpiAwareness(process.Handle, out var awareness);

            Assert.AreEqual(0, result, $"GetProcessDpiAwareness failed with HRESULT 0x{result:X8}.");
            Assert.AreEqual(ProcessDpiAwareness.PerMonitorAware, awareness);
            var windowHandle = WaitForTopLevelWindow(process, TimeSpan.FromSeconds(5));
            Assert.AreNotEqual(IntPtr.Zero, windowHandle, "The WPF app did not expose a top-level window.");
            var windowContext = GetWindowDpiAwarenessContext(windowHandle);
            Assert.IsTrue(
                AreDpiAwarenessContextsEqual(windowContext, new IntPtr(-4)),
                "The WPF app window is not using Per-Monitor V2 DPI awareness.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(5_000))
                {
                    process.Kill(entireProcessTree: true);
                    Assert.IsTrue(process.WaitForExit(5_000), "The test-owned WPF process did not terminate.");
                }
            }
        }
    }

    [TestMethod]
    [Timeout(15_000)]
    public void MainWindowAndPasswordDialogRunWithoutBindingOrSecretCaptureFailure()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            RainArchives.App.App? application = null;
            try
            {
                application = new RainArchives.App.App();
                application.InitializeComponent();
                var window = new RainArchives.App.MainWindow();
                window.Show();
                window.UpdateLayout();
                Assert.IsTrue(window.IsVisible);
                Assert.IsTrue(window.UseLayoutRounding);
                Assert.IsTrue(window.SnapsToDevicePixels);
                Assert.AreEqual("Rain Archives archive browser", AutomationProperties.GetName(window));

                foreach (var brushKey in new[]
                {
                    "BackgroundBrush",
                    "SurfaceBrush",
                    "SurfaceMutedBrush",
                    "SurfaceRaisedBrush",
                    "BorderBrush",
                    "TextBrush",
                    "MutedTextBrush",
                    "AccentBrush",
                    "FocusBrush",
                    "DangerBrush",
                })
                {
                    Assert.IsInstanceOfType<SolidColorBrush>(application.TryFindResource(brushKey));
                }

                Assert.IsInstanceOfType<Style>(application.TryFindResource("PrimaryButtonStyle"));
                Assert.IsInstanceOfType<Style>(application.TryFindResource("BrowserButtonStyle"));
                Assert.IsInstanceOfType<Style>(application.TryFindResource("ArchiveDataGridStyle"));
                Assert.IsInstanceOfType<Geometry>(application.TryFindResource("Icon.Archive"));

                var searchInput = Assert.IsInstanceOfType<TextBox>(window.FindName("SearchInput"));
                Assert.AreEqual("Search archive entries", AutomationProperties.GetName(searchInput));
                StringAssert.Contains(AutomationProperties.GetHelpText(searchInput), "Unicode-aware");
                Assert.AreEqual(0, KeyboardNavigation.GetTabIndex(searchInput));
                Assert.AreEqual(
                    "SearchText",
                    BindingOperations.GetBindingExpression(
                        searchInput,
                        TextBox.TextProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(
                    "Browser.CanSearch",
                    BindingOperations.GetBindingExpression(
                        searchInput,
                        UIElement.IsEnabledProperty)?.ParentBinding.Path.Path);

                var commandBar = Assert.IsInstanceOfType<WrapPanel>(window.FindName("CommandBar"));
                var orderedCommandButtons = new[]
                {
                    Assert.IsInstanceOfType<Button>(window.FindName("OpenArchiveButton")),
                    Assert.IsInstanceOfType<Button>(window.FindName("CreateArchiveButton")),
                    Assert.IsInstanceOfType<Button>(window.FindName("ExtractButton")),
                    Assert.IsInstanceOfType<Button>(window.FindName("RebuildCopyButton")),
                    Assert.IsInstanceOfType<Button>(window.FindName("TestButton")),
                    Assert.IsInstanceOfType<Button>(window.FindName("RecoveryButton")),
                    Assert.IsInstanceOfType<Button>(window.FindName("ActivityButton")),
                    Assert.IsInstanceOfType<Button>(window.FindName("SettingsButton")),
                    Assert.IsInstanceOfType<Button>(window.FindName("ThemeButton")),
                    Assert.IsInstanceOfType<Button>(window.FindName("CancelButton")),
                };
                CollectionAssert.AreEqual(
                    Enumerable.Range(1, 10).ToArray(),
                    orderedCommandButtons.Select(KeyboardNavigation.GetTabIndex).ToArray());
                Assert.AreEqual(Orientation.Horizontal, commandBar.Orientation);

                var welcomePanel = Assert.IsInstanceOfType<Border>(window.FindName("WelcomePanel"));
                var welcomeActionPanel = Assert.IsInstanceOfType<WrapPanel>(
                    window.FindName("WelcomeActionPanel"));
                var welcomeButtons = welcomeActionPanel.Children.OfType<Button>().ToArray();
                Assert.HasCount(3, welcomeButtons);
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "Choose an archive from welcome",
                        "Create archive from welcome",
                        "Open archive recovery center from welcome",
                    },
                    welcomeButtons.Select(AutomationProperties.GetName).ToArray());
                Assert.IsTrue(welcomeButtons.All(button => button.IsTabStop));
                Assert.IsTrue(welcomeButtons.All(button => GetButtonAccessKey(button) is null));
                Assert.AreEqual('C', GetButtonAccessKey(orderedCommandButtons[1]));
                Assert.AreEqual('R', GetButtonAccessKey(orderedCommandButtons[5]));
                AssertNoActiveMnemonicCollisions(
                    commandBar.Children
                        .OfType<Button>()
                        .Concat(welcomeButtons)
                        .Where(button => button.IsVisible && button.IsEnabled));
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.UpdateLayout();
                Assert.IsTrue(commandBar.ActualWidth <= window.ActualWidth);
                Assert.IsTrue(welcomePanel.ActualWidth <= window.ActualWidth);
                window.Width = 1120;
                window.Height = 720;
                window.UpdateLayout();

                var entriesGrid = Assert.IsInstanceOfType<DataGrid>(window.FindName("EntriesGrid"));
                Assert.AreEqual("Archive entries", AutomationProperties.GetName(entriesGrid));
                StringAssert.Contains(AutomationProperties.GetHelpText(entriesGrid), "Press Enter");
                StringAssert.Contains(AutomationProperties.GetHelpText(entriesGrid), "512");
                Assert.IsTrue(entriesGrid.EnableRowVirtualization);
                Assert.IsTrue(entriesGrid.EnableColumnVirtualization);
                Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(entriesGrid));
                Assert.AreEqual(
                    VirtualizationMode.Recycling,
                    VirtualizingPanel.GetVirtualizationMode(entriesGrid));
                Assert.IsTrue(ScrollViewer.GetCanContentScroll(entriesGrid));
                Assert.AreEqual(16, KeyboardNavigation.GetTabIndex(entriesGrid));
                Assert.AreEqual(8, entriesGrid.Columns.Count);
                Assert.IsInstanceOfType<DataGridTemplateColumn>(entriesGrid.Columns[0]);
                Assert.AreSame(application.TryFindResource("ArchiveDataGridStyle"), entriesGrid.Style);
                Assert.AreEqual(
                    "Entries",
                    BindingOperations.GetBindingExpression(
                        entriesGrid,
                        ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(
                    "Browser.SelectedEntry",
                    BindingOperations.GetBindingExpression(
                        entriesGrid,
                        Selector.SelectedItemProperty)?.ParentBinding.Path.Path);
                var entryMenu = Assert.IsInstanceOfType<ContextMenu>(entriesGrid.ContextMenu);
                Assert.AreEqual("Archive entry commands", AutomationProperties.GetName(entryMenu));
                Assert.AreEqual(6, entryMenu.Items.Count);
                var entryMenuItems = entryMenu.Items.OfType<MenuItem>().ToArray();
                Assert.HasCount(5, entryMenuItems);
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "Open selected archive folder",
                        "Safely preview selected archive file",
                        "Show validated archive properties",
                        "Extract selected archive entries",
                        "Test archive integrity",
                    },
                    entryMenuItems
                        .Select(AutomationProperties.GetName)
                        .ToArray());
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "NavigateSelectionCommand",
                        "PreviewSelectionCommand",
                        "ShowPropertiesCommand",
                        "ExtractSelectionCommand",
                        "TestCommand",
                    },
                    entryMenuItems
                        .Select(item => BindingOperations.GetBindingExpression(
                            item,
                            MenuItem.CommandProperty)?.ParentBinding.Path.Path)
                        .ToArray());
                CollectionAssert.AreEqual(
                    new[]
                    {
                        "CommandContext.CanNavigate",
                        "CommandContext.CanPreview",
                        "CommandContext.CanShowProperties",
                        "CommandContext.CanExtractSelected",
                        "CommandContext.CanTestIntegrity",
                    },
                    entryMenuItems
                        .Select(item => BindingOperations.GetBindingExpression(
                            item,
                            UIElement.VisibilityProperty)?.ParentBinding.Path.Path)
                        .ToArray());

                var backButton = Assert.IsInstanceOfType<Button>(window.FindName("BackButton"));
                var forwardButton = Assert.IsInstanceOfType<Button>(window.FindName("ForwardButton"));
                var upButton = Assert.IsInstanceOfType<Button>(window.FindName("UpButton"));
                var rootButton = Assert.IsInstanceOfType<Button>(window.FindName("RootButton"));
                Assert.AreEqual(11, KeyboardNavigation.GetTabIndex(backButton));
                Assert.AreEqual(12, KeyboardNavigation.GetTabIndex(forwardButton));
                Assert.AreEqual(13, KeyboardNavigation.GetTabIndex(upButton));
                Assert.AreEqual(14, KeyboardNavigation.GetTabIndex(rootButton));
                Assert.AreEqual("Back in archive", AutomationProperties.GetName(backButton));
                Assert.AreEqual("Forward in archive", AutomationProperties.GetName(forwardButton));
                Assert.AreEqual("Up one archive folder", AutomationProperties.GetName(upButton));
                Assert.AreEqual("Archive root", AutomationProperties.GetName(rootButton));
                Assert.AreEqual(
                    "BackCommand",
                    BindingOperations.GetBindingExpression(
                        backButton,
                        Button.CommandProperty)?.ParentBinding.Path.Path);

                var browserState = Assert.IsInstanceOfType<Border>(window.FindName("BrowserStatePanel"));
                Assert.AreEqual("Archive browser state", AutomationProperties.GetName(browserState));
                Assert.AreEqual(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(browserState));
                Assert.AreEqual(
                    ((SolidColorBrush)application.TryFindResource("SurfaceRaisedBrush")).Color,
                    ((SolidColorBrush)browserState.Background).Color);
                Assert.AreEqual(
                    "Browser.IsStatePanelVisible",
                    BindingOperations.GetBindingExpression(
                        browserState,
                        UIElement.VisibilityProperty)?.ParentBinding.Path.Path);
                var browserStatus = Assert.IsInstanceOfType<TextBlock>(window.FindName("BrowserStatusText"));
                Assert.AreEqual("Archive browser status", AutomationProperties.GetName(browserStatus));
                Assert.AreEqual(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(browserStatus));
                var previousPage = Assert.IsInstanceOfType<Button>(window.FindName("PreviousPageButton"));
                var nextPage = Assert.IsInstanceOfType<Button>(window.FindName("NextPageButton"));
                Assert.AreEqual(18, KeyboardNavigation.GetTabIndex(previousPage));
                Assert.AreEqual(19, KeyboardNavigation.GetTabIndex(nextPage));
                Assert.AreEqual(
                    "Browser.PreviousPageCommand",
                    BindingOperations.GetBindingExpression(
                        previousPage,
                        Button.CommandProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(
                    "Browser.NextPageCommand",
                    BindingOperations.GetBindingExpression(
                        nextPage,
                        Button.CommandProperty)?.ParentBinding.Path.Path);

                var gestures = window.InputBindings
                    .OfType<KeyBinding>()
                    .Select(binding => (binding.Key, binding.Modifiers))
                    .ToArray();
                CollectionAssert.IsSubsetOf(
                    new[]
                    {
                        (Key.Left, ModifierKeys.Alt),
                        (Key.Right, ModifierKeys.Alt),
                        (Key.Up, ModifierKeys.Alt),
                        (Key.Home, ModifierKeys.Control),
                        (Key.F, ModifierKeys.Control),
                        (Key.OemComma, ModifierKeys.Control),
                        (Key.PageUp, ModifierKeys.Alt),
                        (Key.PageDown, ModifierKeys.Alt),
                        (Key.Enter, ModifierKeys.Alt),
                        (Key.P, ModifierKeys.Control),
                        (Key.T, ModifierKeys.Control),
                        (Key.E, ModifierKeys.Control),
                    },
                    gestures);
                Assert.IsFalse(
                    gestures.Any(gesture => gesture.Key == Key.Escape),
                    "Inspector close must not install a window-global Escape binding.");
                var contextStatus = Assert.IsInstanceOfType<TextBlock>(
                    window.FindName("ContextCommandStatusText"));
                Assert.AreEqual(
                    "Available archive commands",
                    AutomationProperties.GetName(contextStatus));
                Assert.AreEqual(
                    AutomationLiveSetting.Off,
                    AutomationProperties.GetLiveSetting(contextStatus));
                Assert.AreEqual(
                    "CommandContext.StatusAnnouncement",
                    BindingOperations.GetBindingExpression(
                        contextStatus,
                        TextBlock.TextProperty)?.ParentBinding.Path.Path);
                var previewPanel = Assert.IsInstanceOfType<Border>(window.FindName("PreviewPanel"));
                Assert.AreEqual("Archive properties and safe preview", AutomationProperties.GetName(previewPanel));
                StringAssert.Contains(AutomationProperties.GetHelpText(previewPanel), "Validated archive");
                StringAssert.Contains(AutomationProperties.GetHelpText(previewPanel), "Opened only on request");
                Assert.AreEqual(
                    ((SolidColorBrush)application.TryFindResource("SurfaceBrush")).Color,
                    ((SolidColorBrush)previewPanel.Background).Color);
                Assert.IsTrue(previewPanel.Width >= 320);
                Assert.IsTrue(previewPanel.Width <= 380);
                Assert.AreEqual(Visibility.Collapsed, previewPanel.Visibility);
                Assert.AreEqual(
                    "IsInspectorVisible",
                    BindingOperations.GetBindingExpression(
                        previewPanel,
                        UIElement.VisibilityProperty)?.ParentBinding.Path.Path);
                var closeInspector = Assert.IsInstanceOfType<Button>(
                    window.FindName("CloseInspectorButton"));
                Assert.AreEqual("Close archive inspector", AutomationProperties.GetName(closeInspector));
                StringAssert.Contains(AutomationProperties.GetHelpText(closeInspector), "fully drained");
                Assert.AreEqual(20, KeyboardNavigation.GetTabIndex(closeInspector));
                Assert.AreEqual(
                    "CloseInspectorCommand",
                    BindingOperations.GetBindingExpression(
                        closeInspector,
                        Button.CommandProperty)?.ParentBinding.Path.Path);
                var inspectorTabs = Assert.IsInstanceOfType<TabControl>(window.FindName("InspectorTabs"));
                Assert.AreEqual("Archive inspector", AutomationProperties.GetName(inspectorTabs));
                Assert.AreEqual(21, KeyboardNavigation.GetTabIndex(inspectorTabs));
                Assert.AreEqual(
                    "InspectorSelectedTabIndex",
                    BindingOperations.GetBindingExpression(
                        inspectorTabs,
                        Selector.SelectedIndexProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(2, inspectorTabs.Items.Count);
                inspectorTabs.ApplyTemplate();
                Assert.AreSame(application.TryFindResource(typeof(TabControl)), inspectorTabs.Style);
                Assert.IsNotNull(inspectorTabs.Template.FindName("PART_SelectedContentHost", inspectorTabs));
                var inspectorPropertyTab = Assert.IsInstanceOfType<TabItem>(inspectorTabs.Items[0]);
                var inspectorPreviewTab = Assert.IsInstanceOfType<TabItem>(inspectorTabs.Items[1]);
                inspectorPropertyTab.ApplyTemplate();
                inspectorPreviewTab.ApplyTemplate();
                Assert.AreSame(application.TryFindResource(typeof(TabItem)), inspectorPropertyTab.Style);
                Assert.AreSame(application.TryFindResource(typeof(TabItem)), inspectorPreviewTab.Style);
                Assert.AreEqual(
                    ((SolidColorBrush)application.TryFindResource("SurfaceForegroundBrush")).Color,
                    ((SolidColorBrush)inspectorPropertyTab.Foreground).Color);
                var previewState = Assert.IsInstanceOfType<Border>(window.FindName("PreviewStatePanel"));
                Assert.IsTrue(previewState.Focusable);
                Assert.AreEqual(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(previewState));
                Assert.AreEqual(
                    "Preview.StateTitle",
                    BindingOperations.GetBindingExpression(
                        previewState,
                        AutomationProperties.NameProperty)?.ParentBinding.Path.Path);
                var previewText = Assert.IsInstanceOfType<TextBox>(window.FindName("PreviewTextBox"));
                Assert.IsTrue(previewText.IsReadOnly);
                previewText.ApplyTemplate();
                Assert.AreSame(application.TryFindResource(typeof(TextBox)), previewText.Style);
                Assert.IsNotNull(previewText.Template.FindName("PART_ContentHost", previewText));
                Assert.AreEqual("Safe text preview content", AutomationProperties.GetName(previewText));
                Assert.AreEqual(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(previewText));
                StringAssert.Contains(AutomationProperties.GetHelpText(previewText), "Read-only inert text");

                var mainStatus = Assert.IsInstanceOfType<TextBlock>(window.FindName("MainStatusText"));
                var statusBar = Assert.IsInstanceOfType<StatusBar>(window.FindName("MainStatusBar"));
                Assert.AreEqual("Rain Archives status", AutomationProperties.GetName(statusBar));
                Assert.AreEqual(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(statusBar));
                Assert.AreEqual(
                    "StatusText",
                    BindingOperations.GetBindingExpression(
                        mainStatus,
                        TextBlock.TextProperty)?.ParentBinding.Path.Path);

                var themeButton = Assert.IsInstanceOfType<Button>(window.FindName("ThemeButton"));
                Assert.AreEqual("Choose application theme", AutomationProperties.GetName(themeButton));
                StringAssert.Contains(AutomationProperties.GetHelpText(themeButton), "High Contrast");
                Assert.AreEqual(9, KeyboardNavigation.GetTabIndex(themeButton));
                var themeMenu = Assert.IsInstanceOfType<ContextMenu>(themeButton.ContextMenu);
                Assert.AreEqual("Application theme choices", AutomationProperties.GetName(themeMenu));
                var themeChoices = themeMenu.Items.OfType<MenuItem>().ToArray();
                Assert.HasCount(3, themeChoices);
                Assert.IsTrue(themeChoices[0].IsChecked);

                var focusEntry = new ArchiveEntryViewModel(CreateInspectorFocusEntry());
                BindingOperations.ClearBinding(entriesGrid, ItemsControl.ItemsSourceProperty);
                BindingOperations.ClearBinding(entriesGrid, Selector.SelectedItemProperty);
                var browserSurface = Assert.IsInstanceOfType<DockPanel>(previewPanel.Parent);
                BindingOperations.ClearBinding(browserSurface, UIElement.VisibilityProperty);
                browserSurface.Visibility = Visibility.Visible;
                entriesGrid.ItemsSource = new[] { focusEntry };
                entriesGrid.SelectedItem = focusEntry;
                entriesGrid.Visibility = Visibility.Visible;
                window.UpdateLayout();
                var focusHandler = typeof(RainArchives.App.MainWindow).GetMethod(
                    "Browser_FocusRequested",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(focusHandler);
                _ = focusHandler.Invoke(
                    window,
                    [null, new ArchiveBrowserFocusRequestedEventArgs(ArchiveBrowserFocusTarget.EntryList)]);
                Dispatcher.CurrentDispatcher.Invoke(
                    DispatcherPriority.ApplicationIdle,
                    static () => { });
                Assert.IsTrue(entriesGrid.IsKeyboardFocusWithin);
                Assert.AreSame(focusEntry, entriesGrid.SelectedItem);

                var activityButton = FindVisualDescendant<Button>(
                    window,
                    button => string.Equals(
                        AutomationProperties.GetName(button),
                        "Open Activity Center",
                        StringComparison.Ordinal));
                Assert.IsNotNull(activityButton);

                var activityCenterViewModel = new ActivityCenterViewModel(
                    new PassiveRecoveryArchiveClient());
                var activityCenter = new RainArchives.App.Views.ActivityCenterWindow(
                    activityCenterViewModel)
                {
                    Owner = window,
                };
                activityCenter.Measure(new Size(1200, 760));
                activityCenter.Arrange(new Rect(0, 0, 1200, 760));
                activityCenter.UpdateLayout();
                Assert.IsTrue(activityCenter.UseLayoutRounding);
                Assert.IsTrue(activityCenter.SnapsToDevicePixels);
                Assert.AreEqual(
                    "Rain Archives Activity Center",
                    AutomationProperties.GetName(activityCenter));
                var activityRefresh = Assert.IsInstanceOfType<Button>(
                    activityCenter.FindName("RefreshButton"));
                var activityClose = Assert.IsInstanceOfType<Button>(
                    activityCenter.FindName("CloseButton"));
                Assert.AreEqual(0, KeyboardNavigation.GetTabIndex(activityRefresh));
                Assert.AreEqual(1, KeyboardNavigation.GetTabIndex(activityClose));
                Assert.IsTrue(activityRefresh.IsDefault);
                var activityRefreshBinding = BindingOperations.GetBindingExpression(
                    activityRefresh,
                    Button.CommandProperty);
                Assert.AreEqual(
                    "RefreshCommand",
                    activityRefreshBinding?.ParentBinding.Path.Path);
                var activityGrid = Assert.IsInstanceOfType<DataGrid>(
                    activityCenter.FindName("ActivityGrid"));
                Assert.IsTrue(activityGrid.EnableRowVirtualization);
                Assert.IsTrue(activityGrid.EnableColumnVirtualization);
                Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(activityGrid));
                Assert.AreEqual(
                    VirtualizationMode.Recycling,
                    VirtualizingPanel.GetVirtualizationMode(activityGrid));
                Assert.IsTrue(ScrollViewer.GetCanContentScroll(activityGrid));
                Assert.AreEqual(2, KeyboardNavigation.GetTabIndex(activityGrid));
                Assert.AreEqual(
                    "Archive operation activity",
                    AutomationProperties.GetName(activityGrid));
                StringAssert.Contains(
                    AutomationProperties.GetHelpText(activityGrid),
                    "latest bounded snapshot");
                Assert.AreEqual(
                    AutomationLiveSetting.Off,
                    AutomationProperties.GetLiveSetting(activityGrid));
                Assert.AreEqual(13, activityGrid.Columns.Count);
                var activityControlColumn = Assert.IsInstanceOfType<DataGridTemplateColumn>(
                    activityGrid.Columns[^1]);
                var activityCancel = Assert.IsInstanceOfType<Button>(
                    activityControlColumn.CellTemplate.LoadContent());
                Assert.AreEqual("Cancel operation", AutomationProperties.GetName(activityCancel));
                Assert.AreEqual(
                    "CanCancel",
                    BindingOperations.GetBindingExpression(
                        activityCancel,
                        UIElement.VisibilityProperty)?.ParentBinding.Path.Path);
                Assert.AreEqual(
                    "CancelCommand",
                    BindingOperations.GetBindingExpression(
                        activityCancel,
                        Button.CommandProperty)?.ParentBinding.Path.Path);
                var activityStatus = Assert.IsInstanceOfType<TextBlock>(
                    activityCenter.FindName("ActivityStatus"));
                Assert.AreEqual(
                    AutomationLiveSetting.Polite,
                    AutomationProperties.GetLiveSetting(activityStatus));
                activityCenterViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();

                var settingsButton = FindVisualDescendant<Button>(
                    window,
                    button => string.Equals(
                        AutomationProperties.GetName(button),
                        "Open Settings and Presets",
                        StringComparison.Ordinal));
                Assert.IsNotNull(settingsButton);
                StringAssert.Contains(
                    AutomationProperties.GetHelpText(settingsButton),
                    "No archive paths");

                var settingsRoot = Path.Combine(
                    Path.GetTempPath(),
                    "RainArchives.WpfSettingsCenterTests",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(settingsRoot);
                try
                {
                    var settingsViewModel = new SettingsCenterViewModel(
                        new AppSettingsStore(settingsRoot),
                        new ZipPresetStore(settingsRoot),
                        _ => Task.FromResult(SettingsCapabilitySnapshot.Offline));
                    var settingsCenter = new RainArchives.App.Views.SettingsCenterWindow(
                        settingsViewModel)
                    {
                        Owner = window,
                    };
                    settingsCenter.Measure(new Size(1040, 780));
                    settingsCenter.Arrange(new Rect(0, 0, 1040, 780));
                    settingsCenter.UpdateLayout();
                    Assert.IsTrue(settingsCenter.UseLayoutRounding);
                    Assert.IsTrue(settingsCenter.SnapsToDevicePixels);
                    Assert.AreEqual(
                        "Rain Archives Settings and Presets",
                        AutomationProperties.GetName(settingsCenter));
                    Assert.AreEqual(
                        ((SolidColorBrush)application.TryFindResource("BackgroundBrush")).Color,
                        ((SolidColorBrush)settingsCenter.Background).Color);
                    var settingsContentScroll = Assert.IsInstanceOfType<ScrollViewer>(
                        settingsCenter.FindName("SettingsContentScroll"));
                    Assert.IsFalse(settingsContentScroll.CanContentScroll);
                    Assert.AreEqual(
                        ScrollBarVisibility.Disabled,
                        settingsContentScroll.HorizontalScrollBarVisibility);
                    Assert.AreEqual(
                        ScrollBarVisibility.Auto,
                        settingsContentScroll.VerticalScrollBarVisibility);
                    Assert.AreEqual(
                        HorizontalAlignment.Stretch,
                        settingsContentScroll.HorizontalContentAlignment);
                    Assert.AreEqual(PanningMode.VerticalOnly, settingsContentScroll.PanningMode);

                    var defaultFormat = Assert.IsInstanceOfType<ComboBox>(
                        settingsCenter.FindName("DefaultFormatInput"));
                    Assert.AreEqual(1, KeyboardNavigation.GetTabIndex(defaultFormat));
                    Assert.AreEqual(
                        "Formats",
                        BindingOperations.GetBindingExpression(
                            defaultFormat,
                            ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path);
                    Assert.AreEqual(
                        "SelectedDefaultFormat",
                        BindingOperations.GetBindingExpression(
                            defaultFormat,
                            Selector.SelectedItemProperty)?.ParentBinding.Path.Path);

                    var formatDefaults = Assert.IsInstanceOfType<DataGrid>(
                        settingsCenter.FindName("FormatDefaultsGrid"));
                    Assert.IsTrue(formatDefaults.EnableRowVirtualization);
                    Assert.IsTrue(formatDefaults.EnableColumnVirtualization);
                    Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(formatDefaults));
                    Assert.AreEqual(
                        VirtualizationMode.Recycling,
                        VirtualizingPanel.GetVirtualizationMode(formatDefaults));
                    Assert.IsTrue(ScrollViewer.GetCanContentScroll(formatDefaults));
                    Assert.AreEqual("Managed format preset defaults", AutomationProperties.GetName(formatDefaults));
                    Assert.AreEqual(4, formatDefaults.Columns.Count);
                    Assert.AreEqual(290, formatDefaults.MaxHeight);
                    Assert.AreEqual(
                        "CanEditDefaults",
                        BindingOperations.GetBindingExpression(
                            formatDefaults,
                            UIElement.IsEnabledProperty)?.ParentBinding.Path.Path);

                    var presetGrid = Assert.IsInstanceOfType<DataGrid>(
                        settingsCenter.FindName("PresetGrid"));
                    Assert.IsTrue(presetGrid.EnableRowVirtualization);
                    Assert.IsTrue(presetGrid.EnableColumnVirtualization);
                    Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(presetGrid));
                    Assert.AreEqual(
                        VirtualizationMode.Recycling,
                        VirtualizingPanel.GetVirtualizationMode(presetGrid));
                    Assert.AreEqual("User ZIP preset list", AutomationProperties.GetName(presetGrid));
                    Assert.AreEqual(260, presetGrid.MaxHeight);
                    var presetEditor = Assert.IsInstanceOfType<Grid>(
                        settingsCenter.FindName("PresetEditorGrid"));
                    Assert.AreEqual(
                        5,
                        presetEditor.RowDefinitions.Count,
                        "The preset editor must explicitly declare rows 0 through 4.");

                    var rebuildPreset = Assert.IsInstanceOfType<ComboBox>(
                        settingsCenter.FindName("RebuildPresetInput"));
                    Assert.AreEqual(
                        "ZipRebuildPresets",
                        BindingOperations.GetBindingExpression(
                            rebuildPreset,
                            ItemsControl.ItemsSourceProperty)?.ParentBinding.Path.Path);
                    Assert.AreEqual(
                        "ZipRebuildPreset",
                        BindingOperations.GetBindingExpression(
                            rebuildPreset,
                            Selector.SelectedItemProperty)?.ParentBinding.Path.Path);

                    var resetButton = Assert.IsInstanceOfType<Button>(
                        settingsCenter.FindName("ResetButton"));
                    var saveButton = Assert.IsInstanceOfType<Button>(
                        settingsCenter.FindName("SaveButton"));
                    var settingsClose = Assert.IsInstanceOfType<Button>(
                        settingsCenter.FindName("CloseButton"));
                    Assert.AreEqual(12, KeyboardNavigation.GetTabIndex(resetButton));
                    Assert.AreEqual(13, KeyboardNavigation.GetTabIndex(saveButton));
                    Assert.AreEqual(14, KeyboardNavigation.GetTabIndex(settingsClose));
                    StringAssert.Contains(AutomationProperties.GetHelpText(resetButton), "Explicitly replace");
                    Assert.AreEqual(
                        "ResetCommand",
                        BindingOperations.GetBindingExpression(
                            resetButton,
                            Button.CommandProperty)?.ParentBinding.Path.Path);
                    Assert.AreEqual(
                        "SaveCommand",
                        BindingOperations.GetBindingExpression(
                            saveButton,
                            Button.CommandProperty)?.ParentBinding.Path.Path);
                    var settingsStatus = Assert.IsInstanceOfType<TextBlock>(
                        settingsCenter.FindName("SettingsStatusAnnouncement"));
                    Assert.AreEqual(
                        AutomationLiveSetting.Polite,
                        AutomationProperties.GetLiveSetting(settingsStatus));

                    settingsCenter.Width = 760;
                    settingsCenter.Height = 560;
                    settingsCenter.Show();
                    Dispatcher.CurrentDispatcher.Invoke(
                        DispatcherPriority.ApplicationIdle,
                        static () => { });
                    settingsCenter.UpdateLayout();
                    Assert.IsTrue(settingsContentScroll.ViewportHeight > 0);
                    Assert.IsTrue(
                        settingsContentScroll.ExtentHeight > settingsContentScroll.ViewportHeight,
                        "Compact Settings must expose a bounded vertical extent instead of clipping content.");
                    Assert.IsTrue(
                        settingsContentScroll.ScrollableHeight > 0,
                        "Compact Settings must expose a positive physical scroll range.");
                    var safetyBoundaryHeading = FindVisualDescendant<TextBlock>(
                        settingsCenter,
                        text => string.Equals(
                            text.Text,
                            "Safety and privacy boundary",
                            StringComparison.Ordinal));
                    Assert.IsNotNull(safetyBoundaryHeading);
                    var initialSettingsOffset = settingsContentScroll.VerticalOffset;
                    settingsContentScroll.ScrollToEnd();
                    settingsCenter.UpdateLayout();
                    Assert.IsTrue(
                        settingsContentScroll.VerticalOffset > initialSettingsOffset,
                        "Settings physical scrolling did not move the content viewport.");
                    Assert.AreEqual(
                        settingsContentScroll.ScrollableHeight,
                        settingsContentScroll.VerticalOffset,
                        0.5,
                        "Settings did not reach the safety and privacy boundary at the end of the scroll range.");
                    var safetyBoundaryPosition = safetyBoundaryHeading
                        .TransformToAncestor(settingsContentScroll)
                        .Transform(new Point()).Y;
                    Assert.IsTrue(
                        safetyBoundaryPosition >= 0
                        && safetyBoundaryPosition < settingsContentScroll.ViewportHeight,
                        "The final Settings content was not reachable inside the compact viewport.");
                    Assert.IsTrue(settingsClose.ActualHeight > 0, "The stationary Settings footer was not arranged.");
                    settingsCenter.Close();
                    settingsViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    Directory.Delete(settingsRoot, recursive: true);
                }

                var createArchiveButton = FindVisualDescendant<Button>(
                    window,
                    button => string.Equals(
                        AutomationProperties.GetName(button),
                        "Create archive",
                        StringComparison.Ordinal));
                Assert.IsNotNull(createArchiveButton);
                StringAssert.Contains(
                    AutomationProperties.GetHelpText(createArchiveButton),
                    "connected worker");
                var recoveryButton = FindVisualDescendant<Button>(
                    window,
                    button => string.Equals(
                        AutomationProperties.GetName(button),
                        "Open archive recovery center",
                        StringComparison.Ordinal));
                Assert.IsNotNull(recoveryButton);
                StringAssert.Contains(
                    AutomationProperties.GetHelpText(recoveryButton),
                    "retained nested workspaces");
                WpfRebuildCopyAssertions.Run(window);
                ManagedCreationSettingsWpfAssertions.Run();
                PresetMutationPolicyWpfAssertions.Run(window);
                BindingOperations.ClearBinding(entriesGrid, UIElement.VisibilityProperty);
                BindingOperations.ClearBinding(entriesGrid, ItemsControl.ItemsSourceProperty);
                entriesGrid.Visibility = Visibility.Visible;
                entriesGrid.ItemsSource = new[]
                {
                    new ArchiveEntryViewModel(
                        new ArchiveEntryDescriptor(
                            EntryId.New(),
                            null,
                            0,
                            "alpha.txt",
                            "alpha.txt",
                            ArchiveEntryKind.File,
                            65,
                            65,
                            null,
                            null,
                            "Store",
                            null,
                            false,
                            false,
                            null,
                            EntrySafetyStatus.Safe,
                            null)),
                };
                window.UpdateLayout();
                var entryRow = Assert.IsInstanceOfType<DataGridRow>(
                    entriesGrid.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.AreEqual(
                    "alpha.txt, File, archive occurrence 1, security status Safe, not encrypted",
                    AutomationProperties.GetName(entryRow));

                var createDialog = new RainArchives.App.Views.CreateArchiveDialog
                {
                    Owner = window,
                };
                createDialog.Show();
                createDialog.UpdateLayout();
                var themeManager = application.ThemeManager;
                themeManager.Initialize();
                var originalThemePreference = themeManager.Preference;
                var sourceGrid = Assert.IsInstanceOfType<DataGrid>(
                    createDialog.FindName("SourcesGrid"));
                Assert.AreEqual("Archive input list", AutomationProperties.GetName(sourceGrid));
                var addFilesButton = Assert.IsInstanceOfType<Button>(
                    createDialog.FindName("AddFilesButton"));
                var addFolderButton = Assert.IsInstanceOfType<Button>(
                    createDialog.FindName("AddFolderButton"));
                var removeButton = Assert.IsInstanceOfType<Button>(
                    createDialog.FindName("RemoveButton"));
                Assert.AreEqual(0, KeyboardNavigation.GetTabIndex(addFilesButton));
                Assert.AreEqual(1, KeyboardNavigation.GetTabIndex(addFolderButton));
                Assert.AreEqual(2, KeyboardNavigation.GetTabIndex(removeButton));
                Assert.IsTrue(createDialog.TryAddFiles(
                [
                    Path.Combine(Path.GetTempPath(), "first", "alpha.txt"),
                ]));
                Assert.IsFalse(createDialog.TryAddFiles(
                [
                    Path.Combine(Path.GetTempPath(), "second", "ALPHA.txt"),
                    Path.Combine(Path.GetTempPath(), "third", "beta.txt"),
                ]));
                Assert.AreEqual(1, createDialog.Sources.Count);
                var collisionMessage = Assert.IsInstanceOfType<TextBlock>(
                    createDialog.FindName("ValidationMessage"));
                StringAssert.Contains(collisionMessage.Text, "ALPHA.txt");
                StringAssert.Contains(collisionMessage.Text, "Nothing was added");
                Assert.IsTrue(addFilesButton.Focus());
                Assert.IsTrue(addFilesButton.MoveFocus(
                    new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.AreSame(addFolderButton, Keyboard.FocusedElement);
                var destinationInput = Assert.IsInstanceOfType<TextBox>(
                    createDialog.FindName("DestinationInput"));
                Assert.AreEqual(
                    "New ZIP archive destination",
                    AutomationProperties.GetName(destinationInput));
                var presetInput = Assert.IsInstanceOfType<ComboBox>(
                    createDialog.FindName("PresetInput"));
                Assert.AreEqual("ZIP compression preset", AutomationProperties.GetName(presetInput));
                var formatInput = Assert.IsInstanceOfType<ComboBox>(
                    createDialog.FindName("FormatInput"));
                Assert.AreEqual(6, KeyboardNavigation.GetTabIndex(formatInput));
                Assert.IsTrue(sourceGrid.EnableRowVirtualization);
                Assert.IsTrue(sourceGrid.EnableColumnVirtualization);
                Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(sourceGrid));
                Assert.AreEqual(
                    VirtualizationMode.Recycling,
                    VirtualizingPanel.GetVirtualizationMode(sourceGrid));
                Assert.IsTrue(ScrollViewer.GetCanContentScroll(sourceGrid));
                try
                {
                    themeManager.SetPreference(AppThemePreference.Light);
                    createDialog.UpdateLayout();
                    var lightWindowColor = Assert.IsInstanceOfType<SolidColorBrush>(
                        createDialog.Background).Color;
                    var lightGridColor = Assert.IsInstanceOfType<SolidColorBrush>(
                        sourceGrid.Background).Color;

                    themeManager.SetPreference(AppThemePreference.Dark);
                    createDialog.UpdateLayout();
                    var darkWindowColor = Assert.IsInstanceOfType<SolidColorBrush>(
                        createDialog.Background).Color;
                    var darkGridColor = Assert.IsInstanceOfType<SolidColorBrush>(
                        sourceGrid.Background).Color;
                    Assert.AreNotEqual(lightWindowColor, darkWindowColor);
                    Assert.AreNotEqual(lightGridColor, darkGridColor);
                    Assert.AreEqual(
                        Assert.IsInstanceOfType<SolidColorBrush>(
                            application.TryFindResource("BackgroundBrush")).Color,
                        darkWindowColor);
                    Assert.AreEqual(
                        Assert.IsInstanceOfType<SolidColorBrush>(
                            application.TryFindResource("SurfaceBrush")).Color,
                        darkGridColor);
                }
                finally
                {
                    themeManager.SetPreference(originalThemePreference);
                    createDialog.UpdateLayout();
                }
                var selectedPreset = Assert.IsInstanceOfType<RainArchives.App.Views.CompressionPresetOption>(
                    presetInput.SelectedItem);
                Assert.AreEqual(ArchiveCompressionPreset.Balanced, selectedPreset.Value);
                var cancelSetupButton = Assert.IsInstanceOfType<Button>(
                    createDialog.FindName("CancelSetupButton"));
                var createButton = Assert.IsInstanceOfType<Button>(
                    createDialog.FindName("CreateArchiveButton"));
                Assert.IsTrue(cancelSetupButton.IsCancel);
                Assert.IsTrue(createButton.IsDefault);
                Assert.AreEqual(11, KeyboardNavigation.GetTabIndex(cancelSetupButton));
                Assert.AreEqual(12, KeyboardNavigation.GetTabIndex(createButton));
                var createContentScroll = Assert.IsInstanceOfType<ScrollViewer>(
                    createDialog.FindName("CreateContentScroll"));
                createDialog.Width = createDialog.MinWidth;
                createDialog.Height = createDialog.MinHeight;
                createDialog.UpdateLayout();
                Assert.IsTrue(createContentScroll.ActualHeight > 0);
                Assert.IsTrue(createButton.IsVisible);
                Assert.IsTrue(createButton.ActualHeight > 0);
                sourceGrid.SelectedIndex = 0;
                sourceGrid.UpdateLayout();
                var presentationSource = PresentationSource.FromVisual(createDialog);
                Assert.IsNotNull(presentationSource);
                var deleteKey = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    presentationSource,
                    Environment.TickCount,
                    Key.Delete)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                sourceGrid.RaiseEvent(deleteKey);
                Assert.IsTrue(deleteKey.Handled);
                Assert.AreEqual(0, createDialog.Sources.Count);
                createDialog.Close();

                var managedCreationDialog = new RainArchives.App.Views.CreateArchiveDialog(
                    supportsDirectoryCreation: true,
                    presetStore: null,
                    capabilities: null,
                    tarCapabilities: null,
                    managedCapabilities: new GetManagedWriteCapabilitiesResponse(
                    [
                        new ManagedWriteCapability(
                            ManagedArchiveCreationFormat.GZip,
                            CanCreate: true,
                            SupportsTreeCreation: false,
                            CanRebuild: false,
                            CompressionPresets: [ArchiveCompressionPreset.Balanced],
                            MaximumEntries: 1,
                            MaximumSourceBytes: ProtocolConstants.MaximumRawCreationSourceBytes,
                            SupportsEncryption: false,
                            SupportsFilenameEncryption: false,
                            SupportsSplitOutput: false,
                            SupportsExistingDestinationReplacement: false,
                            SupportsInPlaceUpdate: false),
                    ]));
                var managedFormatInput = Assert.IsInstanceOfType<ComboBox>(
                    managedCreationDialog.FindName("FormatInput"));
                managedFormatInput.SelectedItem = managedFormatInput.Items
                    .OfType<RainArchives.App.Views.CreationFormatOption>()
                    .Single(option => option.Value == ArchiveCreationFormat.GZip);
                var managedDestination = Assert.IsInstanceOfType<TextBox>(
                    managedCreationDialog.FindName("DestinationInput"));
                var managedPreset = Assert.IsInstanceOfType<ComboBox>(
                    managedCreationDialog.FindName("PresetInput"));
                var managedPresetName = Assert.IsInstanceOfType<TextBox>(
                    managedCreationDialog.FindName("PresetNameInput"));
                var managedCreate = Assert.IsInstanceOfType<Button>(
                    managedCreationDialog.FindName("CreateArchiveButton"));
                Assert.AreEqual("Create GZIP archive", managedCreationDialog.Title);
                Assert.AreEqual("New GZIP archive destination", AutomationProperties.GetName(managedDestination));
                Assert.AreEqual("GZIP compression preset", AutomationProperties.GetName(managedPreset));
                StringAssert.Contains(AutomationProperties.GetHelpText(managedPreset), "Balanced");
                Assert.IsFalse(managedPresetName.IsEnabled);
                Assert.AreEqual("Create GZIP archive", AutomationProperties.GetName(managedCreate));
                managedCreationDialog.Close();

                var fileOnlyCreationDialog = new RainArchives.App.Views.CreateArchiveDialog(
                    supportsDirectoryCreation: false);
                var disabledFolderButton = Assert.IsInstanceOfType<Button>(
                    fileOnlyCreationDialog.FindName("AddFolderButton"));
                Assert.IsFalse(disabledFolderButton.IsEnabled);
                var limitations = Assert.IsInstanceOfType<TextBlock>(
                    fileOnlyCreationDialog.FindName("LimitationsText"));
                StringAssert.Contains(limitations.Text, "files only");
                fileOnlyCreationDialog.Close();

                var creationMappingDialog = new RainArchives.App.Views.CreateArchiveDialog
                {
                    Owner = window,
                };
                var sourceFile = Path.Combine(Path.GetTempPath(), "alpha.txt");
                var sourceDirectory = Path.Combine(Path.GetTempPath(), "assets");
                var destination = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
                creationMappingDialog.Sources.Add(new RainArchives.App.Views.ArchiveCreationSourceItem(
                    RainArchives.App.Views.ArchiveCreationSourceKind.File,
                    sourceFile,
                    "alpha.txt"));
                creationMappingDialog.Sources.Add(new RainArchives.App.Views.ArchiveCreationSourceItem(
                    RainArchives.App.Views.ArchiveCreationSourceKind.Directory,
                    sourceDirectory,
                    "assets"));
                creationMappingDialog.Loaded += (_, _) =>
                {
                    var mappingDestination = Assert.IsInstanceOfType<TextBox>(
                        creationMappingDialog.FindName("DestinationInput"));
                    mappingDestination.Text = destination;
                    var mappingCreateButton = FindVisualDescendant<Button>(
                        creationMappingDialog,
                        button => string.Equals(
                            AutomationProperties.GetName(button),
                            "Create ZIP archive",
                            StringComparison.Ordinal));
                    Assert.IsNotNull(mappingCreateButton);
                    mappingCreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                Assert.AreEqual(true, creationMappingDialog.ShowDialog());
                var creationSelection = creationMappingDialog.Selection;
                Assert.IsNotNull(creationSelection);
                Assert.AreEqual(destination, creationSelection.DestinationPath);
                Assert.AreEqual(ArchiveCompressionPreset.Balanced, creationSelection.CompressionPreset);
                Assert.AreEqual(new ArchiveCreationFile(sourceFile, "alpha.txt"), creationSelection.Files.Single());
                Assert.AreEqual(
                    new ArchiveCreationDirectory(sourceDirectory, "assets"),
                    creationSelection.Directories.Single());

                var recoveryDialog = new RainArchives.App.Views.ArchiveRecoveryDialog(
                    new PassiveRecoveryArchiveClient())
                {
                    Owner = window,
                };
                recoveryDialog.Show();
                recoveryDialog.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(
                    DispatcherPriority.ApplicationIdle,
                    static () => { });
                var recoveryTabs = Assert.IsInstanceOfType<TabControl>(
                    recoveryDialog.FindName("RecoveryTabs"));
                Assert.AreEqual("Recovery categories", AutomationProperties.GetName(recoveryTabs));
                var creationRecoveryTab = Assert.IsInstanceOfType<TabItem>(
                    recoveryDialog.FindName("CreationRecoveryTab"));
                var nestedRecoveryTab = Assert.IsInstanceOfType<TabItem>(
                    recoveryDialog.FindName("NestedRecoveryTab"));
                var extractionRecoveryTab = Assert.IsInstanceOfType<TabItem>(
                    recoveryDialog.FindName("ExtractionRecoveryTab"));
                var supportRecoveryTab = Assert.IsInstanceOfType<TabItem>(
                    recoveryDialog.FindName("SupportRecoveryTab"));
                Assert.AreEqual(4, recoveryTabs.Items.Count);
                Assert.IsInstanceOfType<ArchiveRecoveryCenterViewModel>(
                    creationRecoveryTab.DataContext);
                Assert.IsInstanceOfType<NestedWorkspaceRecoveryCenterViewModel>(
                    nestedRecoveryTab.DataContext);
                Assert.IsInstanceOfType<ExtractionRecoveryCenterViewModel>(
                    extractionRecoveryTab.DataContext);
                Assert.IsInstanceOfType<RecoverySupportViewModel>(
                    supportRecoveryTab.DataContext);
                Assert.AreEqual(
                    ((SolidColorBrush)application.TryFindResource("BackgroundBrush")).Color,
                    ((SolidColorBrush)recoveryDialog.Background).Color);
                nestedRecoveryTab.IsSelected = true;
                recoveryDialog.UpdateLayout();
                var nestedRecoveryGrid = Assert.IsInstanceOfType<DataGrid>(
                    recoveryDialog.FindName("NestedRecoveryGrid"));
                Assert.AreEqual(
                    "Nested workspace recovery records",
                    AutomationProperties.GetName(nestedRecoveryGrid));
                StringAssert.Contains(
                    AutomationProperties.GetHelpText(nestedRecoveryGrid),
                    "Path-free");
                Assert.AreEqual(1, KeyboardNavigation.GetTabIndex(nestedRecoveryGrid));
                var nestedRecoveryId = Assert.IsInstanceOfType<TextBox>(
                    recoveryDialog.FindName("NestedRecoveryIdText"));
                Assert.IsTrue(nestedRecoveryId.IsReadOnly);
                Assert.AreEqual(
                    "Nested workspace recovery identifier",
                    AutomationProperties.GetName(nestedRecoveryId));
                StringAssert.Contains(
                    AutomationProperties.GetHelpText(nestedRecoveryId),
                    "No filesystem path");
                var nestedRevision = Assert.IsInstanceOfType<TextBox>(
                    recoveryDialog.FindName("NestedRecoveryRevisionText"));
                Assert.IsTrue(nestedRevision.IsReadOnly);
                var nestedSources = Assert.IsInstanceOfType<DataGrid>(
                    recoveryDialog.FindName("NestedRecoverySourceGrid"));
                Assert.AreEqual(
                    "Recovered nested archive sources",
                    AutomationProperties.GetName(nestedSources));
                Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(nestedSources));
                Assert.AreEqual(
                    VirtualizationMode.Recycling,
                    VirtualizingPanel.GetVirtualizationMode(nestedSources));
                var reopenNested = Assert.IsInstanceOfType<Button>(
                    recoveryDialog.FindName("ReopenNestedRecoveryButton"));
                Assert.AreEqual(
                    "Open recovered archive independently",
                    AutomationProperties.GetName(reopenNested));
                StringAssert.Contains(
                    AutomationProperties.GetHelpText(reopenNested),
                    "depth-zero");
                Assert.IsFalse(reopenNested.IsEnabled);
                var discardNested = Assert.IsInstanceOfType<Button>(
                    recoveryDialog.FindName("DiscardNestedRecoveryButton"));
                Assert.AreEqual(
                    "Discard verified nested workspace",
                    AutomationProperties.GetName(discardNested));
                StringAssert.Contains(
                    AutomationProperties.GetHelpText(discardNested),
                    "freshly revalidated");
                Assert.IsFalse(discardNested.IsEnabled);
                var nestedStatus = Assert.IsInstanceOfType<TextBlock>(
                    recoveryDialog.FindName("NestedRecoveryStatusText"));
                Assert.AreEqual(
                    AutomationLiveSetting.Assertive,
                    AutomationProperties.GetLiveSetting(nestedStatus));
                var creationStatus = Assert.IsInstanceOfType<TextBlock>(
                    recoveryDialog.FindName("CreationRecoveryStatusText"));
                Assert.AreEqual(
                    AutomationLiveSetting.Off,
                    AutomationProperties.GetLiveSetting(creationStatus));

                extractionRecoveryTab.IsSelected = true;
                recoveryDialog.UpdateLayout();
                var extractionGrid = Assert.IsInstanceOfType<DataGrid>(
                    recoveryDialog.FindName("ExtractionRecoveryGrid"));
                Assert.AreEqual(
                    "Extraction recovery records",
                    AutomationProperties.GetName(extractionGrid));
                StringAssert.Contains(
                    AutomationProperties.GetHelpText(extractionGrid),
                    "Path-free");
                Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(extractionGrid));
                var inspectExtraction = Assert.IsInstanceOfType<Button>(
                    recoveryDialog.FindName("InspectExtractionRecoveryButton"));
                Assert.IsTrue(inspectExtraction.IsDefault);
                StringAssert.Contains(
                    AutomationProperties.GetHelpText(inspectExtraction),
                    "without changing it");
                Assert.AreEqual(
                    AutomationLiveSetting.Off,
                    AutomationProperties.GetLiveSetting(nestedStatus));
                var extractionStatus = Assert.IsInstanceOfType<TextBlock>(
                    recoveryDialog.FindName("ExtractionRecoveryStatusText"));
                Assert.AreEqual(
                    AutomationLiveSetting.Assertive,
                    AutomationProperties.GetLiveSetting(extractionStatus));

                supportRecoveryTab.IsSelected = true;
                recoveryDialog.UpdateLayout();
                var refreshSupport = Assert.IsInstanceOfType<Button>(
                    recoveryDialog.FindName("RefreshRecoverySupportButton"));
                Assert.AreEqual(
                    "Refresh fixed-field support evidence",
                    AutomationProperties.GetName(refreshSupport));
                Assert.IsTrue(refreshSupport.IsDefault);
                Assert.AreEqual(
                    AutomationLiveSetting.Off,
                    AutomationProperties.GetLiveSetting(extractionStatus));
                var supportStatus = Assert.IsInstanceOfType<TextBlock>(
                    recoveryDialog.FindName("SupportRecoveryStatusText"));
                Assert.AreEqual(
                    AutomationLiveSetting.Assertive,
                    AutomationProperties.GetLiveSetting(supportStatus));
                recoveryDialog.Close();
                recoveryDialog.DrainAsync().AsTask().GetAwaiter().GetResult();

                var blockingRecoveryClient = new BlockingRecoveryArchiveClient();
                var drainingRecoveryDialog = new RainArchives.App.Views.ArchiveRecoveryDialog(
                    blockingRecoveryClient)
                {
                    Owner = window,
                };
                drainingRecoveryDialog.Show();
                drainingRecoveryDialog.UpdateLayout();
                Assert.IsTrue(blockingRecoveryClient.ListStarted.Task.IsCompleted);
                drainingRecoveryDialog.Close();
                var recoveryDrain = drainingRecoveryDialog.DrainAsync().AsTask();
                PumpDispatcherUntil(recoveryDrain, TimeSpan.FromSeconds(2));
                Assert.IsTrue(blockingRecoveryClient.CancellationObserved.Task.IsCompleted);

                Assert.AreEqual(0, themeManager.AttachedWindowCount);
                var dialogServiceRoot = Path.Combine(
                    Path.GetTempPath(),
                    "RainArchives.WpfDialogServiceTests",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dialogServiceRoot);
                try
                {
                    var dialogService = new AppDialogService(
                        window,
                        new ZipPresetStore(dialogServiceRoot),
                        new AppSettingsStore(dialogServiceRoot));
                    var serviceActivityViewModel = new ActivityCenterViewModel(
                        new PassiveRecoveryArchiveClient());
                    dialogService.ShowActivityCenter(serviceActivityViewModel);
                    var serviceActivityWindow = Application.Current.Windows
                        .OfType<RainArchives.App.Views.ActivityCenterWindow>()
                        .Single(candidate => candidate.Owner == window && candidate.IsVisible);
                    Assert.AreSame(window, serviceActivityWindow.Owner);
                    Assert.AreEqual(1, themeManager.AttachedWindowCount);
                    serviceActivityWindow.Close();
                    Assert.AreEqual(0, themeManager.AttachedWindowCount);
                    serviceActivityViewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();

                    var modalCreationObserved = false;
                    _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                        DispatcherPriority.Loaded,
                        () =>
                        {
                            var serviceCreateDialog = Application.Current.Windows
                                .OfType<RainArchives.App.Views.CreateArchiveDialog>()
                                .Single(candidate => candidate.Owner == window && candidate.IsVisible);
                            Assert.AreSame(window, serviceCreateDialog.Owner);
                            Assert.AreEqual(1, themeManager.AttachedWindowCount);
                            modalCreationObserved = true;
                            serviceCreateDialog.Close();
                        });
                    Assert.IsNull(dialogService.ChooseArchiveCreation(supportsDirectoryCreation: true));
                    Assert.IsTrue(modalCreationObserved);
                    Assert.AreEqual(0, themeManager.AttachedWindowCount);
                }
                finally
                {
                    Directory.Delete(dialogServiceRoot, recursive: true);
                }

                const string fixturePassword = "RainArchives-Fixture-2026!";
                PasswordBox? passwordInput = null;
                var untrustedArchiveName = "aes256\r\n\u202E.zip" + new string('x', 300);
                var escapedArchiveName = UntrustedDisplayText.Escape(untrustedArchiveName);
                var dialog = new RainArchives.App.Views.PasswordDialog(untrustedArchiveName)
                {
                    Owner = window,
                };
                Assert.AreEqual(1, themeManager.AttachedWindowCount);
                dialog.Loaded += (_, _) =>
                {
                    var archiveNameText = Assert.IsInstanceOfType<TextBlock>(
                        dialog.FindName("ArchiveNameText"));
                    Assert.AreEqual(escapedArchiveName, archiveNameText.Text);
                    Assert.AreEqual(escapedArchiveName, archiveNameText.ToolTip);
                    StringAssert.Contains(
                        AutomationProperties.GetHelpText(archiveNameText),
                        escapedArchiveName);
                    Assert.AreEqual(TextTrimming.CharacterEllipsis, archiveNameText.TextTrimming);
                    Assert.AreEqual(TextWrapping.NoWrap, archiveNameText.TextWrapping);
                    Assert.IsTrue(archiveNameText.ActualHeight <= 36.1);
                    passwordInput = Assert.IsInstanceOfType<PasswordBox>(
                        dialog.FindName("PasswordInput"));
                    Assert.AreEqual("Archive password", AutomationProperties.GetName(passwordInput));
                    Assert.AreEqual(
                        "Enter the password required to open, preview, test, or extract this archive.",
                        AutomationProperties.GetHelpText(passwordInput));
                    Assert.IsTrue(AutomationProperties.GetIsRequiredForForm(passwordInput));
                    passwordInput.Password = fixturePassword;
                    var continueButton = FindVisualDescendant<Button>(
                        dialog,
                        button => string.Equals(
                            AutomationProperties.GetName(button),
                            "Continue with archive password",
                            StringComparison.Ordinal));
                    Assert.IsNotNull(continueButton);
                    continueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };

                Assert.AreEqual(true, dialog.ShowDialog());
                Assert.AreEqual(0, themeManager.AttachedWindowCount);
                var capturedPassword = dialog.TakePassword();
                try
                {
                    Assert.IsTrue(capturedPassword.AsSpan().SequenceEqual(fixturePassword));
                    Assert.AreEqual(string.Empty, passwordInput?.Password);
                }
                finally
                {
                    Array.Clear(capturedPassword);
                }

                using (var promptCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                {
                    var dialogService = new AppDialogService(window);
                    try
                    {
                        _ = dialogService.RequestPasswordAsync(
                            "cancelled-preview.zip",
                            promptCancellation.Token);
                        Assert.Fail("The cancelled password prompt returned instead of observing cancellation.");
                    }
                    catch (OperationCanceledException) when (promptCancellation.IsCancellationRequested)
                    {
                    }
                }

                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                application?.Shutdown();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = "RainArchives.WpfStartupTest",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "The WPF startup thread did not terminate.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void PumpDispatcherUntil(Task task, TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var registration = timeoutCancellation.Token.Register(
            () => dispatcher.BeginInvoke(() => frame.Continue = false));
        _ = task.ContinueWith(
            _ => dispatcher.BeginInvoke(() => frame.Continue = false),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        Assert.IsTrue(task.IsCompleted, "The WPF recovery dialog did not drain before timeout.");
        task.GetAwaiter().GetResult();
    }

    private static void AssertNoActiveMnemonicCollisions(IEnumerable<Button> buttons)
    {
        var collisions = buttons
            .Select(button => new
            {
                Button = button,
                AccessKey = GetButtonAccessKey(button),
            })
            .Where(item => item.AccessKey.HasValue)
            .GroupBy(item => item.AccessKey!.Value)
            .Where(group => group.Count() > 1)
            .Select(group =>
                $"Alt+{group.Key}: {string.Join(", ", group.Select(item => AutomationProperties.GetName(item.Button)))}")
            .ToArray();

        Assert.AreEqual(
            0,
            collisions.Length,
            $"Active no-session actions share an access key: {string.Join("; ", collisions)}");
    }

    private static char? GetButtonAccessKey(Button button)
    {
        var text = button.Content as string;
        if (text is null && button.Content is DependencyObject content)
        {
            text = (content as AccessText)?.Text ??
                FindVisualDescendant<AccessText>(content, _ => true)?.Text;
        }

        if (text is null)
        {
            return null;
        }

        for (var index = 0; index < text.Length - 1; index++)
        {
            if (text[index] != '_')
            {
                continue;
            }

            if (text[index + 1] == '_')
            {
                index++;
                continue;
            }

            return char.ToUpperInvariant(text[index + 1]);
        }

        return null;
    }

    private static ArchiveEntryDescriptor CreateInspectorFocusEntry() =>
        new(
            EntryId.New(),
            null,
            0,
            "focus.txt",
            "focus.txt",
            ArchiveEntryKind.File,
            5,
            5,
            null,
            null,
            "Store",
            null,
            false,
            false,
            null,
            EntrySafetyStatus.Safe,
            null,
            "focus.txt");

    [DllImport("shcore.dll")]
    private static extern int GetProcessDpiAwareness(
        IntPtr processHandle,
        out ProcessDpiAwareness awareness);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    private enum ProcessDpiAwareness
    {
        Unaware = 0,
        SystemAware = 1,
        PerMonitorAware = 2,
    }

    private static IntPtr WaitForTopLevelWindow(Process process, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        do
        {
            process.Refresh();
            var windowHandle = process.MainWindowHandle;
            if (windowHandle != IntPtr.Zero)
            {
                return windowHandle;
            }

            if (process.HasExited)
            {
                break;
            }

            Thread.Sleep(50);
        }
        while (stopwatch.Elapsed < timeout);

        return IntPtr.Zero;
    }

    private static T? FindVisualDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate && predicate(candidate))
            {
                return candidate;
            }

            var descendant = FindVisualDescendant(child, predicate);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private sealed class PassiveRecoveryArchiveClient : ArchiveClientTestDouble
    {
        public override Task<ListArchiveCreationRecoveriesResponse>
            ListArchiveCreationRecoveriesAsync(
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ListArchiveCreationRecoveriesResponse(
                [],
                ActiveOperationCount: 0,
                SuspiciousOperationCount: 0,
                IncompleteJournalCount: 0,
                IsTruncated: false,
                InspectionIncomplete: false));
        }

        public override Task<ListNestedWorkspaceRecoveriesResponse>
            ListNestedWorkspaceRecoveriesAsync(
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ListNestedWorkspaceRecoveriesResponse(
                [],
                ActiveWorkspaceCount: 0,
                SuspiciousWorkspaceCount: 0,
                IsTruncated: false,
                InspectionIncomplete: false));
        }
    }

    private sealed class BlockingRecoveryArchiveClient : ArchiveClientTestDouble
    {
        public TaskCompletionSource<bool> ListStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<ListArchiveCreationRecoveriesResponse>
            ListArchiveCreationRecoveriesAsync(
                CancellationToken cancellationToken = default)
        {
            ListStarted.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult(true);
                throw;
            }

            throw new InvalidOperationException("The blocking recovery request unexpectedly completed.");
        }
    }
}
