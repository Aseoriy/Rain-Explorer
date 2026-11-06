using System.Xml.Linq;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class ManagedArchiveWriteSourceGuardTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ManagedMenusAreWorkerBoundAndNoLegacyCreationEntryRemains()
    {
        string root = FindRepositoryRoot();
        var pane = XDocument.Load(Path.Combine(root, "Main", "Controls", "PaneView.xaml"));
        XElement create = FindMenu(pane, "MenuCreateArchive");
        XElement rebuild = FindMenu(pane, "MenuArchiveRebuildCopy");

        Assert.Equal("CreateArchive_Click", (string?)create.Attribute("Click"));
        Assert.Contains("worker", create.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ArchiveRebuildCopy_Click", (string?)rebuild.Attribute("Click"));
        Assert.Equal("Collapsed", (string?)rebuild.Attribute("Visibility"));
        Assert.Contains("distinct", rebuild.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pane.Descendants(Presentation + "MenuItem"), element =>
            string.Equals((string?)element.Attribute(Xaml + "Name"), "MenuCompress",
                StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyInProcessArchiveWriterAndUnsafePartialDeleteCannotReturn()
    {
        string root = FindRepositoryRoot();
        string main = Path.Combine(root, "Main");
        Assert.False(File.Exists(Path.Combine(main, "Services", "CompressionService.cs")));

        string[] productionSources = Directory.EnumerateFiles(main, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string combined = string.Join('\n', productionSources.Select(File.ReadAllText));

        Assert.DoesNotContain("CompressionService.CreateZip", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("TryDeletePartialArchive", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("ZipArchiveMode.Create", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("new ZipArchive(", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("System.IO.Compression", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("ZipFile.", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("ArchiveFactory", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SharpCompress.", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteToDirectory", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtractToDirectory", combined, StringComparison.Ordinal);

        var project = XDocument.Load(Path.Combine(main, "RainExplorer.csproj"));
        Assert.DoesNotContain(project.Descendants("PackageReference"), reference =>
            string.Equals(
                (string?)reference.Attribute("Include"),
                "SharpCompress",
                StringComparison.OrdinalIgnoreCase));
        string[] rainArchiveReferences = project.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(
                (string?)reference.Attribute("Include") ?? string.Empty))
            .Where(name => name.StartsWith("RainArchives.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["RainArchives.Client", "RainArchives.Contracts"],
            rainArchiveReferences);
    }

    [Fact]
    public void PhysicalExtractHereUsesOnlyTheBoundedWorkerRoute()
    {
        string root = FindRepositoryRoot();
        string panePath = Path.Combine(root, "Main", "Controls", "PaneView.xaml.cs");
        string paneSource = File.ReadAllText(panePath);
        int methodStart = paneSource.IndexOf(
            "private async void Extract_Click",
            StringComparison.Ordinal);
        int methodEnd = paneSource.IndexOf(
            "private char[]? RequestPhysicalArchivePassword",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        string method = paneSource[methodStart..methodEnd];

        Assert.Contains("ExtractPhysicalArchivesAsync", method, StringComparison.Ordinal);
        Assert.Contains("ExtractPhysicalArchivesToAsync", method, StringComparison.Ordinal);
        Assert.Contains("MaximumArchiveCount", method, StringComparison.Ordinal);
        Assert.Contains("selected.Any(item => item.IsDirectory || !item.HasPhysicalPath)",
            method, StringComparison.Ordinal);
        Assert.DoesNotContain("ArchiveExts", method, StringComparison.Ordinal);
        Assert.DoesNotContain("UniquePath", method, StringComparison.Ordinal);
        Assert.DoesNotContain("UndoService", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RecycleAction", method, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", method, StringComparison.Ordinal);

        int menuStateStart = paneSource.IndexOf(
            "private void FileContextMenu_Opened",
            StringComparison.Ordinal);
        int menuStateEnd = paneSource.IndexOf(
            "private void FileContextMenu_Closed",
            menuStateStart,
            StringComparison.Ordinal);
        Assert.True(menuStateStart >= 0 && menuStateEnd > menuStateStart);
        string menuState = paneSource[menuStateStart..menuStateEnd];
        Assert.Contains("MenuExtract.IsEnabled = physicalArchiveSelection;", menuState,
            StringComparison.Ordinal);
        Assert.Contains("MenuExtractTo.IsEnabled = physicalArchiveSelection;", menuState,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ArchiveExts", menuState, StringComparison.Ordinal);
        Assert.DoesNotContain("hasArchiveExtensionHint", menuState, StringComparison.Ordinal);

        var pane = XDocument.Load(Path.Combine(root, "Main", "Controls", "PaneView.xaml"));
        XElement extract = FindMenu(pane, "MenuExtract");
        XElement extractTo = FindMenu(pane, "MenuExtractTo");
        Assert.Equal("Extract_Click", (string?)extract.Attribute("Click"));
        Assert.Contains("isolated archive worker", extract.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ExtractTo_Click", (string?)extractTo.Attribute("Click"));
        Assert.Contains("without merging or overwriting", extractTo.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CreateArchiveDialog.xaml", "ArchiveDialogSurface", "ArchiveDialogCombo")]
    [InlineData("RebuildArchiveDialog.xaml", "RebuildSurface", "RebuildCombo")]
    public void ManagedWriteDialogsUseThemeReactiveSurfacesAndHighContrastFallback(
        string fileName,
        string surfaceStyleKey,
        string comboStyleKey)
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "Main", "Views", fileName);
        string source = File.ReadAllText(path);
        var document = XDocument.Load(path);
        XElement window = document.Root!;
        XElement surfaceStyle = document.Descendants(Presentation + "Style")
            .Single(element => string.Equals(
                (string?)element.Attribute(Xaml + "Key"),
                surfaceStyleKey,
                StringComparison.Ordinal));
        XElement background = surfaceStyle.Elements(Presentation + "Setter")
            .Single(element => string.Equals(
                (string?)element.Attribute("Property"), "Background", StringComparison.Ordinal));
        XElement windowStyle = window.Element(Presentation + "Window.Style")!
            .Element(Presentation + "Style")!;
        XElement windowForeground = windowStyle.Elements(Presentation + "Setter")
            .Single(element => string.Equals(
                (string?)element.Attribute("Property"), "Foreground", StringComparison.Ordinal));
        XElement comboStyle = document.Descendants(Presentation + "Style")
            .Single(element => string.Equals(
                (string?)element.Attribute(Xaml + "Key"),
                comboStyleKey,
                StringComparison.Ordinal));
        bool isCreation = string.Equals(
            fileName,
            "CreateArchiveDialog.xaml",
            StringComparison.Ordinal);
        string comboItemStyleKey = isCreation ? "ArchiveDialogComboItem" : "RebuildComboItem";
        string buttonStyleKey = isCreation ? "ArchiveDialogButton" : "RebuildButton";
        string primaryButtonStyleKey = isCreation
            ? "ArchiveDialogPrimaryButton"
            : "RebuildPrimaryButton";
        string validationStyleKey = isCreation
            ? "ArchiveDialogValidationText"
            : "RebuildValidationText";
        string buttonTemplateKey = isCreation
            ? "ArchiveDialogHighContrastButtonTemplate"
            : "RebuildHighContrastButtonTemplate";
        XElement comboItemStyle = FindKeyedResource(
            document,
            Presentation + "Style",
            comboItemStyleKey);
        XElement buttonStyle = FindKeyedResource(
            document,
            Presentation + "Style",
            buttonStyleKey);
        XElement primaryButtonStyle = FindKeyedResource(
            document,
            Presentation + "Style",
            primaryButtonStyleKey);
        XElement validationStyle = FindKeyedResource(
            document,
            Presentation + "Style",
            validationStyleKey);
        XElement buttonTemplate = FindKeyedResource(
            document,
            Presentation + "ControlTemplate",
            buttonTemplateKey);

        Assert.Equal("{DynamicResource Text}", (string?)windowForeground.Attribute("Value"));
        Assert.Equal("{DynamicResource MenuBg}", (string?)background.Attribute("Value"));
        Assert.Contains("SystemColors.WindowBrushKey", comboStyle.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("SystemColors.WindowTextBrushKey", comboStyle.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("SystemColors.WindowBrushKey", comboItemStyle.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("SystemColors.HighlightBrushKey", comboItemStyle.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("SystemColors.HighlightTextBrushKey", comboItemStyle.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("SystemColors.WindowBrushKey", buttonStyle.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("SystemColors.HighlightBrushKey", primaryButtonStyle.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("SystemColors.WindowTextBrushKey", validationStyle.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("SystemColors.HighlightTextBrushKey", buttonTemplate.ToString(),
            StringComparison.Ordinal);
        XElement keyboardFocus = buttonTemplate.Descendants(Presentation + "Trigger")
            .Single(element => string.Equals(
                    (string?)element.Attribute("Property"),
                    "IsKeyboardFocused",
                    StringComparison.Ordinal)
                && string.Equals(
                    (string?)element.Attribute("Value"),
                    "True",
                    StringComparison.Ordinal));
        XElement focusBorder = keyboardFocus.Elements(Presentation + "Setter")
            .Single(element => string.Equals(
                (string?)element.Attribute("Property"),
                "BorderThickness",
                StringComparison.Ordinal));
        Assert.Equal("ButtonBorder", (string?)focusBorder.Attribute("TargetName"));
        Assert.Equal("2", (string?)focusBorder.Attribute("Value"));
        Assert.DoesNotContain(keyboardFocus.Elements(Presentation + "Setter"), element =>
            string.Equals(
                (string?)element.Attribute("Property"),
                "BorderBrush",
                StringComparison.Ordinal));
        XElement[] comboBoxes = [.. document.Descendants(Presentation + "ComboBox")];
        if (isCreation)
        {
            const string formatComboItemStyleKey = "ArchiveDialogFormatComboItem";
            XElement formatComboItemStyle = FindKeyedResource(
                document,
                Presentation + "Style",
                formatComboItemStyleKey);
            Assert.Equal(
                $"{{StaticResource {comboItemStyleKey}}}",
                (string?)formatComboItemStyle.Attribute("BasedOn"));
            Assert.Contains(formatComboItemStyle.Elements(Presentation + "Setter"), element =>
                string.Equals(
                    (string?)element.Attribute("Property"),
                    "IsEnabled",
                    StringComparison.Ordinal)
                && string.Equals(
                    (string?)element.Attribute("Value"),
                    "{Binding IsCompatible}",
                    StringComparison.Ordinal));
            Assert.Contains(formatComboItemStyle.Elements(Presentation + "Setter"), element =>
                string.Equals(
                    (string?)element.Attribute("Property"),
                    "ToolTip",
                    StringComparison.Ordinal)
                && string.Equals(
                    (string?)element.Attribute("Value"),
                    "{Binding Availability}",
                    StringComparison.Ordinal));

            XElement formatInput = comboBoxes.Single(combo => string.Equals(
                (string?)combo.Attribute(Xaml + "Name"),
                "FormatInput",
                StringComparison.Ordinal));
            Assert.Equal(
                $"{{StaticResource {formatComboItemStyleKey}}}",
                (string?)formatInput.Attribute("ItemContainerStyle"));
            Assert.All(comboBoxes.Where(combo => !ReferenceEquals(combo, formatInput)), combo =>
                Assert.Equal(
                    $"{{StaticResource {comboItemStyleKey}}}",
                    (string?)combo.Attribute("ItemContainerStyle")));
        }
        else
        {
            Assert.All(comboBoxes, combo =>
                Assert.Equal(
                    $"{{StaticResource {comboItemStyleKey}}}",
                    (string?)combo.Attribute("ItemContainerStyle")));
        }
        Assert.All(document.Descendants(Presentation + "Button"), button =>
        {
            string? style = (string?)button.Attribute("Style");
            Assert.True(
                string.Equals(style, $"{{StaticResource {buttonStyleKey}}}", StringComparison.Ordinal)
                || string.Equals(
                    style,
                    $"{{StaticResource {primaryButtonStyleKey}}}",
                    StringComparison.Ordinal),
                $"Managed-write button retained a non-high-contrast style: {style}");
        });
        XElement validation = document.Descendants(Presentation + "TextBlock")
            .Single(element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "ValidationText",
                StringComparison.Ordinal));
        Assert.Equal($"{{StaticResource {validationStyleKey}}}",
            (string?)validation.Attribute("Style"));
        Assert.Null(validation.Attribute("Foreground"));
        Assert.Contains("SystemParameters.HighContrast", source, StringComparison.Ordinal);
        Assert.Contains("SystemColors.WindowBrushKey", source, StringComparison.Ordinal);
        Assert.Contains("SystemColors.WindowTextBrushKey", source, StringComparison.Ordinal);
        Assert.DoesNotContain("#F216161F", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreationSourceReviewInheritsCurrentThemeInsteadOfSystemListColors()
    {
        string root = FindRepositoryRoot();
        var document = XDocument.Load(
            Path.Combine(root, "Main", "Views", "CreateArchiveDialog.xaml"));
        XElement list = document.Descendants(Presentation + "ListView")
            .Single(element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "SourcesList",
                StringComparison.Ordinal));
        XElement grid = list.Descendants(Presentation + "GridView").Single();
        XElement listStyle = FindKeyedResource(
            document,
            Presentation + "Style",
            "ArchiveDialogSourceList");
        XElement itemStyle = FindKeyedResource(
            document,
            Presentation + "Style",
            "ArchiveDialogSourceItem");
        XElement headerStyle = FindKeyedResource(
            document,
            Presentation + "Style",
            "ArchiveDialogColumnHeader");

        Assert.Equal("{StaticResource ArchiveDialogSourceList}",
            (string?)list.Attribute("Style"));
        Assert.Equal("{StaticResource ArchiveDialogSourceItem}",
            (string?)list.Attribute("ItemContainerStyle"));
        Assert.Equal("{StaticResource ArchiveDialogColumnHeader}",
            (string?)grid.Attribute("ColumnHeaderContainerStyle"));
        Assert.Contains("SystemColors.WindowBrushKey", listStyle.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("SystemColors.WindowTextBrushKey", itemStyle.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("SystemColors.WindowTextBrushKey", headerStyle.ToString(),
            StringComparison.Ordinal);
    }

    private static XElement FindKeyedResource(
        XDocument document,
        XName elementName,
        string key) =>
        document.Descendants(elementName)
            .Single(element => string.Equals(
                (string?)element.Attribute(Xaml + "Key"),
                key,
                StringComparison.Ordinal));

    private static XElement FindMenu(XDocument document, string name) =>
        document.Descendants(Presentation + "MenuItem")
            .Single(element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                name,
                StringComparison.Ordinal));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Main", "Controls", "PaneView.xaml")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Rain Explorer repository root was not found.");
    }
}
