using System;
using System.Collections;
using System.Collections.Specialized;
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
            typeof(System.Windows.Media.Brush),
            typeof(Sparkline),
            new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxValueProperty =
        DependencyProperty.Register(
            nameof(MaxValue),
            typeof(double),
            typeof(Sparkline),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private INotifyCollectionChanged? _observedCollection;

    public IEnumerable? Values
    {
        get => (IEnumerable?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public System.Windows.Media.Brush Stroke
    {
        get => (System.Windows.Media.Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double MaxValue
    {
        get => (double)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(37, 37, 37)), null, new Rect(RenderSize));

        double[] values = Values?.Cast<double>().ToArray() ?? Array.Empty<double>();
        if (values.Length < 2 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        double max = MaxValue > 0 ? MaxValue : Math.Max(values.Max(), 1d);
        double step = ActualWidth / (values.Length - 1);
        StreamGeometry geometry = new();

        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(ToPoint(values[0], 0, step, max), false, false);

            for (int i = 1; i < values.Length; i++)
            {
                context.LineTo(ToPoint(values[i], i, step, max), true, false);
            }
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(Stroke, 2), geometry);
    }

    private System.Windows.Point ToPoint(double value, int index, double step, double max)
    {
        double normalized = Math.Clamp(value / max, 0d, 1d);
        return new System.Windows.Point(index * step, ActualHeight - normalized * ActualHeight);
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
