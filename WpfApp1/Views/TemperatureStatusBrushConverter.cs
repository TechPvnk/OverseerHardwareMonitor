using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Overseer.Services;

namespace Overseer.Views;

public sealed class TemperatureStatusBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush NormalBrush = CreateFrozenBrush("#F54F36");
    private static readonly SolidColorBrush HighBrush = CreateFrozenBrush("#FF9D00");
    private static readonly SolidColorBrush CriticalBrush = CreateFrozenBrush("#FF2B2B");
    private static readonly SolidColorBrush UnavailableBrush = CreateFrozenBrush("#707070");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        TemperatureStatusKind state = value switch
        {
            TemperatureStatus status => status.State,
            TemperatureStatusKind kind => kind,
            _ => TemperatureStatusKind.Unavailable
        };

        return state switch
        {
            TemperatureStatusKind.Normal => NormalBrush,
            TemperatureStatusKind.High => HighBrush,
            TemperatureStatusKind.Critical => CriticalBrush,
            _ => UnavailableBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static SolidColorBrush CreateFrozenBrush(string color)
    {
        SolidColorBrush brush = new((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
