using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace RainExplorer.Controls;

/// <summary>
/// A floating ghost that follows the cursor during a drag. Renders an arbitrary
/// child visual on the adorner layer and is repositioned from GiveFeedback.
/// </summary>
public sealed class DragAdorner : Adorner
{
    private readonly UIElement _child;
    private readonly AdornerLayer _layer;
    private double _offsetX, _offsetY;

    public DragAdorner(UIElement adornedElement, UIElement child, AdornerLayer layer)
        : base(adornedElement)
    {
        _child = child;
        _layer = layer;
        IsHitTestVisible = false;
        Opacity = 0.85;
        _layer.Add(this);
    }

    /// <summary>Move the ghost to a position relative to the adorned element.</summary>
    public void SetPosition(double x, double y)
    {
        _offsetX = x;
        _offsetY = y;
        _layer.Update(AdornedElement);
    }

    public void Detach() => _layer.Remove(this);

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _child;

    protected override Size MeasureOverride(Size constraint)
    {
        _child.Measure(constraint);
        return _child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _child.Arrange(new Rect(_child.DesiredSize));
        return finalSize;
    }

    public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
    {
        var group = new GeneralTransformGroup();
        var baseTransform = base.GetDesiredTransform(transform);
        if (baseTransform is not null) group.Children.Add(baseTransform);
        group.Children.Add(new TranslateTransform(_offsetX, _offsetY));
        return group;
    }
}
