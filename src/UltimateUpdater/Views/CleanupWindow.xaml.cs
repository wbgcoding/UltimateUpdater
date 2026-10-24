using System.Windows;
using System.Windows.Media.Animation;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Views;

public partial class CleanupWindow
{
    private readonly CleanupViewModel _viewModel;
    private Storyboard? _sweep;

    /// <summary>Which 20%-wide progress band the glints' amplitudes were last built for. WPF seals a
    /// storyboard's key-frame VALUES once it has begun, so scaling the glints with progress cannot be done
    /// by nudging the running storyboard: the sweep is rebuilt from scratch whenever progress crosses into
    /// a new band, accepting the brief phase reset (invisible at these amplitudes, well under the ambient
    /// period). -1 so the very first StartSweep always counts as a new band.</summary>
    private int _glintBand = -1;

    /// <summary>The closing pulse and count-up. Its own storyboard because it is a one-shot that begins
    /// exactly where the sweep ends, so it cannot be a child of it. It is held and let go with the run just
    /// like the sweep, ends itself, and is taken off for good when the dialog closes.</summary>
    private Storyboard? _finish;

    /// <summary>
    /// Set the moment this window closes; checked right after the one <c>await</c> in the <c>Loaded</c>
    /// handler below, on the same UI thread, so a window closed while its catalogue was still loading never
    /// goes on to start (or re-trigger elevation for) a scan nobody can see, and never races a fresh window
    /// opened onto the same, reused view model.
    /// </summary>
    private bool _closed;

    /// <summary>
    /// What the ring actually draws. The view model's figure jumps whenever a box is ticked or a target
    /// finishes, and an arc that jumps looks broken, so this follows it on a short animation instead.
    /// </summary>
    public static readonly DependencyProperty DisplayPercentProperty = DependencyProperty.Register(
        nameof(DisplayPercent), typeof(double), typeof(CleanupWindow),
        new PropertyMetadata(0d, OnDisplayPercentChanged));

    /// <summary>The ring's radius, and the same number the track and the Path's box are built from.</summary>
    private const double RingRadius = 90;

    private static void OnDisplayPercentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not CleanupWindow window || e.NewValue is not double percent)
            return;

        window.DrawRing(percent);
    }

    /// <summary>
    /// The arc the ring draws. Called for the starting value as well, or the ring would show a bare track
    /// until the first change came along.
    /// </summary>
    private void DrawRing(double percent)
    {
        if (RingFill is { } ring)
            ring.Data = PercentToArcConverter.Arc(percent, RingRadius);
    }

    public double DisplayPercent
    {
        get => (double)GetValue(DisplayPercentProperty);
        set => SetValue(DisplayPercentProperty, value);
    }

    /// <summary>
    /// The freed figure the finished ring shows, in bytes. Its own property because the number counts up to
    /// the result over a moment instead of appearing at full size: an animation needs something animatable
    /// to run on, and the view model's own string is not that.
    /// </summary>
    public static readonly DependencyProperty DisplayFreedProperty = DependencyProperty.Register(
        nameof(DisplayFreed), typeof(double), typeof(CleanupWindow),
        new PropertyMetadata(0d, OnDisplayFreedChanged));

    private static void OnDisplayFreedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is CleanupWindow window && window.FreedNumber is { } text && e.NewValue is double bytes)
            text.Text = CleanupViewModel.FormatFreed((long)bytes);
    }

    public double DisplayFreed
    {
        get => (double)GetValue(DisplayFreedProperty);
        set => SetValue(DisplayFreedProperty, value);
    }

    /// <summary>
    /// Puts a figure on the ring. The text is written here as well as through the property's changed
    /// callback, because a DependencyProperty raises nothing when handed the value it already holds and
    /// this one defaults to 0: a run that freed NOTHING (a repair-only run always does, as does a run
    /// stopped at once or one where every target was already empty) would otherwise leave the empty XAML
    /// default instead of showing the real result.
    /// </summary>
    private void ShowFreed(long bytes)
    {
        DisplayFreed = bytes;

        if (FreedNumber is { } text)
            text.Text = CleanupViewModel.FormatFreed(bytes);
    }

    public CleanupWindow(CleanupViewModel viewModel)
    {
        InitializeComponent();
        WindowBackdropType = ThemeService.ActiveBackdrop;
        DataContext = viewModel;
        _viewModel = viewModel;
        DialogSizing.FitToWorkArea(this);

        DrawRing(DisplayPercent);

        // The result figure is written, not bound, so it needs its starting value here: a window opened
        // onto a run that is already over shows what it ended on rather than nothing at all. From then on
        // the run's own notifications keep it in step, and the closing count-up takes it over.
        ShowFreed(viewModel.FreedBytes);

        // The window itself shows at once (IsCatalogLoading gates the detail list's loading
        // state in XAML); the scan must still wait for the catalogue to actually exist before it can
        // measure anything.
        Loaded += async (_, _) =>
        {
            try
            {
                await viewModel.CatalogReadyTask;
            }
            catch (Exception ex)
            {
                // CleanupViewModel.LoadCatalogAsync already catches everything and never leaves this task
                // faulted; this is the second line of defence, because an async void continuation is the
                // one place an exception has nowhere else to go and takes the whole app down with it. A
                // future gap in that guard degrades to a logged, closed catalogue state instead of a crash.
                viewModel.Logging.Error("Cleanup: the catalogue task faulted unexpectedly.", ex);

                // A window already closed by the time this runs may be sitting on a view model a fresh
                // window reuses (see _closed's own remark below); that next window must not inherit an
                // error state from a run it never saw.
                if (!_closed)
                    viewModel.CatalogLoadFailed = true;

                return;
            }

            OnCatalogReady();
        };

        // The window's very first painted frame, catalogue load included; logged once.
        var firstFrameLogged = false;
        ContentRendered += (_, _) =>
        {
            if (firstFrameLogged)
                return;

            firstFrameLogged = true;
            viewModel.LogFirstFrame();
        };

        // Whether the per-entry marking in the open detail lists is worth doing at all. Minimized counts as
        // gone: IsVisible stays true for a minimized window, and a list nobody can look at is a list nobody
        // is watching walk. WindowClosed says the same thing for good.
        IsVisibleChanged += (_, _) => ReportVisibility();
        StateChanged += (_, _) => ReportVisibility();

        Closed += (_, _) =>
        {
            _closed = true;
            StopSweep();
            StopFinish();
            viewModel.WindowClosed();
        };

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        // Clicking the main window beside this one closes it. No veto while cleaning: closing puts the
        // run in the background rather than aborting it, so there is nothing for a stray click to break.
        DismissOnDeactivate.Attach(this);
        DialogKeyboard.CloseOnEscape(this);
    }

    /// <summary>
    /// Runs once the catalogue is ready, EXCEPT when the window has already closed by then
    /// (<see cref="_closed"/>): closing the dialog cancels nothing already cleaning, but starting a scan, or
    /// a scan's own elevation prompt, for a window nobody can see is not "nothing", and a fresh window
    /// opened onto the same, reused view model must never race this stale continuation. Split out of
    /// the <c>Loaded</c> handler so a test can drive it directly instead of racing a real background
    /// catalogue build.
    /// </summary>
    private void OnCatalogReady()
    {
        if (_closed)
            return;

        // The ring starts where the work already is. DisplayPercent begins at 0 and only ever GLIDES on
        // a change, so without this a window opened onto a run in progress would draw a bare track
        // until the next progress tick.
        DisplayPercent = _viewModel.RingPercent;

        // The scan starts once the catalogue is ready, so the figures fill in while the user reads the
        // list, but NOT when this window was re-opened onto work that is already going: re-measuring
        // would fight the deletion for the same files, and a second measuring pass would fight the
        // first one.
        if (_viewModel.IsCleaning)
            StartSweep();
        else if (_viewModel.IsPrescanRunning)
            _ = FinishAfterPrescanAsync();
        else if (!_viewModel.IsScanning)
            _ = _viewModel.StartOpenScanAsync();
    }

    /// <summary>The window opened while the startup prescan was still measuring: the prescan is left to
    /// finish (a second pass would fight it over the same folders), then the rest is measured as usual.</summary>
    private async Task FinishAfterPrescanAsync()
    {
        await _viewModel.WaitForPrescanAsync();

        // Closed meanwhile: nothing to measure for a window nobody can see (see OnCatalogReady).
        if (_closed || _viewModel.IsBusy)
            return;

        await _viewModel.StartOpenScanAsync();
    }

    private void ReportVisibility()
    {
        var visible = IsVisible && WindowState != WindowState.Minimized;
        _viewModel.IsWindowVisible = visible;
        ApplySweepVisibility(visible);
    }

    /// <summary>True while the sweep/finish storyboards are paused because the window itself is minimised
    /// or hidden behind another window, as distinct from the run's own pause (<see
    /// cref="CleanupViewModel.IsPaused"/>): kept apart so restoring the window never resumes an animation
    /// the user paused on purpose, and the run's own resume never restarts one nobody can even see yet.
    /// </summary>
    private bool _pausedForVisibility;

    /// <summary>
    /// The sweep is ambient motion nobody can see while this dialog is minimised or hidden; running it at
    /// 30 fps regardless costs battery and CPU for nothing. Pausing here never fights the run's own pause:
    /// hiding a window whose run is already paused changes nothing, and restoring one never resumes a run
    /// the user paused on purpose (checked again in <see cref="OnViewModelPropertyChanged"/>'s IsPaused
    /// branch, which likewise never resumes one still hidden). Reduce-motion is untouched by any of this.
    /// </summary>
    private void ApplySweepVisibility(bool visible)
    {
        if (visible)
        {
            if (!_pausedForVisibility)
                return;

            _pausedForVisibility = false;

            if (_viewModel.IsPaused)
                return;

            _sweep?.Resume(this);
            _finish?.Resume(this);
        }
        else
        {
            if ((_sweep is null && _finish is null) || _viewModel.IsPaused || _pausedForVisibility)
                return;

            _sweep?.Pause(this);
            _finish?.Pause(this);
            _pausedForVisibility = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // A held run holds its animation too: a light that keeps travelling while nothing is being deleted
        // says the opposite of what the dialog says.
        if (e.PropertyName == nameof(CleanupViewModel.IsPaused))
        {
            if (_viewModel.IsPaused)
            {
                _sweep?.Pause(this);
                _finish?.Pause(this);
            }
            else if (!_pausedForVisibility)
            {
                // Still minimised or hidden: ApplySweepVisibility resumes this pair itself once the window
                // is visible again, and must find the run's own pause already lifted rather than fighting it.
                _sweep?.Resume(this);
                _finish?.Resume(this);
            }

            return;
        }

        // The pace of the work, on the clock that is already running, never a rebuilt storyboard, which
        // would restart the stroke from the beginning every time the rate changed.
        if (e.PropertyName == nameof(CleanupViewModel.Activity))
        {
            ApplySweepSpeed();
            return;
        }

        // The run's own end, which is not the same event as this dialog being closed on it: a window
        // re-opened onto a run in progress never goes through the handler that started the sweep, so this
        // is what stops it and plays the closing flourish.
        if (e.PropertyName == nameof(CleanupViewModel.Phase))
        {
            if (!_viewModel.IsCleaning)
                StopSweep();

            return;
        }

        if (e.PropertyName is nameof(CleanupViewModel.FreedText) or "")
        {
            // The count-up owns the figure while it runs; otherwise it simply follows the run.
            if (_finish is null)
                ShowFreed(_viewModel.FreedBytes);
        }

        if (e.PropertyName is not (nameof(CleanupViewModel.RingPercent) or ""))
            return;

        var target = _viewModel.RingPercent;

        // The glints' amplitude tracks progress in fixed 20%-wide bands (see RestartSweepForCurrentBand)
        // rather than continuously, so this only rebuilds on an actual band crossing, never on every single
        // percentage-point tick. Only while a sweep is actually running: a window opened onto a phase with
        // nothing sweeping has no storyboard to rebuild.
        if (_sweep is not null && BandFor(target) != _glintBand)
            RestartSweepForCurrentBand();

        if (Math.Abs(target - DisplayPercent) < 0.01)
            return;

        // Short and eased: long enough to read as motion, short enough that a fast run does not lag
        // behind the numbers beside it. Capped at 60 fps like every other animation here.
        var glide = new DoubleAnimation(target, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Timeline.SetDesiredFrameRate(glide, 60);
        BeginAnimation(DisplayPercentProperty, glide);
    }

    /// <summary>
    /// The dialog moves by dragging its header, not just the few pixels of title bar.
    /// Guarded: DragMove throws if the button is already up by the time it runs.
    /// </summary>
    private void OnHeaderDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState != System.Windows.Input.MouseButtonState.Pressed)
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            _viewModel.Logging.Info("Cleanup dialog header drag: DragMove ignored, the button was already up.");
        }

        // The header band and the root both route here: without this, the same press bubbled on to the root
        // and started a second DragMove after the first had ended, which always threw.
        e.Handled = true;
    }

    /// <summary>
    /// The second confirmation. A cleanup NEVER runs without it: that is the whole safety model.
    /// </summary>
    private async void OnClean(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;

        var (headline, warning) = _viewModel.BuildConfirmationTexts();
        var confirmed = Dialogs.Confirm(
            headline,
            $"{_viewModel.BuildConfirmationDetail()}\n\n{warning}",
            confirmText: loc["CleanupStart"],
            destructive: true);

        if (!confirmed)
            return;

        // A rotational-disk optimisation can run for hours; said BEFORE it starts, beside the
        // existing red confirmation rather than folded into it, since it only ever applies to some runs.
        if (_viewModel.HasSelectedHddDefragTarget)
            Dialogs.Warn(loc["DefragWarningTitle"], loc["DefragWarningBody"]);

        StartSweep();
        try
        {
            // Deliberately not awaited through the window's lifetime: the run belongs to the view model, and
            // the view model outlives this window so the cleaning can carry on in the background.
            await _viewModel.RunCleanupAsync();
        }
        catch (Exception ex)
        {
            // An async void handler is the one place an exception has nowhere to go: it is raised on the
            // dispatcher and takes the process with it. The run itself already survives a single target
            // failing and logs that, so anything arriving here is unexpected and belongs in front of the
            // user rather than in a crash.
            Dialogs.Error(loc["CleanupFailedHeadline"], details: ex.Message);
        }
        finally
        {
            StopSweep();
        }
    }

    /// <summary>
    /// One full pass of the light, out and back. Everything in the animation repeats on exactly this
    /// period: a part with a period of its own drifts out of phase over a long cleanup. Never below
    /// 2.4 s: this is calm ambient motion, not something meant to be watched frame by frame.
    /// </summary>
    private const int AmbientPeriodMs = 2600;

    /// <summary>The glints' own rest, between pulses and in every static render alike -
    /// never 0, matched by the same literal value on each Glint Path in the XAML.</summary>
    private const double RestingGlintOpacity = 0.22;

    /// <summary>
    /// The calm cleaning animation: a light glides across the surface and back, the surface itself breathes
    /// very slightly, and two glints fade in and pop near where the light passes. Opacity, a Brush's own
    /// RelativeTransform and RenderTransforms only (nothing triggers a layout pass or rotates the ring),
    /// capped at 30 fps: ambient background motion, not something the eye tracks. One storyboard for all
    /// of it, so pausing, stopping and removing cover every part at once.
    /// </summary>
    private void StartSweep()
    {
        // ClientAreaAnimation is the system's "animations on" setting, the same gate the main window's
        // toolbar sweep uses. Also covers RestartSweepForCurrentBand, which rebuilds through here, and the
        // closing flourish, which only plays for a sweep that actually ran.
        if (_sweep is not null || !MotionPreference.AnimationsEnabled)
            return;

        var sweep = new Storyboard();
        Timeline.SetDesiredFrameRate(sweep, 30);

        // Half the period each way, auto-reversed: one full out-and-back per period. Addressed through
        // SurfaceLight (the element that owns the mask), never straight at LightSlide itself: a Storyboard
        // target set to a Freezable is accepted without complaint and then never applied.
        sweep.Children.Add(Motion(SurfaceLight, "OpacityMask.RelativeTransform.X", -0.75, 0.75, AmbientPeriodMs / 2));

        // A very slight breathe, well under the 1.06 ceiling, so the surface reads as alive without ever
        // reading as motion in its own right. Addressed through CenterScene, which owns the transform.
        sweep.Children.Add(Motion(CenterScene, "RenderTransform.ScaleX", 1.0, 1.05, AmbientPeriodMs / 2));
        sweep.Children.Add(Motion(CenterScene, "RenderTransform.ScaleY", 1.0, 1.05, AmbientPeriodMs / 2));

        // Timed to roughly where the light is on its own pass, so each glint reads as riding its leading
        // edge rather than blinking on a schedule of its own. Each one also runs its own period (0.95x to
        // 1.15x the ambient period, never below the 2.4s floor) and its own peak scale (under the 1.06
        // ceiling), so three glints never pulse in lockstep the way two on a shared schedule would.
        // Peak opacity and peak scale both scale with the run's own progress (see GlintPeakOpacityFor/
        // GlintPeakScaleFor): quiet near the start, at their full amplitude once the run is nearly done.
        var percent = _viewModel.RingPercent;
        var opacity = GlintPeakOpacityFor(percent);
        AddGlint(sweep, Glint1, delayMs: (int)(AmbientPeriodMs * 0.1), periodMs: AmbientPeriodMs, peakScale: GlintPeakScaleFor(percent, 1.06), peakOpacity: opacity);
        AddGlint(sweep, Glint2, delayMs: (int)(AmbientPeriodMs * 0.6), periodMs: (int)(AmbientPeriodMs * 1.15), peakScale: GlintPeakScaleFor(percent, 1.04), peakOpacity: opacity);
        AddGlint(sweep, Glint3, delayMs: (int)(AmbientPeriodMs * 0.3), periodMs: (int)(AmbientPeriodMs * 0.95), peakScale: GlintPeakScaleFor(percent, 1.05), peakOpacity: opacity);

        _glintBand = BandFor(percent);
        _sweep = sweep;
        _sweep.Begin(this, isControllable: true);
        ApplySweepSpeed();

        // Rebuilt (RestartSweepForCurrentBand) while the window is minimised or hidden: the fresh
        // storyboard begins running like any other, so it needs the same pause applied to the one it
        // replaced, or it would keep animating unseen until the next band crossing.
        if (_pausedForVisibility)
            _sweep.Pause(this);
    }

    /// <summary>Which 20%-wide progress band a percentage falls in (0-4): the unit the glints' amplitudes
    /// are rebuilt on crossing, rather than on every single percentage-point change.</summary>
    private static int BandFor(double percent) => Math.Min(4, (int)(Math.Clamp(percent, 0, 100) / 20));

    /// <summary>0.25 at 0%, rising to 0.55 at 100% (still under the 0.6 hard ceiling
    /// <see cref="CleanupRingAnimationTests"/> pins).</summary>
    private static double GlintPeakOpacityFor(double percent) => 0.25 + 0.30 * Math.Clamp(percent, 0, 100) / 100.0;

    /// <summary>Half of the glint's own excess over 1.0 at 0%, its full excess at 100% - so
    /// <paramref name="ceiling"/> (never above 1.06) is still the maximum anyone ever sees, only reached
    /// once the run is actually almost done.</summary>
    private static double GlintPeakScaleFor(double percent, double ceiling) =>
        1.0 + (ceiling - 1.0) * (0.5 + 0.5 * Math.Clamp(percent, 0, 100) / 100.0);

    /// <summary>
    /// Rebuilds the sweep from scratch so the glints' amplitudes reflect the new band, without going
    /// through <see cref="StopSweep"/> (which would treat this as the run actually ending and play the
    /// closing flourish). Stop and remove first, exactly like StopSweep does, so nothing is left animating
    /// mid-stroke before the fresh storyboard begins.
    /// </summary>
    private void RestartSweepForCurrentBand()
    {
        _sweep?.Stop(this);
        _sweep?.Remove(this);
        _sweep = null;
        StartSweep();
    }

    /// <summary>
    /// The motion runs at the pace of the work: a run deleting file after file looks busy, a repair step
    /// that has gone quiet slows down with it. A speed ratio on the clock that is already running, because
    /// rebuilding the storyboard would jerk the light back to the start of its pass on every change.
    /// </summary>
    private void ApplySweepSpeed() =>
        _sweep?.SetSpeedRatio(this, 0.75 + 0.75 * Math.Clamp(_viewModel.Activity, 0, 1));

    /// <summary>
    /// Every animation here addresses its transform through the ELEMENT that owns it. A Storyboard target
    /// set to a Freezable itself is accepted without complaint and then never applied: which is exactly
    /// the kind of failure that leaves a part of an animation silently standing still.
    /// </summary>
    private static DoubleAnimation Motion(
        System.Windows.DependencyObject target, string path, double from, double to, int durationMs)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(path));
        return animation;
    }

    /// <summary>
    /// One glint: it fades in, pops very slightly (never past the caller's own <paramref name="peakScale"/>,
    /// itself never past the 1.06 ceiling) and fades away again, all within its own period so it never
    /// slides out of phase with the light. Peak opacity is the caller's own <paramref name="peakOpacity"/>
    /// (never past the 0.6 ceiling), eased throughout, never a hard on/off, so it never reads as a blink.
    /// </summary>
    private static void AddGlint(
        Storyboard storyboard, System.Windows.UIElement glint, int delayMs, int periodMs, double peakScale, double peakOpacity)
    {
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var life = TimeSpan.FromMilliseconds(periodMs);

        var fade = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = begin,
            Duration = life,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        // Rests at RestingGlintOpacity, never 0: the XAML-declared value on each
        // glint is this same floor, so a static render (no Storyboard ever runs there) already shows the
        // sparkle instead of nothing, and the running animation now dims toward it between pulses rather
        // than vanishing and popping back from full transparency.
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(RestingGlintOpacity, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(peakOpacity, KeyTime.FromPercent(0.3), new CubicEase { EasingMode = EasingMode.EaseOut }));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(RestingGlintOpacity, KeyTime.FromPercent(0.65), new CubicEase { EasingMode = EasingMode.EaseIn }));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(RestingGlintOpacity, KeyTime.FromPercent(1)));
        Storyboard.SetTarget(fade, glint);
        Storyboard.SetTargetProperty(fade, new PropertyPath(System.Windows.UIElement.OpacityProperty));
        storyboard.Children.Add(fade);

        foreach (var axis in new[] { "ScaleX", "ScaleY" })
        {
            var pop = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = begin,
                Duration = life,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            pop.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0)));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(peakScale, KeyTime.FromPercent(0.3), new CubicEase { EasingMode = EasingMode.EaseOut }));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.65), new CubicEase { EasingMode = EasingMode.EaseIn }));
            pop.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(1)));

            // Addressed through the glint itself (which owns the transform), never straight at its
            // ScaleTransform: see Motion's own remark on why that never applies.
            Storyboard.SetTarget(pop, glint);
            Storyboard.SetTargetProperty(pop, new PropertyPath($"RenderTransform.{axis}"));
            storyboard.Children.Add(pop);
        }
    }

    private void StopSweep()
    {
        // Stop AND remove: a stopped storyboard leaves its animated values in place, so the light would
        // freeze mid-pass at whatever position it happened to be at. Every moving part is a child of this
        // one storyboard, so this covers the surface breathe and both glints as well.
        var wasSweeping = _sweep is not null;
        _sweep?.Stop(this);
        _sweep?.Remove(this);
        _sweep = null;

        // The closing flourish belongs to a run that actually ended here, and it has to outlive this call:
        // the handler that awaited the run calls this again the moment the run returns, a breath after the
        // phase change already did. It is taken off when the dialog closes, and by itself when it is over.
        if (wasSweeping && _viewModel.IsFinished)
            PlayFinish();
    }

    private void StopFinish()
    {
        _finish?.Stop(this);
        _finish?.Remove(this);
        _finish = null;
    }

    /// <summary>
    /// The one closing flourish: the freed figure counting up to its result instead of appearing at full
    /// size. No opacity pulse on the ring: a flash
    /// at the end would contradict the calm the rest of the animation is built for. One shot, and it takes itself
    /// off the moment it is over, so nothing is left holding an animated value.
    /// </summary>
    private void PlayFinish()
    {
        const int FinishMs = 600;

        // Never two at once: a second run finishing while the first flourish still ran would leave the
        // first one's clock on the count.
        StopFinish();

        // Where the count-up ends, and the value left behind once the animation is removed.
        ShowFreed(_viewModel.FreedBytes);

        var finish = new Storyboard();
        Timeline.SetDesiredFrameRate(finish, 60);

        var count = new DoubleAnimation(0, _viewModel.FreedBytes, TimeSpan.FromMilliseconds(FinishMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(count, this);
        Storyboard.SetTargetProperty(count, new PropertyPath(DisplayFreedProperty));
        finish.Children.Add(count);

        finish.Completed += (_, _) => StopFinish();

        _finish = finish;
        _finish.Begin(this, isControllable: true);

        // Same reasoning as the equivalent check at the end of StartSweep: a run finishing while the
        // window is minimised or hidden must not start its closing flourish animating unseen.
        if (_pausedForVisibility)
            _finish.Pause(this);
    }

    /// <summary>The detail list's own column chooser (Size only; see the doc comment on
    /// CleanupViewModel.DetailListColumns for why there is no sort here).</summary>
    private void OnColumnsButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor)
            return;

        ColumnChooser.ShowFor(
            anchor,
            CleanupViewModel.DetailListColumns,
            _viewModel.IsDetailColumnHidden,
            _viewModel.SetDetailColumnHidden,
            _viewModel.ResetDetailColumns);
    }

    /// <summary>
    /// A detail-row's right-click "open folder". Goes through <see cref="UriLauncher.LaunchDetached"/> like
    /// everything else this app opens from the UI: ShellExecute does not return until the shell has taken
    /// the target over, and with a cold Explorer that held this dialog frozen for seconds. The helper also
    /// refuses anything that is not an existing path, and records a folder that vanished between the scan
    /// and the click rather than letting the exception escape.
    /// </summary>
    private void OnEntryOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CleanupEntryViewModel { OpenPath.Length: > 0 } entry)
            return;

        UriLauncher.LaunchDetached(entry.OpenPath, _viewModel.Logging);
    }

    /// <summary>Same clipboard pattern as <see cref="MainWindow.OnErrorPillRightClick"/>: a locked
    /// clipboard is a transient, harmless failure, never worth surfacing.</summary>
    private void OnEntryCopyPathClick(object sender, RoutedEventArgs e)
    {
        // A recycled file's ORIGINAL path: the $R name inside the bin is of no use to anyone.
        if ((sender as FrameworkElement)?.DataContext is not CleanupEntryViewModel { PathText.Length: > 0 } entry)
            return;

        try
        {
            System.Windows.Clipboard.SetText(entry.PathText);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _viewModel.Logging.Info("Cleanup entry copy-path: clipboard was locked by another process.");
        }
    }

    /// <summary>
    /// Opens the earlier-cleanups window, sharing this dialog's own view model rather than
    /// building a second one: the history it shows already lives there. Not modal: the cleanup dialog
    /// stays usable while it is open, same as HistoryWindow beside the main window.
    /// </summary>
    private void OnShowHistory(object sender, RoutedEventArgs e) =>
        new CleanupHistoryWindow(_viewModel) { Owner = this }.Show();
}
