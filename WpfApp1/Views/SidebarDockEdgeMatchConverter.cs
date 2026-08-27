using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Overseer.Services;

namespace Overseer.Views;

public sealed class SidebarDockEdgeMatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is SidebarDockEdge edge
            && Enum.TryParse(parameter?.ToString(), true, out SidebarDockEdge requested)
            && edge == requested;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true && Enum.TryParse(parameter?.ToString(), true, out SidebarDockEdge edge)
            ? edge
            : Binding.DoNothing;
    }
}
