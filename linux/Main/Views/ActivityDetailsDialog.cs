using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// "View details" for one activity (detailed activity history): a plain-language account of what happened,
/// what to try, timestamps, and the system's exact message.
/// </summary>
public sealed class ActivityDetailsDialog : DialogWindow
{
    private readonly ActivityEntry _entry;

    private ActivityDetailsDialog(ActivityEntry entry)
        : base(entry.Title, entry.IconKey, entry.IsFailed ? "Danger" : "AccentBright", width: 520)
    {
        _entry = entry;
        var explanation = ActivityExplainer.Explain(entry);
        var panel = new StackPanel();

        panel.Children.Add(StatusLine(entry));
        panel.Children.Add(Section("WHAT HAPPENED"));
        panel.Children.Add(Message(explanation.Summary, "Text", top: 2));
        if (explanation.Advice is not null)
        {
            panel.Children.Add(Section("WHAT TO TRY"));
            panel.Children.Add(Message(explanation.Advice, "TextDim", top: 2));
        }

        panel.Children.Add(Section("DETAILS"));
        var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), Margin = new Thickness(0, 2, 0, 0) };
        AddFact(facts, "Started", entry.StartedAt.ToString("dddd, MMMM d, yyyy  h:mm:ss tt"));
        if (entry.FinishedAt is { } finished) AddFact(facts, "Finished", finished.ToString("dddd, MMMM d, yyyy  h:mm:ss tt"));
        if (!string.IsNullOrWhiteSpace(entry.DurationText)) AddFact(facts, "Took", entry.DurationText);
        string items = entry.BeginDetail ?? (entry.IsFailed ? "" : entry.Detail);
        if (!string.IsNullOrWhiteSpace(items)) AddFact(facts, ActivityExplainer.IsFolderOpen(entry) ? "Folder" : "Items", items);
        panel.Children.Add(facts);

        string message = entry.FullDetail ?? (entry.IsFailed ? entry.Detail : "");
        if (!string.IsNullOrWhiteSpace(message))
        {
            panel.Children.Add(Section("SYSTEM MESSAGE"));
            var text = new SelectableTextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5,
                FontFamily = (FontFamily?)Application.Current?.FindResource("MonoFont") ?? FontFamily.Default,
            };
            text.Bind(SelectableTextBlock.ForegroundProperty, text.GetResourceObservable("TextDim"));
            var box = new Border
            {
                Child = new ScrollViewer { Content = text, MaxHeight = 160 },
                Padding = new Thickness(10, 8),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 2, 0, 0),
            };
            box.Bind(Border.BackgroundProperty, box.GetResourceObservable("Glass2"));
            box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("Line1"));
            panel.Children.Add(box);
            if (entry.FullDetail is null && entry.IsFailed)
                panel.Children.Add(Message("This entry was saved before detailed history was on, so the message may be shortened.",
                    "TextFaint", top: 6));
        }

        Body = new ScrollViewer { Content = panel, MaxHeight = 560 };
        AddButton("Copy details", () => _ = Copy(), DialogButtonKind.Ghost);
        AddButton("Close", () => Finish(null), DialogButtonKind.Primary, isDefault: true);
    }

    public static Task ShowFor(Window? owner, ActivityEntry entry) => new ActivityDetailsDialog(entry).ShowFor<object>(owner);

    private async Task Copy()
    {
        await ClipboardService.SetTextAsync(this, ActivityExplainer.Describe(_entry));
    }

    private static Control StatusLine(ActivityEntry entry)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 10, 0, 0) };
        string brush = entry.IsFailed ? "Danger" : entry.IsSuccess ? "Ok" : "TextMuted";
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        dot.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, dot.GetResourceObservable(brush));
        var text = new TextBlock { Text = $"{entry.StatusText}  ·  {entry.StartedAt:MMM d, h:mm tt}", FontSize = 12 };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("TextMuted"));
        row.Children.Add(dot);
        row.Children.Add(text);
        return row;
    }

    private static TextBlock Section(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 16, 0, 4) };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable("TextFaint"));
        return block;
    }

    private static void AddFact(Grid grid, string label, string value)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var l = new TextBlock { Text = label, Margin = new Thickness(0, 3) };
        l.Bind(TextBlock.ForegroundProperty, l.GetResourceObservable("TextMuted"));
        var v = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3) };
        v.Bind(SelectableTextBlock.ForegroundProperty, v.GetResourceObservable("Text"));
        Grid.SetRow(l, row);
        Grid.SetRow(v, row);
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
    }
}
