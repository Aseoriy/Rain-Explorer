using Avalonia.Controls;
using Avalonia.Input;

namespace RainExplorer.Controls;

/// <summary>
/// Builds themed menu items (Lucide icon + label + optional shortcut hint) the way the
/// Windows build declares them in XAML: 15px icons, muted by default, accent for primary
/// actions and the danger red for destructive ones.
/// </summary>
public static class MenuFactory
{
    public static MenuItem Item(string header, string? iconKey, Action onClick, string? gesture = null,
        bool enabled = true, string iconBrush = "TextMuted")
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = enabled,
            Icon = iconKey is null ? null : MakeIcon(iconKey, iconBrush),
        };
        if (gesture is not null)
        {
            try { item.InputGesture = KeyGesture.Parse(gesture); }
            catch (ArgumentException) { }
        }
        item.Click += (_, _) => onClick();
        return item;
    }

    public static MenuItem Submenu(string header, string? iconKey, IEnumerable<Control> children, string iconBrush = "TextMuted") =>
        new()
        {
            Header = header,
            Icon = iconKey is null ? null : MakeIcon(iconKey, iconBrush),
            ItemsSource = children.ToList(),
        };

    public static MenuItem Check(string header, bool isChecked, Action onClick)
    {
        var item = new MenuItem
        {
            Header = header,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = isChecked,
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static Icon MakeIcon(string key, string brushKey)
    {
        var icon = new Icon { Kind = key, Width = 15, Height = 15 };
        icon.Bind(Icon.ForegroundProperty, icon.GetResourceObservable(brushKey));
        return icon;
    }
}
