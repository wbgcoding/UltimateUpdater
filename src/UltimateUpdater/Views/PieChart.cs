using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Views;

/// <summary>
/// A donut chart drawn from plain <see cref="Path"/> arcs, no chart package. One path per slice so each can
/// carry its own tooltip and accessible name, and be pulled out a few pixels while the pointer is over it.
/// </summary>
public sealed class PieChart : Canvas
{
    /// <summary>Radius of the empty middle.</summary>
    private const double HoleRadius = 28;

    /// <summary>How far a hovered slice moves outwards.</summary>
    private const double PullOut = 4;

    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(
        nameof(Slices), typeof(IEnumerable<IPieSlice>), typeof(PieChart),
        new PropertyMetadata(null, (d, _) => ((PieChart)d).Rebuild()));

    /// <summary>The fill of a slice that has no shade of its own (the lumped-together one); a theme brush set
    /// from XAML so it follows light and dark.</summary>
    public static readonly DependencyProperty NeutralFillProperty = DependencyProperty.Register(
        nameof(NeutralFill), typeof(Brush), typeof(PieChart),
        new PropertyMetadata(Brushes.Gray, (d, _) => ((PieChart)d).Rebuild()));

    public PieChart()
    {
        // The default 160 px; a caller may set another Width/Height in XAML before the slices arrive.
        Width = 160;
        Height = 160;
        Focusable = false;
    }

    public IEnumerable<IPieSlice>? Slices
    {
        get => (IEnumerable<IPieSlice>?)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public Brush NeutralFill
    {
        get => (Brush)GetValue(NeutralFillProperty);
        set => SetValue(NeutralFillProperty, value);
    }

    private void Rebuild()
    {
        Children.Clear();

        var slices = Slices?.Where(s => s.Fraction > 0).ToList();
        if (slices is not { Count: > 0 } || Width is not > 0 || Height is not > 0)
            return;

        var center = new Point(Width / 2, Height / 2);
        // Room left for the pull-out so a hovered slice is never clipped by the control's own bounds.
        var outer = Math.Min(Width, Height) / 2 - PullOut;
        if (outer <= HoleRadius)
            return;

        var total = slices.Sum(s => s.Fraction);
        var start = -90.0;
        foreach (var slice in slices)
        {
            var sweep = slice.Fraction / total * 360.0;
            var path = new Path
            {
                Data = SliceGeometry(center, outer, HoleRadius, start, sweep),
                Fill = slice.Shade ?? NeutralFill,
                // A hairline in the card colour keeps neighbouring slices apart.
                StrokeThickness = 1,
                ToolTip = slice.ToolTipText,
                RenderTransform = new TranslateTransform(),
            };
            path.SetResourceReference(Shape.StrokeProperty, "CardBackgroundFillColorSecondaryBrush");
            AutomationProperties.SetName(path, slice.ToolTipText);

            var middle = (start + sweep / 2) * Math.PI / 180.0;
            var dx = Math.Cos(middle) * PullOut;
            var dy = Math.Sin(middle) * PullOut;
            path.MouseEnter += (_, _) => Move(path, dx, dy);
            path.MouseLeave += (_, _) => Move(path, 0, 0);

            Children.Add(path);
            start += sweep;
        }
    }

    private static void Move(Path path, double x, double y)
    {
        var transform = (TranslateTransform)path.RenderTransform;
        transform.X = x;
        transform.Y = y;
    }

    /// <summary>One ring segment from <paramref name="startAngle"/> over <paramref name="sweep"/> degrees
    /// (0 = three o'clock, clockwise). A whole circle cannot be one arc, so it is a ring of two ellipses.</summary>
    internal static Geometry SliceGeometry(Point center, double outer, double inner, double startAngle, double sweep)
    {
        if (sweep >= 359.99)
        {
            return new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new EllipseGeometry(center, outer, outer),
                new EllipseGeometry(center, inner, inner));
        }

        Point At(double radius, double angle)
        {
            var radians = angle * Math.PI / 180.0;
            return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        }

        var end = startAngle + sweep;
        var large = sweep > 180.0;
        var figure = new PathFigure { StartPoint = At(outer, startAngle), IsClosed = true };
        figure.Segments.Add(new ArcSegment(At(outer, end), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(At(inner, end), true));
        figure.Segments.Add(new ArcSegment(At(inner, startAngle), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
