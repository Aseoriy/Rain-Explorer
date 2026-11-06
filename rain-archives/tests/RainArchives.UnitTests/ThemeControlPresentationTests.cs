using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RainArchives.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class ThemeControlPresentationTests
{
    [TestMethod]
    public void OwnedControlTemplatesInstantiateRequiredPartsAndSemanticStatesOnSta()
    {
        RunOnStaThread(() =>
        {
            var panel = new StackPanel();
            var window = CreateThemedWindow(panel);
            var textBox = new TextBox { Text = "Text" };
            var passwordBox = new PasswordBox { Password = "secret" };
            var comboBox = new ComboBox
            {
                IsEditable = true,
                ItemsSource = new[] { "Balanced", "Fast", "Small" },
                SelectedIndex = 0,
            };
            var checkBox = new CheckBox
            {
                Content = "Three-state control",
                IsChecked = false,
                IsThreeState = true,
            };
            var firstTab = new TabItem { Header = "Properties", Content = "Property content" };
            var secondTab = new TabItem { Header = "Safe preview", Content = "Preview content" };
            var disabledTab = new TabItem { Header = "Unavailable", Content = "Disabled content", IsEnabled = false };
            var tabControl = new TabControl
            {
                Height = 100,
                Items = { firstTab, secondTab, disabledTab },
                SelectedIndex = 0,
            };
            var progressBar = new ProgressBar
            {
                Height = 8,
                IsIndeterminate = false,
                Maximum = 100,
                Value = 40,
            };
            var expander = new Expander
            {
                Header = "Advanced metadata",
                Content = "Expanded content",
                IsExpanded = true,
            };
            var dataGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                Height = 100,
                Width = 220,
                ItemsSource = Enumerable.Range(0, 20)
                    .Select(index => new GridRow($"row-{index}", index % 2 == 0))
                    .ToArray(),
                SelectedIndex = 0,
            };
            dataGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Name",
                Binding = new Binding(nameof(GridRow.Name)),
                Width = 180,
            });
            var checkBoxColumn = new DataGridCheckBoxColumn
            {
                Header = "Included",
                Binding = new Binding(nameof(GridRow.Included)) { Mode = BindingMode.TwoWay },
                ElementStyle = Assert.IsInstanceOfType<Style>(window.FindResource("DataGridCheckBoxElementStyle")),
                EditingElementStyle = Assert.IsInstanceOfType<Style>(
                    window.FindResource("DataGridCheckBoxEditingElementStyle")),
                Width = 100,
            };
            dataGrid.Columns.Add(checkBoxColumn);
            var textColumn = new DataGridTextColumn
            {
                Header = "Metadata",
                Binding = new Binding(nameof(GridRow.EditableText)) { Mode = BindingMode.TwoWay },
                EditingElementStyle = Assert.IsInstanceOfType<Style>(
                    window.FindResource("DataGridTextEditingElementStyle")),
                Width = 140,
            };
            dataGrid.Columns.Add(textColumn);
            var verticalScrollBar = new ScrollBar
            {
                Height = 100,
                Maximum = 100,
                ViewportSize = 20,
                Value = 30,
            };
            var horizontalScrollBar = new ScrollBar
            {
                Width = 180,
                Maximum = 100,
                Orientation = Orientation.Horizontal,
                ViewportSize = 20,
                Value = 30,
            };
            var statusBar = new StatusBar
            {
                Items = { "Ready" },
            };
            var label = new Label { Content = "_Name", Target = textBox };
            var menuItem = new MenuItem
            {
                Header = "_System",
                IsCheckable = true,
                IsChecked = true,
            };
            var separator = new Separator();
            var disabledMenuItem = new MenuItem { Header = "_Unavailable", IsEnabled = false };
            var contextMenu = new ContextMenu
            {
                Items = { menuItem, separator, disabledMenuItem },
            };
            AddThemeResources(contextMenu.Resources);
            var menuButton = new Button
            {
                Content = "Theme",
                ContextMenu = contextMenu,
            };
            var toolTip = new ToolTip
            {
                Content = "Semantic tooltip",
                PlacementTarget = menuButton,
            };
            ToolTipService.SetToolTip(menuButton, toolTip);

            foreach (var child in new UIElement[]
                     {
                         label,
                         textBox,
                         passwordBox,
                         comboBox,
                         checkBox,
                         tabControl,
                         progressBar,
                         expander,
                         dataGrid,
                         verticalScrollBar,
                         horizontalScrollBar,
                         statusBar,
                         menuButton,
                     })
            {
                panel.Children.Add(child);
            }

            try
            {
                window.Show();
                window.UpdateLayout();
                comboBox.IsDropDownOpen = true;
                toolTip.IsOpen = true;
                PumpDispatcher();

                AssertOwnedTemplate(textBox, window, typeof(TextBox));
                AssertOwnedTemplate(passwordBox, window, typeof(PasswordBox));
                AssertOwnedTemplate(comboBox, window, typeof(ComboBox));
                AssertOwnedTemplate(checkBox, window, typeof(CheckBox));
                AssertOwnedTemplate(tabControl, window, typeof(TabControl));
                AssertOwnedTemplate(firstTab, window, typeof(TabItem));
                AssertOwnedTemplate(secondTab, window, typeof(TabItem));
                AssertOwnedTemplate(disabledTab, window, typeof(TabItem));
                AssertOwnedTemplate(progressBar, window, typeof(ProgressBar));
                AssertOwnedTemplate(expander, window, typeof(Expander));
                AssertOwnedTemplate(verticalScrollBar, window, typeof(ScrollBar));
                AssertOwnedTemplate(horizontalScrollBar, window, typeof(ScrollBar));
                AssertOwnedTemplate(statusBar, window, typeof(StatusBar));
                AssertOwnedTemplate(label, window, typeof(Label));
                AssertOwnedTemplate(toolTip, window, typeof(ToolTip));
                AssertOwnedTemplate(menuButton, window, typeof(Button));

                AssertTemplatePart<ScrollViewer>(textBox, "PART_ContentHost");
                AssertTemplatePart<ScrollViewer>(passwordBox, "PART_ContentHost");
                var popup = AssertTemplatePart<Popup>(comboBox, "PART_Popup");
                Assert.IsTrue(popup.IsOpen);
                var comboToggle = AssertTemplatePart<ToggleButton>(comboBox, "ToggleButton");
                var comboToggleBorder = AssertTemplatePart<Border>(comboToggle, "ToggleBorder");
                Assert.AreEqual(
                    ResourceColor(window, "AccentWashBrush"),
                    BrushColor(comboToggleBorder.Background, "open ComboBox toggle surface"));
                Assert.AreEqual(
                    ResourceColor(window, "AccentBrush"),
                    BrushColor(comboToggleBorder.BorderBrush, "open ComboBox toggle border"));
                var editableTextBox = AssertTemplatePart<TextBox>(comboBox, "PART_EditableTextBox");
                Assert.AreEqual(
                    ResourceColor(window, "AccentBrush"),
                    BrushColor(editableTextBox.SelectionBrush, "editable ComboBox selection"));
                Assert.AreEqual(
                    ResourceColor(window, "AccentForegroundBrush"),
                    BrushColor(editableTextBox.SelectionTextBrush, "editable ComboBox selection text"));
                AssertTemplatePart<Track>(verticalScrollBar, "PART_Track");
                AssertTemplatePart<Track>(horizontalScrollBar, "PART_Track");
                AssertTemplatePart<ContentPresenter>(tabControl, "PART_SelectedContentHost");
                AssertTemplatePart<FrameworkElement>(progressBar, "PART_Track");
                var determinateIndicator = AssertTemplatePart<Border>(progressBar, "PART_Indicator");
                var indeterminateIndicator = AssertTemplatePart<FrameworkElement>(
                    progressBar,
                    "IndeterminateIndicator");

                var checkGlyph = AssertTemplatePart<FrameworkElement>(checkBox, "CheckGlyph");
                var indeterminateGlyph = AssertTemplatePart<FrameworkElement>(checkBox, "IndeterminateGlyph");
                var checkBorder = AssertTemplatePart<Border>(checkBox, "CheckBorder");
                Assert.AreEqual(Visibility.Collapsed, checkGlyph.Visibility);
                Assert.AreEqual(Visibility.Collapsed, indeterminateGlyph.Visibility);

                checkBox.IsChecked = true;
                PumpDispatcher();
                Assert.AreEqual(Visibility.Visible, checkGlyph.Visibility);
                Assert.AreEqual(Visibility.Collapsed, indeterminateGlyph.Visibility);

                checkBox.IsChecked = null;
                PumpDispatcher();
                Assert.AreEqual(Visibility.Collapsed, checkGlyph.Visibility);
                Assert.AreEqual(Visibility.Visible, indeterminateGlyph.Visibility);

                checkBox.IsChecked = false;
                checkBox.IsEnabled = false;
                PumpDispatcher();
                Assert.AreEqual(
                    ResourceColor(window, "DisabledTextBrush"),
                    BrushColor(checkBox.Foreground, "disabled CheckBox foreground"));
                Assert.AreEqual(
                    ResourceColor(window, "SurfaceMutedBrush"),
                    BrushColor(checkBorder.Background, "disabled CheckBox surface"));
                checkBox.IsEnabled = true;

                Assert.AreEqual(Visibility.Visible, determinateIndicator.Visibility);
                Assert.AreEqual(Visibility.Collapsed, indeterminateIndicator.Visibility);
                progressBar.IsIndeterminate = true;
                PumpDispatcher();
                Assert.AreEqual(Visibility.Collapsed, determinateIndicator.Visibility);
                Assert.AreEqual(Visibility.Visible, indeterminateIndicator.Visibility);
                progressBar.IsEnabled = false;
                PumpDispatcher();
                Assert.AreEqual(
                    ResourceColor(window, "DisabledTextBrush"),
                    BrushColor(determinateIndicator.Background, "disabled determinate ProgressBar segment"));
                progressBar.IsEnabled = true;

                Assert.AreEqual(
                    ResourceColor(window, "SurfaceBrush"),
                    BrushColor(
                        AssertTemplatePart<Border>(firstTab, "TabBorder").Background,
                        "selected TabItem surface"));
                Assert.AreEqual(
                    ResourceColor(window, "SurfaceMutedBrush"),
                    BrushColor(secondTab.Background, "unselected TabItem surface"));
                Assert.AreEqual(
                    ResourceColor(window, "DisabledTextBrush"),
                    BrushColor(disabledTab.Foreground, "disabled TabItem foreground"));
                Assert.AreEqual(
                    Visibility.Visible,
                    AssertTemplatePart<FrameworkElement>(expander, "ExpandSite").Visibility);

                var comboItem = Assert.IsInstanceOfType<ComboBoxItem>(
                    comboBox.ItemContainerGenerator.ContainerFromIndex(0));
                AssertOwnedTemplate(comboItem, window, "SemanticComboBoxItemStyle");

                comboBox.IsDropDownOpen = false;
                contextMenu.PlacementTarget = menuButton;
                contextMenu.IsOpen = true;
                PumpDispatcher();
                AssertOwnedTemplate(contextMenu, contextMenu, typeof(ContextMenu));
                AssertOwnedTemplate(menuItem, contextMenu, typeof(MenuItem));
                AssertOwnedTemplate(disabledMenuItem, contextMenu, typeof(MenuItem));
                AssertOwnedTemplate(separator, contextMenu, MenuItem.SeparatorStyleKey);
                Assert.AreEqual(
                    Visibility.Visible,
                    AssertTemplatePart<FrameworkElement>(menuItem, "CheckMark").Visibility);
                AssertTemplatePart<Border>(menuItem, "ItemBorder");
                Assert.AreEqual(
                    ResourceColor(window, "DisabledTextBrush"),
                    BrushColor(disabledMenuItem.Foreground, "disabled MenuItem foreground"));
                contextMenu.IsOpen = false;
                PumpDispatcher();

                var buttonBorder = AssertTemplatePart<Border>(menuButton, "ButtonBorder");
                menuButton.IsEnabled = false;
                PumpDispatcher();
                Assert.AreEqual(
                    ResourceColor(window, "SurfaceMutedBrush"),
                    BrushColor(buttonBorder.Background, "disabled Button surface"));
                Assert.AreEqual(
                    ResourceColor(window, "BorderBrush"),
                    BrushColor(buttonBorder.BorderBrush, "disabled Button border"));
                Assert.AreEqual(
                    ResourceColor(window, "DisabledTextBrush"),
                    BrushColor(menuButton.Foreground, "disabled Button foreground"));

                var generatedHeader = FindVisualDescendant<DataGridColumnHeader>(dataGrid);
                Assert.IsNotNull(generatedHeader);
                AssertOwnedTemplate(generatedHeader, window, "SemanticDataGridColumnHeaderStyle");
                var generatedRow = Assert.IsInstanceOfType<DataGridRow>(
                    dataGrid.ItemContainerGenerator.ContainerFromIndex(0));
                AssertOwnedTemplate(generatedRow, window, "SemanticDataGridRowStyle");
                Assert.AreEqual(
                    ResourceColor(window, "SelectionBrush"),
                    BrushColor(generatedRow.Background, "selected DataGridRow surface"));

                dataGrid.UnselectAll();
                dataGrid.SelectionUnit = DataGridSelectionUnit.Cell;
                dataGrid.SelectedCells.Add(new DataGridCellInfo(dataGrid.Items[0], dataGrid.Columns[0]));
                dataGrid.UpdateLayout();
                PumpDispatcher();
                var generatedCell = GetDataGridCell(dataGrid, rowIndex: 0, columnIndex: 0);
                AssertOwnedTemplate(generatedCell, window, "SemanticDataGridCellStyle");
                Assert.IsTrue(generatedCell.IsSelected, "The selected row did not expose selected cell state.");
                Assert.AreEqual(
                    ResourceColor(window, "SelectionBrush"),
                    BrushColor(
                        AssertTemplatePart<Border>(generatedCell, "CellBorder").Background,
                        "selected DataGridCell surface"));

                var checkBoxCell = GetDataGridCell(dataGrid, rowIndex: 0, columnIndex: 1);
                var generatedCheckBox = Assert.IsInstanceOfType<CheckBox>(
                    FindVisualDescendant<CheckBox>(checkBoxCell));
                AssertOwnedTemplate(generatedCheckBox, window, "DataGridCheckBoxElementStyle");
                Assert.IsNotNull(
                    generatedCheckBox.Template.FindName("CheckGlyph", generatedCheckBox),
                    "DataGridCheckBoxColumn display mode bypassed the owned CheckBox template.");
                Assert.IsFalse(generatedCheckBox.IsHitTestVisible);
                Assert.IsFalse(generatedCheckBox.Focusable);

                dataGrid.CurrentCell = new DataGridCellInfo(dataGrid.Items[0], checkBoxColumn);
                checkBoxCell.Focus();
                Assert.IsTrue(dataGrid.BeginEdit(), "The DataGrid checkbox cell did not enter editing mode.");
                dataGrid.UpdateLayout();
                PumpDispatcher();
                var editingCheckBox = Assert.IsInstanceOfType<CheckBox>(
                    FindVisualDescendant<CheckBox>(checkBoxCell));
                AssertOwnedTemplate(editingCheckBox, window, "DataGridCheckBoxEditingElementStyle");
                Assert.IsNotNull(
                    editingCheckBox.Template.FindName("CheckGlyph", editingCheckBox),
                    "DataGridCheckBoxColumn editing mode bypassed the owned CheckBox template.");
                Assert.IsTrue(editingCheckBox.IsHitTestVisible);
                Assert.IsTrue(editingCheckBox.Focusable);
                dataGrid.CancelEdit();

                dataGrid.CurrentCell = new DataGridCellInfo(dataGrid.Items[0], textColumn);
                var textCell = GetDataGridCell(dataGrid, rowIndex: 0, columnIndex: 2);
                textCell.Focus();
                Assert.IsTrue(dataGrid.BeginEdit(), "The DataGrid text cell did not enter editing mode.");
                dataGrid.UpdateLayout();
                PumpDispatcher();
                var editingTextBox = Assert.IsInstanceOfType<TextBox>(
                    FindVisualDescendant<TextBox>(textCell));
                AssertOwnedTemplate(editingTextBox, window, "DataGridTextEditingElementStyle");
                AssertTemplatePart<ScrollViewer>(editingTextBox, "PART_ContentHost");
                dataGrid.CancelEdit();

                var internalScrollBars = FindVisualDescendants<ScrollBar>(dataGrid).ToArray();
                Assert.IsTrue(internalScrollBars.Length >= 2, "The constrained DataGrid did not create both scroll paths.");
                Assert.IsTrue(internalScrollBars.Any(scrollBar => scrollBar.Orientation == Orientation.Vertical));
                Assert.IsTrue(internalScrollBars.Any(scrollBar => scrollBar.Orientation == Orientation.Horizontal));
                foreach (var internalScrollBar in internalScrollBars)
                {
                    AssertOwnedTemplate(internalScrollBar, window, "SemanticScrollBarStyle");
                }

                var generatedStatusItem = Assert.IsInstanceOfType<StatusBarItem>(
                    statusBar.ItemContainerGenerator.ContainerFromIndex(0));
                AssertOwnedTemplate(generatedStatusItem, window, "SemanticStatusBarItemStyle");
                Assert.IsTrue(dataGrid.EnableRowVirtualization);
                Assert.AreEqual(
                    VirtualizationMode.Recycling,
                    VirtualizingPanel.GetVirtualizationMode(dataGrid));
            }
            finally
            {
                contextMenu.IsOpen = false;
                comboBox.IsDropDownOpen = false;
                toolTip.IsOpen = false;
                window.Close();
            }
        });
    }

    [TestMethod]
    public void OpenContextMenuAndComboBoxPopupFollowPaletteReplacementOnSta()
    {
        RunOnStaThread(() =>
        {
            var panel = new StackPanel();
            var window = CreateThemedWindow(panel);
            var checkedItem = new MenuItem
            {
                Header = "System",
                IsCheckable = true,
                IsChecked = true,
            };
            var disabledItem = new MenuItem
            {
                Header = "Unavailable",
                IsEnabled = false,
            };
            var separator = new Separator();
            var contextMenu = new ContextMenu
            {
                Items = { checkedItem, separator, disabledItem },
            };
            AddThemeResources(contextMenu.Resources);
            var button = new Button { Content = "Theme", ContextMenu = contextMenu };
            var comboBox = new ComboBox
            {
                ItemsSource = new[] { "Light", "Dark", "System" },
                SelectedIndex = 0,
            };
            panel.Children.Add(button);
            panel.Children.Add(comboBox);

            try
            {
                window.Show();
                window.UpdateLayout();

                contextMenu.PlacementTarget = button;
                contextMenu.IsOpen = true;
                PumpDispatcher();
                contextMenu.ApplyTemplate();
                var contextBorder = AssertTemplatePart<Border>(contextMenu, "ContextMenuBorder");
                AssertOwnedTemplate(contextMenu, contextMenu, typeof(ContextMenu));
                AssertOwnedTemplate(checkedItem, contextMenu, typeof(MenuItem));
                AssertOwnedTemplate(disabledItem, contextMenu, typeof(MenuItem));
                AssertOwnedTemplate(separator, contextMenu, MenuItem.SeparatorStyleKey);
                var lightMenuColor = BrushColor(contextBorder.Background, "light ContextMenu");
                AssertContextMenuPalette(window, contextBorder, checkedItem, separator, disabledItem, "light");

                ReplacePalette(window, "Dark");
                ReplacePalette(contextMenu, "Dark");
                var darkMenuColor = BrushColor(contextBorder.Background, "dark ContextMenu");
                AssertContextMenuPalette(window, contextBorder, checkedItem, separator, disabledItem, "dark");
                Assert.AreNotEqual(lightMenuColor, darkMenuColor);

                ReplacePalette(window, "HighContrast");
                ReplacePalette(contextMenu, "HighContrast");
                AssertContextMenuPalette(
                    window,
                    contextBorder,
                    checkedItem,
                    separator,
                    disabledItem,
                    "High Contrast");
                contextMenu.IsOpen = false;

                ReplacePalette(window, "Light");
                comboBox.IsDropDownOpen = true;
                PumpDispatcher();
                comboBox.ApplyTemplate();
                var popup = AssertTemplatePart<Popup>(comboBox, "PART_Popup");
                Assert.IsTrue(popup.IsOpen);
                var dropDownBorder = AssertTemplatePart<Border>(comboBox, "DropDownBorder");
                var comboItem = Assert.IsInstanceOfType<ComboBoxItem>(
                    comboBox.ItemContainerGenerator.ContainerFromIndex(0));
                AssertOwnedTemplate(comboItem, window, "SemanticComboBoxItemStyle");
                var lightPopupColor = BrushColor(dropDownBorder.Background, "light ComboBox popup");
                AssertComboBoxPopupPalette(window, dropDownBorder, comboItem, "light");

                ReplacePalette(window, "Dark");
                var darkPopupColor = BrushColor(dropDownBorder.Background, "dark ComboBox popup");
                AssertComboBoxPopupPalette(window, dropDownBorder, comboItem, "dark");
                Assert.AreNotEqual(lightPopupColor, darkPopupColor);

                ReplacePalette(window, "HighContrast");
                AssertComboBoxPopupPalette(window, dropDownBorder, comboItem, "High Contrast");
                Assert.IsTrue(comboBox.IsDropDownOpen, "Palette replacement closed the active ComboBox popup.");
            }
            finally
            {
                contextMenu.IsOpen = false;
                comboBox.IsDropDownOpen = false;
                window.Close();
            }
        });
    }

    private static Window CreateThemedWindow(Panel panel)
    {
        var window = new Window
        {
            Width = 720,
            Height = 640,
            ShowInTaskbar = false,
            Content = new ScrollViewer { Content = panel },
        };
        AddThemeResources(window.Resources);
        return window;
    }

    private static void AddThemeResources(ResourceDictionary resources)
    {
        resources.MergedDictionaries.Add(LoadDictionary("Light"));
        resources.MergedDictionaries.Add(LoadDesignResources());
    }

    private static void AssertContextMenuPalette(
        FrameworkElement resourceRoot,
        Border contextBorder,
        MenuItem checkedItem,
        Separator separator,
        MenuItem disabledItem,
        string paletteLabel)
    {
        Assert.AreEqual(
            ResourceColor(resourceRoot, "SurfaceRaisedBrush"),
            BrushColor(contextBorder.Background, $"{paletteLabel} ContextMenu"));
        Assert.AreEqual(
            ResourceColor(resourceRoot, "AccentBrush"),
            BrushColor(
                AssertTemplatePart<System.Windows.Shapes.Path>(checkedItem, "CheckMark").Stroke,
                $"{paletteLabel} checked MenuItem glyph"));
        Assert.AreEqual(
            ResourceColor(resourceRoot, "DisabledTextBrush"),
            BrushColor(disabledItem.Foreground, $"{paletteLabel} disabled MenuItem"));
        var separatorRule = Assert.IsInstanceOfType<Border>(FindVisualDescendant<Border>(separator));
        Assert.AreEqual(
            ResourceColor(resourceRoot, "BorderBrush"),
            BrushColor(separatorRule.Background, $"{paletteLabel} Separator"));
    }

    private static void AssertComboBoxPopupPalette(
        FrameworkElement resourceRoot,
        Border dropDownBorder,
        ComboBoxItem selectedItem,
        string paletteLabel)
    {
        Assert.AreEqual(
            ResourceColor(resourceRoot, "SurfaceRaisedBrush"),
            BrushColor(dropDownBorder.Background, $"{paletteLabel} ComboBox popup"));
        Assert.AreEqual(
            ResourceColor(resourceRoot, "SelectionBrush"),
            BrushColor(
                AssertTemplatePart<Border>(selectedItem, "ItemBorder").Background,
                $"{paletteLabel} selected ComboBox item"));
        Assert.AreEqual(
            ResourceColor(resourceRoot, "SelectionForegroundBrush"),
            BrushColor(selectedItem.Foreground, $"{paletteLabel} selected ComboBox item foreground"));
    }

    private static void ReplacePalette(FrameworkElement root, string palette)
    {
        root.Resources.MergedDictionaries[0] = LoadDictionary(palette);
        root.UpdateLayout();
        PumpDispatcher();
    }

    private static void AssertOwnedTemplate(
        Control control,
        FrameworkElement resourceRoot,
        object styleKey)
    {
        control.ApplyTemplate();
        Assert.IsNotNull(control.Template, $"{control.GetType().Name} has no control template.");
        var expectedStyle = Assert.IsInstanceOfType<Style>(resourceRoot.FindResource(styleKey));
        Assert.IsTrue(
            StyleChainContains(control.Style, expectedStyle),
            $"{control.GetType().Name} did not resolve or derive from the shared style.");
    }

    private static bool StyleChainContains(Style? actual, Style expected)
    {
        for (var current = actual; current is not null; current = current.BasedOn)
        {
            if (ReferenceEquals(current, expected))
            {
                return true;
            }
        }

        return false;
    }

    private static T AssertTemplatePart<T>(Control control, string name)
        where T : DependencyObject
    {
        control.ApplyTemplate();
        return Assert.IsInstanceOfType<T>(control.Template.FindName(name, control), name);
    }

    private static Color ResourceColor(FrameworkElement root, string key) =>
        BrushColor(Assert.IsInstanceOfType<Brush>(root.FindResource(key)), key);

    private static Color BrushColor(Brush brush, string label) =>
        Assert.IsInstanceOfType<SolidColorBrush>(brush, $"{label} is not a solid semantic brush.").Color;

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

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate)
            {
                yield return candidate;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static DataGridCell GetDataGridCell(DataGrid dataGrid, int rowIndex, int columnIndex)
    {
        var row = Assert.IsInstanceOfType<DataGridRow>(
            dataGrid.ItemContainerGenerator.ContainerFromIndex(rowIndex));
        var presenter = Assert.IsInstanceOfType<DataGridCellsPresenter>(
            FindVisualDescendant<DataGridCellsPresenter>(row));
        return Assert.IsInstanceOfType<DataGridCell>(
            presenter.ItemContainerGenerator.ContainerFromIndex(columnIndex));
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, static () => { });

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
            Name = "RainArchives.ThemeControlPresentationTests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "The themed-control STA thread did not terminate.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class GridRow(string name, bool included)
    {
        public string Name { get; } = name;

        public bool Included { get; set; } = included;

        public string EditableText { get; set; } = "metadata";
    }
}
