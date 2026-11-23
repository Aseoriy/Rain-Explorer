using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// "Rename several files": find &amp; replace (optionally a regular expression), change case, add numbers
/// and change the extension, with a live preview. Returns the plan to run, or null when cancelled.
/// </summary>
public sealed class BatchRenameDialog : DialogWindow
{
    private readonly IReadOnlyList<(string Path, bool IsDirectory)> _items;
    private readonly TextBox _find = Field("Find"), _replace = Field("Replace with"), _separator = Field("");
    private readonly TextBox _extension = Field("e.g. jpg");
    private readonly CheckBox _matchCase = new() { Content = "Match case" };
    private readonly CheckBox _regex = new() { Content = "Regular expression" };
    private readonly CheckBox _includeExt = new() { Content = "Include the extension" };
    private readonly CheckBox _changeExt = new() { Content = "Change the extension to" };
    private readonly ComboBox _case = new() { ItemsSource = new[] { "Keep as is", "lowercase", "UPPERCASE", "Title Case" }, SelectedIndex = 0, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _numbering = new() { ItemsSource = new[] { "No numbers", "Before the name", "After the name" }, SelectedIndex = 0, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly NumericUpDown _start = new() { Value = 1, Minimum = 0, Maximum = 999999, Increment = 1, FormatString = "0", Width = 110 };
    private readonly NumericUpDown _digits = new() { Value = 1, Minimum = 1, Maximum = 9, Increment = 1, FormatString = "0", Width = 110 };
    private readonly StackPanel _preview = new();
    private readonly TextBlock _summary;
    private readonly Button _renameButton;
    private List<RenamePlanItem> _plan = [];
    private bool _queued;

    private BatchRenameDialog(IReadOnlyList<(string Path, bool IsDirectory)> items)
        : base(items.Count == 1 ? "Rename" : $"Rename {items.Count} items", "pencil", width: 860)
    {
        _items = items;
        _separator.Text = " ";
        _separator.Width = 70;

        var options = new StackPanel { Width = 330, Spacing = 4 };
        options.Children.Add(Section("FIND & REPLACE"));
        options.Children.Add(_find);
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
        options.Children.Add(_extension);

        var previewBox = new Border
        {
            Child = new ScrollViewer { Content = _preview, MaxHeight = 470, Padding = new Thickness(10, 8) },
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), MinHeight = 200,
        };
        previewBox.Bind(Border.BackgroundProperty, previewBox.GetResourceObservable("Glass1"));
        previewBox.Bind(Border.BorderBrushProperty, previewBox.GetResourceObservable("Line1"));
        var previewColumn = new DockPanel();
        var previewLabel = Section("PREVIEW");
        DockPanel.SetDock(previewLabel, Dock.Top);
        previewColumn.Children.Add(previewLabel);
        previewColumn.Children.Add(previewBox);

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,20,*"), Margin = new Thickness(0, 6, 0, 0) };
        layout.Children.Add(options);
        Grid.SetColumn(previewColumn, 2);
        layout.Children.Add(previewColumn);

        _summary = Message("", "TextMuted", top: 12);
        var body = new StackPanel();
        body.Children.Add(layout);
        body.Children.Add(_summary);
        Body = body;

        AddButton("Cancel", () => Finish(null));
        _renameButton = AddButton("Rename", () =>
        {
            if (_plan.Any(p => p.Changes) && _plan.All(p => p.Problem is null)) Finish(_plan);
        }, DialogButtonKind.Primary, isDefault: true);

        foreach (var box in new[] { _find, _replace, _separator, _extension }) box.TextChanged += (_, _) => Queue();
        foreach (var check in new[] { _matchCase, _regex, _includeExt, _changeExt }) check.IsCheckedChanged += (_, _) => Queue();
        _case.SelectionChanged += (_, _) => Queue();
        _numbering.SelectionChanged += (_, _) => Queue();
        _start.ValueChanged += (_, _) => Queue();
        _digits.ValueChanged += (_, _) => Queue();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => _find.Focus(), DispatcherPriority.Input);
        Refresh();
    }

    public static Task<List<RenamePlanItem>?> Ask(Window? owner, IReadOnlyList<(string Path, bool IsDirectory)> items) =>
        new BatchRenameDialog(items).ShowFor<List<RenamePlanItem>>(owner);

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
        StartAt = (int)(_start.Value ?? 1),
        Digits = (int)(_digits.Value ?? 1),
        Separator = _separator.Text ?? "",
        Extension = _changeExt.IsChecked == true ? _extension.Text ?? "" : null,
    };

    private void Queue()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; Refresh(); }, DispatcherPriority.Background);
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
        _summary.Text = problems > 0
            ? $"⚠️ {problems} name{(problems == 1 ? " has" : "s have")} a problem (marked in red). Fix {(problems == 1 ? "it" : "them")} to continue."
            : changing == 0 ? "Nothing changes yet. Pick an option on the left."
            : $"{changing} of {_plan.Count} item{(_plan.Count == 1 ? "" : "s")} will be renamed. You can undo this with Ctrl+Z.";
        _renameButton.IsEnabled = changing > 0 && problems == 0;
        _renameButton.Content = changing > 0 ? $"Rename {changing}" : "Rename";
    }

    private static Control Row(RenamePlanItem item)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), Margin = new Thickness(0, 3) };
        var oldName = new TextBlock { Text = item.OldName, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        oldName.Bind(TextBlock.ForegroundProperty, oldName.GetResourceObservable("TextMuted"));
        var arrow = new TextBlock { Text = "→", Margin = new Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center };
        arrow.Bind(TextBlock.ForegroundProperty, arrow.GetResourceObservable("TextFaint"));
        var newName = new TextBlock
        {
            Text = item.NewName, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = item.Changes ? FontWeight.SemiBold : FontWeight.Normal,
        };
        newName.Bind(TextBlock.ForegroundProperty, newName.GetResourceObservable(
            item.Problem is not null ? "Danger" : item.Changes ? "Text" : "TextFaint"));
        if (item.Problem is not null) ToolTip.SetTip(grid, item.Problem);
        grid.Children.Add(oldName);
        Grid.SetColumn(arrow, 1);
        grid.Children.Add(arrow);
        Grid.SetColumn(newName, 2);
        grid.Children.Add(newName);
        if (item.Problem is null) return grid;
        var stack = new StackPanel();
        stack.Children.Add(grid);
        var why = new TextBlock { Text = item.Problem, FontSize = 11, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap };
        why.Bind(TextBlock.ForegroundProperty, why.GetResourceObservable("Danger"));
        stack.Children.Add(why);
        return stack;
    }

    private static TextBox Field(string watermark)
    {
        var box = new TextBox { Height = 32, Watermark = watermark };
        box.Classes.Add("field");
        return box;
    }

    private static TextBlock Section(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 12, 0, 4) };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable("TextFaint"));
        return block;
    }

    private static Control Labeled(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), Margin = new Thickness(0, 2) };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("TextDim"));
        grid.Children.Add(text);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }
}
