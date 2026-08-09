using System.Windows;
using System.Windows.Media.Animation;

namespace RainExplorer.Helpers;

/// <summary>Smoothly reveals variable-height content without scaling its layout.</summary>
public static class ExpandCollapseBehavior
{
    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.RegisterAttached(
            "IsExpanded",
            typeof(bool),
            typeof(ExpandCollapseBehavior),
            new PropertyMetadata(true, OnIsExpandedChanged));

    public static void SetIsExpanded(DependencyObject element, bool value) =>
        element.SetValue(IsExpandedProperty, value);

    public static bool GetIsExpanded(DependencyObject element) =>
        (bool)element.GetValue(IsExpandedProperty);

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        bool expanded = (bool)e.NewValue;
        if (!element.IsLoaded)
        {
            element.Loaded -= Element_Loaded;
            element.Loaded += Element_Loaded;
            if (!expanded) element.Visibility = Visibility.Collapsed;
            return;
        }
        Animate(element, expanded);
    }

    private static void Element_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        element.Loaded -= Element_Loaded;
        Animate(element, GetIsExpanded(element), initial: true);
    }

    private static void Animate(FrameworkElement element, bool expanded, bool initial = false)
    {
        element.BeginAnimation(FrameworkElement.HeightProperty, null);
        element.BeginAnimation(UIElement.OpacityProperty, null);

        if (initial)
        {
            element.Height = double.NaN;
            element.Opacity = expanded ? 1 : 0;
            element.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (expanded)
        {
            element.Visibility = Visibility.Visible;
            element.Height = double.NaN;
            element.Measure(new Size(Math.Max(0, element.ActualWidth), double.PositiveInfinity));
            double target = Math.Max(0, element.DesiredSize.Height);
            element.Height = 0;
            element.Opacity = 0;

            var height = new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            height.Completed += (_, _) =>
            {
                element.BeginAnimation(FrameworkElement.HeightProperty, null);
                element.Height = double.NaN;
            };
            element.BeginAnimation(FrameworkElement.HeightProperty, height);
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }
        else
        {
            double start = element.ActualHeight;
            element.Height = start;
            var height = new DoubleAnimation(start, 0, TimeSpan.FromMilliseconds(125))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            height.Completed += (_, _) =>
            {
                element.BeginAnimation(FrameworkElement.HeightProperty, null);
                element.Height = double.NaN;
                element.Visibility = Visibility.Collapsed;
            };
            element.BeginAnimation(FrameworkElement.HeightProperty, height);
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(90)));
        }
    }
}
