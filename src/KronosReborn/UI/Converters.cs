using System;
using System.Globalization;
using System.Windows;
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
/// Turns a boolean into a <see cref="Visibility"/>: shown while it is true.
/// </summary>
/// <remarks>
/// The return type is the whole point. <see cref="Visibility"/> is not a bool and WPF will not
/// convert one into the other: a converter that returns a bool leaves the binding unable to produce
/// a Visibility, the binding is discarded, and the property keeps whatever default it was given. For
/// a Visibility that default is Visible, so the element simply stays on screen and the failure is
/// invisible except in a debugger.
/// </remarks>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>
/// Turns a boolean into a <see cref="Visibility"/>: shown while it is false.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="BooleanToVisibilityConverter"/>, and it exists for the same reason:
/// returning a bool does not work, so the inversion has to happen here rather than being left to a
/// binding that cannot express it.
/// </remarks>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}
