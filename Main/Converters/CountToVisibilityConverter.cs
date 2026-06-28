using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RainExplorer.Converters;

/// <summary>Visible when the bound count/collection is non-empty; Collapsed otherwise.
/// Pass parameter "invert" to flip (Visible only when empty).</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = value switch
        {
            int i => i,
            System.Collections.ICollection c => c.Count,
            _ => 0,
        };
        bool any = count > 0;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) any = !any;
        return any ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
