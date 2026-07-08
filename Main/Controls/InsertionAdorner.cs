using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace RainExplorer.Controls;

/// <summary>
/// A thin horizontal accent line (with rounded end caps) drawn on the adorner layer to
/// show where a dragged sidebar pin will be dropped when reordering. This is deliberately
/// distinct from the "drop into folder" highlight so reordering doesn't look like a move.
/// </summary>
public sealed class InsertionAdorner : Adorner
{
    private double _y = -1;
    private readonly Brush _brush;
    private readonly Pen _pen;

    public InsertionAdorner(UIElement adorned) : base(adorned)
    {
        IsHitTestVisible = false;
        _brush = (Application.Current?.TryFindResource("AccentBright") as Brush) ?? Brushes.MediumPurple;
        _pen = new Pen(_brush, 2);
        if (_pen.CanFreeze) _pen.Freeze();
    }

    /// <summary>Position the line at <paramref name="y"/> (in the adorned element's coordinates).</summary>
    public void SetY(double y)
    {
        _y = y;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_y < 0 || AdornedElement is not FrameworkElement fe) return;
        double w = fe.ActualWidth;
        dc.DrawLine(_pen, new Point(8, _y), new Point(w - 8, _y));
        dc.DrawEllipse(_brush, null, new Point(8, _y), 3, 3);
        dc.DrawEllipse(_brush, null, new Point(w - 8, _y), 3, 3);
    }
}
