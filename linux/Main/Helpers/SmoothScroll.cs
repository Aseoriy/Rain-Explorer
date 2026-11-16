using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Helpers;

/// <summary>
/// "Smooth scrolling" setting: a mouse-wheel notch glides to its target over a few frames instead of
/// jumping 50 px at once. Touchpad and precision-wheel input (fractional deltas) is already smooth and is
/// left to Avalonia, as are Ctrl+wheel and Performance mode. One class handler covers every ScrollViewer.
/// </summary>
public static class SmoothScroll
{
    private const double TimeConstant = 0.075;   // seconds; ~95% of the way in 225 ms
    private static bool _installed;

    private sealed class State
    {
        public Vector Target;
        public Vector LastSet;
        public bool Running;
        public TimeSpan? LastFrame;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        InputElement.PointerWheelChangedEvent.AddClassHandler<ScrollViewer>(OnWheel, RoutingStrategies.Tunnel);
    }

    /// <summary>Pixels one wheel notch scrolls for the chosen speed.</summary>
    internal static double StepFor(ScrollSpeed speed) => speed switch
    {
        ScrollSpeed.Slow => 40,
        ScrollSpeed.Fast => 100,
        _ => 60,
    };

    /// <summary>Mouse wheels send whole notches; touchpads and hi-res wheels send fractions.</summary>
    internal static bool IsNotch(Vector delta) =>
        (delta.X != 0 || delta.Y != 0)
        && Math.Abs(delta.X - Math.Round(delta.X)) < 0.001
        && Math.Abs(delta.Y - Math.Round(delta.Y)) < 0.001;

    private static void OnWheel(ScrollViewer sender, PointerWheelEventArgs e)
    {
        var settings = SettingsStore.Instance.Settings;
        if (e.Handled || !settings.SmoothScrolling || settings.PerformanceMode) return;
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 || !IsNotch(e.Delta)) return;

        Vector delta = e.Delta;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0 && delta.X == 0) delta = new Vector(delta.Y, 0);

        // The tunnel reaches the outermost ScrollViewer first; scroll the innermost one that can move.
        ScrollViewer? target = FindTarget(e.Source as Visual, delta);
        if (target is null || TopLevel.GetTopLevel(target) is not { } top) return;
        e.Handled = true;

        var state = States.GetOrCreateValue(target);
        Vector max = MaxOffset(target);
        if (!state.Running || Distance(target.Offset, state.LastSet) > 1)
            state.Target = target.Offset;   // start from where the view really is (scrollbar drags, keys)
        double step = StepFor(settings.ScrollSpeed);
        state.Target = Clamp(new Vector(state.Target.X - delta.X * step, state.Target.Y - delta.Y * step), max);
        if (state.Running) return;

        state.Running = true;
        state.LastFrame = null;
        state.LastSet = target.Offset;
        top.RequestAnimationFrame(time => Frame(target, top, state, time));
    }

    private static void Frame(ScrollViewer viewer, TopLevel top, State state, TimeSpan time)
    {
        // Something else moved the view (scrollbar, keyboard, a reveal): stop gliding.
        if (Distance(viewer.Offset, state.LastSet) > 1 || viewer.GetVisualRoot() is null)
        {
            state.Running = false;
            return;
        }
        double dt = state.LastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0.001, 0.05) : 1 / 60.0;
        state.LastFrame = time;
        Vector target = Clamp(state.Target, MaxOffset(viewer));
        Vector current = viewer.Offset;
        double k = 1 - Math.Exp(-dt / TimeConstant);
        Vector next = current + (target - current) * k;
        if (Distance(next, target) < 0.5) next = target;
        viewer.Offset = next;
        state.LastSet = viewer.Offset;
        if (next == target)
        {
            state.Running = false;
            return;
        }
        top.RequestAnimationFrame(t => Frame(viewer, top, state, t));
    }

    private static ScrollViewer? FindTarget(Visual? source, Vector delta)
    {
        for (Visual? v = source; v is not null; v = v.GetVisualParent())
        {
            if (v is not ScrollViewer viewer) continue;
            Vector max = MaxOffset(viewer);
            Vector offset = viewer.Offset;
            bool canY = delta.Y != 0 && (delta.Y > 0 ? offset.Y > 0.5 : offset.Y < max.Y - 0.5);
            bool canX = delta.X != 0 && (delta.X > 0 ? offset.X > 0.5 : offset.X < max.X - 0.5);
            if (canY || canX) return viewer;
        }
        return null;
    }

    private static Vector MaxOffset(ScrollViewer viewer) =>
        new(Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width), Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height));

    private static Vector Clamp(Vector v, Vector max) =>
        new(Math.Clamp(v.X, 0, max.X), Math.Clamp(v.Y, 0, max.Y));

    private static double Distance(Vector a, Vector b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
