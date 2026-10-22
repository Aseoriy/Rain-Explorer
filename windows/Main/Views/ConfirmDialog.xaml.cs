using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RainExplorer.Views;

/// <summary>A small themed yes/no confirmation dialog.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message,
        string okText = "OK", string cancelText = "Cancel", bool danger = true)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        CancelButton.Content = cancelText;
        if (!danger)
        {
            Glyph.Data = (Geometry)FindResource("Ic.info");
            Glyph.Stroke = (Brush)FindResource("AccentBright");
        }
    }

    /// <summary>Show modally and return true if the user confirmed.</summary>
    public static bool Ask(Window? owner, string title, string message,
        string okText = "OK", string cancelText = "Cancel", bool danger = true)
    {
        var dlg = new ConfirmDialog(title, message, okText, cancelText, danger) { Owner = owner };
        return dlg.ShowDialog() == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
