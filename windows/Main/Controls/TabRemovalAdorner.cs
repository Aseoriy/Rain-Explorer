using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace RainExplorer.Controls;

/// <summary>Lets a removed tab finish a short fade without delaying the model update.</summary>
internal sealed class TabRemovalAdorner : Adorner
{
    private readonly ImageSource _snapshot;
    private readonly Rect _bounds;

    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(TabRemovalAdorner),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private TabRemovalAdorner(UIElement adornedElement, ImageSource snapshot, Rect bounds)
        : base(adornedElement)
    {
        _snapshot = snapshot;
        _bounds = bounds;
        IsHitTestVisible = false;
    }

    private double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public static void Begin(ListBox bar, ListBoxItem container)
    {
        if (container.ActualWidth < 1 || container.ActualHeight < 1) return;
        var layer = AdornerLayer.GetAdornerLayer(bar);
        if (layer is null) return;

        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(container);
            int pixelWidth = Math.Max(1, (int)Math.Ceiling(container.ActualWidth * dpi.DpiScaleX));
            int pixelHeight = Math.Max(1, (int)Math.Ceiling(container.ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight,
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(container);
            bitmap.Freeze();

            Point origin = container.TranslatePoint(new Point(), bar);
            var adorner = new TabRemovalAdorner(bar, bitmap,
                new Rect(origin, new Size(container.ActualWidth, container.ActualHeight)));
            layer.Add(adorner);

            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(115))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            animation.Completed += (_, _) => layer.Remove(adorner);
            adorner.BeginAnimation(ProgressProperty, animation);
        }
        catch
        {
            // The row can disappear between the collection event and the render snapshot.
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double scale = 1 - 0.055 * Progress;
        var center = new Point(_bounds.X + _bounds.Width / 2, _bounds.Y + _bounds.Height / 2);
        drawingContext.PushOpacity(1 - Progress);
        drawingContext.PushTransform(new TranslateTransform(0, -3 * Progress));
        drawingContext.PushTransform(new ScaleTransform(scale, scale, center.X, center.Y));
        drawingContext.DrawImage(_snapshot, _bounds);
        drawingContext.Pop();
        drawingContext.Pop();
        drawingContext.Pop();
    }
}
