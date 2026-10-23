using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace UltimateUpdater.Views;

/// <summary>
/// Turns a 0-100 percentage into the arc of the cleanup dialog's ring. Hand-drawn geometry rather than a
/// ProgressBar: a circular gauge has no WPF equivalent, and this project already avoids WPF-UI's
/// ProgressBar in measured containers because it recurses. ConverterParameter is the ring's radius in
/// device-independent pixels; the geometry is drawn in a box of (2*radius + stroke) so the stroke
/// is never clipped.
/// </summary>
public sealed class PercentToArcConverter : IValueConverter
{
    private const double StrokeAllowance = 14;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value switch
        {
            double d => d,
            int i => i,
            _ => 0d,
        };

        var radius = parameter is string text && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var r)
            ? r
            : 90d;

        return Arc(percent, radius);
    }

    /// <summary>
    /// The arc as geometry, callable without going through a binding: the cleanup window assigns it
    /// directly, because an ancestor binding cannot resolve once the render harness has detached the content
    /// from its window.
    /// </summary>
    public static Geometry Arc(double percent, double radius)
    {
        percent = Math.Clamp(percent, 0, 100);

        var centre = Centre(radius);
        var start = new System.Windows.Point(centre, centre - radius);   // twelve o'clock

        // A full circle cannot be drawn as one arc (start == end): two halves instead.
        if (percent >= 100)
        {
            var bottom = new System.Windows.Point(centre, centre + radius);
            var full = new StreamGeometry();
            using (var ctx = full.Open())
            {
                ctx.BeginFigure(start, isFilled: false, isClosed: false);
                ctx.ArcTo(bottom, new System.Windows.Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                ctx.ArcTo(start, new System.Windows.Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }

            full.Freeze();
            return full;
        }

        // A sliver of an arc renders as a dot or, with rounded caps, as a blob bigger than the arc itself .
        // which is what made a small figure look broken. Below the threshold the ring is
        // simply drawn empty; the number in the middle still states the real value.
        const double SmallestVisibleArc = 0.8;
        if (percent < SmallestVisibleArc)
        {
            var empty = new StreamGeometry();
            empty.Freeze();
            return empty;
        }

        var angle = percent / 100.0 * 2 * Math.PI;
        var end = new System.Windows.Point(
            centre + radius * Math.Sin(angle),
            centre - radius * Math.Cos(angle));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(
                end,
                new System.Windows.Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: percent > 50,
                SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// The centre of the box <see cref="Arc"/> draws in: the same point every angle here is measured
    /// from. Public because the ring's moving parts are positioned against it from outside.
    /// </summary>
    public static double Centre(double radius) => radius + StrokeAllowance / 2;

    /// <summary>
    /// Where the arc's leading tip sits, in the same box coordinates <see cref="Arc"/> uses: same centre,
    /// same twelve-o'clock start, same clockwise angle. Pure, so the dot that rides the arc's head can be
    /// placed by a transform instead of a second geometry.
    /// </summary>
    public static System.Windows.Point PointOn(double percent, double radius)
    {
        percent = Math.Clamp(percent, 0, 100);

        var centre = Centre(radius);
        var angle = percent / 100.0 * 2 * Math.PI;

        return new System.Windows.Point(
            centre + radius * Math.Sin(angle),
            centre - radius * Math.Cos(angle));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
