using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Helpers;

/// <summary>
/// "Smooth scrolling" setting: mouse-wheel input glides to its target over a few frames instead of
/// jumping at once. Every wheel delta is handled, scaled to its size: fast spins (wheel acceleration,
/// free-spin wheels, hi-res wheels) mix whole notches with partial ones, and letting the partial ones
/// fall back to WPF's instant scroll made the view jump under a running glide and cut it off (a visible
/// hitch). Ctrl+wheel and lists that scroll by item rather than by pixel are left to WPF. One class
/// handler covers every ScrollViewer. Port of the Linux SmoothScroll.
/// </summary>
public static class SmoothScroll
{
    private const double TimeConstant = 0.075;   // seconds; ~95% of the way in 225 ms
    private const int Notch = Mouse.MouseWheelDeltaForOneLine;   // 120
    private static bool _installed;

    private sealed class State
    {
        public Point Target;
        public Point LastSet;
        public bool Running;
        public TimeSpan? LastFrame;
        public EventHandler? Tick;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    /// <summary>Where the setting is read from; tests swap in their own settings object.</summary>
    internal static Func<AppSettings> SettingsSource = () => SettingsStore.Instance.Settings;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnWheel));
    }

    /// <summary>Pixels one wheel notch scrolls for the chosen speed.</summary>
    internal static double StepFor(ScrollSpeed speed) => speed switch
    {
        ScrollSpeed.Slow => 40,
        ScrollSpeed.Fast => 100,
        _ => 60,
    };

    /// <summary>How many notches a wheel delta is (120 = one notch; partial and multiple notches scale).</summary>
    internal static double Notches(int delta) => delta / (double)Notch;

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        AppSettings settings = SettingsSource();
        if (e.Handled || !settings.SmoothScrolling || e.Delta == 0) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return;
        bool horizontal = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        double notches = Notches(e.Delta);

        // The preview tunnels from the outermost ScrollViewer; scroll the innermost one that can move.
        ScrollViewer? viewer = FindTarget(e.OriginalSource as DependencyObject, notches, horizontal);
        if (viewer is null) return;
        e.Handled = true;

        var state = States.GetOrCreateValue(viewer);
        var current = new Point(viewer.HorizontalOffset, viewer.VerticalOffset);
        if (!state.Running) state.Target = current;
        else if (Distance(current, state.LastSet) > 1) Rebase(state, current);
        double step = StepFor(settings.ScrollSpeed) * notches;
        state.Target = horizontal
            ? new Point(state.Target.X - step, state.Target.Y)
            : new Point(state.Target.X, state.Target.Y - step);
        state.Target = Clamp(state.Target, viewer);
        if (state.Running) return;

        state.Running = true;
        state.LastFrame = null;
        state.LastSet = current;
        state.Tick = (_, args) => Frame(viewer, state, ((RenderingEventArgs)args).RenderingTime);
        CompositionTarget.Rendering += state.Tick;
    }

    private static void Frame(ScrollViewer viewer, State state, TimeSpan time)
    {
        if (state.LastFrame == time) return;   // Rendering can fire more than once per frame
        var current = new Point(viewer.HorizontalOffset, viewer.VerticalOffset);
        if (PresentationSource.FromVisual(viewer) is null)
        {
            Stop(state);
            return;
        }
        if (Distance(current, state.LastSet) > 1)
        {
            // The user grabbed the scrollbar (or something jumped the view a whole page or more): stop.
            // Smaller shifts (a virtualized list correcting its estimated offset, an arrow key) just move
            // the glide along with them instead of cutting it off, which looked like a hitch.
            if (Mouse.LeftButton == MouseButtonState.Pressed
                || Distance(current, state.LastSet) >= Math.Max(viewer.ViewportHeight, 200))
            {
                Stop(state);
                return;
            }
            Rebase(state, current);
        }
        double dt = state.LastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0.001, 0.05) : 1 / 60.0;
        state.LastFrame = time;
        Point target = Clamp(state.Target, viewer);
        double k = 1 - Math.Exp(-dt / TimeConstant);
        var next = new Point(current.X + (target.X - current.X) * k, current.Y + (target.Y - current.Y) * k);
        if (Distance(next, target) < 0.5) next = target;
        if (next.X != current.X) viewer.ScrollToHorizontalOffset(next.X);
        if (next.Y != current.Y) viewer.ScrollToVerticalOffset(next.Y);
        state.LastSet = next;
        if (next == target) Stop(state);
    }

    /// <summary>The view moved by itself: keep the remaining glide distance, measured from where it is now.</summary>
    private static void Rebase(State state, Point current)
    {
        state.Target = new Point(state.Target.X + current.X - state.LastSet.X,
                                 state.Target.Y + current.Y - state.LastSet.Y);
        state.LastSet = current;
    }

    private static void Stop(State state)
    {
        state.Running = false;
        if (state.Tick is not null) CompositionTarget.Rendering -= state.Tick;
        state.Tick = null;
    }

    private static ScrollViewer? FindTarget(DependencyObject? source, double notches, bool horizontal)
    {
        for (DependencyObject? d = source; d is not null; d = Parent(d))
        {
            if (d is not ScrollViewer viewer || !ScrollsByPixel(viewer)) continue;
            bool can = horizontal
                ? (notches > 0 ? viewer.HorizontalOffset > 0.5 : viewer.HorizontalOffset < viewer.ScrollableWidth - 0.5)
                : (notches > 0 ? viewer.VerticalOffset > 0.5 : viewer.VerticalOffset < viewer.ScrollableHeight - 0.5);
            if (can) return viewer;
            // An item-scrolling list in between keeps WPF's own wheel handling.
        }
        return null;
    }

    /// <summary>
    /// Offsets are pixels unless the content scrolls logically: a StackPanel, or a VirtualizingStackPanel
    /// whose ScrollUnit is Item, report offsets in items, which a pixel glide can't drive.
    /// </summary>
    internal static bool ScrollsByPixel(ScrollViewer viewer)
    {
        if (!viewer.CanContentScroll) return true;
        // CanContentScroll only matters when the content (or a list's items panel) does its own scrolling.
        object? host = viewer.Content is ItemsPresenter ? FindPanel(viewer) : viewer.Content;
        if (host is null) return false;            // list not laid out yet: leave it to WPF
        if (host is not IScrollInfo) return true;  // e.g. a WrapPanel grid: the ScrollViewer scrolls pixels
        return host is VirtualizingPanel panel
            && VirtualizingPanel.GetScrollUnit(ItemsControl.GetItemsOwner(panel) ?? (DependencyObject)panel)
                == ScrollUnit.Pixel;
    }

    private static Panel? FindPanel(ScrollViewer viewer)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(viewer);
        while (queue.Count > 0)
        {
            DependencyObject d = queue.Dequeue();
            if (d is Panel { IsItemsHost: true } panel) return panel;
            int count = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
        }
        return null;
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);

    private static Point Clamp(Point p, ScrollViewer viewer) =>
        new(Math.Clamp(p.X, 0, Math.Max(0, viewer.ScrollableWidth)),
            Math.Clamp(p.Y, 0, Math.Max(0, viewer.ScrollableHeight)));

    private static double Distance(Point a, Point b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
