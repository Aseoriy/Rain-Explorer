using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace RainExplorer.Services;

/// <summary>
/// Live tooltip and menu text for shortcuts, so hints follow Settings → Shortcuts.
/// Tooltips: <c>ToolTip="{Binding [tab.close], Source={x:Static svc:ShortcutHints.Instance}}"</c> → "Close tab (Ctrl+W)".
/// Menu items: <c>svc:ShortcutHints.Action="file.copy"</c> keeps InputGestureText on the current key.
/// </summary>
public sealed class ShortcutHints : INotifyPropertyChanged
{
    public static ShortcutHints Instance { get; } = new();
    private static readonly ConditionalWeakTable<MenuItem, string> MenuActions = new();
    private static readonly List<WeakReference<MenuItem>> Tracked = [];

    private ShortcutHints()
    {
        ShortcutService.Changed += () =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(System.Windows.Data.Binding.IndexerName));
            RefreshTracked();
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
            return key is null ? action.Name : $"{action.Name} ({ShortcutService.Describe(key.Value)})";
        }
    }

    public static readonly DependencyProperty ActionProperty = DependencyProperty.RegisterAttached(
        "Action", typeof(string), typeof(ShortcutHints), new PropertyMetadata(null, OnActionChanged));

    public static string? GetAction(DependencyObject d) => (string?)d.GetValue(ActionProperty);
    public static void SetAction(DependencyObject d, string? value) => d.SetValue(ActionProperty, value);

    private static void OnActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MenuItem item && e.NewValue is string id) Bind(item, id);
    }

    /// <summary>Show an action's current key on a menu item, now and whenever shortcuts change.</summary>
    public static MenuItem Bind(MenuItem item, string actionId)
    {
        _ = Instance;
        if (!MenuActions.TryGetValue(item, out _))
        {
            lock (Tracked) Tracked.Add(new WeakReference<MenuItem>(item));
        }
        MenuActions.AddOrUpdate(item, actionId);
        item.InputGestureText = ShortcutService.Hint(actionId);
        return item;
    }

    private static void RefreshTracked()
    {
        lock (Tracked)
        {
            Tracked.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var weak in Tracked)
            {
                if (weak.TryGetTarget(out var item) && MenuActions.TryGetValue(item, out string? id))
                    item.InputGestureText = ShortcutService.Hint(id);
            }
        }
    }
}
