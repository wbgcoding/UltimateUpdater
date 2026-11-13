using System.Globalization;
using System.Windows.Media;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>The cleanup ring: arc size, where its tip and the dot riding it sit, and the empty and full edge cases.</summary>
public class PercentToArcGeometryTests
{
    private const double Radius = 100;
    private const double Tolerance = 1e-9;

    [Fact]
    public void The_centre_is_the_radius_plus_half_the_stroke_allowance() =>
        Assert.Equal(108, PercentToArcConverter.Centre(100));

    [Theory]
    [InlineData(0, 0, -1)]      // twelve o'clock
    [InlineData(25, 1, 0)]      // three o'clock
    [InlineData(50, 0, 1)]      // six o'clock
    [InlineData(75, -1, 0)]     // nine o'clock
    public void The_arc_tip_sits_on_the_clock_position_of_the_percentage(double percent, double dx, double dy)
    {
        var tip = PercentToArcConverter.PointOn(percent, Radius);
        var centre = PercentToArcConverter.Centre(Radius);

        Assert.Equal(centre + dx * Radius, tip.X, Tolerance);
        Assert.Equal(centre + dy * Radius, tip.Y, Tolerance);
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(250, 100)]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(double.PositiveInfinity, 100)]
    public void A_percentage_outside_zero_to_hundred_is_clamped_for_the_tip(double raw, double clamped)
    {
        var tip = PercentToArcConverter.PointOn(raw, Radius);
        var expected = PercentToArcConverter.PointOn(clamped, Radius);

        Assert.Equal(expected.X, tip.X, Tolerance);
        Assert.Equal(expected.Y, tip.Y, Tolerance);
    }

    [Fact]
    public void The_tip_of_a_full_ring_is_back_at_the_start()
    {
        var start = PercentToArcConverter.PointOn(0, Radius);
        var end = PercentToArcConverter.PointOn(100, Radius);

        Assert.Equal(start.X, end.X, 1e-6);
        Assert.Equal(start.Y, end.Y, 1e-6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.79)]
    [InlineData(-10)]
    public void A_sliver_below_the_visible_threshold_is_drawn_as_an_empty_ring(double percent) =>
        Assert.True(PercentToArcConverter.Arc(percent, Radius).Bounds.IsEmpty);

    [Fact]
    public void The_smallest_visible_arc_is_drawn()
    {
        var arc = PercentToArcConverter.Arc(0.8, Radius);

        Assert.False(arc.Bounds.IsEmpty);
    }

    [Fact]
    public void A_half_ring_spans_the_full_height_and_only_the_right_half_of_the_width()
    {
        var bounds = PercentToArcConverter.Arc(50, Radius).Bounds;
        var centre = PercentToArcConverter.Centre(Radius);

        Assert.Equal(centre - Radius, bounds.Top, 0.5);
        Assert.Equal(centre + Radius, bounds.Bottom, 0.5);
        Assert.Equal(centre, bounds.Left, 0.5);
        Assert.Equal(centre + Radius, bounds.Right, 0.5);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public void A_full_or_larger_ring_is_the_whole_circle_and_frozen(double percent)
    {
        var arc = PercentToArcConverter.Arc(percent, Radius);
        var centre = PercentToArcConverter.Centre(Radius);

        Assert.True(arc.IsFrozen);
        Assert.Equal(centre - Radius, arc.Bounds.Left, 0.5);
        Assert.Equal(centre + Radius, arc.Bounds.Right, 0.5);
        Assert.Equal(centre - Radius, arc.Bounds.Top, 0.5);
        Assert.Equal(centre + Radius, arc.Bounds.Bottom, 0.5);
    }

    [Fact]
    public void A_larger_percentage_never_draws_a_shorter_arc_across_the_half_way_mark()
    {
        // the large-arc flag flips past 50%: the drawn extent must keep growing through it
        var before = PercentToArcConverter.Arc(49, Radius).Bounds;
        var after = PercentToArcConverter.Arc(51, Radius).Bounds;

        Assert.True(after.Width >= before.Width - 1e-6, $"width shrank {before.Width} -> {after.Width}");
        Assert.True(after.Height >= before.Height - 1e-6);
    }

    [Fact]
    public void The_binding_takes_a_double_or_an_int_and_treats_anything_else_as_empty()
    {
        var converter = new PercentToArcConverter();
        var culture = CultureInfo.InvariantCulture;

        var fromDouble = (Geometry)converter.Convert(50.0, typeof(Geometry), "100", culture);
        var fromInt = (Geometry)converter.Convert(50, typeof(Geometry), "100", culture);
        var fromText = (Geometry)converter.Convert("50", typeof(Geometry), "100", culture);
        var fromNull = (Geometry)converter.Convert(null, typeof(Geometry), "100", culture);

        Assert.Equal(fromDouble.Bounds, fromInt.Bounds);
        Assert.True(fromText.Bounds.IsEmpty);
        Assert.True(fromNull.Bounds.IsEmpty);
        Assert.False(fromDouble.Bounds.IsEmpty);
    }

    [Theory]
    [InlineData("100", 100)]
    [InlineData("42.5", 42.5)]
    [InlineData(null, 105)]
    [InlineData("not a number", 105)]
    [InlineData("", 105)]
    public void The_converter_parameter_is_the_radius_read_with_the_invariant_culture_and_defaults_to_105(string? parameter, double radius)
    {
        var converter = new PercentToArcConverter();
        // a culture whose decimal separator is a comma must not change how "42.5" is read
        var german = new CultureInfo("de-DE");

        var viaBinding = (Geometry)converter.Convert(60.0, typeof(Geometry), parameter, german);
        var direct = PercentToArcConverter.Arc(60, radius);

        Assert.Equal(direct.Bounds, viaBinding.Bounds);
    }

    [Fact]
    public void The_ring_cannot_be_converted_back() =>
        Assert.Throws<NotSupportedException>(() =>
            new PercentToArcConverter().ConvertBack(null, typeof(double), null, CultureInfo.InvariantCulture));
}
