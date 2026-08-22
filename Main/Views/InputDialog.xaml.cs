using System.Windows;
using System.Windows.Input;

namespace RainExplorer.Views;

public partial class InputDialog : Window
{
    public string Value => Input.Text;

    public InputDialog(string title, string prompt, string initial, int? initialSelectionLength = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        PromptText.Text = prompt;
        Input.Text = initial;
        Loaded += (_, _) =>
        {
            Input.Focus();
            if (initialSelectionLength is int length)
                Input.Select(0, Math.Clamp(length, 0, Input.Text.Length));
            else
                Input.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
