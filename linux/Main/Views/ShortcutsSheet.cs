using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// The keyboard shortcuts sheet (F1 or ?): every action with its current keys, grouped, with a search box.
/// "Customize…" opens Settings → Shortcuts.
/// </summary>
public sealed class ShortcutsSheet : DialogWindow
{
    private readonly StackPanel _list = new();
    private readonly TextBox _search;
    private readonly TextBlock _empty;

    /// <summary>Set by the caller: opens Settings on the Shortcuts page.</summary>
    public Action? CustomizeRequested { get; init; }

    private ShortcutsSheet() : base("Keyboard shortcuts", "hash", width: 560)
    {
        _search = new TextBox { Height = 32, Watermark = "Search shortcuts…", Margin = new Thickness(0, 12, 0, 6) };
        _search.Classes.Add("field");
        _search.TextChanged += (_, _) => Fill();
        _empty = Message("", "TextMuted", top: 10);

        var panel = new StackPanel();
        panel.Children.Add(_search);
        panel.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 460, Padding = new Thickness(0, 0, 10, 0) });
        panel.Children.Add(_empty);
        Body = panel;

        AddButton("Customize…", () =>
        {
            Finish(null);
            CustomizeRequested?.Invoke();
        }, DialogButtonKind.Ghost);
        AddButton("Close", () => Finish(null), DialogButtonKind.Primary, isDefault: true);
        ShortcutService.Changed += Fill;
        Closed += (_, _) => ShortcutService.Changed -= Fill;
        Opened += (_, _) => Dispatcher.UIThread.Post(() => _search.Focus(), DispatcherPriority.Input);
        Fill();
    }

    public static Task ShowFor(Window owner, Action customize) =>
        new ShortcutsSheet { CustomizeRequested = customize }.ShowFor<object>(owner);

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
                Text = group.Key.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, shown == 0 ? 6 : 16, 0, 6),
            };
            header.Bind(TextBlock.ForegroundProperty, header.GetResourceObservable("TextFaint"));
            _list.Children.Add(header);
            foreach (var action in rows)
            {
                _list.Children.Add(Row(action));
                shown++;
            }
        }
        _empty.Text = shown == 0 ? $"No shortcuts match “{query}”." : "";
        _empty.IsVisible = shown == 0;
    }

    private static Control Row(ShortcutAction action)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 3) };
        var name = new TextBlock { Text = action.Name, VerticalAlignment = VerticalAlignment.Center };
        name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable("Text"));
        grid.Children.Add(name);
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var gestures = ShortcutService.GesturesFor(action.Id);
        if (gestures.Count == 0)
        {
            var none = new TextBlock { Text = "None", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            none.Bind(TextBlock.ForegroundProperty, none.GetResourceObservable("TextFaint"));
            keys.Children.Add(none);
        }
        foreach (var gesture in gestures) keys.Children.Add(KeyChip(ShortcutService.Describe(gesture)));
        Grid.SetColumn(keys, 1);
        grid.Children.Add(keys);
        return grid;
    }

    /// <summary>A small rounded "key cap" for a shortcut.</summary>
    internal static Border KeyChip(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeight.SemiBold };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("TextDim"));
        var chip = new Border
        {
            Child = label, Padding = new Thickness(8, 3), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
        };
        chip.Bind(Border.BackgroundProperty, chip.GetResourceObservable("Glass2"));
        chip.Bind(Border.BorderBrushProperty, chip.GetResourceObservable("Line2"));
        return chip;
    }
}
