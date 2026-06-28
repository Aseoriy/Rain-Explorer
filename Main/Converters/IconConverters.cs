using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RainExplorer.Converters;

/// <summary>
/// Resolves a Lucide icon key (e.g. "folder", "image") to the matching
/// <see cref="Geometry"/> resource defined in Themes/Icons.xaml ("Ic.&lt;key&gt;").
/// Lets data-bound rows pick a vector glyph the same way the source app picked
/// an emoji by extension.
/// </summary>
public sealed class IconKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0) return null;
        return Application.Current?.TryFindResource("Ic." + key) as Geometry;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Uppercases a string for display (column headers) without touching the source copy.</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.ToUpperInvariant() ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
