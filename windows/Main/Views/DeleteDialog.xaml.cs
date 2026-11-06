using System.Windows;
using System.Windows.Input;

namespace RainExplorer.Views;

/// <summary>The user's choice in the delete prompt.</summary>
public enum DeleteChoice { Cancel, Recycle, Permanent }

/// <summary>
/// A themed delete prompt offering both "Recycle Bin" and "Delete permanently".
/// Shown when the delete behavior is set to prompt each time.
/// </summary>
public partial class DeleteDialog : Window
{
    private DeleteChoice _choice = DeleteChoice.Cancel;

    public DeleteDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    /// <summary>Show modally; returns the user's choice (Cancel if dismissed).</summary>
    public static DeleteChoice Ask(Window? owner, string message)
    {
        var dlg = new DeleteDialog(message) { Owner = owner };
        dlg.ShowDialog();
        return dlg._choice;
    }

    private void Recycle_Click(object sender, RoutedEventArgs e) { _choice = DeleteChoice.Recycle; DialogResult = true; }
    private void Permanent_Click(object sender, RoutedEventArgs e) { _choice = DeleteChoice.Permanent; DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _choice = DeleteChoice.Cancel; DialogResult = false; }

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
