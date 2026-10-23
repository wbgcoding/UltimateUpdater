using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;
using Wpf.Ui.Appearance;

namespace UltimateUpdater.Tests;

/// <summary>
/// The cleanup ring's motion. A rendered snapshot cannot prove that something MOVES, so what is checked
/// here is everything about it that IS provable: that every part really is animated (a storyboard target
/// WPF quietly ignores is the classic way half an animation ends up standing still), that stopping puts
/// every one of those properties back on its own value instead of freezing it mid-stroke, that holding the
/// run holds the clock, and that the pace of the work reaches the clock as a speed ratio. How it looks is
/// a design decision, not something a test can arbitrate.
///
/// Nothing composites an unshown window on a test machine, so WPF's clock never ticks here by itself:
/// these tests seek the storyboard (which applies immediately) and drive the time manager by hand.
/// </summary>
[Collection("wpf")]
public class CleanupRingAnimationTests : IDisposable
{
    /// <summary>The sweep only runs with Windows animations on, which a build server often has off.</summary>
    public CleanupRingAnimationTests() => MotionPreference.AnimationsEnabledForTest = true;

    public void Dispose() => MotionPreference.AnimationsEnabledForTest = null;

    /// <summary>Everything the sweep animates. A part added to it and forgotten here would be free to keep
    /// its animated value after a stop, and nothing would notice.</summary>
    private static (DependencyObject Target, DependencyProperty Property, string Name)[] SweptProperties(CleanupWindow window) =>
    [
        (window.LightSlide, TranslateTransform.XProperty, "LightSlide.X"),
        (window.SurfaceBreathe, ScaleTransform.ScaleXProperty, "SurfaceBreathe.ScaleX"),
        (window.SurfaceBreathe, ScaleTransform.ScaleYProperty, "SurfaceBreathe.ScaleY"),
        (window.Glint1, UIElement.OpacityProperty, "Glint1.Opacity"),
        (window.Glint2, UIElement.OpacityProperty, "Glint2.Opacity"),
        (window.Glint3, UIElement.OpacityProperty, "Glint3.Opacity"),
        (window.Glint1Scale, ScaleTransform.ScaleXProperty, "Glint1Scale.ScaleX"),
        (window.Glint1Scale, ScaleTransform.ScaleYProperty, "Glint1Scale.ScaleY"),
        (window.Glint2Scale, ScaleTransform.ScaleXProperty, "Glint2Scale.ScaleX"),
        (window.Glint2Scale, ScaleTransform.ScaleYProperty, "Glint2Scale.ScaleY"),
        (window.Glint3Scale, ScaleTransform.ScaleXProperty, "Glint3Scale.ScaleX"),
        (window.Glint3Scale, ScaleTransform.ScaleYProperty, "Glint3Scale.ScaleY"),
    ];

    /// <summary>What the closing flourish animates on top of that. No opacity pulse on the ring any more
    /// (22.08.2026), only the freed figure counts up.</summary>
    private static (DependencyObject Target, DependencyProperty Property, string Name)[] FinishProperties(CleanupWindow window) =>
    [
        (window, CleanupWindow.DisplayFreedProperty, "DisplayFreed"),
    ];

    /// <summary>
    /// Every part of the sweep actually moves. WPF accepts a storyboard aimed straight at a transform and
    /// then never applies it, without any error at all, the only way to catch that is to look at the
    /// values, which is what this does across a whole stroke.
    /// </summary>
    [Fact]
    public void Every_part_of_the_sweep_really_moves()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, _) = CreateCleaningWindow();

            Call(window, "StartSweep");
            var sweep = Field<Storyboard>(window, "_sweep")!;

            var moved = new HashSet<string>();
            foreach (var fraction in new[] { 0.1, 0.25, 0.4, 0.55, 0.7, 0.85, 1.0, 1.2 })
            {
                sweep.SeekAlignedToLastTick(
                    window, TimeSpan.FromMilliseconds(2600 * fraction), TimeSeekOrigin.BeginTime);

                foreach (var (target, property, name) in SweptProperties(window))
                {
                    if (!Equals(target.GetValue(property), BaseValue(target, property)))
                        moved.Add(name);
                }
            }

            var stuck = SweptProperties(window).Select(p => p.Name).Where(name => !moved.Contains(name)).ToList();
            Assert.True(stuck.Count == 0, $"never moved during a whole stroke: {string.Join(", ", stuck)}");

            Call(window, "StopSweep");
            Tick();
        });
    }

    [Fact]
    public void Stopping_the_sweep_leaves_no_animated_value_behind()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, _) = CreateCleaningWindow();

            Call(window, "StartSweep");
            var sweep = Field<Storyboard>(window, "_sweep")!;

            // Mid-pass, so there is genuinely something to leave behind: the light is at the far end of
            // its travel here, nowhere near the value it rests on.
            sweep.SeekAlignedToLastTick(window, TimeSpan.FromMilliseconds(650), TimeSeekOrigin.BeginTime);
            Assert.NotEqual(
                window.LightSlide.GetAnimationBaseValue(TranslateTransform.XProperty), window.LightSlide.X);

            Call(window, "StopSweep");
            Tick();

            AssertNothingAnimated(window, SweptProperties(window));
            AssertNothingAnimated(window, FinishProperties(window));
        });
    }

    [Fact]
    public void A_held_run_stops_the_clock_and_carrying_on_starts_it_again()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, viewModel) = CreateCleaningWindow();

            Call(window, "StartSweep");
            var sweep = Field<Storyboard>(window, "_sweep")!;
            Tick();
            Thread.Sleep(60);
            Tick();

            viewModel.IsPaused = true;

            // The clock's own reading is not the proof, it settles by a millisecond or two as the pause
            // takes hold. What matters is that it stops carrying the animation forward.
            Assert.True(TickUntil(() => sweep.GetCurrentGlobalSpeed(window) == 0), "the sweep kept running");
            var held = Values(window);

            Thread.Sleep(60);
            Tick();
            Assert.Equal(held, Values(window));

            viewModel.IsPaused = false;
            Assert.True(
                TickUntil(() => (sweep.GetCurrentGlobalSpeed(window) ?? 0) > 0),
                "the sweep's clock did not start again");

            Thread.Sleep(60);
            Tick();
            Assert.NotEqual(held, Values(window));

            Call(window, "StopSweep");
            Tick();
        });
    }

    /// <summary>
    /// The pace of the work reaches the animation as a speed ratio on the clock that is already running.
    /// Rebuilding the storyboard for a new tempo would jerk the broom back to the start of its stroke.
    /// </summary>
    [Fact]
    public void The_sweep_takes_its_speed_from_the_runs_own_pace()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, viewModel) = CreateCleaningWindow();

            Call(window, "StartSweep");
            var sweep = Field<Storyboard>(window, "_sweep")!;
            Tick();

            viewModel.Activity = 1;
            Assert.True(
                TickUntil(() => Math.Abs((sweep.GetCurrentGlobalSpeed(window) ?? 0) - 1.5) < 0.001),
                $"a busy run left the sweep at {sweep.GetCurrentGlobalSpeed(window)}");
            Assert.Same(sweep, Field<Storyboard>(window, "_sweep"));

            viewModel.Activity = 0;
            Assert.True(
                TickUntil(() => Math.Abs((sweep.GetCurrentGlobalSpeed(window) ?? 0) - 0.75) < 0.001),
                $"a stalled run left the sweep at {sweep.GetCurrentGlobalSpeed(window)}");
            Assert.Same(sweep, Field<Storyboard>(window, "_sweep"));

            Call(window, "StopSweep");
            Tick();
        });
    }

    /// <summary>
    /// The run ending is what ends the sweep and plays the one closing flourish, not the dialog being
    /// closed on it, which a window re-opened onto a running cleanup would never see.
    /// </summary>
    [Fact]
    public void The_end_of_the_run_ends_the_sweep_and_plays_the_closing_flourish()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, viewModel) = CreateCleaningWindow();

            Call(window, "StartSweep");
            viewModel.FreedBytes = 1_234_567;
            viewModel.Phase = CleanupPhase.Finished;

            Assert.Null(Field<Storyboard>(window, "_sweep"));
            var finish = Field<Storyboard>(window, "_finish");
            Assert.NotNull(finish);

            // Part way through: the figure is on its way up rather than already at the result.
            finish.SeekAlignedToLastTick(window, TimeSpan.FromMilliseconds(200), TimeSeekOrigin.BeginTime);
            Assert.InRange(window.DisplayFreed, 1, 1_234_566);

            // It also has to survive the handler that awaited the run stopping the sweep a second time,
            // which happens a breath after the phase change that started it.
            Call(window, "StopSweep");
            Assert.NotNull(Field<Storyboard>(window, "_finish"));

            // Closing the dialog takes it off, and everything it touched is back on its own value, the
            // freed figure ending on the run's result.
            Call(window, "StopFinish");
            Tick();

            Assert.Null(Field<Storyboard>(window, "_finish"));
            AssertNothingAnimated(window, SweptProperties(window));
            AssertNothingAnimated(window, FinishProperties(window));
            Assert.Equal(1_234_567d, window.DisplayFreed);
            Assert.Equal(CleanupViewModel.FormatFreed(1_234_567), window.FreedNumber.Text);
        });
    }

    /// <summary>
    /// A run can genuinely free nothing, a repair-only run ALWAYS does (repair reports 0 freed bytes), and
    /// so does a run stopped at once or one whose targets were already empty. The figure is written from a
    /// DependencyProperty callback and that property already holds 0, so nothing was raised and the 38px
    /// centre figure stayed at its empty XAML default. "+0 B" is a result; a blank ring is a bug.
    /// </summary>
    [Fact]
    public void A_run_that_freed_nothing_still_shows_a_figure_rather_than_an_empty_ring()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, viewModel) = CreateCleaningWindow();

            // Opened onto a finished run that freed nothing: the starting value alone must already show.
            Assert.Equal(CleanupViewModel.FormatFreed(0), window.FreedNumber.Text);

            Call(window, "StartSweep");
            viewModel.FreedBytes = 0;
            viewModel.Phase = CleanupPhase.Finished;

            Call(window, "StopFinish");
            Tick();

            Assert.Equal(CleanupViewModel.FormatFreed(0), window.FreedNumber.Text);
            Assert.False(string.IsNullOrEmpty(window.FreedNumber.Text));
        });
    }

    /// <summary>A held run holds the closing flourish too, the same way it holds the sweep.</summary>
    [Fact]
    public void The_closing_flourish_is_held_with_the_run()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, viewModel) = CreateCleaningWindow();

            Call(window, "StartSweep");
            viewModel.FreedBytes = 900_000;
            viewModel.Phase = CleanupPhase.Finished;
            var finish = Field<Storyboard>(window, "_finish")!;
            Tick();
            Thread.Sleep(60);
            Tick();

            viewModel.IsPaused = true;

            Assert.True(TickUntil(() => finish.GetIsPaused(window)), "the closing flourish kept running");
            var held = window.DisplayFreed;

            Thread.Sleep(60);
            Tick();
            Assert.Equal(held, window.DisplayFreed);

            viewModel.IsPaused = false;
            Tick();

            Call(window, "StopSweep");
            Call(window, "StopFinish");
            Tick();
        });
    }

    /// <summary>Hard limit ("ruhig + hochwertig", calm and high-quality): nothing here ever rotates, the
    /// ring shimmer and the head-dot pulse that used to are both gone, and the replacement must never grow
    /// one back by accident.</summary>
    [Fact]
    public void No_animation_ever_rotates_anything()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, _) = CreateCleaningWindow();

            Call(window, "StartSweep");
            var sweep = Field<Storyboard>(window, "_sweep")!;

            foreach (var child in sweep.Children)
            {
                var path = Storyboard.GetTargetProperty(child)?.Path ?? string.Empty;
                Assert.DoesNotContain("Angle", path, StringComparison.Ordinal);
                Assert.IsNotType<RotateTransform>(Storyboard.GetTarget(child));
            }

            Call(window, "StopSweep");
            Tick();
        });
    }

    /// <summary>Hard limits: at least a 2.4 s period, at most 0.6 peak opacity, at most
    /// 1.06 peak scale, ambient motion, not something meant to be watched frame by frame.</summary>
    [Fact]
    public void The_ambient_motion_never_exceeds_its_own_hard_limits()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, _) = CreateCleaningWindow();

            var periodMs = (int)typeof(CleanupWindow)
                .GetField("AmbientPeriodMs", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Assert.True(periodMs >= 2400, $"the ambient period is {periodMs} ms, below the agreed 2.4 s floor");

            Call(window, "StartSweep");
            var sweep = Field<Storyboard>(window, "_sweep")!;

            foreach (var child in sweep.Children)
            {
                var path = Storyboard.GetTargetProperty(child)?.Path ?? string.Empty;
                var isOpacity = path.EndsWith("Opacity", StringComparison.Ordinal);
                var isScale = path.Contains("Scale", StringComparison.Ordinal);

                double peak = child switch
                {
                    DoubleAnimation da => Math.Max(Math.Abs(da.From ?? 0), Math.Abs(da.To ?? 0)),
                    DoubleAnimationUsingKeyFrames kf => kf.KeyFrames.Cast<DoubleKeyFrame>().Max(k => Math.Abs(k.Value)),
                    _ => 0,
                };

                if (isOpacity)
                    Assert.True(peak <= 0.6, $"{path} peaks at opacity {peak}");
                if (isScale)
                    Assert.True(peak <= 1.06, $"{path} scales to {peak}");
            }

            Call(window, "StopSweep");
            Tick();
        });
    }

    /// <summary>
    /// The glints' peak opacity and peak scale both rise with the run's own progress: quiet near the start,
    /// at their full amplitude once the run is nearly done - never flattened onto one shared ramp, and
    /// never past the hard ceilings the ambient-motion test above already pins.
    /// </summary>
    [Fact]
    public void Glint_peak_amplitude_scales_with_the_runs_progress()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, viewModel) = CreateCleaningWindow();

            viewModel.CleanPercent = 0;
            Call(window, "StartSweep");
            var sweepAtZero = Field<Storyboard>(window, "_sweep")!;
            var opacityAtZero = PeakGlintOpacities(window, sweepAtZero);
            var scaleAtZero = PeakGlintScales(window, sweepAtZero);
            Call(window, "StopSweep");
            Tick();

            viewModel.CleanPercent = 100;
            Call(window, "StartSweep");
            var sweepAtFull = Field<Storyboard>(window, "_sweep")!;
            var opacityAtFull = PeakGlintOpacities(window, sweepAtFull);
            var scaleAtFull = PeakGlintScales(window, sweepAtFull);

            Assert.Equal(3, opacityAtZero.Length);
            Assert.All(opacityAtZero, o => Assert.True(o <= 0.3, $"peak opacity at 0% is {o}, expected <= 0.3"));
            Assert.All(opacityAtFull, o => Assert.True(o <= 0.55, $"peak opacity at 100% is {o}, expected <= 0.55"));
            Assert.True(
                opacityAtFull.Max() > opacityAtZero.Max(),
                $"peak opacity at 100% ({opacityAtFull.Max()}) is not greater than at 0% ({opacityAtZero.Max()})");

            // Never flattened onto one shared ramp: the three glints' own ceilings (1.06/1.04/1.05) already
            // differ, so "still differ" means more than one distinct value survives the progress scaling,
            // not that all three are pairwise unique.
            Assert.True(scaleAtFull.Distinct().Count() > 1, "every glint scales to the same peak - flattened onto one ramp");

            // Every scale peak stays under the 1.06 hard ceiling even at 100%, where the full ceiling is reached.
            Assert.All(scaleAtFull, s => Assert.True(s <= 1.06, $"peak scale at 100% is {s}, expected <= 1.06"));
            Assert.All(scaleAtZero, s => Assert.True(s <= 1.06, $"peak scale at 0% is {s}, expected <= 1.06"));

            Call(window, "StopSweep");
            Tick();
        });
    }

    /// <summary>
    /// Progress crossing into a new 20%-wide band rebuilds the sweep (WPF seals key-frame values once a
    /// storyboard has begun, so the running one cannot simply be nudged) - proven by the sweep's identity
    /// itself changing, not by its amplitude, since a same-band change must NOT rebuild anything.
    /// </summary>
    [Fact]
    public void Progress_crossing_a_band_rebuilds_the_sweep_but_staying_inside_one_does_not()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, viewModel) = CreateCleaningWindow();

            viewModel.CleanPercent = 5;
            Call(window, "StartSweep");
            var first = Field<Storyboard>(window, "_sweep");

            // Still inside the same 0-19 band: no rebuild.
            viewModel.CleanPercent = 12;
            Assert.Same(first, Field<Storyboard>(window, "_sweep"));

            // Crosses into the 20-39 band: rebuilt.
            viewModel.CleanPercent = 25;
            var second = Field<Storyboard>(window, "_sweep");
            Assert.NotSame(first, second);
            Assert.NotNull(second);

            Call(window, "StopSweep");
            Tick();
        });
    }

    /// <summary>
    /// The platter ring and the actuator arm are drawn once, in their own colours
    /// (AccentFillColorDefaultBrush / TextFillColorSecondaryBrush against the body's
    /// CardBackgroundFillColorDefaultBrush), never masked or never animated, unlike the body and the travelling
    /// light, that alone proves nothing about how the two read against the body once actually painted. A
    /// resource-key comparison would have missed the exact bug <see cref="PressedPixelTests"/> exists to
    /// catch elsewhere: two different keys can still resolve to visually near-identical colours. This
    /// renders the real pixels and samples the three parts by their known design coordinates (mapped
    /// through the 24-unit design box, its x3 Viewbox scale, and the 14px centring offset inside the 100px
    /// scene) rather than trusting anything about where WPF laid the window out.
    /// </summary>
    [Theory]
    [InlineData(ApplicationTheme.Dark)]
    [InlineData(ApplicationTheme.Light)]
    public void The_platter_ring_and_the_arm_read_distinct_from_the_body_in_both_themes(ApplicationTheme theme)
    {
        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(theme);

            var (window, _) = CreateCleaningWindow();
            window.DisplayPercent = 40;

            // The travelling light is a separate, deliberately unproven concern here (its rest position sits
            // at the parked end of its own translate range), turning it off keeps this probe about the
            // three static parts only.
            window.SurfaceLight.Opacity = 0;

            // Laid out and rendered the same way WindowLayoutTests proves the cleaning state itself (the
            // window is never shown, so this stands in for Loaded); kept attached to the window rather than
            // detached into a throwaway host, so TranslatePoint below still reads real, laid-out coordinates.
            var root = (FrameworkElement)window.Content;
            var width = (int)window.Width;
            var height = (int)window.Height;
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);

            // Design-space points (0-24 box) mapped to CenterScene-local pixels via x3 (Viewbox 72/24) plus
            // the 14px offset that centres the 72px Viewbox inside the 100px CenterScene, then to window
            // pixels via CenterScene's own laid-out position - never assumed, always read back.
            var origin = window.CenterScene.TranslatePoint(new Point(0, 0), root);
            var bodyOnly = PixelAt(bitmap, InWindow(origin, 4.5, 3));      // corner of the body, clear of the ring and the arm
            var ring = PixelAt(bitmap, InWindow(origin, 16.25, 11.2));    // midway between the ring's outer and inner radius
            var arm = PixelAt(bitmap, InWindow(origin, 15.25, 15.1));     // midway along the tapered arm

            Assert.True(
                ColourDistance(bodyOnly, ring) > 24,
                $"{theme}: the platter ring {ring} is not distinguishable from the body {bodyOnly}.");
            Assert.True(
                ColourDistance(bodyOnly, arm) > 24,
                $"{theme}: the actuator arm {arm} is not distinguishable from the body {bodyOnly}.");
        });
    }

    private static (int X, int Y) InWindow(Point sceneOrigin, double designX, double designY) => (
        (int)Math.Round(sceneOrigin.X + (designX * 3) + 14),
        (int)Math.Round(sceneOrigin.Y + (designY * 3) + 14));

    private static (byte R, byte G, byte B, byte A) PixelAt(RenderTargetBitmap bitmap, (int X, int Y) point)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var offset = (point.Y * stride) + (point.X * 4);
        // Pbgra32.
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset], pixels[offset + 3]);
    }

    private static double ColourDistance((byte R, byte G, byte B, byte A) a, (byte R, byte G, byte B, byte A) b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    /// <summary>Every glint's own fade (opacity) animation's peak value, in <see cref="GlintElements"/>
    /// order. The fade is built from the DependencyProperty overload of <see cref="PropertyPath"/>
    /// (<c>AddGlint</c>'s call for <see cref="System.Windows.UIElement.OpacityProperty"/>), whose own
    /// <see cref="PropertyPath.Path"/> reads back as the literal string "(0)", never the word "Opacity" -
    /// unlike the pop (scale) animations below, built from the STRING overload instead.</summary>
    private static double[] PeakGlintOpacities(CleanupWindow window, Storyboard sweep) =>
        GlintElements(window)
            .Select(glint => sweep.Children
                .OfType<DoubleAnimationUsingKeyFrames>()
                .Where(child => ReferenceEquals(Storyboard.GetTarget(child), glint))
                .Where(child => Storyboard.GetTargetProperty(child)?.Path == "(0)")
                .Select(child => child.KeyFrames.Cast<DoubleKeyFrame>().Max(k => Math.Abs(k.Value)))
                .Single())
            .ToArray();

    /// <summary>Every glint's own pop (scale) animation's peak value (ScaleX and ScaleY always carry the
    /// same peak, so either one stands for the glint).</summary>
    private static double[] PeakGlintScales(CleanupWindow window, Storyboard sweep) =>
        GlintElements(window)
            .Select(glint => sweep.Children
                .OfType<DoubleAnimationUsingKeyFrames>()
                .Where(child => ReferenceEquals(Storyboard.GetTarget(child), glint))
                .Where(child => (Storyboard.GetTargetProperty(child)?.Path ?? "").Contains("Scale", StringComparison.Ordinal))
                .Select(child => child.KeyFrames.Cast<DoubleKeyFrame>().Max(k => Math.Abs(k.Value)))
                .First())
            .ToArray();

    private static UIElement[] GlintElements(CleanupWindow window) =>
        [window.Glint1, window.Glint2, window.Glint3];

    /// <summary>The ring itself never became part of the calm redesign, its fill still has to track
    /// <see cref="CleanupWindow.DisplayPercent"/> exactly the way it always did.</summary>
    [Fact]
    public void The_progress_geometry_still_follows_DisplayPercent()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, _) = CreateCleaningWindow();

            window.DisplayPercent = 40;
            var at40 = window.RingFill.Data!.ToString();

            window.DisplayPercent = 75;
            var at75 = window.RingFill.Data!.ToString();

            Assert.NotEqual(at40, at75);
            Assert.Equal(PercentToArcConverter.Arc(75, 90).ToString(), at75);
        });
    }

    /// <summary>
    /// Before this fix, the window's <c>Loaded</c> handler awaited the catalogue and then unconditionally
    /// started a scan (or the sweep), with no check on whether the window itself had closed while that
    /// await was still pending. A user who closes the dialog while the catalogue is still loading must
    /// get neither: no scan started for a window nobody can see any more (which could also mean a delayed
    /// UAC prompt), and no sweep animation kicked off on a closed window. <c>OnCatalogReady</c> is the
    /// method the real <c>Loaded</c> handler calls once its one await completes; driven directly here so
    /// the guard is proven without racing a real background catalogue build.
    /// </summary>
    [Fact]
    public void A_window_closed_before_the_catalogue_is_ready_never_starts_a_scan_afterwards()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, viewModel) = CreateCleaningWindow();
            viewModel.Phase = CleanupPhase.Choosing;
            Assert.False(viewModel.IsScanning);

            SetField(window, "_closed", true);
            Call(window, "OnCatalogReady");

            Assert.False(viewModel.IsScanning);
            Assert.Null(Field<Storyboard>(window, "_sweep"));
        });
    }

    /// <summary>
    /// The sweep (and the closing flourish, if one is running) is ambient motion nobody can see while the
    /// dialog is minimised or hidden behind another window; before this fix it kept running at 30 fps
    /// forever regardless. <c>ApplySweepVisibility</c> is called directly rather than through a real
    /// <c>WindowState</c>/<c>IsVisible</c> change, which a window never <c>Show()</c>-n in a test cannot
    /// exercise faithfully.
    /// </summary>
    [Fact]
    public void Hiding_the_window_pauses_the_sweep_and_restoring_it_resumes()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, _) = CreateCleaningWindow();

            Call(window, "StartSweep");
            var sweep = Field<Storyboard>(window, "_sweep")!;
            Tick();
            Thread.Sleep(60);
            Tick();

            CallWithArg(window, "ApplySweepVisibility", false);
            Assert.True(TickUntil(() => sweep.GetCurrentGlobalSpeed(window) == 0), "the sweep kept running while hidden");
            var held = Values(window);

            Thread.Sleep(60);
            Tick();
            Assert.Equal(held, Values(window));

            CallWithArg(window, "ApplySweepVisibility", true);
            Assert.True(
                TickUntil(() => (sweep.GetCurrentGlobalSpeed(window) ?? 0) > 0),
                "restoring the window never resumed the sweep");

            Thread.Sleep(60);
            Tick();
            Assert.NotEqual(held, Values(window));

            Call(window, "StopSweep");
            Tick();
        });
    }

    /// <summary>
    /// Hiding and restoring a window whose run the user paused on purpose must never itself resume the
    /// sweep, that is the run's own pause to lift, not the window's visibility.
    /// </summary>
    [Fact]
    public void Restoring_a_window_never_resumes_a_sweep_the_run_itself_paused()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, viewModel) = CreateCleaningWindow();

            Call(window, "StartSweep");
            var sweep = Field<Storyboard>(window, "_sweep")!;
            Tick();
            Thread.Sleep(60);
            Tick();

            viewModel.IsPaused = true;
            Assert.True(TickUntil(() => sweep.GetCurrentGlobalSpeed(window) == 0), "the run's own pause never took hold");

            CallWithArg(window, "ApplySweepVisibility", false);
            CallWithArg(window, "ApplySweepVisibility", true);

            Thread.Sleep(60);
            Tick();
            Assert.Equal(0, sweep.GetCurrentGlobalSpeed(window));

            // Only now, the run's own resume, actually starts it again.
            viewModel.IsPaused = false;
            Assert.True(
                TickUntil(() => (sweep.GetCurrentGlobalSpeed(window) ?? 0) > 0), "the run's own resume never started the sweep");

            Call(window, "StopSweep");
            Tick();
        });
    }

    /// <summary>
    /// The reverse interaction: the run's own pause is lifted WHILE the window is still hidden. The
    /// hidden-pause must survive that (the window is still the reason nothing is animating), and only
    /// actually restoring the window resumes it, proving the two pause reasons do not fight each other.
    /// </summary>
    [Fact]
    public void The_runs_own_resume_never_restarts_a_sweep_still_hidden()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (window, viewModel) = CreateCleaningWindow();

            Call(window, "StartSweep");
            var sweep = Field<Storyboard>(window, "_sweep")!;
            Tick();
            Thread.Sleep(60);
            Tick();

            CallWithArg(window, "ApplySweepVisibility", false);
            Assert.True(TickUntil(() => sweep.GetCurrentGlobalSpeed(window) == 0), "hiding never paused the sweep");

            viewModel.IsPaused = true;
            viewModel.IsPaused = false;

            Thread.Sleep(60);
            Tick();
            Assert.Equal(0, sweep.GetCurrentGlobalSpeed(window));

            CallWithArg(window, "ApplySweepVisibility", true);
            Assert.True(
                TickUntil(() => (sweep.GetCurrentGlobalSpeed(window) ?? 0) > 0), "restoring the window never resumed the sweep");

            Call(window, "StopSweep");
            Tick();
        });
    }

    private static void CallWithArg<T>(CleanupWindow window, string method, T arg) =>
        typeof(CleanupWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [arg]);

    private static void SetField<T>(CleanupWindow window, string field, T value) =>
        typeof(CleanupWindow)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, value);

    /// <summary>Everything the sweep animates, as it stands right now.</summary>
    private static List<object?> Values(CleanupWindow window) =>
        SweptProperties(window).Select(p => (object?)p.Target.GetValue(p.Property)).ToList();

    private static void AssertNothingAnimated(
        CleanupWindow window, (DependencyObject Target, DependencyProperty Property, string Name)[] properties)
    {
        foreach (var (target, property, name) in properties)
        {
            Assert.True(
                Equals(target.GetValue(property), BaseValue(target, property)),
                $"{name} still carries an animated value: {target.GetValue(property)} instead of {BaseValue(target, property)}");
        }
    }

    /// <summary>The property's own value, ignoring any animation on top of it. Two different types declare
    /// it, the transforms are Freezables, the shapes and the window are elements.</summary>
    private static object? BaseValue(DependencyObject target, DependencyProperty property) => target switch
    {
        Animatable animatable => animatable.GetAnimationBaseValue(property),
        UIElement element => element.GetAnimationBaseValue(property),
        _ => target.GetValue(property),
    };

    /// <summary>
    /// Ticks until <paramref name="condition"/> holds, or gives up. Pausing, resuming and a new speed
    /// ratio are all pending operations WPF applies on a tick, and the animations here are capped at 60 fps
    ///, so a single tick can land inside the frame that has already been dealt with.
    /// </summary>
    private static bool TickUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            Tick();

            if (condition())
                return true;

            Thread.Sleep(20);
        }

        return false;
    }

    /// <summary>
    /// Drives WPF's own time manager one step. Nothing composites an unshown window on a test machine, so
    /// the render loop that would normally do this never runs, and without it a Pause or a Stop stays
    /// pending for ever and no test could tell a working one from a broken one.
    /// </summary>
    private static void Tick()
    {
        var mediaContextType = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext")
            ?? throw new InvalidOperationException("WPF's MediaContext is no longer where it was.");
        var context = mediaContextType
            .GetMethod("From", BindingFlags.NonPublic | BindingFlags.Static)
            ?.Invoke(null, [Dispatcher.CurrentDispatcher])
            ?? throw new InvalidOperationException("WPF's MediaContext could not be reached.");
        var timeManager = context.GetType()
            .GetField("_timeManager", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(context)
            ?? throw new InvalidOperationException("WPF's TimeManager could not be reached.");

        timeManager.GetType()
            .GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(timeManager, null);
    }

    private static (CleanupWindow Window, CleanupViewModel ViewModel) CreateCleaningWindow()
    {
        var logging = new LoggingService();
        var settings = new SettingsService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-ring-").FullName, "settings.json"));
        var history = new CleanupHistoryService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-ring-history-").FullName, "history.json"));

        var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings, history: history)
        {
            Phase = CleanupPhase.Cleaning,
        };
        WpfHarness.WaitForCleanupCatalog(viewModel);

        return (new CleanupWindow(viewModel), viewModel);
    }

    private static void Call(CleanupWindow window, string method) =>
        typeof(CleanupWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);

    private static T? Field<T>(CleanupWindow window, string field)
        where T : class =>
        typeof(CleanupWindow)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window) as T;
}
