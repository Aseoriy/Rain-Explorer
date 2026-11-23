using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using System.Runtime.CompilerServices;

namespace RainExplorer.Services;

/// <summary>
/// Live tooltip and menu text for shortcuts, so hints follow Settings → Shortcuts. Bind a tooltip with
/// <c>{ReflectionBinding [tab.close], Source={x:Static svc:ShortcutHints.Instance}}</c> → "Close tab (Ctrl+W)".
/// </summary>
public sealed class ShortcutHints : INotifyPropertyChanged
{
    public static ShortcutHints Instance { get; } = new();
    private static readonly ConditionalWeakTable<MenuItem, string> MenuActions = new();

    private ShortcutHints()
    {
        ShortcutService.Changed += () =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>"Close tab (Ctrl+W)", or just the name when the action has no key.</summary>
    public string this[string id]
    {
        get
        {
            var action = ShortcutService.Find(id);
            if (action is null) return id;
            var key = ShortcutService.PrimaryGesture(id);
            return key is null ? action.Name : $"{action.Name} ({ShortcutService.Describe(key)})";
        }
    }

    /// <summary>Show an action's current key on a menu item, now and whenever shortcuts change.</summary>
    public static MenuItem Bind(MenuItem item, string actionId)
    {
        MenuActions.AddOrUpdate(item, actionId);
        item.InputGesture = ShortcutService.PrimaryGesture(actionId);
        return item;
    }

    /// <summary>Refresh the key shown on bound menu items (call before a long-lived menu opens).</summary>
    public static void Refresh(IEnumerable<object?> items)
    {
        foreach (var item in items.OfType<MenuItem>())
            if (MenuActions.TryGetValue(item, out string? id)) item.InputGesture = ShortcutService.PrimaryGesture(id);
    }

    internal static KeyGesture? GestureFor(string id) => ShortcutService.PrimaryGesture(id);
}
