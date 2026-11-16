using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// "View details" for one activity (detailed activity history): a plain-language account of what happened,
/// what to try, timestamps, and the system's exact message. Port of the Linux ActivityDetailsDialog.
/// </summary>
public partial class ActivityDetailsDialog : Window
{
    private readonly ActivityEntry _entry;

    public ActivityDetailsDialog(ActivityEntry entry)
    {
        InitializeComponent();
        _entry = entry;
        var explanation = ActivityExplainer.Explain(entry);

        TitleText.Text = entry.Title;
        StatusText.Text = $"{entry.StatusText}  ·  {entry.StartedAt:MMM d, h:mm tt}";
        StatusDot.Fill = (Brush)FindResource(entry.IsFailed ? "Danger" : entry.IsSuccess ? "Ok" : "TextMuted");

        SummaryText.Text = explanation.Summary;
        AdviceText.Text = explanation.Advice ?? string.Empty;
        AdvicePanel.Visibility = explanation.Advice is null ? Visibility.Collapsed : Visibility.Visible;

        AddFact("Started", entry.StartedAt.ToString("dddd, MMMM d, yyyy  h:mm:ss tt"));
        if (entry.FinishedAt is { } finished) AddFact("Finished", finished.ToString("dddd, MMMM d, yyyy  h:mm:ss tt"));
        if (!string.IsNullOrWhiteSpace(entry.DurationText)) AddFact("Took", entry.DurationText);
        string items = entry.BeginDetail ?? (entry.IsFailed ? "" : entry.Detail);
        if (!string.IsNullOrWhiteSpace(items)) AddFact(ActivityExplainer.IsFolderOpen(entry) ? "Folder" : "Items", items);

        string message = entry.FullDetail ?? (entry.IsFailed ? entry.Detail : "");
        MessagePanel.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Text = message;
        ShortenedNote.Visibility = entry.FullDetail is null && entry.IsFailed ? Visibility.Visible : Visibility.Collapsed;
    }

    public static void ShowFor(Window? owner, ActivityEntry entry)
    {
        var dialog = new ActivityDetailsDialog(entry);
        if (owner is not null) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
    }

    private void AddFact(string label, string value)
    {
        int row = Facts.RowDefinitions.Count;
        Facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, Margin = new Thickness(0, 3, 0, 3) };
        l.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        var v = new TextBox { Text = value, Margin = new Thickness(0, 3, 0, 3), Style = (Style)FindResource("Selectable") };
        Grid.SetRow(l, row);
        Grid.SetRow(v, row);
        Grid.SetColumn(v, 1);
        Facts.Children.Add(l);
        Facts.Children.Add(v);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ActivityExplainer.Describe(_entry));
            CopiedText.Visibility = Visibility.Visible;
        }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy; try again */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
