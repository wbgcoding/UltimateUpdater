using System.Windows;
using System.Windows.Media.Animation;

namespace UltimateUpdater.Views.Cleanup;

public partial class CleanupWindow
{
    private readonly CleanupViewModel _viewModel;
    private Storyboard? _sweep;

    /// <summary>The closing pulse and count-up. Its own storyboard because it is a one-shot that begins where the sweep ends. Held and let go with the run like the sweep, and taken off when the dialog closes.</summary>
    private Storyboard? _finish;

    /// <summary>Set the moment this window closes; checked after the one <c>await</c> in the <c>Loaded</c> handler, so a window closed while its catalogue loaded never starts (or re-triggers elevation for) a scan nobody can see, nor races a fresh window opened onto the same, reused view model.</summary>
    private bool _closed;

    /// <summary>What the ring draws. The view model's figure jumps on every tick or finished target, so this follows it on a short animation.</summary>
    public static readonly DependencyProperty DisplayPercentProperty = DependencyProperty.Register(
        nameof(DisplayPercent), typeof(double), typeof(CleanupWindow),
        new PropertyMetadata(0d, OnDisplayPercentChanged));

    /// <summary>The ring's radius, shared by the track and the Path's box.</summary>
    private const double RingRadius = 105;

    private static void OnDisplayPercentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not CleanupWindow window || e.NewValue is not double percent)
            return;

        window.DrawRing(percent);
    }

    /// <summary>The arc the ring draws. Also called for the starting value, or the ring would show a bare track until the first change.</summary>
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

    /// <summary>The freed figure the finished ring shows, in bytes. Its own property so the count-up has something animatable to run on.</summary>
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

    /// <summary>Puts a figure on the ring. Written here as well as in the changed callback: a DependencyProperty raises nothing for a value it already holds and this one defaults to 0, so a run that freed nothing (a repair-only run, one stopped at once, one where every target was empty) would otherwise leave the empty XAML default.</summary>
    private void ShowFreed(long bytes)
    {
        DisplayFreed = bytes;

        if (FreedNumber is { } text)
            text.Text = CleanupViewModel.FormatFreed(bytes);
    }

    public CleanupWindow(CleanupViewModel viewModel)
    {
        InitializeComponent();
        Resources.MergedDictionaries.OfType<Cleanup.CleanupTemplates>().Single().Logging = viewModel.Logging;
        WindowBackdropType = ThemeService.ActiveBackdrop;
        DataContext = viewModel;
        _viewModel = viewModel;
        DialogSizing.FitToWorkArea(this);

        DrawRing(DisplayPercent);

        // The result figure is written, not bound, so it needs its starting value here: a window opened onto a finished run shows what it ended on. After that the run's notifications keep it in step and the closing count-up takes over.
        ShowFreed(viewModel.FreedBytes);

        // The window shows at once (IsCatalogLoading gates the detail list's loading state in XAML); the scan waits until the catalogue exists.
        Loaded += async (_, _) =>
        {
            try
            {
                await viewModel.CatalogReadyTask;
            }
            catch (Exception ex)
            {
                // CleanupViewModel.LoadCatalogAsync already catches everything; this is the second line of defence, since an exception in an async void continuation takes the whole app down. A gap in that guard degrades to a logged, closed catalogue state.
                viewModel.Logging.Error("Cleanup: the catalogue task faulted unexpectedly.", ex);

                // A window already closed by now may sit on a view model a fresh window reuses (see _closed); that window must not inherit an error state from a run it never saw.
                if (!_closed)
                    viewModel.CatalogLoadFailed = true;

                return;
            }

            OnCatalogReady();
        };

        // The window's first painted frame, catalogue load included; logged once.
        var firstFrameLogged = false;
        ContentRendered += (_, _) =>
        {
            if (firstFrameLogged)
                return;

            firstFrameLogged = true;
            viewModel.LogFirstFrame();
        };

        // Whether per-entry marking in the open detail lists is worth doing. Minimized counts as gone: IsVisible stays true for a minimized window. WindowClosed says the same for good.
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

        // Deliberately no DismissOnDeactivate: this is a window of its own beside the main one (see MainViewModel.ShowCleanup), so working in the main window, a new scan included, leaves it open.
        DialogKeyboard.CloseOnEscape(this);
    }

    /// <summary>Runs once the catalogue is ready, except when the window has already closed (<see cref="_closed"/>): starting a scan, or its elevation prompt, for a window nobody can see is not harmless, and a fresh window on the same reused view model must not race this stale continuation. Split out of <c>Loaded</c> so a test can drive it directly.</summary>
    private void OnCatalogReady()
    {
        if (_closed)
            return;

        // The ring starts where the work already is: DisplayPercent begins at 0 and only glides on a change, so a window opened onto a run in progress would show a bare track.
        DisplayPercent = _viewModel.RingPercent;

        // The scan starts once the catalogue is ready, so figures fill in while the user reads, but not when the window was re-opened onto work already going: re-measuring would fight the deletion for the same files.
        if (_viewModel.IsCleaning)
            StartSweep();
        else if (_viewModel.IsPrescanRunning)
            _ = FinishAfterPrescanAsync();
        else if (!_viewModel.IsScanning)
            _ = _viewModel.StartOpenScanAsync();
    }

    /// <summary>The window opened while the startup prescan was still measuring: it is left to finish (a second pass would fight it over the same folders), then the rest is measured.</summary>
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

    /// <summary>True while the sweep and finish storyboards are paused because the window is minimised or hidden, apart from the run's own pause (<see cref="CleanupViewModel.IsPaused"/>), so restoring never resumes an animation the user paused on purpose.</summary>
    private bool _pausedForVisibility;

    /// <summary>The sweep is ambient motion nobody sees while the dialog is minimised or hidden; running it at 30 fps then costs battery and CPU for nothing. Pausing never fights the run's own pause, and restoring never resumes a run the user paused (see the IsPaused branch of <see cref="OnViewModelPropertyChanged"/>). Reduce-motion is untouched.</summary>
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
        // A held run holds its animation too: a light that keeps travelling while nothing is deleted says the opposite of the dialog.
        if (e.PropertyName == nameof(CleanupViewModel.IsPaused))
        {
            if (_viewModel.IsPaused)
            {
                _sweep?.Pause(this);
                _finish?.Pause(this);
            }
            else if (!_pausedForVisibility)
            {
                // Still minimised or hidden: ApplySweepVisibility resumes this pair once the window is visible, and must find the run's pause already lifted.
                _sweep?.Resume(this);
                _finish?.Resume(this);
            }

            return;
        }

        // The pace of the work, on the running clock, never a rebuilt storyboard, which would restart the stroke on every rate change.
        if (e.PropertyName == nameof(CleanupViewModel.Activity))
        {
            ApplySweepSpeed();
            return;
        }

        // The run's own end, which is not the dialog closing: a window re-opened onto a run in progress never went through the handler that started the sweep, so this stops it and plays the closing flourish.
        if (e.PropertyName == nameof(CleanupViewModel.Phase))
        {
            if (!_viewModel.IsCleaning)
                StopSweep();

            return;
        }

        if (e.PropertyName is nameof(CleanupViewModel.FreedText) or "")
        {
            // The count-up owns the figure while it runs; otherwise it follows the run.
            if (_finish is null)
                ShowFreed(_viewModel.FreedBytes);
        }

        if (e.PropertyName is not (nameof(CleanupViewModel.RingPercent) or ""))
            return;

        var target = _viewModel.RingPercent;

        if (Math.Abs(target - DisplayPercent) < 0.01)
            return;

        // Short and eased: long enough to read as motion, short enough that a fast run does not lag behind the numbers. Capped at 60 fps like every other animation here.
        var glide = new DoubleAnimation(target, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Timeline.SetDesiredFrameRate(glide, 60);
        BeginAnimation(DisplayPercentProperty, glide);
    }

    /// <summary>The dialog moves by dragging its header, not just the title bar. Guarded: DragMove throws if the button is already up.</summary>
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

        // The header band and the root both route here; without this the press bubbled to the root and started a second DragMove after the first ended, which throws.
        e.Handled = true;
    }

    /// <summary>The second confirmation. A cleanup never runs without it.</summary>
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

        // A rotational-disk optimisation can run for hours; said before it starts, beside the red confirmation, since it applies only to some runs.
        if (_viewModel.HasSelectedHddDefragTarget)
            Dialogs.Warn(loc["DefragWarningTitle"], loc["DefragWarningBody"]);

        StartSweep();
        try
        {
            // Not awaited through the window's lifetime: the run belongs to the view model, which outlives this window so cleaning can carry on.
            await _viewModel.RunCleanupAsync();
        }
        catch (Exception ex)
        {
            // An exception in an async void handler would take the process down. The run already survives a single target failing, so anything arriving here is unexpected and belongs in front of the user; logged first so the stack is on record.
            _viewModel.Logging.Error("Cleanup run failed", ex);
            Dialogs.Error(loc["CleanupFailedHeadline"], details: ex.Message);
        }
        finally
        {
            StopSweep();
        }
    }

    /// <summary>One full pass of the light, out and back. Everything in the animation repeats on this period, since a part with its own period drifts out of phase over a long cleanup. Never below 2.4 s: calm ambient motion.</summary>
    private const int AmbientPeriodMs = 2600;

    /// <summary>The calm cleaning animation: a light glides across the surface and back while the surface breathes slightly. Opacity, a Brush's RelativeTransform and RenderTransforms only (no layout pass, no ring rotation), capped at 30 fps. One storyboard, so pausing, stopping and removing cover every part.</summary>
    private void StartSweep()
    {
        // ClientAreaAnimation is the system "animations on" setting, the gate the main window's toolbar sweep uses; it also covers the closing flourish, which plays only for a sweep that ran.
        if (_sweep is not null || !MotionPreference.AnimationsEnabled)
            return;

        var sweep = new Storyboard();
        Timeline.SetDesiredFrameRate(sweep, 30);

        // Half the period each way, auto-reversed. Addressed through SurfaceLight (the element that owns the mask), never at LightSlide itself: a Storyboard target set to a Freezable is accepted and then never applied.
        sweep.Children.Add(Motion(SurfaceLight, "OpacityMask.RelativeTransform.X", -0.75, 0.75, AmbientPeriodMs / 2));

        // A slight breathe, well under the 1.06 ceiling, so the surface reads as alive without reading as motion. Addressed through CenterScene, which owns the transform.
        sweep.Children.Add(Motion(CenterScene, "RenderTransform.ScaleX", 1.0, 1.05, AmbientPeriodMs / 2));
        sweep.Children.Add(Motion(CenterScene, "RenderTransform.ScaleY", 1.0, 1.05, AmbientPeriodMs / 2));

        _sweep = sweep;
        _sweep.Begin(this, isControllable: true);
        ApplySweepSpeed();

        // Started while minimised, hidden or held: the storyboard begins running like any other, so the pause is applied at once.
        if (_pausedForVisibility || _viewModel.IsPaused)
            _sweep.Pause(this);
    }

    /// <summary>The motion runs at the pace of the work: a run deleting file after file looks busy, a quiet repair step slows down. A speed ratio on the running clock, since rebuilding the storyboard would jerk the light back to the start.</summary>
    private void ApplySweepSpeed() =>
        _sweep?.SetSpeedRatio(this, 0.75 + 0.75 * Math.Clamp(_viewModel.Activity, 0, 1));

    /// <summary>Every animation addresses its transform through the element that owns it: a Storyboard target set to a Freezable is accepted and then never applied, leaving a part silently standing still.</summary>
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

    private void StopSweep()
    {
        // Stop and remove: a stopped storyboard leaves its animated values in place, so the light would freeze mid-pass. Every moving part is a child of this storyboard, including the surface breathe.
        var wasSweeping = _sweep is not null;
        _sweep?.Stop(this);
        _sweep?.Remove(this);
        _sweep = null;

        // The closing flourish belongs to a run that ended here and must outlive this call: the awaiting handler calls this again right after the phase change did. It is taken off when the dialog closes, and by itself when over.
        if (wasSweeping && _viewModel.IsFinished)
            PlayFinish();
    }

    private void StopFinish()
    {
        _finish?.Stop(this);
        _finish?.Remove(this);
        _finish = null;
    }

    /// <summary>The closing flourish: the freed figure counting up to its result. One shot, taken off the moment it is over so nothing keeps an animated value.</summary>
    private void PlayFinish()
    {
        const int FinishMs = 600;

        // Never two at once: a second run finishing mid-flourish would leave the first clock on the count.
        StopFinish();

        // Where the count-up ends, and the value left once the animation is removed.
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

        // As at the end of StartSweep: a run finishing while the window is minimised or hidden must not start its flourish unseen.
        if (_pausedForVisibility)
            _finish.Pause(this);
    }

    /// <summary>The detail list's own column chooser (Size only, no sort; see CleanupViewModel.DetailListColumns).</summary>
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

    /// <summary>Opens the earlier-cleanups window on this dialog's own view model, which already holds the history. Non-modal, like HistoryWindow beside the main window.</summary>
    private void OnShowHistory(object sender, RoutedEventArgs e) => ShowHistoryWindow(expandNewest: false);

    /// <summary>The report of the run that just ended: the same window, with the newest entry unfolded.</summary>
    private void OnShowReport(object sender, RoutedEventArgs e) => ShowHistoryWindow(expandNewest: true);

    /// <summary>One history window per cleanup window: a second request brings the open one forward instead of stacking another.</summary>
    internal void ShowHistoryWindow(bool expandNewest)
    {
        var existing = OwnedWindows.OfType<CleanupHistoryWindow>().FirstOrDefault();
        if (existing is not null)
        {
            if (expandNewest)
                existing.ShowNewestRun();

            existing.Activate();
            return;
        }

        new CleanupHistoryWindow(_viewModel, expandNewest) { Owner = this }.Show();
    }
}
