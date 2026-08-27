using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Overseer.Views;

public sealed class SidebarBackgroundBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double opacity = value is double number ? Math.Clamp(number, 0.4d, 1d) : 1d;
        Color color = parameter is string colorText
            ? (Color)ColorConverter.ConvertFromString(colorText)
            : Colors.Black;
        SolidColorBrush brush = new(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
