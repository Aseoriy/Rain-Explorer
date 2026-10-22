using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace RainExplorer.Helpers;

/// <summary>Windows Rain's motion timings, with interrupted animations safely replaced.</summary>
internal static class Motion
{
    private static readonly ConditionalWeakTable<Control, CancellationTokenSource> Running = new();
    public static readonly AttachedProperty<bool> PopupRevealProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("PopupReveal", typeof(Motion));
    public static readonly AttachedProperty<double> PopupOffsetProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("PopupOffset", typeof(Motion));

    static Motion() => PopupRevealProperty.Changed.AddClassHandler<Control>((control, _) =>
    {
        control.AttachedToVisualTree -= PopupAttached;
        control.DetachedFromVisualTree -= PopupDetached;
        if (GetPopupReveal(control))
        {
            control.AttachedToVisualTree += PopupAttached;
            control.DetachedFromVisualTree += PopupDetached;
        }
    });

    public static bool GetPopupReveal(Control control) => control.GetValue(PopupRevealProperty);
    public static void SetPopupReveal(Control control, bool value) => control.SetValue(PopupRevealProperty, value);
    public static double GetPopupOffset(Control control) => control.GetValue(PopupOffsetProperty);
    public static void SetPopupOffset(Control control, double value) => control.SetValue(PopupOffsetProperty, value);
    private static void PopupAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control control) return;
        control.Opacity = 0;
        // Start after popup creation/layout, so the first opening does not spend
        // its animation time constructing and styling the popup off-screen.
        Dispatcher.UIThread.Post(() =>
        {
            if (control.GetVisualRoot() is null) return;
            control.Opacity = 1;
            StartPopupMotion(control);
        }, DispatcherPriority.Render);
    }

    private static void StartPopupMotion(Control control)
    {
        double offset = GetPopupOffset(control);
        if (offset == 0)
        {
            _ = Play(control, 150, new LinearEasing(),
                [Frame(0, (Visual.OpacityProperty, 0)), Frame(1, (Visual.OpacityProperty, 1))]);
            return;
        }
        var transform = new TranslateTransform();
        control.RenderTransform = transform;
        _ = PlayAnimations(control,
        [
            CreateAnimation(150, new QuadraticEaseOut(),
                [Frame(0, (Visual.OpacityProperty, 0)), Frame(1, (Visual.OpacityProperty, 1))]),
            CreateAnimation(170, new QuadraticEaseOut(),
                [Frame(0, (TranslateTransform.YProperty, offset)), Frame(1, (TranslateTransform.YProperty, 0))]),
        ], () => { if (ReferenceEquals(control.RenderTransform, transform)) control.RenderTransform = null; });
    }
    private static void PopupDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control) { Cancel(control); control.Opacity = 1; }
    }

    public static KeyFrame Frame(double cue, params (AvaloniaProperty Property, double Value)[] values)
    {
        var frame = new KeyFrame { Cue = new Cue(cue) };
        foreach (var (property, value) in values) frame.Setters.Add(new Setter(property, value));
        return frame;
    }

    public static void Cancel(Control control)
    {
        if (!Running.TryGetValue(control, out var previous)) return;
        Running.Remove(control);
        previous.Cancel();
    }

    public static Task Play(Control control, double milliseconds, Easing easing,
        KeyFrame[] frames, Action? completed = null, FillMode fillMode = FillMode.None) =>
        PlayAnimations(control, [CreateAnimation(milliseconds, easing, frames, fillMode)], completed);

    private static Animation CreateAnimation(double milliseconds, Easing easing, KeyFrame[] frames,
        FillMode fillMode = FillMode.None)
    {
        var animation = new Animation { Duration = TimeSpan.FromMilliseconds(milliseconds), Easing = easing, FillMode = fillMode };
        foreach (var frame in frames) animation.Children.Add(frame);
        return animation;
    }

    private static async Task PlayAnimations(Control control, Animation[] animations, Action? completed)
    {
        Cancel(control);
        using var cancellation = new CancellationTokenSource();
        Running.Add(control, cancellation);
        try
        {
            await Task.WhenAll(animations.Select(animation => animation.RunAsync(control, cancellation.Token)));
            if (!cancellation.IsCancellationRequested) completed?.Invoke();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (Running.TryGetValue(control, out var current) && ReferenceEquals(current, cancellation))
                Running.Remove(control);
        }
    }

    public static Task Arrive(Control control, double scale = 0.96, double y = 3,
        double opacity = 0, double milliseconds = 145)
    {
        Cancel(control);
        var transforms = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } };
        control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        control.RenderTransform = transforms;
        return Play(control, milliseconds, new CubicEaseOut(),
        [
            Frame(0, (Visual.OpacityProperty, opacity), (ScaleTransform.ScaleXProperty, scale),
                (ScaleTransform.ScaleYProperty, scale), (TranslateTransform.YProperty, y)),
            Frame(1, (Visual.OpacityProperty, 1), (ScaleTransform.ScaleXProperty, 1),
                (ScaleTransform.ScaleYProperty, 1), (TranslateTransform.YProperty, 0)),
        ], () =>
        {
            if (ReferenceEquals(control.RenderTransform, transforms)) control.RenderTransform = null;
        });
    }

    public static Task Slide(Control control, double delta, double milliseconds = 150)
    {
        Cancel(control);
        var transform = new TranslateTransform();
        control.RenderTransform = transform;
        return Play(control, milliseconds, new CubicEaseOut(),
            [Frame(0, (TranslateTransform.XProperty, delta)), Frame(1, (TranslateTransform.XProperty, 0))],
            () => { if (ReferenceEquals(control.RenderTransform, transform)) control.RenderTransform = null; });
    }

    public static void AnimateProjectMenu(ContextMenu menu)
    {
        bool closing = false;
        bool finishClose = false;
        menu.Opened += (_, _) => closing = false;
        menu.Closing += (_, e) =>
        {
            if (finishClose || menu.GetVisualRoot() is null) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            double opacity = menu.Opacity;
            double y = menu.RenderTransform?.Value.M32 ?? 0;
            Cancel(menu);
            menu.RenderTransform = new TranslateTransform();
            _ = Play(menu, 110, new LinearEasing(),
                [Frame(0, (Visual.OpacityProperty, opacity), (TranslateTransform.YProperty, y)),
                 Frame(1, (Visual.OpacityProperty, 0), (TranslateTransform.YProperty, -6))], () =>
                 {
                     finishClose = true;
                     // Cancelling a property-driven close leaves Popup.IsOpen false
                     // while its host is visible. Close the host directly on completion.
                     if (menu.Parent is Popup popup) popup.Close();
                     else menu.Close();
                     finishClose = false;
                     closing = false;
                     menu.RenderTransform = null;
                 });
        };
    }
}
