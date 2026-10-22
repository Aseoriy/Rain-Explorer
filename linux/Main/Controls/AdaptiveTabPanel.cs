using Avalonia;
using Avalonia.Controls;
using RainExplorer.ViewModels;

namespace RainExplorer.Controls;

/// <summary>
/// Keeps a horizontal tab row inside its viewport by sharing the available width
/// between regular tabs while leaving pinned tabs compact. Port of the Windows build's
/// AdaptiveTabPanel.
/// </summary>
public sealed class AdaptiveTabPanel : Panel
{
    public static readonly StyledProperty<double> MinimumItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveTabPanel, double>(nameof(MinimumItemWidth), 52);

    public static readonly StyledProperty<double> MaximumItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveTabPanel, double>(nameof(MaximumItemWidth), 210);

    public static readonly StyledProperty<double> PinnedItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveTabPanel, double>(nameof(PinnedItemWidth), 40);

    /// <summary>Width kept free at the end of the row for the new-tab button.</summary>
    public static readonly StyledProperty<double> ReservedWidthProperty =
        AvaloniaProperty.Register<AdaptiveTabPanel, double>(nameof(ReservedWidth));

    private double _regularItemWidth;
    private double? _lockedRegularItemWidth;
    private double _lockedAvailableWidth;
    private double _availableWidth;

    static AdaptiveTabPanel()
    {
        AffectsMeasure<AdaptiveTabPanel>(MinimumItemWidthProperty, MaximumItemWidthProperty,
            PinnedItemWidthProperty, ReservedWidthProperty);
    }

    public double MinimumItemWidth { get => GetValue(MinimumItemWidthProperty); set => SetValue(MinimumItemWidthProperty, value); }
    public double MaximumItemWidth { get => GetValue(MaximumItemWidthProperty); set => SetValue(MaximumItemWidthProperty, value); }
    public double PinnedItemWidth { get => GetValue(PinnedItemWidthProperty); set => SetValue(PinnedItemWidthProperty, value); }
    public double ReservedWidth { get => GetValue(ReservedWidthProperty); set => SetValue(ReservedWidthProperty, value); }

    /// <summary>Keep the current tab width while consecutive close buttons are clicked.</summary>
    public void LockCurrentWidths()
    {
        if (_regularItemWidth <= 0) return;
        _lockedRegularItemWidth = _regularItemWidth;
        _lockedAvailableWidth = _availableWidth;
    }

    public void UnlockWidths()
    {
        if (_lockedRegularItemWidth is null) return;
        _lockedRegularItemWidth = null;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double availableWidth = double.IsInfinity(availableSize.Width) ? 0 : Math.Max(0, availableSize.Width - ReservedWidth);
        _availableWidth = availableWidth;
        if (_lockedRegularItemWidth is not null && Math.Abs(availableWidth - _lockedAvailableWidth) > 1)
            _lockedRegularItemWidth = null;

        int pinnedCount = Children.Count(IsPinned);
        int regularCount = Children.Count - pinnedCount;
        double pinnedWidth = Math.Max(0, PinnedItemWidth);
        double remaining = Math.Max(0, availableWidth - pinnedCount * pinnedWidth);
        double naturalRegularWidth = regularCount == 0 ? 0 : Math.Min(MaximumItemWidth, remaining / regularCount);

        // Prefer a readable minimum, but continue shrinking for very crowded rows so
        // the new-tab button always remains visible instead of falling off-screen.
        if (regularCount > 0 && naturalRegularWidth < MinimumItemWidth)
            naturalRegularWidth = Math.Max(32, remaining / regularCount);

        _regularItemWidth = _lockedRegularItemWidth is double locked
            ? Math.Min(locked, Math.Max(0, remaining))
            : naturalRegularWidth;

        double height = 0;
        foreach (Control child in Children)
        {
            double width = IsPinned(child) ? pinnedWidth : _regularItemWidth;
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        double desiredWidth = pinnedCount * pinnedWidth + regularCount * _regularItemWidth;
        return new Size(desiredWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (Control child in Children)
        {
            double width = IsPinned(child) ? PinnedItemWidth : _regularItemWidth;
            child.Arrange(new Rect(x, 0, Math.Max(0, width), finalSize.Height));
            x += width;
        }
        return finalSize;
    }

    /// <summary>Left edge of each realised tab, for drag-reordering hit tests.</summary>
    public IReadOnlyList<(Control Child, double X, double Width)> Slots()
    {
        var slots = new List<(Control, double, double)>();
        double x = 0;
        foreach (Control child in Children)
        {
            double width = IsPinned(child) ? PinnedItemWidth : _regularItemWidth;
            slots.Add((child, x, width));
            x += width;
        }
        return slots;
    }

    private static bool IsPinned(Control child) => child.DataContext is TabViewModel { IsPinned: true };
}
