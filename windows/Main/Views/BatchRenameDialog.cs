using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// "Rename several files": find &amp; replace (optionally a regular expression), change case, add numbers
/// and change the extension, with a live preview. Returns the plan to run, or null when cancelled.
/// Port of the Linux BatchRenameDialog.
/// </summary>
public sealed class BatchRenameDialog : RainDialog
{
    private readonly IReadOnlyList<(string Path, bool IsDirectory)> _items;
    private readonly TextBox _find = Field("Find"), _replace = Field("Replace with"), _separator = Field("");
    private readonly TextBox _extension = Field("e.g. jpg");
    private readonly TextBox _start = NumberField("1"), _digits = NumberField("1");
    private readonly CheckBox _matchCase = Check("Match case");
    private readonly CheckBox _regex = Check("Regular expression");
    private readonly CheckBox _includeExt = Check("Include the extension");
    private readonly CheckBox _changeExt = Check("Change the extension to");
    private readonly ComboBox _case = Combo("Keep as is", "lowercase", "UPPERCASE", "Title Case");
    private readonly ComboBox _numbering = Combo("No numbers", "Before the name", "After the name");
    private readonly StackPanel _preview = new();
    private readonly TextBlock _summary;
    private readonly Button _renameButton;
    private List<RenamePlanItem> _plan = [];
    private bool _queued;

    private BatchRenameDialog(IReadOnlyList<(string Path, bool IsDirectory)> items)
        : base(items.Count == 1 ? "Rename" : $"Rename {items.Count} items", "pencil", width: 880)
    {
        _items = items;
        _separator.Text = " ";
        _separator.Width = 70;
        _separator.HorizontalAlignment = HorizontalAlignment.Left;

        var options = new StackPanel { Width = 330 };
        options.Children.Add(Section("FIND & REPLACE", top: 4));
        options.Children.Add(_find);
        _replace.Margin = new Thickness(0, 6, 0, 0);
        options.Children.Add(_replace);
        options.Children.Add(_matchCase);
        options.Children.Add(_regex);
        options.Children.Add(_includeExt);
        options.Children.Add(Section("CASE"));
        options.Children.Add(_case);
        options.Children.Add(Section("NUMBERS"));
        options.Children.Add(_numbering);
        options.Children.Add(Labeled("Start at", _start));
        options.Children.Add(Labeled("Digits", _digits));
        options.Children.Add(Labeled("Separator", _separator));
        options.Children.Add(Section("EXTENSION"));
        options.Children.Add(_changeExt);
        _extension.Margin = new Thickness(0, 6, 0, 0);
        options.Children.Add(_extension);

        var previewBox = new Border
        {
            Child = new ScrollViewer
            {
                Content = _preview, MaxHeight = 470, Padding = new Thickness(10, 8, 10, 8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), MinHeight = 200,
        };
        previewBox.SetResourceReference(Border.BackgroundProperty, "Glass1");
        previewBox.SetResourceReference(Border.BorderBrushProperty, "Line1");
        var previewColumn = new DockPanel();
        var previewLabel = Section("PREVIEW", top: 4);
        DockPanel.SetDock(previewLabel, Dock.Top);
        previewColumn.Children.Add(previewLabel);
        previewColumn.Children.Add(previewBox);

        var layout = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(options);
        Grid.SetColumn(previewColumn, 2);
        layout.Children.Add(previewColumn);

        _summary = Message("", "TextMuted", top: 12);
        var body = new StackPanel();
        body.Children.Add(layout);
        body.Children.Add(_summary);
        Body = body;

        AddButton("Cancel", () => Finish(false), isCancel: true);
        _renameButton = AddButton("Rename", () =>
        {
            if (_plan.Any(p => p.Changes) && _plan.All(p => p.Problem is null)) Finish(true);
        }, RainDialogButton.Primary, isDefault: true);

        foreach (var box in new[] { _find, _replace, _separator, _extension, _start, _digits })
            box.TextChanged += (_, _) => Queue();
        foreach (var check in new[] { _matchCase, _regex, _includeExt, _changeExt })
        {
            check.Checked += (_, _) => Queue();
            check.Unchecked += (_, _) => Queue();
        }
        _case.SelectionChanged += (_, _) => Queue();
        _numbering.SelectionChanged += (_, _) => Queue();
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => _find.Focus(), DispatcherPriority.Input);
        Refresh();
    }

    /// <summary>Show the dialog; the plan to run, or null when cancelled.</summary>
    public static List<RenamePlanItem>? Ask(Window? owner, IReadOnlyList<(string Path, bool IsDirectory)> items)
    {
        var dialog = new BatchRenameDialog(items);
        return dialog.ShowOver(owner) ? dialog._plan : null;
    }

    /// <summary>The options as currently set (also used by tests).</summary>
    internal BatchRenameOptions Options => new()
    {
        Find = _find.Text ?? "",
        Replace = _replace.Text ?? "",
        MatchCase = _matchCase.IsChecked == true,
        UseRegex = _regex.IsChecked == true,
        IncludeExtension = _includeExt.IsChecked == true,
        Case = (NameCase)Math.Max(0, _case.SelectedIndex),
        Numbering = (Numbering)Math.Max(0, _numbering.SelectedIndex),
        StartAt = Number(_start, 1, 0, 999999),
        Digits = Number(_digits, 1, 1, 9),
        Separator = _separator.Text ?? "",
        Extension = _changeExt.IsChecked == true ? _extension.Text ?? "" : null,
    };

    private static int Number(TextBox box, int fallback, int min, int max) =>
        int.TryParse(box.Text, NumberStyles.None, CultureInfo.CurrentCulture, out int value)
            ? Math.Clamp(value, min, max) : fallback;

    private void Queue()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(() => { _queued = false; Refresh(); }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        bool numbers = _numbering.SelectedIndex > 0;
        _start.IsEnabled = _digits.IsEnabled = _separator.IsEnabled = numbers;
        _extension.IsEnabled = _changeExt.IsChecked == true;

        _plan = BatchRenameService.Plan(_items, Options);
        _preview.Children.Clear();
        foreach (var item in _plan.Take(500)) _preview.Children.Add(Row(item));
        if (_plan.Count > 500) _preview.Children.Add(Message($"…and {_plan.Count - 500} more", "TextFaint", top: 6));

        int changing = _plan.Count(p => p.Changes);
        int problems = _plan.Count(p => p.Problem is not null);
        string undoKeys = ShortcutService.Hint("edit.undo");
        _summary.Text = problems > 0
            ? $"⚠️ {problems} name{(problems == 1 ? " has" : "s have")} a problem (marked in red). Fix {(problems == 1 ? "it" : "them")} to continue."
            : changing == 0 ? "Nothing changes yet. Pick an option on the left."
            : $"{changing} of {_plan.Count} item{(_plan.Count == 1 ? "" : "s")} will be renamed."
              + (undoKeys.Length > 0 ? $" You can undo this with {undoKeys}." : " You can undo this.");
        _renameButton.IsEnabled = changing > 0 && problems == 0;
        _renameButton.Content = changing > 0 ? $"Rename {changing}" : "Rename";
    }

    private static FrameworkElement Row(RenamePlanItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var oldName = new TextBlock { Text = item.OldName, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        oldName.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        var arrow = new TextBlock { Text = "→", Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
        var newName = new TextBlock
        {
            Text = item.NewName, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = item.Changes ? FontWeights.SemiBold : FontWeights.Normal,
        };
        newName.SetResourceReference(TextBlock.ForegroundProperty,
            item.Problem is not null ? "Danger" : item.Changes ? "Text" : "TextFaint");
        if (item.Problem is not null) grid.ToolTip = item.Problem;
        grid.Children.Add(oldName);
        Grid.SetColumn(arrow, 1);
        grid.Children.Add(arrow);
        Grid.SetColumn(newName, 2);
        grid.Children.Add(newName);
        if (item.Problem is null) return grid;
        var stack = new StackPanel();
        stack.Children.Add(grid);
        var why = new TextBlock { Text = item.Problem, FontSize = 11, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap };
        why.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        stack.Children.Add(why);
        return stack;
    }

    private static TextBox Field(string placeholder)
    {
        var box = new TextBox { Height = 32, Tag = placeholder };
        box.SetResourceReference(StyleProperty, "FieldBox");
        if (placeholder.Length > 0) System.Windows.Automation.AutomationProperties.SetName(box, placeholder);
        return box;
    }

    private static TextBox NumberField(string value)
    {
        var box = Field("");
        box.Text = value;
        box.Width = 110;
        box.HorizontalAlignment = HorizontalAlignment.Left;
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (e.DataObject.GetData(typeof(string)) is not string text || !text.All(char.IsAsciiDigit)) e.CancelCommand();
        });
        return box;
    }

    private static CheckBox Check(string text)
    {
        var check = new CheckBox { Content = text, Margin = new Thickness(0, 8, 0, 0) };
        check.SetResourceReference(ForegroundProperty, "TextDim");
        return check;
    }

    private static ComboBox Combo(params string[] choices) =>
        new() { ItemsSource = choices, SelectedIndex = 0, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };

    private static TextBlock Section(string text, double top = 14)
    {
        var block = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, top, 0, 6) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
        return block;
    }

    private static FrameworkElement Labeled(string label, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        grid.Children.Add(text);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }
}
