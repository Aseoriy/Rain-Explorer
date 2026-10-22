using System.IO;
using System.Windows;
using System.Windows.Input;

using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>A single themed choice for all file conflicts in one transfer.</summary>
public partial class ConflictDialog : Window
{
    private ConflictDialog(string title, string message, string detail, bool moving)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        DetailText.Text = detail;
        ReplaceAllButton.Content = moving ? "Replace and move all" : "Replace all";
    }

    public TransferConflictChoice Choice { get; private set; } = TransferConflictChoice.Cancel;

    public static TransferConflictChoice Ask(Window? owner, bool moving,
        IReadOnlyList<TransferConflict> conflicts)
    {
        if (conflicts.Count == 0) return TransferConflictChoice.SkipAll;

        int count = conflicts.Count;
        string itemLabel = count == 1 ? "one item" : $"{count} items";
        string operation = moving ? "move" : "copy";
        string destination = Path.GetDirectoryName(conflicts[0].Destination) ?? "the destination";
        string sample = string.Join(", ", conflicts.Take(3).Select(c =>
            $"\u201c{Path.GetFileName(c.Source.TrimEnd(Path.DirectorySeparatorChar))}\u201d"));
        if (count > 3) sample += ", …";

        var dialog = new ConflictDialog(
            $"Replace items while you {operation}",
            $"The destination already contains {itemLabel}.",
            $"Choose once for this transfer to {destination}. Conflicts include {sample}.",
            moving)
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };

        return dialog.ShowDialog() == true ? dialog.Choice : TransferConflictChoice.Cancel;
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        Choice = TransferConflictChoice.ReplaceAll;
        DialogResult = true;
    }

    private void SkipAll_Click(object sender, RoutedEventArgs e)
    {
        Choice = TransferConflictChoice.SkipAll;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = TransferConflictChoice.Cancel;
        DialogResult = false;
    }

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
