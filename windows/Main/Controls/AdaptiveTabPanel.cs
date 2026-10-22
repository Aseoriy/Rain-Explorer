using System.Windows;
using System.Windows.Controls;
using RainExplorer.ViewModels;

namespace RainExplorer.Controls;

/// <summary>
/// Keeps a horizontal tab row inside its viewport by sharing the available width
/// between regular tabs while leaving pinned tabs compact.
/// </summary>
public sealed class AdaptiveTabPanel : Panel
{
    public static readonly DependencyProperty MinimumItemWidthProperty = DependencyProperty.Register(
        nameof(MinimumItemWidth), typeof(double), typeof(AdaptiveTabPanel),
        new FrameworkPropertyMetadata(52d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaximumItemWidthProperty = DependencyProperty.Register(
        nameof(MaximumItemWidth), typeof(double), typeof(AdaptiveTabPanel),
        new FrameworkPropertyMetadata(210d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty PinnedItemWidthProperty = DependencyProperty.Register(
        nameof(PinnedItemWidth), typeof(double), typeof(AdaptiveTabPanel),
        new FrameworkPropertyMetadata(40d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private double _regularItemWidth;
    private double? _lockedRegularItemWidth;
    private double _lockedAvailableWidth;

    public double MinimumItemWidth
    {
        get => (double)GetValue(MinimumItemWidthProperty);
        set => SetValue(MinimumItemWidthProperty, value);
    }

    public double MaximumItemWidth
    {
        get => (double)GetValue(MaximumItemWidthProperty);
        set => SetValue(MaximumItemWidthProperty, value);
    }

    public double PinnedItemWidth
    {
        get => (double)GetValue(PinnedItemWidthProperty);
        set => SetValue(PinnedItemWidthProperty, value);
    }

    /// <summary>Keep the current tab width while consecutive close buttons are clicked.</summary>
    public void LockCurrentWidths()
    {
        if (_regularItemWidth <= 0) return;
        _lockedRegularItemWidth = _regularItemWidth;
        _lockedAvailableWidth = ActualWidth;
    }

    public void UnlockWidths()
    {
        if (_lockedRegularItemWidth is null) return;
        _lockedRegularItemWidth = null;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double availableWidth = ResolveAvailableWidth(availableSize.Width);
        if (_lockedRegularItemWidth is not null
            && Math.Abs(availableWidth - _lockedAvailableWidth) > 1)
            _lockedRegularItemWidth = null;

        int pinnedCount = InternalChildren.Cast<UIElement>().Count(IsPinned);
        int regularCount = InternalChildren.Count - pinnedCount;
        double pinnedWidth = Math.Max(0, PinnedItemWidth);
        double remaining = Math.Max(0, availableWidth - pinnedCount * pinnedWidth);
        double naturalRegularWidth = regularCount == 0
            ? 0
            : Math.Min(MaximumItemWidth, remaining / regularCount);

        // Prefer a readable minimum, but continue shrinking for very crowded rows so
        // the new-tab button always remains visible instead of falling off-screen.
        if (regularCount > 0 && naturalRegularWidth < MinimumItemWidth)
            naturalRegularWidth = Math.Max(32, remaining / regularCount);

        _regularItemWidth = _lockedRegularItemWidth is double locked
            ? Math.Min(locked, Math.Max(0, remaining))
            : naturalRegularWidth;

        foreach (UIElement child in InternalChildren)
        {
            double width = IsPinned(child) ? pinnedWidth : _regularItemWidth;
            child.Measure(new Size(width, availableSize.Height));
        }

        double desiredWidth = pinnedCount * pinnedWidth + regularCount * _regularItemWidth;
        double desiredHeight = InternalChildren.Cast<UIElement>()
            .Select(child => child.DesiredSize.Height)
            .DefaultIfEmpty(0)
            .Max();
        return new Size(Math.Min(availableWidth, desiredWidth), desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            double width = IsPinned(child) ? PinnedItemWidth : _regularItemWidth;
            child.Arrange(new Rect(x, 0, Math.Max(0, width), finalSize.Height));
            x += width;
        }
        return finalSize;
    }

    private double ResolveAvailableWidth(double measuredWidth)
    {
        if (!double.IsInfinity(measuredWidth) && !double.IsNaN(measuredWidth)) return measuredWidth;
        var owner = ItemsControl.GetItemsOwner(this);
        return owner?.ActualWidth > 0 ? owner.ActualWidth : 0;
    }

    private static bool IsPinned(UIElement child) =>
        child is FrameworkElement { DataContext: TabViewModel { IsPinned: true } };
}
