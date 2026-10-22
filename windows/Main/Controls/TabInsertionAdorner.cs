using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace RainExplorer.Controls;

/// <summary>Shows the exact insertion edge while a tab is being dragged.</summary>
public sealed class TabInsertionAdorner : Adorner
{
    private double _x;

    public TabInsertionAdorner(UIElement adornedElement) : base(adornedElement)
    {
        IsHitTestVisible = false;
    }

    public double X
    {
        get => _x;
        set
        {
            if (Math.Abs(_x - value) < 0.1) return;
            _x = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double y = 3;
        double bottom = Math.Max(y + 2, AdornedElement.RenderSize.Height - 3);
        var accent = (Brush)Application.Current.FindResource("AccentBright");
        var glow = new Pen(new SolidColorBrush(Color.FromArgb(90, 190, 120, 255)), 7);
        var line = new Pen(accent, 2);
        drawingContext.DrawLine(glow, new Point(_x, y), new Point(_x, bottom));
        drawingContext.DrawLine(line, new Point(_x, y), new Point(_x, bottom));
        drawingContext.DrawEllipse(accent, null, new Point(_x, y + 1), 3, 3);
        drawingContext.DrawEllipse(accent, null, new Point(_x, bottom - 1), 3, 3);
    }
}
