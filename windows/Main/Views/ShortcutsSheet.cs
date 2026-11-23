using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// The keyboard shortcuts sheet (F1 or ?): every action with its current keys, grouped, with a search box.
/// "Customize…" opens Settings → Shortcuts. Port of the Linux ShortcutsSheet.
/// </summary>
public sealed class ShortcutsSheet : RainDialog
{
    private readonly StackPanel _list = new();
    private readonly TextBox _search;
    private readonly TextBlock _empty;
    private bool _customize;

    private ShortcutsSheet() : base("Keyboard shortcuts", "hash", width: 560)
    {
        _search = new TextBox { Height = 32, Margin = new Thickness(0, 12, 0, 6), Tag = "Search shortcuts…" };
        _search.SetResourceReference(StyleProperty, "FieldBox");
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search shortcuts");
        _search.TextChanged += (_, _) => Fill();
        _empty = Message("", "TextMuted", top: 10);

        var panel = new StackPanel();
        panel.Children.Add(_search);
        panel.Children.Add(new ScrollViewer
        {
            Content = _list, MaxHeight = 460, Padding = new Thickness(0, 0, 10, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        panel.Children.Add(_empty);
        Body = panel;

        AddButton("Customize…", () =>
        {
            _customize = true;
            Finish(true);
        });
        AddButton("Close", () => Finish(false), RainDialogButton.Primary, isDefault: true);
        ShortcutService.Changed += Fill;
        Closed += (_, _) => ShortcutService.Changed -= Fill;
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => _search.Focus(), DispatcherPriority.Input);
        Fill();
    }

    /// <summary>Show the sheet; <paramref name="customize"/> runs when "Customize…" was chosen.</summary>
    public static void ShowFor(Window owner, Action customize)
    {
        var sheet = new ShortcutsSheet();
        sheet.ShowOver(owner);
        if (sheet._customize) customize();
    }

    private void Fill()
    {
        _list.Children.Clear();
        string query = _search.Text?.Trim() ?? "";
        int shown = 0;
        foreach (var group in ShortcutService.Actions.GroupBy(a => a.Group))
        {
            var rows = group.Where(a => query.Length == 0
                || a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || ShortcutService.DescribeAction(a.Id).Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (rows.Count == 0) continue;
            var header = new TextBlock
            {
                Text = group.Key.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, shown == 0 ? 6 : 16, 0, 6),
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
            _list.Children.Add(header);
            foreach (var action in rows)
            {
                _list.Children.Add(Row(action));
                shown++;
            }
        }
        _empty.Text = shown == 0 ? $"No shortcuts match “{query}”." : "";
        _empty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static FrameworkElement Row(ShortcutAction action)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { Text = action.Name, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        grid.Children.Add(name);
        var keys = new StackPanel { Orientation = Orientation.Horizontal };
        var gestures = ShortcutService.GesturesFor(action.Id);
        if (gestures.Count == 0)
        {
            var none = new TextBlock { Text = "None", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            none.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
            keys.Children.Add(none);
        }
        foreach (var gesture in gestures)
        {
            var chip = KeyChip(ShortcutService.Describe(gesture));
            chip.Margin = new Thickness(6, 0, 0, 0);
            keys.Children.Add(chip);
        }
        Grid.SetColumn(keys, 1);
        grid.Children.Add(keys);
        return grid;
    }

    /// <summary>A small rounded "key cap" for a shortcut.</summary>
    internal static Border KeyChip(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        var chip = new Border
        {
            Child = label, Padding = new Thickness(8, 3, 8, 3), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
        };
        chip.SetResourceReference(Border.BackgroundProperty, "Glass2");
        chip.SetResourceReference(Border.BorderBrushProperty, "Line2");
        return chip;
    }
}
