using System;
using System.Globalization;
using System.Windows.Data;

namespace Overseer.Views;

public sealed class UnavailableValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string? text = value?.ToString();
        return string.IsNullOrWhiteSpace(text)
            || text.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Initializing", StringComparison.OrdinalIgnoreCase)
                ? "—"
                : text;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
