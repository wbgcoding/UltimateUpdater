using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Controls;
using UltimateUpdater.Models;
using UltimateUpdater.Models.Settings;

namespace UltimateUpdater.Views.Main;

public partial class MainWindow : FluentWindow
{
    private readonly SettingsService _settings;
    private readonly MainViewModel _viewModel;
    private readonly ThemeService _themeService;
    private readonly LoggingService? _logging;

    /// <summary>Only activations after the first are logged; the first is the start-up paint.</summary>
    private bool _hadFirstActivation;

    public MainWindow(
        MainViewModel viewModel, ThemeService themeService, SettingsService settings, LoggingService? logging = null)
    {
        _settings = settings;
        _viewModel = viewModel;
        _themeService = themeService;
        _logging = logging;
        themeService.AttachWindow(this);
        InitializeComponent();
        WindowBackdropType = ThemeService.ActiveBackdrop;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Sets the starting fill directly, without a glide: a window that opens mid-batch should show the real percentage at once.
        UpdateInstallBarFill(animate: false);

        // The toolbar cleanup sweep is ambient motion nobody sees while the window is minimised or hidden behind another (e.g. close-to-tray); kept apart from the run's own IsCleanupRunning check.
        IsVisibleChanged += (_, _) => ApplyToolbarSweepVisibility(IsVisible && WindowState != WindowState.Minimized);
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
                _restoreState = WindowState;
            ApplyToolbarSweepVisibility(IsVisible && WindowState != WindowState.Minimized);
        };

        RestorePlacement(viewModel.WindowPlacement);

        Toolbar.ToolbarWrapPanel.SizeChanged += (_, _) => UpdateFilterChipLabels();
        LocalizationService.Instance.PropertyChanged += (_, _) => ScheduleFilterChipLabelUpdate();
        SizeChanged += (_, _) => UpdateToolbarUninstallerLabel();
        LocalizationService.Instance.PropertyChanged += (_, _) => UpdateToolbarUninstallerLabel();
        Loaded += (_, _) => UpdateToolbarUninstallerLabel();

        Activated += OnMainWindowActivated;

        // Tells a main window falling behind the cleanup window apart from a close or a hide.
        Deactivated += (_, _) =>
        {
            if (_logging is not null && Application.Current?.Windows.OfType<CleanupWindow>().Any() == true)
                _logging.Info("Main window deactivated while the cleanup window is open");
        };

        Closing += (_, args) =>
        {
            // RestoreBounds, not Left/Width: a maximized window reports the maximized frame and a minimized one -32000, which would discard the user's real size. Minimized from maximized still counts as maximized.
            var maximized = WindowState == WindowState.Maximized
                            || (WindowState == WindowState.Minimized && _restoreState == WindowState.Maximized);
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            viewModel.SaveWindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, maximized);
            viewModel.SaveMainTableLayout();

            if (Application.Current is not App app || app.IsExiting)
                return;

            // Closing the window during an install would end the process, and the elevated worker stops with the app, abandoning an approved batch. Close-to-tray keeps the interval scanner alive; a real exit comes from the tray menu.
            if (viewModel.IsInstalling || _settings.Current.Ui.CloseToTray || app.StartedInTray)
            {
                args.Cancel = true;
                _logging?.Info("Main window closing: hidden to tray");
                Hide();
                _logging?.Info("Main window hidden");
                return;
            }

            _logging?.Info("Main window closing: exiting the app");

            // ShutdownMode is OnExplicitShutdown so a --tray start survives this window being opened and closed, which makes ending the app this window's job when it really closes.
            app.ExitApplication();

            // "Keep it running" in the quit confirmation: the window must stay too, or tray Open would call Show() on a closed window and throw into the crash handler.
            if (!app.IsExiting)
                args.Cancel = true;
        };
    }

    /// <summary>Test seam: the real hook waits for the next composited frame via <see cref="System.Windows.Media.CompositionTarget.Rendering"/>, which a headless render test never gets, so a test runs its callback synchronously instead.</summary>
    internal Action<Action>? NextRenderFrameOverride { get; set; }

    private void ScheduleNextRenderFrame(Action callback)
    {
        if (NextRenderFrameOverride is { } overrideHook)
        {
            overrideHook(callback);
            return;
        }

        void OnRendering(object? renderSender, EventArgs renderArgs)
        {
            System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
            callback();
        }

        System.Windows.Media.CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Measures the short stall after tabbing away and back: starts a Stopwatch on Activated and writes one Info line on the next composited frame, the number that matters. Only activations after the first are logged.</summary>
    private void OnMainWindowActivated(object? sender, EventArgs e)
    {
        if (!_hadFirstActivation)
        {
            _hadFirstActivation = true;
            return;
        }

        if (_logging is null)
            return;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        ScheduleNextRenderFrame(() =>
        {
            stopwatch.Stop();
            var rows = CountRealizedTableRows();
            var backdrop = _themeService.CurrentBackdrop;
            _logging.Info(
                $"Window activated: first frame after {stopwatch.ElapsedMilliseconds} ms, {rows} rows realized, backdrop {backdrop}");
        });
    }

    /// <summary>The table's ListBox has grouped virtualization off, so every generated container is a realized row; counted off the generator, since the item count would include unrealized rows.</summary>
    private int CountRealizedTableRows()
    {
        if (Table.UpdatesListBox.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
            return 0;

        var count = 0;
        for (var i = 0; i < Table.UpdatesListBox.Items.Count; i++)
        {
            if (Table.UpdatesListBox.ItemContainerGenerator.ContainerFromIndex(i) is not null)
                count++;
        }

        return count;
    }

    /// <summary>Routes Ctrl+F (see the window's InputBindings in XAML). Focusing is a view concern, so a RoutedCommand serves it in the same InputBindings list as <see cref="KeyboardShortcuts"/>.</summary>
    public static readonly RoutedCommand FocusSearchCommand = new(nameof(FocusSearchCommand), typeof(MainWindow));

    /// <summary>Opens the F1 overview (see <see cref="ShowShortcutsWindow"/>); a view-only command like <see cref="FocusSearchCommand"/>.</summary>
    public static readonly RoutedCommand ShowShortcutsCommand = new(nameof(ShowShortcutsCommand), typeof(MainWindow));

    private void OnFocusSearchExecuted(object sender, ExecutedRoutedEventArgs e) => TryFocusSearch();

    private void OnShowShortcutsExecuted(object sender, ExecutedRoutedEventArgs e) => ShowShortcutsWindow();

    /// <summary>The toolbar's search box, reached through its control (tests and <see cref="TryFocusSearch"/>).</summary>
    internal Wpf.Ui.Controls.TextBox SearchBox => Toolbar.SearchBox;

    /// <summary>Returns whether the search box took focus, so it is testable without live keyboard modifier state.</summary>
    internal bool TryFocusSearch()
    {
        if (!SearchBox.Focus())
            return false;

        SearchBox.SelectAll();
        return true;
    }

    /// <summary>One window per app instance, non-modal, closing on the next click elsewhere (<see cref="DismissOnDeactivate"/>).</summary>
    internal void ShowShortcutsWindow()
    {
        var existing = Application.Current?.Windows.OfType<ShortcutsWindow>().FirstOrDefault(w => w.IsLoaded);
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        new ShortcutsWindow { Owner = this }.Show();
    }

    /// <summary>Keeps the install band's hand-drawn fill in step with the view model, gliding on a live change; a plain XAML Binding on Value would fight the animation, so the bar has none.</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ShowPersistentFailureFilter)
            or nameof(MainViewModel.PersistentFailureFilterLabel)
            or nameof(MainViewModel.PersistentFailureChipLabel) or "")
        {
            ScheduleFilterChipLabelUpdate();
        }

        if (e.PropertyName is nameof(MainViewModel.InstallOverallPercent) or "")
            UpdateInstallBarFill(animate: true);

        if (e.PropertyName is nameof(MainViewModel.IsCleanupRunning) or "")
            UpdateCleanupIconSweep();
    }

    /// <summary>Narrowest window width that shows the uninstaller button's text; below it the button keeps its icon and tooltip, so the filter chips stay on the search row.</summary>
    private const double UninstallerLabelMinWidth = 1100;

    private void UpdateToolbarUninstallerLabel()
    {
        // A language switch can come from any thread (a worker job applies its own language).
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateToolbarUninstallerLabel);
            return;
        }

        var label = ActualWidth >= UninstallerLabelMinWidth ? LocalizationService.Instance["ToolbarUninstallerLabel"] : null;
        if (!Equals(Toolbar.ToolbarUninstallerButton.Content, label))
            Toolbar.ToolbarUninstallerButton.Content = label;
    }

    private bool _chipLabelUpdateQueued;

    /// <summary>The chips' texts change after the notification that triggers this, so measuring waits until the bindings settle.</summary>
    private void ScheduleFilterChipLabelUpdate()
    {
        if (_chipLabelUpdateQueued)
            return;

        _chipLabelUpdateQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            _chipLabelUpdateQueued = false;
            UpdateFilterChipLabels();
        });
    }

    /// <summary>Whether the chips can keep their text: the width the search box and every shown chip need with text, worked out from the text itself (not the chips' current size), against the toolbar's width. The toolbar's width does not depend on its children, so switching the text off cannot flip the answer back.</summary>
    private void UpdateFilterChipLabels()
    {
        var available = Toolbar.ToolbarWrapPanel.ActualWidth;
        if (available <= 0)
            return;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labels = new List<System.Windows.Controls.TextBlock>();
        var needed = SearchBox.Width + SearchBox.Margin.Left + SearchBox.Margin.Right;
        foreach (var chip in new[] { Toolbar.FilterCriticalChip, Toolbar.FilterIgnoredChip, Toolbar.FilterManualChip, Toolbar.FilterFailuresChip })
        {
            if (chip.Visibility == Visibility.Collapsed
                || chip.Content is not System.Windows.Controls.StackPanel content
                || content.Children.OfType<System.Windows.Controls.TextBlock>().FirstOrDefault() is not { } label)
            {
                continue;
            }

            labels.Add(label);
            var text = new FormattedText(
                label.Text, System.Globalization.CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                label.FontSize, System.Windows.Media.Brushes.Black, pixelsPerDip);

            // 2 = the chip's border, the icon is as wide as its font size, 4 = rounding slack.
            var iconWidth = content.Children.OfType<Wpf.Ui.Controls.SymbolIcon>().FirstOrDefault()?.FontSize ?? 16;
            needed += chip.Margin.Left + chip.Margin.Right + chip.Padding.Left + chip.Padding.Right + 2 + iconWidth
                + label.Margin.Left + text.WidthIncludingTrailingWhitespace + 4;
        }

        var compact = available < needed;
        foreach (var label in labels)
            label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The travelling light's clock, kept so <see cref="ApplyToolbarSweepVisibility"/> can pause and resume it; BeginAnimation returns no handle, so it is built with <c>CreateClock(hasControllableRoot: true)</c>.</summary>
    private AnimationClock? _toolbarSweepClock;

    /// <summary>True while the sweep is paused because the window is minimised or hidden, apart from the run's own state, so a window restored while nothing is cleaning never resumes a removed clock.</summary>
    private bool _toolbarSweepPausedForVisibility;

    /// <summary>The travelling light on the toolbar cleanup button's disk (ToolbarCleaningLight in the XAML), a smaller mirror of the cleanup dialog's centre animation. A plain BeginAnimation on the transform it owns, not a Storyboard on a Style trigger: animating a Brush's RelativeTransform that way corrupted later renders of other windows in the render harness. Gated on ClientAreaAnimation, the system "animations on" setting.</summary>
    private void UpdateCleanupIconSweep()
    {
        if (_viewModel.IsCleanupRunning && MotionPreference.AnimationsEnabled)
        {
            if (_toolbarSweepClock is not null)
                return;

            var sweep = new DoubleAnimation(-0.75, 0.75, TimeSpan.FromSeconds(1.3))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Timeline.SetDesiredFrameRate(sweep, 30);

            var clock = (AnimationClock)sweep.CreateClock(hasControllableRoot: true);
            _toolbarSweepClock = clock;
            Toolbar.ToolbarLightSlide.ApplyAnimationClock(TranslateTransform.XProperty, clock);

            // Started while already minimised or hidden: match the paused state, or the fresh clock would animate unseen until the next visibility change.
            if (_toolbarSweepPausedForVisibility)
                clock.Controller?.Pause();
        }
        else
        {
            Toolbar.ToolbarLightSlide.ApplyAnimationClock(TranslateTransform.XProperty, null);
            Toolbar.ToolbarLightSlide.X = -0.75;
            _toolbarSweepClock = null;
        }
    }

    /// <summary>Pausing never fights the run's own state, and restoring never starts a sweep the run already ended.</summary>
    private void ApplyToolbarSweepVisibility(bool visible)
    {
        if (visible)
        {
            if (!_toolbarSweepPausedForVisibility)
                return;

            _toolbarSweepPausedForVisibility = false;
            _toolbarSweepClock?.Controller?.Resume();
        }
        else
        {
            if (_toolbarSweepClock is null || _toolbarSweepPausedForVisibility)
                return;

            _toolbarSweepClock.Controller?.Pause();
            _toolbarSweepPausedForVisibility = true;
        }
    }

    private void UpdateInstallBarFill(bool animate)
    {
        var target = _viewModel.InstallOverallPercent;

        if (!animate)
        {
            InstallBand.InstallBar.BeginAnimation(PillProgressBar.ValueProperty, null);
            InstallBand.InstallBar.Value = target;
            return;
        }

        // Short and eased: long enough to read as motion, short enough not to lag behind the numbers beside it.
        var glide = new DoubleAnimation(target, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Timeline.SetDesiredFrameRate(glide, 60);
        InstallBand.InstallBar.BeginAnimation(PillProgressBar.ValueProperty, glide);
    }

    /// <summary>Enter and Space on the source pill (see <see cref="KeyboardActivation"/>); Ctrl+C on an error pill copies its reason, as the right-click does.</summary>
    private void OnClickTargetKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => HandleClickTargetKeyDown(sender, e);

    /// <summary>The shared body of the click-target key handlers: also used by the table's sortable column headers.</summary>
    internal static void HandleClickTargetKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (CopyErrorReason(sender))
                e.Handled = true;

            return;
        }

        KeyboardActivation.ActivateOnEnterOrSpace(sender, e);
    }

    /// <summary>Right-click a source pill to copy its raw error reason, as the tooltip says. No-op for a pill with nothing to copy.</summary>
    private void OnErrorPillRightClick(object sender, MouseButtonEventArgs e) => CopyErrorReason(sender);

    /// <summary>True when the element is a pill with a reason and that reason was put on the clipboard.</summary>
    private static bool CopyErrorReason(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProviderStatusViewModel { CopyableErrorText.Length: > 0 } status)
            return false;

        try
        {
            System.Windows.Clipboard.SetText(status.CopyableErrorText);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // the clipboard can be held open by another process; a failed copy is not worth surfacing
            return false;
        }
    }

    /// <summary>The last state that was not minimized, so tray Open and a second launch restore a maximized window as maximized.</summary>
    private WindowState _restoreState = WindowState.Normal;

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _restoreState;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void RestorePlacement(WindowPlacement? placement)
    {
        var saved = placement is { Width: > 0, Height: > 0 }
            ? new Rect(placement.Left, placement.Top, placement.Width, placement.Height)
            : (Rect?)null;

        var (bounds, onMonitor) = ResolveRestoreBounds(
            saved, saved is { } rect ? MonitorFor(rect) : null, PrimaryMonitorArea(), new System.Windows.Size(Width, Height));

        // Any other startup location re-centres on the monitor under the mouse and discards these bounds, and a minimum larger than the work area makes WPF grow the window past the clamp, which can put the title bar above the top edge.
        WindowStartupLocation = WindowStartupLocation.Manual;
        MinWidth = Math.Min(MinWidth, bounds.Width);
        MinHeight = Math.Min(MinHeight, bounds.Height);
        ApplyBounds(bounds);
        if (onMonitor && placement is { Maximized: true })
        {
            WindowState = WindowState.Maximized;
            _restoreState = WindowState.Maximized;
        }
    }

    /// <summary>A monitor's full rectangle and its work area (without the taskbar), both in DIPs.</summary>
    internal readonly record struct MonitorArea(Rect Bounds, Rect WorkArea);

    /// <summary>The monitor the OS picks for a rect in DIPs: the one it overlaps most, the nearest when none. The scale is the primary display's, which <see cref="SystemParameters"/> and all saved coordinates use.</summary>
    private static MonitorArea MonitorFor(Rect dips)
    {
        var pixelsPerDip = System.Windows.Forms.SystemInformation.PrimaryMonitorSize.Width / SystemParameters.PrimaryScreenWidth;

        Rect ToDips(System.Drawing.Rectangle r) =>
            new(r.Left / pixelsPerDip, r.Top / pixelsPerDip, r.Width / pixelsPerDip, r.Height / pixelsPerDip);

        var pixels = new System.Drawing.Rectangle(
            (int)Math.Round(dips.Left * pixelsPerDip), (int)Math.Round(dips.Top * pixelsPerDip),
            (int)Math.Round(dips.Width * pixelsPerDip), (int)Math.Round(dips.Height * pixelsPerDip));
        var monitor = System.Windows.Forms.Screen.FromRectangle(pixels);
        return new MonitorArea(ToDips(monitor.Bounds), ToDips(monitor.WorkingArea));
    }

    private static MonitorArea PrimaryMonitorArea() =>
        new(new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight), SystemParameters.WorkArea);

    /// <summary>Where the window opens. A saved rect that overlaps its monitor (<paramref name="monitorForSaved"/>) is fitted to that monitor's work area, not the bounding box of all monitors, which has dead corners and ignores a secondary taskbar. Nothing saved, or a rect that overlaps no monitor, centres the default size on the primary work area. <c>OnMonitor</c> is true only for the first case.</summary>
    internal static (Rect Bounds, bool OnMonitor) ResolveRestoreBounds(
        Rect? saved, MonitorArea? monitorForSaved, MonitorArea primary, System.Windows.Size defaultSize)
    {
        if (saved is { } rect && monitorForSaved is { } monitor && rect.IntersectsWith(monitor.Bounds))
            return (ClampToVisible(rect, monitor.WorkArea), true);

        // Nothing usable saved: the XAML size centred on the primary display can be taller than the work area (1024x768) and push the title bar above the edge.
        var workArea = primary.WorkArea;
        var centred = new Rect(
            workArea.Left + (workArea.Width - defaultSize.Width) / 2,
            workArea.Top + (workArea.Height - defaultSize.Height) / 2,
            defaultSize.Width, defaultSize.Height);
        return (ClampToVisible(centred, workArea), false);
    }

    private void ApplyBounds(Rect bounds)
    {
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    /// <summary>A saved rect that merely intersects the virtual screen can still be almost entirely off it (a removed second monitor leaving a sliver), so this pulls it fully onto the screen: shrinks it if larger, then slides it so no edge falls outside. Pure geometry, unit-tested directly.</summary>
    internal static Rect ClampToVisible(Rect saved, Rect virtualScreen)
    {
        var width = Math.Min(saved.Width, virtualScreen.Width);
        var height = Math.Min(saved.Height, virtualScreen.Height);

        var left = Math.Clamp(saved.Left, virtualScreen.Left, virtualScreen.Right - width);
        var top = Math.Clamp(saved.Top, virtualScreen.Top, virtualScreen.Bottom - height);

        return new Rect(left, top, width, height);
    }
}
