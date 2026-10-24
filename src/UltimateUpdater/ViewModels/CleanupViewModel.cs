using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Views;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// Backs the cleanup dialog. The scan starts as soon as the window opens and
/// streams each target's figure onto its row; the ring in the middle shows the selected share of what could
/// be freed and turns into a progress ring while cleaning. Nothing is ever deleted without the explicit
/// second confirmation; there is deliberately no automatic cleanup at all.
/// </summary>
public partial class CleanupViewModel : ObservableObject
{
    private readonly CleanupService _service;
    private readonly LoggingService _logging;
    private readonly SettingsService _settings;

    /// <summary>For the window's own code-behind, which has no service of its own but still hands paths to
    /// <see cref="UriLauncher"/> and needs somewhere for a refusal to be recorded.</summary>
    internal LoggingService Logging => _logging;
    private CancellationTokenSource? _scanCts;

    /// <summary>The running scan itself, not only its cancellation source: a run must be able to wait for
    /// the scan to actually stop touching <see cref="Items"/>, which cancelling the token alone does not
    /// guarantee (see <see cref="CleanupViewModel.AwaitScanUnwindAsync"/>).</summary>
    private Task? _scanTask;

    private CancellationTokenSource? _cleanCts;

    /// <summary>
    /// Holds the run between files. Shared with the elevated helper through <see cref="_cleanJob"/>, so
    /// Pause holds both halves of a run rather than only the unelevated one.
    /// </summary>
    private readonly PauseGate _pause = new();

    /// <summary>
    /// The elevated halves of the current run, so Pause and Stop reach them too. A list rather than one
    /// handle: several targets can be in flight at once now, and a single field would have left Pause
    /// talking to whichever job happened to start last.
    /// </summary>
    private readonly List<ElevatedJobHandle> _cleanJobs = [];

    /// <summary>
    /// The app-wide elevated helper. Shared with installing, so whichever came first paid for the prompt
    /// and this dialog raises none of its own.
    /// </summary>
    private readonly ElevatedHost? _elevated;

    /// <summary>Running since the moment this dialog started opening, so <see cref="LogFirstFrame"/> can
    /// report how long the window actually took to show something, catalogue load included.</summary>
    private readonly System.Diagnostics.Stopwatch _openStopwatch = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>The thread this view model was actually constructed on, deliberately NOT <see
    /// cref="_dispatcher"/>'s "the real app's UI thread if one happens to exist" fallback: <see
    /// cref="CleanupItemViewModel"/>'s constructor creates a <see
    /// cref="System.Windows.Data.ListCollectionView"/> that is permanently tied to whatever dispatcher was
    /// current when it was built, and that must be the SAME dispatcher a later caller mutates it from. In
    /// the real app that is always the UI thread anyway (this type is only ever constructed there); a test
    /// harness that happens to run a second, unrelated <see cref="System.Windows.Application"/> elsewhere
    /// in the process must not redirect this one to that other thread.</summary>
    private readonly System.Windows.Threading.Dispatcher _catalogDispatcher =
        System.Windows.Threading.Dispatcher.CurrentDispatcher;

    /// <summary>Defaults to the real <see cref="CleanupCatalog.Build"/>; a test overrides it to prove
    /// <see cref="LoadCatalogAsync"/> survives a catalogue that fails outright, without needing a real
    /// failure condition on the machine running it.</summary>
    private readonly Func<Action<string>?, IReadOnlyList<CleanupTarget>> _catalogBuilder;

    /// <summary>The UI thread; elevated replies arrive on the pipe reader thread and must be marshalled
    /// before touching a bound property (null fallback for the harness).</summary>
    private readonly System.Windows.Threading.Dispatcher _dispatcher =
        System.Windows.Application.Current?.Dispatcher
        ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

    public LocalizationService Loc => LocalizationService.Instance;

    private IReadOnlyList<CleanupItemViewModel> items = [];

    /// <summary>Empty until <see cref="CatalogReadyTask"/> finishes; see that property's own comment.</summary>
    public IReadOnlyList<CleanupItemViewModel> Items
    {
        get => items;
        private set
        {
            items = value;
            OnPropertyChanged();
            OnHiddenCategoriesChanged();
        }
    }

    /// <summary>True from construction until the catalogue has been built; the window shows a loading
    /// state while this is true instead of the (still empty) detail list.</summary>
    [ObservableProperty]
    private bool isCatalogLoading = true;

    /// <summary>True once building the catalogue itself failed outright (not a single drive skipped, see
    /// <see cref="CleanupCatalog.GatherDriveFacts{TDrive}"/>, but the whole background build throwing): the
    /// window shows a plain error in place of the loading state instead of hanging on it forever, and
    /// <see cref="Items"/>/<see cref="Groups"/> simply stay empty.</summary>
    [ObservableProperty]
    private bool catalogLoadFailed;

    /// <summary>
    /// Completes once <see cref="Items"/> and <see cref="Groups"/> are populated. Building the catalogue
    /// (<see cref="CleanupCatalog.Build"/>) walks every drive through WMI and checks a long list of
    /// folders for existence, real disk and WMI I/O that must stay off the UI thread inside this
    /// constructor, or the window's very first frame blocks. A test proving the constructor itself
    /// stays free of that work only has to check that <see cref="Items"/> is still empty right after
    /// construction; awaiting this task is the seam for everything else.
    /// </summary>
    public Task CatalogReadyTask { get; }

    private IReadOnlyList<CleanupGroupViewModel> groups = [];

    /// <summary>The dialog's sections; <see cref="Items"/> grouped for display only. Empty until <see
    /// cref="CatalogReadyTask"/> finishes.</summary>
    public IReadOnlyList<CleanupGroupViewModel> Groups
    {
        get => groups;
        private set { groups = value; OnPropertyChanged(); }
    }

    private readonly CleanupHistoryService _cleanupHistory;

    public CleanupViewModel(
        CleanupService service, LoggingService logging, SettingsService settings, ElevatedHost? elevated = null,
        CleanupHistoryService? history = null, Func<Action<string>?, IReadOnlyList<CleanupTarget>>? catalogBuilder = null)
    {
        _service = service;
        _logging = logging;
        _settings = settings;
        _elevated = elevated;
        _cleanupHistory = history ?? new CleanupHistoryService(logging);
        _catalogBuilder = catalogBuilder ?? CleanupCatalog.Build;

        CatalogReadyTask = LoadCatalogAsync();

        foreach (var entry in _cleanupHistory.Load())
            History.Add(new CleanupHistoryRowViewModel(entry, RemoveHistoryEntry));

        // Runs only while a cleanup does (started/stopped by RunCleanupAsync); an idle dialog owns no timer.
        _entryProgressTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _entryProgressTimer.Tick += (_, _) => FlushEntryProgress();

        // The remembered column chooser state for the cleanup detail list.
        if (_settings.Current.Ui.Tables.TryGetValue("cleanup", out var tableState))
            isSizeColumnHidden = tableState.HiddenColumns.Contains("size");

        // Subscribed before the catalogue exists on purpose: a helper starting or stopping while
        // LoadCatalogAsync is still running must still be reflected once Items is actually populated
        // (see the elevation hint call at the end of LoadCatalogAsync).
        if (_elevated is not null)
            _elevated.StateChanged += OnElevatedStateChanged;
    }

    /// <summary>
    /// Fetches the catalogue's raw data (<see cref="CleanupCatalog.Build"/>: a per-drive WMI lookup plus a
    /// handful of <c>Directory.Exists</c> checks, plain data, no WPF types) on a background thread, then
    /// wraps it into view models and publishes them on the thread this view model itself was constructed
    /// on. That second step cannot run on just any thread: <see cref="CleanupItemViewModel"/>'s own
    /// constructor creates a <see cref="System.Windows.Data.ListCollectionView"/> over its entry list,
    /// which binds itself to whatever dispatcher is current at the time and refuses every later mutation
    /// from a different one. <see cref="_catalogDispatcher"/>'s own <c>InvokeAsync</c> queues that work
    /// instead of running it inline, so the real app (whose dispatcher is already pumping its main loop)
    /// picks it up on its own, and a test with no message loop of its own can still observe it by pumping
    /// a nested frame while it waits (see <c>WpfHarness.WaitForCleanupCatalog</c>).
    /// </summary>
    private async Task LoadCatalogAsync()
    {
        try
        {
            var targets = await Task.Run(() => _catalogBuilder(_logging.Warn));

            await _catalogDispatcher.InvokeAsync(() =>
            {
                var (builtItems, builtGroups) = BuildItemsAndGroups(targets);
                Items = builtItems;
                Groups = builtGroups;
                IsCatalogLoading = false;

                // A row's "needs admin" mark must reflect what is ALREADY true the instant the
                // catalogue appears (the helper may have been started earlier, by an install in the same
                // session), not just the target's own NeedsElevation set blindly at construction and only
                // corrected once the scan's own elevation step later runs.
                SetElevationHints(show: !HasElevationAvailable());
            }).Task;
        }
        catch (Exception ex)
        {
            // A single unreadable drive is already handled inside the catalogue build itself (skipped and
            // logged, see CleanupCatalog.GatherDriveFacts); anything that still reaches here is broader
            // (a folder-existence check, a registry read) and would fault this very task, which the
            // window's Loaded handler awaits unguarded and would re-throw on the dispatcher, taking the whole
            // app down. This must never rethrow: the dialog shows a plain error instead, and the user
            // closes it rather than losing the app underneath it.
            _logging.Error("Cleanup: the catalogue could not be built.", ex);

            await _catalogDispatcher.InvokeAsync(() =>
            {
                IsCatalogLoading = false;
                CatalogLoadFailed = true;
            }).Task;
        }
    }

    private (IReadOnlyList<CleanupItemViewModel> Items, IReadOnlyList<CleanupGroupViewModel> Groups) BuildItemsAndGroups(
        IReadOnlyList<CleanupTarget> targets)
    {
        var built = targets
            .Select(target => new CleanupItemViewModel(
                target, RefreshTotals, PermanentlyExcludePath,
                target.Kind == CleanupKind.RecycleBin ? _settings.Current.CleanupRecycleBinMinAgeDays : 0,
                target.Kind == CleanupKind.RecycleBin ? OnRecycleBinMinAgeDaysChanged : null))
            .ToList();

        foreach (var item in built.Where(i => i.IsComponentCleanup))
            item.ComponentStoreHost = this;

        // Restore last session's checked state. Recycle bin and leftover-installs are
        // excluded on purpose; their "off" default is a safety decision, not a convenience, so they stay
        // out regardless of what was saved. An id with no saved entry (new catalogue target, or nothing
        // saved yet) simply keeps the DefaultEnabled it was constructed with just above.
        foreach (var item in built)
        {
            if (item.Target.Kind is CleanupKind.RecycleBin or CleanupKind.LeftoverInstalls)
                continue;

            if (_settings.Current.CleanupSelection.TryGetValue(item.Target.Id, out var wasSelected))
                item.IsSelected = wasSelected;
        }

        // Fixed once here, never rebuilt; grouping is a view over the same Items,
        // built in a stable order so an Expander the user opened never resets while a scan is filling
        // in figures underneath it.
        var builtGroups = CleanupGroups.Order
            .Select(key => new CleanupGroupViewModel(
                key, built.Where(i => CleanupGroups.For(i.Target) == key).ToList(),
                key == CleanupGroupKey.Registry ? () => _settings.Current.RegistryCleanup : null,
                key == CleanupGroupKey.Registry ? _logging : null))
            .Where(group => group.Items.Count > 0)
            .ToList();

        return (built, builtGroups);
    }

    /// <summary>True once admin rights are available for this run: the process itself is elevated, or the
    /// app-wide elevated helper is already connected. Read live, never cached, so a helper started or lost
    /// after this dialog opened is reflected without waiting for the next scan.</summary>
    private bool HasElevationAvailable() => ElevationService.IsElevated || (_elevated?.IsRunning ?? false);

    /// <summary>Fires on whatever thread noticed the helper start or stop (see <see
    /// cref="ElevatedHost.StateChanged"/>); marshalled here before touching any bound property. Posted, never
    /// waited on: the pipe thread raising this must not block against a UI thread that is itself waiting on
    /// that pipe, which is exactly where app exit stands while it shuts the helper down.</summary>
    private void OnElevatedStateChanged() =>
        _dispatcher.BeginInvoke(() => SetElevationHints(show: !HasElevationAvailable()));

    /// <summary>
    /// The cleanup detail list's own column list for the shared chooser
    /// (<see cref="Views.ColumnChooser"/>). Unlike the main and history tables, this list has no clickable
    /// header row and no per-column sort of its own, so only hiding is offered here; the checkbox/name
    /// columns identify the row and may not be hidden, "size" is the one real optional column (the date
    /// line below the name is not a column of its own).
    /// </summary>
    public static IReadOnlyList<ColumnDescriptor> DetailListColumns { get; } =
    [
        new("size", () => LocalizationService.Instance["ColumnSize"], CanHide: true),
    ];

    /// <summary>Whether the detail list's Size column is hidden; bound from CleanupWindow.xaml on every
    /// entry row via a DataTrigger, alongside that row's own HasSize.</summary>
    [ObservableProperty]
    private bool isSizeColumnHidden;

    public bool IsDetailColumnHidden(string columnId) => columnId == "size" && IsSizeColumnHidden;

    public void SetDetailColumnHidden(string columnId, bool hidden)
    {
        if (columnId == "size")
            IsSizeColumnHidden = hidden;

        SaveTableLayout();
    }

    public void ResetDetailColumns()
    {
        IsSizeColumnHidden = false;
        SaveTableLayout();
    }

    private void SaveTableLayout()
    {
        _settings.Current.Ui.Tables["cleanup"] = new TableLayoutState
        {
            HiddenColumns = IsSizeColumnHidden ? ["size"] : [],
        };
        _settings.SaveDeferred();
    }

    /// <summary>One elevated job's Pause/Resume failing (a dead pipe, the job having just ended) must not
    /// stop the rest of the batch from being told the same thing; logged, never thrown further.</summary>
    private async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logging.Warn($"Cleanup: telling an elevated job to pause/resume failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The language changed while this dialog's view model was alive; it outlives its window on purpose, so
    /// it is the one that has to re-read its own texts. Every property here builds its text in code, and the
    /// areas and history rows own more of them.
    /// </summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(TopAreas));
        OnPropertyChanged(nameof(FreeSpaceNowText));
        OnPropertyChanged(nameof(LastCleanupText));
        OnPropertyChanged(nameof(EmptyCountText));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(SkippedText));
        OnPropertyChanged(nameof(StoppingRepairText));
        OnPropertyChanged(nameof(HistoryEmptyText));
        OnPropertyChanged(nameof(DoneCountText));
        OnPropertyChanged(nameof(RemainingCountText));
        OnPropertyChanged(nameof(HistoryEmptyOverviewText));
        OnPropertyChanged(nameof(RestorePointText));
        OnPropertyChanged(nameof(ComponentStoreStatusText));
        OnPropertyChanged(nameof(MeasuredAtText));

        foreach (var item in Items)
            item.RefreshLocalizedText();

        foreach (var group in Groups)
            group.RefreshLocalizedText();

        foreach (var entry in History)
            entry.RefreshLocalizedText();

        // The three category names and the run-count sentence read localized text through their own
        // properties, rebuilt fresh on every get; re-raising is enough, nothing to rebuild by hand.
        RaiseHistoryOverviewChanged();
    }

    /// <summary>The dialog is closing, but nothing stops; closing cancels NOTHING. The elevated
    /// helper stays; it belongs to the app.</summary>
    public void WindowClosed()
    {
        // A run carries on behind the closed dialog, and from here on it costs nothing at all: there is
        // nothing to mark up while nobody can see it.
        IsWindowVisible = false;
        _windowAttached = false;

        // Deliberately NOT unsubscribed from the elevated helper here:
        // a still-busy instance is exactly the one MainViewModel.ShowCleanup() reuses for the next
        // window, and unsubscribing on every close would leave that reopened window's admin badge deaf to
        // the helper starting or stopping until the next full rescan.
        // WindowAttached() below re-subscribes idempotently whenever a window attaches; Discard() is the
        // only place that unsubscribes for good, once this instance itself is thrown away.

        // Remember what is checked for next time. Recycle bin and leftover-installs are
        // deliberately never written here; see the matching skip in the constructor.
        foreach (var item in Items)
        {
            if (item.Target.Kind is CleanupKind.RecycleBin or CleanupKind.LeftoverInstalls)
                continue;

            _settings.Current.CleanupSelection[item.Target.Id] = item.IsSelected;
        }

        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or ArgumentException)
        {
            _logging.Error("Failed to save settings.json", ex);
        }
    }

    /// <summary>Ends a measuring pass for good; used when this view model is being replaced outright.</summary>
    public void CancelScan() => _scanCts?.Cancel();

    /// <summary>True between <see cref="WindowAttached"/> and the window closing. Read by scans as each
    /// target starts: with no window, a scan runs in background thread mode; with one, at normal priority.
    /// Written on the UI thread, read from scan continuations.</summary>
    private volatile bool _windowAttached;

    /// <summary>Re-subscribes to the elevated helper's state changes when a (re)opened window attaches to
    /// this view model; idempotent (unsubscribes first), so a window that reopens on a still-busy,
    /// reused instance (see <see cref="WindowClosed"/>) never ends up with two live handlers. Harmless to
    /// call again on a freshly constructed instance, whose constructor already subscribed once.</summary>
    internal void WindowAttached()
    {
        _windowAttached = true;

        // A reused instance would otherwise time the reopened window from the FIRST open and log minutes.
        _openStopwatch.Restart();

        if (_elevated is null)
            return;

        _elevated.StateChanged -= OnElevatedStateChanged;
        _elevated.StateChanged += OnElevatedStateChanged;
    }

    /// <summary>Called only once this instance is being discarded for good, never merely because its
    /// window closed (see <see cref="WindowClosed"/>): the app-wide, session-lifetime elevated helper would
    /// otherwise keep calling back into an instance nobody can reach any more, and keep it alive for as
    /// long as the helper itself lives.</summary>
    internal void Discard()
    {
        _windowAttached = false;
        if (_elevated is not null)
            _elevated.StateChanged -= OnElevatedStateChanged;
    }

    /// <summary>True while this view model still has work of its own; it must not be replaced then. The
    /// component store analysis counts: reopening the dialog must reuse the instance that owns the running
    /// DISM call instead of starting a second analysis next to it.</summary>
    public bool IsBusy => IsScanning || IsCleaning || IsAnalyzingComponentStore || _runClaimed;

    /// <summary>"Last restore point: {date}", or the "none yet" line when this program has never
    /// created one; same read-back <see cref="Views.InstallPreflightWindow"/> shows.</summary>
    public string RestorePointText => RestorePointThrottle.DescribeLastCreated(_logging);

    /// <summary>Test seam: replaces the real rstrui.exe launch so a test never pops a live system dialog.</summary>
    internal Action? OpenRestorePointDialogOverride { get; set; }

    /// <summary>Opens the same "Create a restore point" dialog Windows itself ships
    /// (System32\rstrui.exe, full path so this never resolves through %PATH%).</summary>
    [RelayCommand]
    private void OpenRestorePointDialog()
    {
        if (OpenRestorePointDialogOverride is not null)
        {
            OpenRestorePointDialogOverride();
            return;
        }

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rstrui.exe");
        UriLauncher.LaunchDetached(path, _logging);
    }

    /// <summary>Called once by the window's own first-rendered-frame event; logs how long the whole open
    /// actually took, catalogue load included, so a slow open shows up in the log rather than only as a
    /// feeling.</summary>
    internal void LogFirstFrame() =>
        _logging.Info($"cleanup window first frame after {OpenElapsedMilliseconds} ms");

    internal long OpenElapsedMilliseconds => _openStopwatch.ElapsedMilliseconds;
}
