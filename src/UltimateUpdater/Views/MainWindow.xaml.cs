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
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Views;

public partial class MainWindow : FluentWindow
{
    private readonly SettingsService _settings;
    private readonly MainViewModel _viewModel;
    private readonly ThemeService _themeService;
    private readonly LoggingService? _logging;

    /// <summary>Only activations AFTER the first are logged, the first is the start-up paint, a separate
    /// concern.</summary>
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

        // Sets the starting fill directly, no glide: a window that opens mid-batch should show the real
        // percentage at once, not animate up from zero the way a live change does.
        UpdateInstallBarFill(animate: false);

        // The toolbar cleanup sweep is ambient motion nobody can see while this window is minimised or
        // hidden behind another (e.g. close-to-tray), same reasoning as CleanupWindow.xaml.cs's own
        // ApplySweepVisibility; kept separate from the run's own IsCleanupRunning check.
        IsVisibleChanged += (_, _) => ApplyToolbarSweepVisibility(IsVisible && WindowState != WindowState.Minimized);
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
                _restoreState = WindowState;
            ApplyToolbarSweepVisibility(IsVisible && WindowState != WindowState.Minimized);
        };

        RestorePlacement(viewModel.WindowPlacement);

        Activated += OnMainWindowActivated;

        // The main window falling behind while the cleanup window is open is the case this line helps tell
        // apart from a close or a hide.
        Deactivated += (_, _) =>
        {
            if (_logging is not null && Application.Current?.Windows.OfType<CleanupWindow>().Any() == true)
                _logging.Info("Main window deactivated while the cleanup window is open");
        };

        Closing += (_, args) =>
        {
            // RestoreBounds, not Left/Width: a maximized window reports the maximized frame and a
            // minimized one reports -32000, either of which would throw the user's actual window size
            // away if saved directly.
            // Minimized from maximized still counts as maximized: saving false reopened it un-maximized.
            var maximized = WindowState == WindowState.Maximized
                            || (WindowState == WindowState.Minimized && _restoreState == WindowState.Maximized);
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            viewModel.SaveWindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, maximized);
            viewModel.SaveMainTableLayout();

            if (Application.Current is not App app || app.IsExiting)
                return;

            // Closing the window during an install would end the process, and the elevated worker
            // stops as soon as the app is gone: so a batch the user approved would be abandoned
            // halfway through by closing a window, which is a natural thing to do while it runs.
            // Close-to-tray keeps the interval scanner alive; a real exit comes from the tray menu.
            if (viewModel.IsInstalling || _settings.Current.Ui.CloseToTray || app.StartedInTray)
            {
                args.Cancel = true;
                _logging?.Info("Main window closing: hidden to tray");
                Hide();
                _logging?.Info("Main window hidden");
                return;
            }

            _logging?.Info("Main window closing: exiting the app");

            // ShutdownMode is OnExplicitShutdown, so that a --tray start survives this window being
            // opened and closed again. That makes ending the app this window's job when it really closes.
            app.ExitApplication();

            // "Keep it running" in the quit confirmation: the app stays, so must its window. Closed anyway,
            // the process lived on with only the tray icon, and tray Open then called Show() on a closed
            // window, which threw into the crash handler and ended the very work the user chose to keep.
            if (!app.IsExiting)
                args.Cancel = true;
        };
    }

    /// <summary>Test seam: the real hook waits for the next composited frame via
    /// <see cref="System.Windows.Media.CompositionTarget.Rendering"/>, which a headless render test never
    /// gets, so a test sets this to run its callback synchronously instead.</summary>
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

    /// <summary>
    /// Measures the short stall some users see after tabbing away and back, since nothing else on the
    /// activation path is blocking. Starts a Stopwatch on Activated and writes one Info line on the NEXT
    /// composited frame after that, which is the number that matters, not how long the event handler
    /// itself took. Only activations after the first are logged; the first is the start-up paint, a
    /// separate concern.
    /// </summary>
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

    /// <summary>The main table's ListBox has grouped virtualization off, so every generated container is
    /// a realized row; counted straight off the generator rather than the item count, which would also
    /// include rows that have not been realized yet.</summary>
    private int CountRealizedTableRows()
    {
        if (UpdatesListBox.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
            return 0;

        var count = 0;
        for (var i = 0; i < UpdatesListBox.Items.Count; i++)
        {
            if (UpdatesListBox.ItemContainerGenerator.ContainerFromIndex(i) is not null)
                count++;
        }

        return count;
    }

    /// <summary>The stable, language-independent identity of a group; see
    /// <see cref="UpdateRowViewModel.CategoryKey"/>. The view still groups by the
    /// translated CategoryDisplay text (group.Name), which is what a language switch changes; every row
    /// in one group shares the same CategoryKey, so the first one answers for the whole group.</summary>
    private static string? StableGroupKey(CollectionViewGroup group) =>
        group.Items.Count > 0 && group.Items[0] is UpdateRowViewModel row ? row.CategoryKey : group.Name as string;

    /// <summary>Applies the remembered collapse state to a group header as it is (re)built.</summary>
    private void OnGroupExpanderLoaded(object sender, RoutedEventArgs e)
    {
        // the un-prefixed <Expander> in XAML is the standard WPF control, not WPF-UI's own
        if (sender is System.Windows.Controls.Expander { DataContext: CollectionViewGroup group } expander
            && DataContext is MainViewModel viewModel)
        {
            expander.IsExpanded = viewModel.IsGroupExpanded(StableGroupKey(group));
        }
    }

    /// <summary>Remembers a group's collapse state so the next rebuild (e.g. a scan finding more rows) keeps it.</summary>
    private void OnGroupExpanderToggled(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Expander { DataContext: CollectionViewGroup group } expander
            && DataContext is MainViewModel viewModel)
        {
            viewModel.SetGroupExpanded(StableGroupKey(group), expander.IsExpanded);
        }
    }

    /// <summary>The main table's own column chooser, built from MainViewModel's static column
    /// list; the shared builder lives in Views/ColumnChooser.cs.</summary>
    private void OnColumnsButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor)
            return;

        ColumnChooser.ShowFor(
            anchor,
            MainViewModel.MainTableColumns,
            _viewModel.IsMainColumnHidden,
            _viewModel.SetMainColumnHidden,
            _viewModel.ResetMainColumns);
    }

    /// <summary>The filter menu (Views/FilterMenu.cs): the table's switches, built
    /// from MainViewModel's own entry list the same way the column chooser is.</summary>
    private void OnFilterButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor)
            return;

        FilterMenu.ShowFor(anchor, _viewModel.BuildFilterEntries());
    }

    /// <summary>
    /// Double-clicking a row runs its Open action where it has one: the store and launcher rows exist
    /// only to be opened manually, and they carry no checkbox, so the whole row is the target.
    /// </summary>
    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UpdateRowViewModel { CanOpen: true } row })
            row.OpenCommand.Execute(null);
    }

    /// <summary>Routes Ctrl+F (see the window's own InputBindings in XAML). Focusing a control lives in the
    /// view, so this has no view model command; a RoutedCommand keeps it in the same InputBindings list
    /// <see cref="KeyboardShortcuts"/>'s table drives for every other shortcut.</summary>
    public static readonly RoutedCommand FocusSearchCommand = new(nameof(FocusSearchCommand), typeof(MainWindow));

    /// <summary>Opens the F1 overview (see <see cref="ShowShortcutsWindow"/>); the other command with no
    /// view model command of its own, for the same reason as <see cref="FocusSearchCommand"/> above.</summary>
    public static readonly RoutedCommand ShowShortcutsCommand = new(nameof(ShowShortcutsCommand), typeof(MainWindow));

    private void OnFocusSearchExecuted(object sender, ExecutedRoutedEventArgs e) => TryFocusSearch();

    private void OnShowShortcutsExecuted(object sender, ExecutedRoutedEventArgs e) => ShowShortcutsWindow();

    /// <summary>Its own outcome, whether the search box actually took focus, is directly testable without
    /// depending on live global keyboard modifier state, which a headless test cannot fake.</summary>
    internal bool TryFocusSearch()
    {
        if (!SearchBox.Focus())
            return false;

        SearchBox.SelectAll();
        return true;
    }

    /// <summary>One window per app instance, non-modal, closing on the next click elsewhere in the app
    /// (<see cref="DismissOnDeactivate"/>, wired inside the window's own constructor).</summary>
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

    /// <summary>Keeps the install band's hand-drawn fill in step with the view model, gliding on a live
    /// change instead of jumping: a plain XAML Binding on Value would fight this animation on every tick,
    /// so the bar carries no Binding of its own.</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.InstallOverallPercent) or "")
            UpdateInstallBarFill(animate: true);

        if (e.PropertyName is nameof(MainViewModel.IsCleanupRunning) or "")
            UpdateCleanupIconSweep();
    }

    /// <summary>The travelling light's own clock, kept so <see cref="ApplyToolbarSweepVisibility"/> can
    /// pause and resume it without tearing it down; a plain BeginAnimation call hands back no handle of
    /// its own, so this one is built with <c>CreateClock(hasControllableRoot: true)</c> instead.</summary>
    private AnimationClock? _toolbarSweepClock;

    /// <summary>True while the sweep is paused because the window itself is minimised or hidden behind
    /// another, as distinct from the run's own state: kept apart so a window restored while nothing is
    /// cleaning never resumes a clock that was already removed.</summary>
    private bool _toolbarSweepPausedForVisibility;

    /// <summary>
    /// The travelling light on the toolbar cleanup button's disk (ToolbarCleaningLight in the XAML), the
    /// smaller-scale mirror of the cleanup dialog's own centre animation. A plain BeginAnimation call
    /// straight on the transform it owns, not a Storyboard hung off a Style trigger: a Storyboard animating
    /// a path through a Brush's own RelativeTransform via a Style's implicit target corrupted later renders
    /// of unrelated windows in the render harness. Gated on ClientAreaAnimation, the actual "animations on"
    /// system setting, like the disk's own breathe.
    /// </summary>
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
            ToolbarLightSlide.ApplyAnimationClock(TranslateTransform.XProperty, clock);

            // Started while already minimised or hidden (e.g. a window restored straight into a
            // cleanup already running): match the paused state ApplyToolbarSweepVisibility set, or the
            // fresh clock would animate unseen until the next visibility change.
            if (_toolbarSweepPausedForVisibility)
                clock.Controller?.Pause();
        }
        else
        {
            ToolbarLightSlide.ApplyAnimationClock(TranslateTransform.XProperty, null);
            ToolbarLightSlide.X = -0.75;
            _toolbarSweepClock = null;
        }
    }

    /// <summary>Pausing here never fights the run's own state, and restoring the window never starts a
    /// sweep the run itself already ended.</summary>
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
            InstallBar.BeginAnimation(PillProgressBar.ValueProperty, null);
            InstallBar.Value = target;
            return;
        }

        // Short and eased: long enough to read as motion,
        // short enough that a fast-moving batch does not lag behind the numbers beside it.
        var glide = new DoubleAnimation(target, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Timeline.SetDesiredFrameRate(glide, 60);
        InstallBar.BeginAnimation(PillProgressBar.ValueProperty, glide);
    }

    /// <summary>
    /// Row-level keyboard shortcuts. Space toggles the checkbox, Enter runs the row's primary action, Entf
    /// ignores. Fires ONLY when the row itself (the ListBoxItem, or a Steam child's own line) has focus, not
    /// one of its buttons or its checkbox: those handle Space/Enter themselves, and re-handling here would
    /// fire twice. Attached once at the ListBox: PreviewKeyDown tunnels from there to whichever element has
    /// focus, nested Steam children included. Arrow-key/Tab movement between rows and their children needs
    /// no code here: it is ordinary WPF Selector/KeyboardNavigation behaviour once every focusable element
    /// exists.
    /// </summary>
    private void OnUpdatesListPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is not FrameworkElement { DataContext: UpdateRowViewModel row } focused)
            return;

        if (focused is not (System.Windows.Controls.ListBoxItem or System.Windows.Controls.Border))
            return;

        // Chorded keys belong to the window's KeyBindings: handling Ctrl+Enter here ran the focused row's own
        // action instead of "install selected", the shortcut the tooltip and the F1 window promise.
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        switch (e.Key)
        {
            case Key.Space:
                row.ToggleSelectCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                row.ExecuteDefaultAction();
                e.Handled = true;
                break;
            case Key.Delete:
                if (row.IgnoreCommand.CanExecute(null))
                {
                    row.IgnoreCommand.Execute(null);
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>Reachable from inside the table (see the CommandBinding beside <c>UpdatesListBox</c> in
    /// XAML): forwards to the SAME SelectAllCommand the window-level Ctrl+A KeyBinding already runs when
    /// focus sits somewhere the ListBox does not intercept the key first - no second copy of the selection
    /// logic.</summary>
    private void OnUpdatesListSelectAllExecuted(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        _viewModel.SelectAllCommand.Execute(null);
        e.Handled = true;
    }

    private void OnUpdatesListSelectAllCanExecute(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = true;
        e.Handled = true;
    }

    // WinForms carries same-named Point/MouseEventArgs/DragEventArgs types (GlobalUsings.cs), so all
    // three are qualified at their use site here rather than added to that project-wide alias list.
    private System.Windows.Point? _queueDragStart;
    private UpdateRowViewModel? _queueDragRow;
    private System.Windows.Documents.AdornerLayer? _queueDragAdornerLayer;
    private DragAdorner? _queueDragAdorner;

    /// <summary>Remembers the row and pointer position a drag might start from. Set on every press,
    /// consumed only if the pointer actually moves far enough while still held (see <see cref="OnQueueRowMouseMove"/>).</summary>
    private void OnQueueRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _queueDragStart = e.GetPosition(null);
        _queueDragRow = (sender as FrameworkElement)?.DataContext as UpdateRowViewModel;
    }

    private void OnQueueRowMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // A press that ended without a drag: it must not arm a drag for a LATER press that this card's
            // MouseDown never saw (the remove button handles its own press, but its MouseMove still bubbles
            // here), which dragged the previously clicked card instead and swallowed the removal.
            _queueDragRow = null;
            _queueDragStart = null;
            return;
        }

        if (_queueDragRow is null || _queueDragStart is not { } start
            || !ReferenceEquals((sender as FrameworkElement)?.DataContext, _queueDragRow))
            return;

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var dragged = _queueDragRow;
        _queueDragRow = null;

        var draggedElement = (UIElement)sender;
        _queueDragAdornerLayer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(draggedElement);
        if (_queueDragAdornerLayer is not null)
        {
            _queueDragAdorner = new DragAdorner(draggedElement, draggedElement);
            _queueDragAdornerLayer.Add(_queueDragAdorner);
        }

        try
        {
            DragDrop.DoDragDrop(draggedElement, dragged, System.Windows.DragDropEffects.Move);
        }
        finally
        {
            if (_queueDragAdornerLayer is not null && _queueDragAdorner is not null)
                _queueDragAdornerLayer.Remove(_queueDragAdorner);
            _queueDragAdornerLayer = null;
            _queueDragAdorner = null;
        }
    }

    /// <summary>
    /// Moves the drag preview to follow the cursor. GiveFeedback carries no screen point of its own, so
    /// the actual position comes from WinForms' Cursor (already referenced in this project for the tray
    /// icon), converted into the adorned element's own coordinate space.
    /// </summary>
    private void OnQueueRowGiveFeedback(object sender, System.Windows.GiveFeedbackEventArgs e)
    {
        if (_queueDragAdorner is not null && sender is UIElement element)
        {
            var screenPoint = System.Windows.Forms.Cursor.Position;
            var relative = element.PointFromScreen(new System.Windows.Point(screenPoint.X, screenPoint.Y));
            _queueDragAdorner.UpdatePosition(new System.Windows.Point(relative.X + 12, relative.Y + 8));
        }

        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void OnQueueRowDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(UpdateRowViewModel)) ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Reorders the queue panel's own list AND pushes the new order down into whatever is
    /// actually holding those rows back. <see cref="MainViewModel.MoveQueueRow"/> does the latter, so a
    /// drag-and-drop here changes real install order, not just this visual list.</summary>
    private void OnQueueRowDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(typeof(UpdateRowViewModel)) is not UpdateRowViewModel dragged
            || (sender as FrameworkElement)?.DataContext is not UpdateRowViewModel target
            || ReferenceEquals(dragged, target)
            || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var oldIndex = viewModel.QueueRows.IndexOf(dragged);
        var newIndex = viewModel.QueueRows.IndexOf(target);
        if (oldIndex >= 0 && newIndex >= 0)
        {
            viewModel.MoveQueueRow(oldIndex, newIndex);
            RefreshQueueRowAutomationNames(viewModel);
        }

        e.Handled = true;
    }

    /// <summary>Entf removes the focused queue row (the same command its own reveal-on-focus/hover
    /// button already carries, found by name within the row's own DataTemplate rather than re-deriving
    /// the ItemsControl's DataContext by hand); Alt+Up/Down moves it one place, since drag is otherwise
    /// the only way to reorder the queue. Alt survives only as <see cref="System.Windows.Input.KeyEventArgs.SystemKey"/>
    /// while held, e.Key itself reports <see cref="Key.System"/> for any key chorded with it.</summary>
    private void OnQueueRowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not FrameworkElement card)
            return;

        if (e.Key == Key.Delete)
        {
            if (card.FindName("RemoveQueueButton") is not Wpf.Ui.Controls.Button { Command: { } command } button)
                return;

            if (command.CanExecute(button.CommandParameter))
            {
                command.Execute(button.CommandParameter);
                e.Handled = true;
            }

            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || (key != Key.Up && key != Key.Down))
            return;

        if (card.DataContext is not UpdateRowViewModel row || DataContext is not MainViewModel viewModel)
            return;

        var oldIndex = viewModel.QueueRows.IndexOf(row);
        if (oldIndex < 0)
            return;

        var newIndex = key == Key.Up ? oldIndex - 1 : oldIndex + 1;
        if (newIndex < 0 || newIndex >= viewModel.QueueRows.Count)
            return;

        viewModel.MoveQueueRow(oldIndex, newIndex);
        RefreshQueueRowAutomationNames(viewModel);
        e.Handled = true;
    }

    /// <summary>The row card's own accessible name at the moment it is created; the reorder handlers
    /// above then refresh every already-generated row afterward (see
    /// <see cref="RefreshQueueRowAutomationNames"/> for why a move needs its own explicit pass).</summary>
    private void OnQueueRowCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement card && DataContext is MainViewModel viewModel)
            SetQueueRowAutomationName(card, viewModel);
    }

    private static void SetQueueRowAutomationName(FrameworkElement card, MainViewModel viewModel)
    {
        if (card.DataContext is not UpdateRowViewModel row)
            return;

        var index = viewModel.QueueRows.IndexOf(row);
        if (index < 0)
            return;

        AutomationProperties.SetName(card,
            string.Format(viewModel.Loc["QueueRowPositionAutomation"], row.Name, index + 1, viewModel.QueueRows.Count));
    }

    /// <summary>
    /// Re-stamps every currently generated queue row's accessible name after a reorder. WPF assigns a
    /// container's <c>AlternationIndex</c> only when that container is generated and never recomputes it
    /// for the OTHER rows a Move shifted, so a binding on it would leave every row but the one just
    /// (re)generated announcing a stale position; setting the name by hand for the whole visible set after
    /// every move avoids that.
    /// </summary>
    private void RefreshQueueRowAutomationNames(MainViewModel viewModel)
    {
        if (QueueItemsControl.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
            return;

        for (var i = 0; i < QueueItemsControl.Items.Count; i++)
        {
            if (QueueItemsControl.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container)
                continue;
            if (FindQueueRowCard(container) is { } card)
                SetQueueRowAutomationName(card, viewModel);
        }
    }

    private static FrameworkElement? FindQueueRowCard(DependencyObject root)
    {
        if (root is FrameworkElement { Name: "QueueRowCard" } match)
            return match;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            if (FindQueueRowCard(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;

        return null;
    }

    /// <summary>Enter/Space on a sortable column header or the source pill; see
    /// <see cref="KeyboardActivation"/>.</summary>
    private void OnClickTargetKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => KeyboardActivation.ActivateOnEnterOrSpace(sender, e);

    /// <summary>Right-click a source pill to copy its raw error reason: the tooltip says so.
    /// Silent no-op for a pill with nothing to copy.</summary>
    private void OnErrorPillRightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProviderStatusViewModel { CopyableErrorText.Length: > 0 } status)
            return;

        try
        {
            System.Windows.Clipboard.SetText(status.CopyableErrorText);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // the clipboard can be held open by another process; a failed copy is not worth surfacing
        }
    }

    /// <summary>The last state that was not minimized, so tray Open and a second launch restore a
    /// maximized window maximized instead of forcing it to Normal.</summary>
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

        // Any other startup location re-centres on the monitor under the mouse and throws these bounds away,
        // and a minimum larger than the work area makes WPF grow the window past the clamp; either one put the
        // title bar above the top edge of a smaller or more scaled monitor.
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

    /// <summary>
    /// The monitor the OS picks for a rect in DIPs: the one it overlaps most, the nearest when it overlaps
    /// none. The scale is the primary display's, which is what <see cref="SystemParameters"/> (and so every
    /// coordinate saved before) is expressed in.
    /// </summary>
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

    /// <summary>
    /// Where the window opens. A saved rect that overlaps its monitor (<paramref name="monitorForSaved"/>,
    /// as the OS resolves it) is fitted to that monitor's work area, not to the bounding box of all
    /// monitors, which has dead corners and ignores a secondary taskbar. Nothing saved, or a rect that
    /// overlaps no monitor any more, centres the default size on the primary work area.
    /// <c>OnMonitor</c> is true only for the first case.
    /// </summary>
    internal static (Rect Bounds, bool OnMonitor) ResolveRestoreBounds(
        Rect? saved, MonitorArea? monitorForSaved, MonitorArea primary, System.Windows.Size defaultSize)
    {
        if (saved is { } rect && monitorForSaved is { } monitor && rect.IntersectsWith(monitor.Bounds))
            return (ClampToVisible(rect, monitor.WorkArea), true);

        // Nothing usable saved: the XAML size centred on the primary display can be taller than the work area
        // (1024x768), which pushed the title bar above the edge.
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

    /// <summary>
    /// A saved rect that merely INTERSECTS the current virtual screen can still be almost entirely off it
    /// (e.g. a second monitor removed since, leaving only a sliver on the primary one), so the caller's
    /// intersect check alone does not guarantee a visible window. Pulls the rect fully onto the screen:
    /// shrinks it first if it is wider/taller than the screen, then slides it so no edge falls outside.
    /// Pure geometry (no WPF dependency beyond <see cref="Rect"/>), unit-tested directly.
    /// </summary>
    internal static Rect ClampToVisible(Rect saved, Rect virtualScreen)
    {
        var width = Math.Min(saved.Width, virtualScreen.Width);
        var height = Math.Min(saved.Height, virtualScreen.Height);

        var left = Math.Clamp(saved.Left, virtualScreen.Left, virtualScreen.Right - width);
        var top = Math.Clamp(saved.Top, virtualScreen.Top, virtualScreen.Bottom - height);

        return new Rect(left, top, width, height);
    }
}
