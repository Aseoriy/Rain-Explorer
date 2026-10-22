using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace RainExplorer.Helpers;

/// <summary>Port of Windows Rain's variable-height group/sidebar reveal.</summary>
public static class ExpandCollapseBehavior
{
    public static readonly AttachedProperty<bool> IsExpandedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsExpanded", typeof(ExpandCollapseBehavior), true);

    static ExpandCollapseBehavior() => IsExpandedProperty.Changed.AddClassHandler<Control>((control, _) => Changed(control));

    public static void SetIsExpanded(Control control, bool value) => control.SetValue(IsExpandedProperty, value);
    public static bool GetIsExpanded(Control control) => control.GetValue(IsExpandedProperty);

    private static void Changed(Control control)
    {
        control.AttachedToVisualTree -= Attached;
        control.AttachedToVisualTree += Attached;
        control.DetachedFromVisualTree -= Detached;
        control.DetachedFromVisualTree += Detached;
        if (control.GetVisualRoot() is null) { SetInitial(control); return; }
        bool expanded = GetIsExpanded(control);
        double currentHeight = control.Bounds.Height;
        double currentOpacity = control.IsVisible ? control.Opacity : 0;
        bool wasVisible = control.IsVisible;
        Motion.Cancel(control);
        control.ClipToBounds = true;
        control.IsVisible = true;
        control.Height = double.NaN;
        control.Measure(new Size(Math.Max(0, control.Bounds.Width), double.PositiveInfinity));
        double target = expanded ? control.DesiredSize.Height : 0;
        control.Height = target;
        _ = Motion.Play(control, expanded ? 150 : 125,
            expanded ? new CubicEaseOut() : new CubicEaseIn(),
        [
            Motion.Frame(0, (Control.HeightProperty, wasVisible ? currentHeight : 0),
                (Visual.OpacityProperty, currentOpacity)),
            Motion.Frame(1, (Control.HeightProperty, target), (Visual.OpacityProperty, expanded ? 1 : 0)),
        ], () => SetInitial(control));
    }

    private static void SetInitial(Control control)
    {
        control.Height = double.NaN;
        control.Opacity = GetIsExpanded(control) ? 1 : 0;
        control.IsVisible = GetIsExpanded(control);
    }

    private static void Attached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control) SetInitial(control);
    }

    private static void Detached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control) Motion.Cancel(control);
    }
}
