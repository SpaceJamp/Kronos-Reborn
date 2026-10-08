using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace KronosReborn.UI;

/// <summary>
/// Converts a hex color string to a SolidColorBrush.
/// </summary>
public sealed class StringToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && hex.StartsWith('#'))
        {
            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            catch
            {
                return Brushes.Gray;
            }
        }

        // For version presence: parameter "HasValue" means show accent color if value exists, gray if null
        if (parameter is string param && param == "HasValue")
        {
            return value is not null ? Brushes.Black : Brushes.Gray;
        }

        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Inverts a boolean (for visibility).
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b) return !b;
        if (value is int i) return i == 0;
        return true;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
