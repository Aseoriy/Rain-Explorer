using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class SecondaryWindowPresentationTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] SecondaryWindowFiles =
    [
        "ActivityCenterWindow.xaml",
        "SettingsCenterWindow.xaml",
        "ArchiveRecoveryDialog.xaml",
        "CreateArchiveDialog.xaml",
        "RebuildArchiveDialog.xaml",
        "PasswordDialog.xaml",
    ];

    [TestMethod]
    public void SecondaryWindowsUseDynamicSemanticPalettesAndBoundedLayouts()
    {
        foreach (var fileName in SecondaryWindowFiles)
        {
            var document = LoadView(fileName);
            var root = document.Root;
            Assert.IsNotNull(root, $"{fileName} has no root element.");
            Assert.AreEqual(
                "{DynamicResource BackgroundBrush}",
                (string?)root.Attribute("Background"),
                $"{fileName} must use the semantic window background.");
            Assert.AreEqual(
                "{DynamicResource BackgroundForegroundBrush}",
                (string?)root.Attribute("Foreground"),
                $"{fileName} must use the paired semantic window foreground.");

            foreach (var attribute in root.DescendantsAndSelf().Attributes())
            {
                var value = attribute.Value;
                Assert.IsFalse(
                    value.Contains("SystemColors.", StringComparison.Ordinal),
                    $"{fileName} directly consumes a system color in {attribute.Name}: {value}");
                Assert.IsFalse(
                    Regex.IsMatch(value, @"^\{StaticResource [^}]*Brush\}$", RegexOptions.CultureInvariant),
                    $"{fileName} statically binds a palette brush in {attribute.Name}: {value}");
            }
        }

        var create = LoadView("CreateArchiveDialog.xaml");
        AssertBoundedScrollViewer(create, "CreateContentScroll");
        Assert.AreEqual("Create archive", (string?)create.Root?.Attribute("Title"));
        Assert.AreEqual("Create a new archive", (string?)GetNamed(create, "HeaderText").Attribute("Text"));
        Assert.AreEqual(
            "New archive destination",
            (string?)GetNamed(create, "DestinationInput").Attribute("AutomationProperties.Name"));
        Assert.AreEqual(
            "Compression preset",
            (string?)GetNamed(create, "PresetInput").Attribute("AutomationProperties.Name"));
        StringAssert.Contains(
            (string?)GetNamed(create, "LimitationsText").Attribute("Text") ?? string.Empty,
            "distinct absent destination");
        var rebuild = LoadView("RebuildArchiveDialog.xaml");
        AssertBoundedScrollViewer(rebuild, "RebuildContentScroll");

        var passwordName = GetNamed(LoadView("PasswordDialog.xaml"), "ArchiveNameText");
        Assert.AreEqual("36", (string?)passwordName.Attribute("MaxHeight"));
        Assert.AreEqual("CharacterEllipsis", (string?)passwordName.Attribute("TextTrimming"));
        Assert.AreEqual("NoWrap", (string?)passwordName.Attribute("TextWrapping"));

        var presetEditor = GetNamed(LoadView("SettingsCenterWindow.xaml"), "PresetEditorGrid");
        var presetRows = presetEditor
            .Element(Presentation + "Grid.RowDefinitions")?
            .Elements(Presentation + "RowDefinition")
            .ToArray() ?? [];
        Assert.AreEqual(5, presetRows.Length, "The settings preset editor must keep rows 0 through 4.");

        var settings = LoadView("SettingsCenterWindow.xaml");
        var settingsScroll = GetNamed(settings, "SettingsContentScroll");
        Assert.AreEqual("False", (string?)settingsScroll.Attribute("CanContentScroll"));
        Assert.AreEqual("Stretch", (string?)settingsScroll.Attribute("HorizontalContentAlignment"));
        Assert.AreEqual("VerticalOnly", (string?)settingsScroll.Attribute("PanningMode"));
        Assert.AreEqual("Disabled", (string?)settingsScroll.Attribute("HorizontalScrollBarVisibility"));
        Assert.AreEqual("Auto", (string?)settingsScroll.Attribute("VerticalScrollBarVisibility"));
        Assert.AreEqual("290", (string?)GetNamed(settings, "FormatDefaultsGrid").Attribute("MaxHeight"));
        Assert.AreEqual("260", (string?)GetNamed(settings, "PresetGrid").Attribute("MaxHeight"));
        Assert.IsTrue(settings
            .Descendants(Presentation + "TextBlock")
            .Any(element =>
                string.Equals((string?)element.Attribute("Text"), "{Binding ConstraintText}", StringComparison.Ordinal)
                && string.Equals((string?)element.Attribute("TextWrapping"), "Wrap", StringComparison.Ordinal)),
            "Long managed-format constraints must wrap inside the bounded grid column.");
    }

    [TestMethod]
    public void SharedControlDictionaryOwnsEveryExposedWpfChromePath()
    {
        var app = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RainArchives.App",
            "App.xaml"));
        var mergedSources = app
            .Descendants(Presentation + "ResourceDictionary")
            .Select(dictionary => (string?)dictionary.Attribute("Source"))
            .Where(source => source is not null)
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { "Themes/Light.xaml", "Themes/Controls.xaml", "Themes/DesignTokens.xaml" },
            mergedSources,
            "The active palette must remain first, followed by owned control chrome and product tokens.");

        var controls = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RainArchives.App",
            "Themes",
            "Controls.xaml"));
        var designTokens = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RainArchives.App",
            "Themes",
            "DesignTokens.xaml"));
        var templatedStyles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SemanticTextBoxStyle"] = "TextBox",
            ["SemanticPasswordBoxStyle"] = "PasswordBox",
            ["ComboBoxToggleButtonStyle"] = "ToggleButton",
            ["SemanticComboBoxItemStyle"] = "ComboBoxItem",
            ["SemanticComboBoxStyle"] = "ComboBox",
            ["SemanticContextMenuStyle"] = "ContextMenu",
            ["SemanticMenuItemStyle"] = "MenuItem",
            ["SemanticSeparatorStyle"] = "Separator",
            ["ScrollBarLineButtonStyle"] = "RepeatButton",
            ["ScrollBarPageButtonStyle"] = "RepeatButton",
            ["ScrollBarThumbStyle"] = "Thumb",
            ["SemanticScrollBarStyle"] = "ScrollBar",
            ["SemanticCheckBoxStyle"] = "CheckBox",
            ["SemanticTabControlStyle"] = "TabControl",
            ["SemanticTabItemStyle"] = "TabItem",
            ["SemanticDataGridColumnHeaderStyle"] = "DataGridColumnHeader",
            ["SemanticDataGridCellStyle"] = "DataGridCell",
            ["SemanticProgressBarStyle"] = "ProgressBar",
            ["ExpanderHeaderToggleStyle"] = "ToggleButton",
            ["SemanticExpanderStyle"] = "Expander",
            ["SemanticToolTipStyle"] = "ToolTip",
            ["SemanticStatusBarStyle"] = "StatusBar",
            ["SemanticStatusBarItemStyle"] = "StatusBarItem",
            ["SemanticLabelStyle"] = "Label",
        };
        foreach (var expectation in templatedStyles)
        {
            var style = GetKeyed(controls, expectation.Key);
            Assert.AreEqual(expectation.Value, (string?)style.Attribute("TargetType"), expectation.Key);
            Assert.IsTrue(
                style.Descendants(Presentation + "Setter").Any(setter =>
                    string.Equals((string?)setter.Attribute("Property"), "Template", StringComparison.Ordinal)),
                $"{expectation.Key} must own its WPF chrome instead of falling through to the OS theme.");
        }

        foreach (var targetType in new[]
                 {
                     "TextBox",
                     "PasswordBox",
                     "ComboBox",
                     "ComboBoxItem",
                     "ContextMenu",
                     "MenuItem",
                     "Separator",
                     "ScrollBar",
                     "CheckBox",
                     "TabControl",
                     "TabItem",
                     "DataGrid",
                     "DataGridCell",
                     "DataGridColumnHeader",
                     "DataGridRow",
                     "ProgressBar",
                     "Expander",
                     "ToolTip",
                     "StatusBar",
                     "StatusBarItem",
                     "Label",
                 })
        {
            Assert.IsTrue(
                controls.Root!.Elements(Presentation + "Style")
                    .Concat(designTokens.Root!.Elements(Presentation + "Style"))
                    .Any(style =>
                    style.Attribute(Xaml + "Key") is null
                    && string.Equals((string?)style.Attribute("TargetType"), targetType, StringComparison.Ordinal)),
                $"The shared dictionary has no implicit {targetType} style.");
        }

        foreach (var composition in new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TextBox"] = "{StaticResource SemanticTextBoxStyle}",
            ["PasswordBox"] = "{StaticResource SemanticPasswordBoxStyle}",
            ["ComboBox"] = "{StaticResource SemanticComboBoxStyle}",
        })
        {
            var implicitStyle = designTokens.Root!.Elements(Presentation + "Style").Single(style =>
                style.Attribute(Xaml + "Key") is null
                && string.Equals((string?)style.Attribute("TargetType"), composition.Key, StringComparison.Ordinal));
            Assert.AreEqual(composition.Value, (string?)implicitStyle.Attribute("BasedOn"));
        }

        foreach (var styleKey in new[]
                 {
                     "DataGridCheckBoxElementStyle",
                     "DataGridCheckBoxEditingElementStyle",
                 })
        {
            var style = GetKeyed(controls, styleKey);
            Assert.AreEqual("CheckBox", (string?)style.Attribute("TargetType"));
            Assert.AreEqual("{StaticResource SemanticCheckBoxStyle}", (string?)style.Attribute("BasedOn"));
        }

        var displayCheckBoxStyle = GetKeyed(controls, "DataGridCheckBoxElementStyle");
        Assert.AreEqual(
            "False",
            (string?)displayCheckBoxStyle.Descendants(Presentation + "Setter")
                .Single(setter => string.Equals(
                    (string?)setter.Attribute("Property"),
                    "IsHitTestVisible",
                    StringComparison.Ordinal))
                .Attribute("Value"));
        var editingCheckBoxStyle = GetKeyed(controls, "DataGridCheckBoxEditingElementStyle");
        Assert.AreEqual(
            "True",
            (string?)editingCheckBoxStyle.Descendants(Presentation + "Setter")
                .Single(setter => string.Equals(
                    (string?)setter.Attribute("Property"),
                    "IsHitTestVisible",
                    StringComparison.Ordinal))
                .Attribute("Value"));
        var textEditingStyle = GetKeyed(controls, "DataGridTextEditingElementStyle");
        Assert.AreEqual("TextBox", (string?)textEditingStyle.Attribute("TargetType"));
        Assert.AreEqual(
            "{StaticResource SemanticTextBoxStyle}",
            (string?)textEditingStyle.Attribute("BasedOn"));
        var menuSeparatorStyle = GetKeyed(controls, "{x:Static MenuItem.SeparatorStyleKey}");
        Assert.AreEqual("Separator", (string?)menuSeparatorStyle.Attribute("TargetType"));
        Assert.AreEqual(
            "{StaticResource SemanticSeparatorStyle}",
            (string?)menuSeparatorStyle.Attribute("BasedOn"));

        var comboToggle = GetKeyed(controls, "ComboBoxToggleButtonStyle");
        foreach (var triggerProperty in new[] { "IsMouseOver", "IsPressed" })
        {
            Assert.IsTrue(
                comboToggle.Descendants(Presentation + "Trigger").Any(trigger =>
                    string.Equals((string?)trigger.Attribute("Property"), triggerProperty, StringComparison.Ordinal)
                    && string.Equals((string?)trigger.Attribute("Value"), "True", StringComparison.Ordinal)),
                $"The ComboBox toggle has no explicit {triggerProperty} visual state.");
        }

        var standardButton = GetKeyed(designTokens, "StandardButtonStyle");
        Assert.IsTrue(
            standardButton.Descendants(Presentation + "ControlTemplate").Any(template =>
                string.Equals((string?)template.Attribute("TargetType"), "Button", StringComparison.Ordinal)
                && template.Descendants().Any(element => string.Equals(
                    (string?)element.Attribute(Xaml + "Name"),
                    "ButtonBorder",
                    StringComparison.Ordinal))),
            "The implicit Button path must retain the owned ButtonBorder template.");
        foreach (var triggerProperty in new[] { "IsMouseOver", "IsPressed", "IsEnabled" })
        {
            Assert.IsTrue(
                standardButton.Descendants(Presentation + "Trigger").Any(trigger =>
                    string.Equals((string?)trigger.Attribute("Property"), triggerProperty, StringComparison.Ordinal)),
                $"StandardButtonStyle has no explicit {triggerProperty} visual state.");
        }

        var menuItemStyle = GetKeyed(controls, "SemanticMenuItemStyle");
        Assert.IsTrue(
            menuItemStyle.Descendants(Presentation + "Trigger").Any(trigger =>
                string.Equals((string?)trigger.Attribute("Property"), "IsHighlighted", StringComparison.Ordinal)
                && string.Equals((string?)trigger.Attribute("Value"), "True", StringComparison.Ordinal)
                && trigger.Descendants(Presentation + "Setter").Any(setter =>
                    string.Equals((string?)setter.Attribute("TargetName"), "ItemBorder", StringComparison.Ordinal)
                    && string.Equals((string?)setter.Attribute("Property"), "Background", StringComparison.Ordinal)
                    && string.Equals(
                        (string?)setter.Attribute("Value"),
                        "{DynamicResource AccentWashBrush}",
                        StringComparison.Ordinal))),
            "The owned MenuItem template must retain its semantic highlighted state.");
        Assert.IsTrue(
            GetKeyed(controls, "SemanticContextMenuStyle")
                .Descendants(Presentation + "ItemsPresenter")
                .Any(presenter => string.Equals(
                    (string?)presenter.Attribute("KeyboardNavigation.DirectionalNavigation"),
                    "Cycle",
                    StringComparison.Ordinal)),
            "ContextMenu directional keyboard navigation must remain cyclic.");
        Assert.IsTrue(
            GetKeyed(controls, "SemanticComboBoxStyle")
                .Descendants(Presentation + "ItemsPresenter")
                .Any(presenter => string.Equals(
                    (string?)presenter.Attribute("KeyboardNavigation.DirectionalNavigation"),
                    "Contained",
                    StringComparison.Ordinal)),
            "ComboBox popup directional keyboard navigation must remain contained.");

        var rebuildCheckBoxColumns = LoadView("RebuildArchiveDialog.xaml")
            .Descendants(Presentation + "DataGridCheckBoxColumn")
            .ToArray();
        Assert.HasCount(2, rebuildCheckBoxColumns);
        foreach (var column in rebuildCheckBoxColumns)
        {
            Assert.AreEqual(
                "{StaticResource DataGridCheckBoxElementStyle}",
                (string?)column.Attribute("ElementStyle"));
            Assert.AreEqual(
                "{StaticResource DataGridCheckBoxEditingElementStyle}",
                (string?)column.Attribute("EditingElementStyle"));
        }
        var rebuildTextEditingColumns = LoadView("RebuildArchiveDialog.xaml")
            .Descendants(Presentation + "DataGridTextColumn")
            .Where(column => string.Equals((string?)column.Attribute("Header"), "Final time", StringComparison.Ordinal)
                || string.Equals((string?)column.Attribute("Header"), "Final attributes", StringComparison.Ordinal))
            .ToArray();
        Assert.HasCount(2, rebuildTextEditingColumns);
        foreach (var column in rebuildTextEditingColumns)
        {
            Assert.AreEqual(
                "{StaticResource DataGridTextEditingElementStyle}",
                (string?)column.Attribute("EditingElementStyle"));
        }

        foreach (var attribute in controls.Root!.DescendantsAndSelf().Attributes())
        {
            Assert.IsFalse(
                attribute.Value.Contains("SystemColors.", StringComparison.Ordinal),
                $"Controls.xaml bypasses the semantic palette in {attribute.Name}: {attribute.Value}");
            Assert.IsFalse(
                Regex.IsMatch(attribute.Value, @"^#[0-9A-Fa-f]{6,8}$", RegexOptions.CultureInvariant),
                $"Controls.xaml hard-codes a color in {attribute.Name}: {attribute.Value}");
            Assert.IsFalse(
                Regex.IsMatch(attribute.Value, @"^\{StaticResource [^}]*Brush\}$", RegexOptions.CultureInvariant),
                $"Controls.xaml freezes a semantic brush in {attribute.Name}: {attribute.Value}");
        }

        foreach (var partName in new[]
                 {
                     "PART_ContentHost",
                     "PART_EditableTextBox",
                     "PART_Popup",
                     "PART_Track",
                     "PART_Indicator",
                     "PART_SelectedContentHost",
                     "PART_LeftHeaderGripper",
                     "PART_RightHeaderGripper",
                 })
        {
            Assert.IsTrue(
                controls.Descendants().Any(element =>
                    string.Equals((string?)element.Attribute(Xaml + "Name"), partName, StringComparison.Ordinal)),
                $"Owned templates do not preserve required part {partName}.");
        }
    }

    [TestMethod]
    public void SecondaryWindowAccessKeysTabOrderAndButtonAutomationAreComplete()
    {
        foreach (var fileName in SecondaryWindowFiles)
        {
            var document = LoadView(fileName);
            foreach (var button in document.Descendants(Presentation + "Button"))
            {
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace((string?)button.Attribute("AutomationProperties.Name")),
                    $"{fileName} contains a button without an Automation name.");
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace((string?)button.Attribute("AutomationProperties.HelpText")),
                    $"{fileName} contains a button without bounded Automation help text.");
            }
        }

        foreach (var fileName in SecondaryWindowFiles.Where(file =>
                     !string.Equals(file, "ArchiveRecoveryDialog.xaml", StringComparison.Ordinal)))
        {
            var document = LoadView(fileName);
            AssertNoAccessKeyCollisions(document.Descendants(), fileName);
            AssertSequentialTabOrder(document.Descendants(), fileName);
        }

        var recovery = LoadView("ArchiveRecoveryDialog.xaml");
        var recoveryTabs = recovery.Descendants(Presentation + "TabItem").ToArray();
        Assert.AreEqual(4, recoveryTabs.Length);
        foreach (var tab in recoveryTabs)
        {
            var scope = recoveryTabs.Cast<XElement>().Concat(tab.Descendants());
            var tabName = (string?)tab.Attribute(Xaml + "Name") ?? "unnamed recovery tab";
            AssertNoAccessKeyCollisions(scope, tabName);
            AssertSequentialTabOrder(tab.Descendants(), tabName);
        }

        AssertDefaultAndCancel(
            LoadView("SettingsCenterWindow.xaml"),
            ("Save settings and presets", "IsDefault"),
            ("Close Settings and Presets", "IsCancel"));
        AssertDefaultAndCancel(
            LoadView("CreateArchiveDialog.xaml"),
            ("Create archive", "IsDefault"),
            ("Cancel archive creation setup", "IsCancel"));
        AssertDefaultAndCancel(
            LoadView("RebuildArchiveDialog.xaml"),
            ("Confirm ZIP rebuild copy", "IsDefault"),
            ("Cancel ZIP rebuild setup", "IsCancel"));
        AssertDefaultAndCancel(
            LoadView("PasswordDialog.xaml"),
            ("Continue with archive password", "IsDefault"),
            ("Cancel password prompt", "IsCancel"));
    }

    [TestMethod]
    public void LiveRegionsAndLargeReviewGridsKeepDeliberateOwnersAndRecycling()
    {
        var activity = LoadView("ActivityCenterWindow.xaml");
        Assert.AreEqual(
            "Off",
            (string?)GetNamed(activity, "ActivityGrid").Attribute("AutomationProperties.LiveSetting"));
        Assert.AreEqual(
            "Polite",
            (string?)GetNamed(activity, "ActivityStatus").Attribute("AutomationProperties.LiveSetting"));

        var recovery = LoadView("ArchiveRecoveryDialog.xaml");
        var liveStyle = GetKeyed(recovery, "RecoverySelectedStatusStyle");
        var liveSetters = liveStyle
            .Descendants(Presentation + "Setter")
            .Where(setter => string.Equals(
                (string?)setter.Attribute("Property"),
                "AutomationProperties.LiveSetting",
                StringComparison.Ordinal))
            .Select(setter => (string?)setter.Attribute("Value"))
            .ToArray();
        CollectionAssert.AreEqual(new[] { "Off", "Assertive" }, liveSetters);
        foreach (var statusName in new[]
                 {
                     "CreationRecoveryStatusText",
                     "NestedRecoveryStatusText",
                     "ExtractionRecoveryStatusText",
                     "SupportRecoveryStatusText",
                 })
        {
            var status = GetNamed(recovery, statusName);
            Assert.AreEqual("{StaticResource RecoverySelectedStatusStyle}", (string?)status.Attribute("Style"));
            Assert.IsNull(status.Attribute("AutomationProperties.LiveSetting"));
        }

        AssertRecyclingGrid(LoadView("CreateArchiveDialog.xaml"), "SourcesGrid");
        var rebuild = LoadView("RebuildArchiveDialog.xaml");
        foreach (var gridName in new[]
                 {
                     "EntriesGrid",
                     "FilesGrid",
                     "TreePlanGrid",
                     "FolderAddsGrid",
                     "MetadataGrid",
                 })
        {
            AssertRecyclingGrid(rebuild, gridName);
        }
    }

    private static void AssertBoundedScrollViewer(XDocument document, string name)
    {
        var scroll = GetNamed(document, name);
        Assert.AreEqual("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
        Assert.AreEqual("Disabled", (string?)scroll.Attribute("HorizontalScrollBarVisibility"));
    }

    private static void AssertRecyclingGrid(XDocument document, string name)
    {
        var grid = GetNamed(document, name);
        Assert.AreEqual("True", (string?)grid.Attribute("EnableRowVirtualization"));
        Assert.AreEqual("True", (string?)grid.Attribute("EnableColumnVirtualization"));
        Assert.AreEqual("True", (string?)grid.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.AreEqual("Recycling", (string?)grid.Attribute("VirtualizingPanel.VirtualizationMode"));
        Assert.AreEqual("True", (string?)grid.Attribute("ScrollViewer.CanContentScroll"));
    }

    private static void AssertDefaultAndCancel(
        XDocument document,
        params (string AutomationName, string AttributeName)[] expectations)
    {
        foreach (var expectation in expectations)
        {
            var button = document
                .Descendants(Presentation + "Button")
                .Single(element => string.Equals(
                    (string?)element.Attribute("AutomationProperties.Name"),
                    expectation.AutomationName,
                    StringComparison.Ordinal));
            Assert.AreEqual("True", (string?)button.Attribute(expectation.AttributeName));
        }
    }

    private static void AssertNoAccessKeyCollisions(IEnumerable<XElement> elements, string scope)
    {
        var interactiveNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Button",
            "CheckBox",
            "Expander",
            "Label",
            "TabItem",
        };
        var collisions = elements
            .Where(element => interactiveNames.Contains(element.Name.LocalName))
            .Select(element => new
            {
                Element = element,
                Key = GetAccessKey(
                    (string?)element.Attribute("Content") ??
                    (string?)element.Attribute("Header")),
            })
            .Where(item => item.Key.HasValue)
            .GroupBy(item => item.Key!.Value)
            .Where(group => group.Count() > 1)
            .Select(group =>
                $"Alt+{group.Key}: {string.Join(", ", group.Select(item =>
                    (string?)item.Element.Attribute("AutomationProperties.Name") ??
                    (string?)item.Element.Attribute("Content") ??
                    (string?)item.Element.Attribute("Header") ??
                    item.Element.Name.LocalName))}")
            .ToArray();
        Assert.AreEqual(0, collisions.Length, $"{scope} has access-key collisions: {string.Join("; ", collisions)}");
    }

    private static char? GetAccessKey(string? text)
    {
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

    private static void AssertSequentialTabOrder(IEnumerable<XElement> elements, string scope)
    {
        var values = elements
            .Select(element =>
                (string?)element.Attribute("KeyboardNavigation.TabIndex") ??
                (string?)element.Attribute("TabIndex"))
            .Where(value => value is not null)
            .Select(value => int.Parse(value!))
            .Order()
            .ToArray();
        Assert.IsTrue(values.Length > 0, $"{scope} has no explicit tab order.");
        CollectionAssert.AreEqual(
            Enumerable.Range(0, values.Length).ToArray(),
            values,
            $"{scope} tab indices must be explicit, unique, and contiguous.");
    }

    private static XElement GetNamed(XDocument document, string name) =>
        document.Root!.DescendantsAndSelf().Single(element =>
            string.Equals((string?)element.Attribute(Xaml + "Name"), name, StringComparison.Ordinal));

    private static XElement GetKeyed(XDocument document, string key) =>
        document.Root!.DescendantsAndSelf().Single(element =>
            string.Equals((string?)element.Attribute(Xaml + "Key"), key, StringComparison.Ordinal));

    private static XDocument LoadView(string fileName) =>
        XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "RainArchives.App",
            "Views",
            fileName));

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
}
