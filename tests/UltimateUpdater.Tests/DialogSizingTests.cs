using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="DialogSizing"/> fixes a real window ending up roughly one caption's height taller than its
/// own content whenever ExtendsContentIntoTitleBar is combined with SizeToContent, seen as a big empty
/// band under the button row on the uninstall warning and many other dialogs. The live-measurement half
/// needs a real, shown window and can only be proven on the VM (the offscreen render harness cannot
/// reproduce this class of bug at all); this covers the pure clamping arithmetic on its own, so at least
/// that part has a red-before-green test.
/// </summary>
public class DialogSizingTests
{
    [Fact]
    public void Keeps_the_desired_height_when_it_is_within_bounds()
    {
        var result = DialogSizing.ComputeFittedHeight(desiredHeight: 300, minHeight: 0, maxHeight: double.PositiveInfinity);

        Assert.Equal(300, result);
    }

    [Fact]
    public void Never_goes_below_the_windows_own_MinHeight()
    {
        var result = DialogSizing.ComputeFittedHeight(desiredHeight: 50, minHeight: 200, maxHeight: double.PositiveInfinity);

        Assert.Equal(200, result);
    }

    [Fact]
    public void Never_goes_above_the_windows_own_MaxHeight()
    {
        var result = DialogSizing.ComputeFittedHeight(desiredHeight: 900, minHeight: 0, maxHeight: 640);

        Assert.Equal(640, result);
    }

    [Fact]
    public void MaxHeight_of_NaN_means_no_ceiling()
    {
        var result = DialogSizing.ComputeFittedHeight(desiredHeight: 900, minHeight: 0, maxHeight: double.NaN);

        Assert.Equal(900, result);
    }

    /// <summary>
    /// A finite MaxHeight bounds the re-measure the same way it bounds the final result: a virtualizing
    /// list measured with an unbounded height must realize every item to answer it, which is what turned a
    /// 500-row history window into seconds of lag on every scroll-driven re-fit.
    /// </summary>
    [Fact]
    public void A_finite_MaxHeight_bounds_the_measure_height()
    {
        var result = DialogSizing.ComputeMeasureHeight(maxHeight: 640);

        Assert.Equal(640, result);
    }

    [Fact]
    public void An_infinite_MaxHeight_leaves_the_measure_height_unbounded()
    {
        var result = DialogSizing.ComputeMeasureHeight(maxHeight: double.PositiveInfinity);

        Assert.Equal(double.PositiveInfinity, result);
    }

    [Fact]
    public void MaxHeight_of_NaN_leaves_the_measure_height_unbounded()
    {
        var result = DialogSizing.ComputeMeasureHeight(maxHeight: double.NaN);

        Assert.Equal(double.PositiveInfinity, result);
    }

    /// <summary>1366x768 laptop: the work area is about 728 px high, the cleanup window asks for 880.</summary>
    [Fact]
    public void A_window_taller_than_the_work_area_is_cut_to_the_work_area_height()
    {
        var workArea = new System.Windows.Rect(0, 0, 1366, 728);
        var centred = new System.Windows.Rect(193, -76, 980, 880);

        var result = DialogSizing.ClampToWorkArea(centred, workArea);

        Assert.Equal(728, result.Height);
        Assert.Equal(980, result.Width);
        Assert.Equal(0, result.Top);
        Assert.Equal(193, result.Left);
    }

    [Fact]
    public void A_window_that_fits_the_work_area_is_left_alone()
    {
        var workArea = new System.Windows.Rect(0, 0, 1920, 1040);
        var bounds = new System.Windows.Rect(460, 80, 1000, 880);

        Assert.Equal(bounds, DialogSizing.ClampToWorkArea(bounds, workArea));
    }

    /// <summary>A second monitor to the left of the primary has negative coordinates; the clamp must use them.</summary>
    [Fact]
    public void A_window_hanging_off_the_bottom_of_a_secondary_work_area_is_slid_back_up()
    {
        var workArea = new System.Windows.Rect(-1280, 0, 1280, 984);
        var bounds = new System.Windows.Rect(-1100, 300, 800, 720);

        var result = DialogSizing.ClampToWorkArea(bounds, workArea);

        Assert.Equal(264, result.Top);
        Assert.Equal(720, result.Height);
        Assert.True(result.Bottom <= workArea.Bottom);
        Assert.Equal(-1100, result.Left);
    }
}
