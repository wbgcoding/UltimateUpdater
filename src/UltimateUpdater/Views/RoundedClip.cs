using System.Windows;
using System.Windows.Media;

namespace UltimateUpdater.Views;

/// <summary>
/// Clips an element to a rounded rectangle of its own size. A Border only rounds its own background and
/// stroke, never its child, so a scroll viewer inside a rounded card paints its square scrollbar surface
/// over the card's corners. Attached to the child with the card's inner radius (outer radius minus the
/// stroke) it keeps the scrollbar flush against the edge and still inside the curve.
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0d, OnRadiusChanged));

    public static void SetRadius(FrameworkElement element, double value) => element.SetValue(RadiusProperty, value);

    public static double GetRadius(FrameworkElement element) => (double)element.GetValue(RadiusProperty);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        element.SizeChanged -= OnSizeChanged;
        if ((double)e.NewValue <= 0)
        {
            element.ClearValue(UIElement.ClipProperty);
            return;
        }

        element.SizeChanged += OnSizeChanged;
        Apply(element);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        var radius = GetRadius(element);
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return;

        var clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius);
        clip.Freeze();
        element.Clip = clip;
    }
}
