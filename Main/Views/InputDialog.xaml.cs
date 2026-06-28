using System.Windows;
using System.Windows.Input;

namespace RainExplorer.Views;

public partial class InputDialog : Window
{
    public string Value => Input.Text;

    public InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        TitleText.Text = title;
        PromptText.Text = prompt;
        Input.Text = initial;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
