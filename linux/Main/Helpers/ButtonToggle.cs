using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace RainExplorer.Helpers;

/// <summary>
/// Lets the button that opened a menu or flyout close it again. Clicking outside a light-dismiss popup
/// closes it on the press, and the same press then reaches the button underneath, whose click would open
/// it straight back up. Each popup notes when it closed; a click on its own button right after that is the
/// "close" click and is ignored. Clicking elsewhere, Escape and the popup's own buttons work as before.
/// </summary>
public static class ButtonToggle
{
    private const long Window = 350;   // ms between the dismissing press and the button's click
    private static readonly ConditionalWeakTable<Control, StrongBox<long>> ClosedAt = new();

    public static void NoteClosed(Control anchor) =>
        ClosedAt.AddOrUpdate(anchor, new StrongBox<long>(Environment.TickCount64));

    /// <summary>True when the popup belonging to <paramref name="anchor"/> closed a moment ago.</summary>
    public static bool JustClosed(Control anchor) =>
        ClosedAt.TryGetValue(anchor, out var at) && Environment.TickCount64 - at.Value < Window;

    /// <summary>Remember when this menu closes, so a click on <paramref name="anchor"/> doesn't reopen it.</summary>
    public static void Track(ContextMenu menu, Control anchor) => menu.Closed += (_, _) => NoteClosed(anchor);

    public static void Track(Popup popup, Func<Control?> anchor) => popup.Closed += (_, _) =>
    {
        if (anchor() is { } control) NoteClosed(control);
    };
}
