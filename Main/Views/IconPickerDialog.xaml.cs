using System.Windows;
using System.Windows.Input;
using RainExplorer.Models;

namespace RainExplorer.Views;

/// <summary>Lets the user pick a Lucide icon key for a pinned Quick Access item.</summary>
public partial class IconPickerDialog : Window
{
    public string? SelectedKey => IconList.SelectedItem as string;

    public IconPickerDialog(string? current = null)
    {
        InitializeComponent();
        IconList.ItemsSource = IconCatalog.Keys;
        IconList.SelectedItem = current is not null && IconCatalog.Keys.Contains(current)
            ? current : IconCatalog.Keys[0];
        Loaded += (_, _) => IconList.ScrollIntoView(IconList.SelectedItem);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedKey is null) { DialogResult = false; return; }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void IconList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedKey is not null) DialogResult = true;
    }

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
