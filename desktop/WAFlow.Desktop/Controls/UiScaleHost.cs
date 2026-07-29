using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WAFlow.Desktop.Controls;

/// <summary>
/// Gives the child an inverse-size layout surface and then scales the rendered
/// result back into the available window. Unlike a plain LayoutTransform this
/// lets zooming out reveal more content instead of leaving unused space.
/// </summary>
public sealed class UiScaleHost : Decorator
{
    private readonly ScaleTransform _transform = new(1, 1);

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(
        nameof(Scale),
        typeof(double),
        typeof(UiScaleHost),
        new FrameworkPropertyMetadata(
            1d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange,
            OnScaleChanged,
            CoerceScale));

    public double Scale
    {
        get => (double)GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    public UiScaleHost()
    {
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return new Size();
        var logicalConstraint = Inverse(constraint, Scale);
        Child.Measure(logicalConstraint);
        return new Size(
            Fit(constraint.Width, Child.DesiredSize.Width * Scale),
            Fit(constraint.Height, Child.DesiredSize.Height * Scale));
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is null) return arrangeSize;
        _transform.ScaleX = Scale;
        _transform.ScaleY = Scale;
        Child.RenderTransformOrigin = new Point(0, 0);
        Child.RenderTransform = _transform;
        Child.Arrange(new Rect(new Point(), Inverse(arrangeSize, Scale)));
        return arrangeSize;
    }

    private static object CoerceScale(DependencyObject element, object value) =>
        Math.Clamp((double)value, 0.8d, 1.25d);

    private static void OnScaleChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is UiScaleHost host)
        {
            host.InvalidateMeasure();
            host.InvalidateArrange();
        }
    }

    private static Size Inverse(Size size, double scale) => new(
        double.IsInfinity(size.Width) ? double.PositiveInfinity : size.Width / scale,
        double.IsInfinity(size.Height) ? double.PositiveInfinity : size.Height / scale);

    private static double Fit(double available, double desired) =>
        double.IsInfinity(available) ? desired : available;
}
