using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RainExplorer.Converters;

/// <summary>true =&gt; Collapsed, false =&gt; Visible. (The opposite of the built-in converter.)</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
