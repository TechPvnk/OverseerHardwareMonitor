using System;
using System.Windows;
using System.Windows.Media;
using Overseer.Services;

namespace Overseer.Views;

public sealed class TemperatureStatusBar : FrameworkElement
{
    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(
            nameof(Status),
            typeof(TemperatureStatus),
            typeof(TemperatureStatusBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public TemperatureStatus? Status
    {
        get => (TemperatureStatus?)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        Rect bounds = new(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        double radius = Math.Min(bounds.Height / 2d, 3d);
        drawingContext.DrawRoundedRectangle(CreateBrush(Color.FromRgb(45, 45, 45), 0.82), null, bounds, radius, radius);

        TemperatureStatus? status = Status;
        if (status is null || !status.IsAvailable || !status.TemperatureCelsius.HasValue)
        {
            drawingContext.DrawRoundedRectangle(CreateBrush(Color.FromRgb(112, 112, 112), 0.45), null, bounds, radius, radius);
            return;
        }

        double scaleMax = Math.Max(status.Threshold.CriticalCelsius, 1f);
        double ratio = Math.Clamp(status.TemperatureCelsius.Value / scaleMax, 0.04d, 1d);
        Rect fill = new(bounds.X, bounds.Y, bounds.Width * ratio, bounds.Height);

        Color color = StatusColor(status.State);
        LinearGradientBrush fillBrush = new()
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        fillBrush.GradientStops.Add(new GradientStop(Color.FromArgb(95, color.R, color.G, color.B), 0));
        fillBrush.GradientStops.Add(new GradientStop(color, 1));
        fillBrush.Freeze();

        drawingContext.DrawRoundedRectangle(fillBrush, null, fill, radius, radius);
    }

    private static Color StatusColor(TemperatureStatusKind state)
    {
        return state switch
        {
            TemperatureStatusKind.Normal => Color.FromRgb(0x39, 0xD3, 0x53),
            TemperatureStatusKind.High => Color.FromRgb(0xFF, 0x9D, 0x00),
            TemperatureStatusKind.Critical => Color.FromRgb(0xFF, 0x2B, 0x2B),
            _ => Color.FromRgb(0x70, 0x70, 0x70)
        };
    }

    private static SolidColorBrush CreateBrush(Color color, double opacity)
    {
        SolidColorBrush brush = new(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }
}
