using System.Globalization;
using Avalonia.Data.Converters;

namespace RainExplorer.Controls;

/// <summary>Upper-cases section headers ("QUICK ACCESS") without changing the stored name.</summary>
public sealed class UpperConverter : IValueConverter
{
    public static readonly UpperConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.ToUpper(culture);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows the containing folder of a search result ("~/Documents/Notes").</summary>
public sealed class ParentFolderConverter : IValueConverter
{
    public static readonly ParentFolderConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string path ? Services.PathUtil.Tildify(Path.GetDirectoryName(path) ?? path) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Menu shortcut text in the Windows build's style ("Ctrl+X", "Del", "Alt+Enter").</summary>
public sealed class GestureTextConverter : IValueConverter
{
    public static readonly GestureTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Avalonia.Input.KeyGesture gesture) return null;
        string key = gesture.Key switch
        {
            Avalonia.Input.Key.Delete => "Del",
            Avalonia.Input.Key.Return => "Enter",
            Avalonia.Input.Key.OemComma => ",",
            _ => gesture.Key.ToString(),
        };
        var parts = new List<string>();
        if (gesture.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) parts.Add("Shift");
        if (gesture.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)) parts.Add("Alt");
        parts.Add(key);
        return string.Join('+', parts);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when a count is above zero (WPF's CountToVisibility).</summary>
public sealed class CountToBoolConverter : IValueConverter
{
    public static readonly CountToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
