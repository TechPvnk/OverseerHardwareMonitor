using System;
using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Overseer.Views;

public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(
            nameof(Values),
            typeof(IEnumerable),
            typeof(Sparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValuesChanged));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(
            nameof(Stroke),
            typeof(Brush),
            typeof(Sparkline),
            new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxValueProperty =
        DependencyProperty.Register(
            nameof(MaxValue),
            typeof(double),
            typeof(Sparkline),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(
            nameof(StrokeThickness),
            typeof(double),
            typeof(Sparkline),
            new FrameworkPropertyMetadata(1.35d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowFillProperty =
        DependencyProperty.Register(
            nameof(ShowFill),
            typeof(bool),
            typeof(Sparkline),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridProperty =
        DependencyProperty.Register(
            nameof(ShowGrid),
            typeof(bool),
            typeof(Sparkline),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowAxisLabelsProperty =
        DependencyProperty.Register(
            nameof(ShowAxisLabels),
            typeof(bool),
            typeof(Sparkline),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush ChartBackground = CreateBrush(Color.FromRgb(18, 18, 18), 1d);
    private static readonly Pen GridPen = CreateFrozenPen(Color.FromRgb(46, 46, 46), 0.45d, 0.72d);
    private static readonly Brush AxisTextBrush = CreateBrush(Color.FromRgb(130, 130, 130), 0.82d);

    private INotifyCollectionChanged? _observedCollection;

    public IEnumerable? Values
    {
        get => (IEnumerable?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double MaxValue
    {
        get => (double)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public bool ShowFill
    {
        get => (bool)GetValue(ShowFillProperty);
        set => SetValue(ShowFillProperty, value);
    }

    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public bool ShowAxisLabels
    {
        get => (bool)GetValue(ShowAxisLabelsProperty);
        set => SetValue(ShowAxisLabelsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        Rect bounds = new(0, 0, RenderSize.Width, RenderSize.Height);
        drawingContext.DrawRectangle(ChartBackground, null, bounds);

        if (ShowGrid)
        {
            DrawGrid(drawingContext, bounds);
        }

        double[] values = Values?.Cast<double>().ToArray() ?? Array.Empty<double>();
        double max = MaxValue > 0 ? MaxValue : Math.Max(values.DefaultIfEmpty(1d).Max(), 1d);

        if (ShowAxisLabels)
        {
            DrawAxisLabels(drawingContext, bounds, max);
        }

        if (values.Length < 2 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        double step = ActualWidth / (values.Length - 1);
        StreamGeometry lineGeometry = BuildLineGeometry(values, step, max);
        lineGeometry.Freeze();

        if (ShowFill)
        {
            StreamGeometry fillGeometry = BuildFillGeometry(values, step, max);
            fillGeometry.Freeze();
            drawingContext.DrawGeometry(CreateFillBrush(Stroke), null, fillGeometry);
        }

        drawingContext.DrawGeometry(null, new Pen(CreateOpacityBrush(Stroke, 0.22), Math.Max(StrokeThickness + 3.5d, 4d)), lineGeometry);
        drawingContext.DrawGeometry(null, new Pen(Stroke, StrokeThickness), lineGeometry);
    }

    private StreamGeometry BuildLineGeometry(double[] values, double step, double max)
    {
        StreamGeometry geometry = new();
        using StreamGeometryContext context = geometry.Open();
        context.BeginFigure(ToPoint(values[0], 0, step, max), false, false);

        for (int i = 1; i < values.Length; i++)
        {
            context.LineTo(ToPoint(values[i], i, step, max), true, false);
        }

        return geometry;
    }

    private StreamGeometry BuildFillGeometry(double[] values, double step, double max)
    {
        StreamGeometry geometry = new();
        using StreamGeometryContext context = geometry.Open();
        Point first = ToPoint(values[0], 0, step, max);
        context.BeginFigure(new Point(first.X, ActualHeight), true, true);
        context.LineTo(first, true, false);

        for (int i = 1; i < values.Length; i++)
        {
            context.LineTo(ToPoint(values[i], i, step, max), true, false);
        }

        context.LineTo(new Point((values.Length - 1) * step, ActualHeight), true, false);
        return geometry;
    }

    private void DrawGrid(DrawingContext drawingContext, Rect bounds)
    {
        const int horizontalLines = 4;
        const int verticalLines = 6;

        for (int i = 1; i < horizontalLines; i++)
        {
            double y = bounds.Height * i / horizontalLines;
            drawingContext.DrawLine(GridPen, new Point(0, y), new Point(bounds.Width, y));
        }

        for (int i = 1; i < verticalLines; i++)
        {
            double x = bounds.Width * i / verticalLines;
            drawingContext.DrawLine(GridPen, new Point(x, 0), new Point(x, bounds.Height));
        }
    }

    private void DrawAxisLabels(DrawingContext drawingContext, Rect bounds, double max)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Typeface typeface = new("Segoe UI");

        foreach (double value in new[] { max, max * 0.66d, max * 0.33d })
        {
            string label = $"{value:0}°";
            FormattedText text = new(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10d, AxisTextBrush, pixelsPerDip);
            double y = ActualHeight - Math.Clamp(value / max, 0d, 1d) * ActualHeight - text.Height / 2d;
            drawingContext.DrawText(text, new Point(2, Math.Clamp(y, 1d, Math.Max(1d, ActualHeight - text.Height - 1d))));
        }

        FormattedText seconds = new("60s", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10d, AxisTextBrush, pixelsPerDip);
        drawingContext.DrawText(seconds, new Point(Math.Max(0, bounds.Width - seconds.Width - 2), Math.Max(0, bounds.Height - seconds.Height - 1)));
    }


    private Point ToPoint(double value, int index, double step, double max)
    {
        double normalized = Math.Clamp(value / max, 0d, 1d);
        return new Point(index * step, ActualHeight - normalized * ActualHeight);
    }

    private static Brush CreateFillBrush(Brush source)
    {
        Color color = ExtractColor(source, Colors.OrangeRed);
        LinearGradientBrush brush = new()
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(58, color.R, color.G, color.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(20, color.R, color.G, color.B), 0.58));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
        brush.Freeze();
        return brush;
    }

    private static Brush CreateOpacityBrush(Brush source, double opacity)
    {
        Color color = ExtractColor(source, Colors.OrangeRed);
        return CreateBrush(color, opacity);
    }

    private static Color ExtractColor(Brush brush, Color fallback)
    {
        return brush switch
        {
            SolidColorBrush solid => solid.Color,
            _ => fallback
        };
    }

    private static SolidColorBrush CreateBrush(Color color, double opacity)
    {
        SolidColorBrush brush = new(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Color color, double thickness, double opacity)
    {
        Pen pen = new(CreateBrush(color, opacity), thickness);
        pen.Freeze();
        return pen;
    }

    private static void OnValuesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        Sparkline sparkline = (Sparkline)dependencyObject;
        sparkline.DetachCollectionChanged();

        if (e.NewValue is INotifyCollectionChanged collection)
        {
            sparkline._observedCollection = collection;
            collection.CollectionChanged += sparkline.OnCollectionChanged;
        }

        sparkline.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private void DetachCollectionChanged()
    {
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged -= OnCollectionChanged;
            _observedCollection = null;
        }
    }
}

