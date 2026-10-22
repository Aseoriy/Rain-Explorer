using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace RainExplorer.Controls;

/// <summary>
/// Draws one Lucide line icon ("Ic.&lt;Kind&gt;" from Themes/Icons.axaml) scaled to the
/// control's size. The stroke follows the inherited Foreground, so an icon inside a
/// button recolours with the button's hover/disabled state — the same behaviour as the
/// Windows build's IconPath style, without a binding per icon.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    /// <summary>Stroke width in device-independent pixels (not scaled with the icon).</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.6);

    /// <summary>Mirror horizontally (used for "redo", drawn from the "undo" glyph).</summary>
    public static readonly StyledProperty<bool> FlipProperty =
        AvaloniaProperty.Register<Icon, bool>(nameof(Flip));

    private Geometry? _geometry;

    public Icon() => IsHitTestVisible = false;

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty, StrokeThicknessProperty, FlipProperty);
        KindProperty.Changed.AddClassHandler<Icon>((icon, _) => icon.ResolveGeometry());
    }

    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public bool Flip { get => GetValue(FlipProperty); set => SetValue(FlipProperty, value); }

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        ResolveGeometry();
    }

    private void ResolveGeometry()
    {
        _geometry = null;
        if (!string.IsNullOrEmpty(Kind)
            && (this.TryFindResource("Ic." + Kind, out object? value)
                || (Application.Current?.TryFindResource("Ic." + Kind, out value) ?? false))
            && value is Geometry geometry)
            _geometry = geometry;
        else if (!string.IsNullOrEmpty(Kind)
                 && Application.Current?.TryFindResource("Ic.file", out object? fallback) == true)
            _geometry = fallback as Geometry;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsNaN(Width) ? 16 : Width,
        double.IsNaN(Height) ? 16 : Height);

    public override void Render(DrawingContext context)
    {
        if (_geometry is null || Foreground is null) return;
        double size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        double scale = size / 24.0;
        double offsetX = (Bounds.Width - size) / 2;
        double offsetY = (Bounds.Height - size) / 2;
        Matrix transform = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY);
        if (Flip)
            transform = Matrix.CreateScale(-scale, scale) * Matrix.CreateTranslation(offsetX + size, offsetY);

        var pen = new Pen(Foreground, StrokeThickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(transform))
            context.DrawGeometry(null, pen, _geometry);
    }
}
