using System.Windows;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>
/// A saved window position that merely intersects the current virtual screen can still be almost
/// entirely off it (monitor removed/resized since the position was saved), <see cref="MainWindow.ClampToVisible"/>
/// pulls it fully back onto the screen instead of restoring it verbatim.
/// </summary>
public class WindowPlacementClampTests
{
    private static readonly Rect VirtualScreen = new(0, 0, 1920, 1080);

    [Fact]
    public void A_rect_fully_inside_the_screen_is_left_unchanged()
    {
        var saved = new Rect(100, 100, 800, 600);

        var result = MainWindow.ClampToVisible(saved, VirtualScreen);

        Assert.Equal(saved, result);
    }

    [Fact]
    public void A_rect_mostly_off_the_right_edge_is_slid_fully_onto_the_screen()
    {
        // Only 10px overlaps the virtual screen at all (a removed second monitor scenario), a plain
        // IntersectsWith check would have let this through unchanged, leaving the window almost entirely
        // unreachable.
        var saved = new Rect(1910, 100, 800, 600);

        var result = MainWindow.ClampToVisible(saved, VirtualScreen);

        Assert.True(VirtualScreen.Contains(result), "the clamped rect must lie fully within the virtual screen");
        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
    }

    [Fact]
    public void A_rect_off_the_top_left_is_slid_onto_the_screen()
    {
        var saved = new Rect(-750, -550, 800, 600);

        var result = MainWindow.ClampToVisible(saved, VirtualScreen);

        Assert.True(VirtualScreen.Contains(result));
    }

    [Fact]
    public void A_rect_wider_or_taller_than_the_screen_is_shrunk_to_fit()
    {
        var saved = new Rect(-200, -100, 2400, 1400);

        var result = MainWindow.ClampToVisible(saved, VirtualScreen);

        Assert.True(VirtualScreen.Contains(result));
        Assert.True(result.Width <= VirtualScreen.Width);
        Assert.True(result.Height <= VirtualScreen.Height);
    }

    [Fact]
    public void A_window_taller_than_the_work_area_is_fitted_below_the_top_edge()
    {
        var workArea = new Rect(0, 0, 1024, 728);
        var centred = new Rect(-68, -46, 1160, 820);

        var result = MainWindow.ClampToVisible(centred, workArea);

        Assert.True(result.Top >= workArea.Top);
        Assert.True(result.Bottom <= workArea.Bottom);
        Assert.True(result.Left >= workArea.Left);
    }

    // Two unequal monitors: a 1920x1080 primary with a taskbar, and a shorter 1280x1024 monitor to its right
    // whose own taskbar takes 40 px. The bounding box of both has dead corners no monitor covers; the
    // monitor handed to the resolver is the one the OS picks for the saved rect (nearest when it overlaps none).
    private static readonly MainWindow.MonitorArea PrimaryMonitor =
        new(new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040));

    private static readonly MainWindow.MonitorArea SecondMonitor =
        new(new Rect(1920, 0, 1280, 1024), new Rect(1920, 0, 1280, 984));

    [Fact]
    public void A_saved_rect_taller_than_the_shorter_monitor_is_fitted_to_that_monitors_work_area()
    {
        // Saved on a tall monitor that has since been replaced by the shorter one.
        var saved = new Rect(2000, 300, 1000, 1000);

        var (bounds, onMonitor) = MainWindow.ResolveRestoreBounds(saved, SecondMonitor, PrimaryMonitor, new Size(1160, 820));

        Assert.True(onMonitor);
        Assert.True(SecondMonitor.WorkArea.Contains(bounds), $"{bounds} must lie inside {SecondMonitor.WorkArea}");
    }

    [Fact]
    public void A_saved_rect_in_the_dead_corner_between_the_monitors_goes_to_the_primary_work_area()
    {
        // Inside the bounding box of both monitors, but below the shorter one and right of the primary.
        var saved = new Rect(2200, 1040, 800, 500);

        var (bounds, onMonitor) = MainWindow.ResolveRestoreBounds(saved, SecondMonitor, PrimaryMonitor, new Size(1160, 820));

        Assert.False(onMonitor);
        Assert.Equal(new Rect(380, 110, 1160, 820), bounds);
    }

    [Fact]
    public void A_rect_on_no_monitor_at_all_uses_the_primary_work_area_with_the_default_size_fitted()
    {
        var small = new MainWindow.MonitorArea(new Rect(0, 0, 1024, 768), new Rect(0, 0, 1024, 728));

        var (bounds, onMonitor) = MainWindow.ResolveRestoreBounds(
            new Rect(5000, 5000, 800, 600), small, small, new Size(1160, 820));

        Assert.False(onMonitor);
        Assert.True(small.WorkArea.Contains(bounds));
    }

    [Fact]
    public void A_missing_saved_rect_centres_the_default_size_on_the_primary_work_area()
    {
        var (bounds, onMonitor) = MainWindow.ResolveRestoreBounds(null, null, PrimaryMonitor, new Size(1160, 820));

        Assert.False(onMonitor);
        Assert.Equal(new Rect(380, 110, 1160, 820), bounds);
    }

    [Fact]
    public void A_rect_hanging_below_the_primary_taskbar_is_pulled_above_it()
    {
        var saved = new Rect(100, 600, 1000, 480);

        var (bounds, onMonitor) = MainWindow.ResolveRestoreBounds(saved, PrimaryMonitor, PrimaryMonitor, new Size(1160, 820));

        Assert.True(onMonitor);
        Assert.Equal(1040, bounds.Bottom);
    }
}
