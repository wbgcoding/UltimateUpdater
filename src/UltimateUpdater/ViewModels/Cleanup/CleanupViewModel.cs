using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels.Cleanup;

/// <summary>Backs the cleanup dialog. The scan starts when the window opens and streams each target's figure onto its row; the ring shows the selected share and turns into a progress ring while cleaning. Nothing is deleted without the explicit second confirmation, and there is no automatic cleanup.</summary>
public partial class CleanupViewModel : ObservableObject
{
    private readonly CleanupService _service;
    private readonly LoggingService _logging;
    private readonly SettingsService _settings;

    /// <summary>For the window's code-behind, which hands paths to <see cref="UriLauncher"/> and needs somewhere to record a refusal.</summary>
    internal LoggingService Logging => _logging;
    private CancellationTokenSource? _scanCts;

    /// <summary>The running scan itself, not only its cancellation source: a run must wait for the scan to stop touching <see cref="Items"/> (see <see cref="CleanupViewModel.AwaitScanUnwindAsync"/>).</summary>
    private Task? _scanTask;

    private CancellationTokenSource? _cleanCts;

    /// <summary>Holds the run between files. Shared with the elevated helper through <see cref="_cleanJob"/>, so Pause holds both halves.</summary>
    private readonly PauseGate _pause = new();

    /// <summary>The elevated halves of the current run, so Pause and Stop reach them. A list because several targets can be in flight at once.</summary>
    private readonly List<ElevatedJobHandle> _cleanJobs = [];

    /// <summary>The app-wide elevated helper, shared with installing, so this dialog raises no prompt of its own.</summary>
    private readonly ElevatedHost? _elevated;

    /// <summary>Running since this dialog started opening, so <see cref="LogFirstFrame"/> can report how long the window took, catalogue load included.</summary>
    private readonly System.Diagnostics.Stopwatch _openStopwatch = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>The thread this view model was constructed on, deliberately not <see cref="_dispatcher"/>'s fallback: <see cref="CleanupItemViewModel"/> creates a <see cref="System.Windows.Data.ListCollectionView"/> tied to the dispatcher current at construction, and later mutations must come from that same one.</summary>
    private readonly System.Windows.Threading.Dispatcher _catalogDispatcher =
        System.Windows.Threading.Dispatcher.CurrentDispatcher;

    /// <summary>Defaults to the real <see cref="CleanupCatalog.Build"/>; a test overrides it to prove <see cref="LoadCatalogAsync"/> survives a failing catalogue.</summary>
    private readonly Func<Action<string>?, IReadOnlyList<CleanupTarget>> _catalogBuilder;

    /// <summary>The UI thread; elevated replies arrive on the pipe reader thread and must be marshalled before touching a bound property.</summary>
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

    /// <summary>True from construction until the catalogue has been built; the window shows a loading state meanwhile.</summary>
    [ObservableProperty]
    private bool isCatalogLoading = true;

    /// <summary>True when the whole background catalogue build failed: the window shows a plain error instead of loading forever, and <see cref="Items"/> and <see cref="Groups"/> stay empty.</summary>
    [ObservableProperty]
    private bool catalogLoadFailed;

    /// <summary>Completes once <see cref="Items"/> and <see cref="Groups"/> are populated. Building the catalogue walks every drive through WMI and checks many folders, so it stays off the UI thread and out of the constructor. A test checks that <see cref="Items"/> is still empty right after construction; awaiting this task is the seam for everything else.</summary>
    public Task CatalogReadyTask { get; }

    private IReadOnlyList<CleanupGroupViewModel> groups = [];

    /// <summary>The dialog's sections: <see cref="Items"/> grouped for display. Empty until <see cref="CatalogReadyTask"/> finishes.</summary>
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
        _entryProgressTimer.Tick += (_, _) =>
        {
            FlushEntryProgress();
            RefreshElapsed();
            DecayStalledActivity(System.Diagnostics.Stopwatch.GetTimestamp());
        };

        // The remembered column chooser state for the cleanup detail list.
        if (_settings.Current.Ui.Tables.TryGetValue("cleanup", out var tableState))
            isSizeColumnHidden = tableState.HiddenColumns.Contains("size");

        // Subscribed before the catalogue exists: a helper starting or stopping while LoadCatalogAsync runs must still be reflected once Items is populated.
        if (_elevated is not null)
            _elevated.StateChanged += OnElevatedStateChanged;
    }

    /// <summary>Fetches the catalogue's raw data on a background thread, then wraps it into view models on the thread this view model was constructed on: <see cref="CleanupItemViewModel"/> creates a <see cref="System.Windows.Data.ListCollectionView"/> bound to the current dispatcher, which refuses later mutations from another. <c>InvokeAsync</c> queues the work, so a test without a message loop can observe it by pumping a nested frame (see <c>WpfHarness.WaitForCleanupCatalog</c>).</summary>
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

                // A row's "needs admin" mark must reflect what is already true when the catalogue appears: the helper may have been started earlier by an install.
                SetElevationHints(show: !HasElevationAvailable());
            }).Task;
        }
        catch (Exception ex)
        {
            // One unreadable drive is handled inside the catalogue build; anything reaching here is broader and would fault this task, which the window's Loaded handler awaits unguarded and would re-throw on the dispatcher. Never rethrow: the dialog shows a plain error.
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

        foreach (var item in built)
            item.RescanCommand = RescanItemCommand;

        // Restore last session's checked state. Recycle bin and leftover-installs are excluded on purpose: their "off" default is a safety decision. An id with no saved entry keeps its DefaultEnabled.
        foreach (var item in built)
        {
            if (item.Target.Kind is CleanupKind.RecycleBin or CleanupKind.LeftoverInstalls)
                continue;

            if (_settings.Current.CleanupSelection.TryGetValue(item.Target.Id, out var wasSelected))
                item.IsSelected = wasSelected;
        }

        // Fixed once and never rebuilt, in a stable order, so an Expander the user opened never resets while a scan fills in figures.
        var builtGroups = CleanupGroups.Order
            .Select(key => new CleanupGroupViewModel(
                key, built.Where(i => CleanupGroups.For(i.Target) == key).ToList(),
                key == CleanupGroupKey.Registry ? () => _settings.Current.RegistryCleanup : null,
                key == CleanupGroupKey.Registry ? _logging : null,
                WillSkipForMissingAdmin))
            .Where(group => group.Items.Count > 0)
            .ToList();

        foreach (var group in builtGroups)
            group.RescanCommand = RescanGroupCommand;

        return (built, builtGroups);
    }

    /// <summary>True once admin rights are available: the process is elevated or the app-wide helper is connected. Read live so a helper started or lost after the dialog opened is reflected.</summary>
    private bool HasElevationAvailable() => ElevationService.IsElevated || (_elevated?.IsRunning ?? false);

    /// <summary>Fires on whatever thread noticed the helper start or stop (see <see cref="ElevatedHost.StateChanged"/>). Posted, never waited on: the pipe thread must not block against a UI thread that is itself waiting on that pipe, as at app exit.</summary>
    private void OnElevatedStateChanged() =>
        _dispatcher.BeginInvoke(() =>
        {
            SetElevationHints(show: !HasElevationAvailable());

            // Rights that arrive mid-session (an admin install started the helper): measure what they unlock now, quietly.
            if (HasElevationAvailable())
            {
                StartQuietComponentStoreAnalysis();
                StartLateMeasuring();
            }
        });

    /// <summary>The detail list's columns for the shared chooser (<see cref="ColumnChooser"/>). This list has no header row and no sort, so only hiding is offered; the checkbox and name columns may not be hidden, and "size" is the one optional column.</summary>
    public static IReadOnlyList<ColumnDescriptor> DetailListColumns { get; } =
    [
        new("size", () => LocalizationService.Instance["ColumnSize"], CanHide: true),
    ];

    /// <summary>Whether the detail list's Size column is hidden; bound from CleanupWindow.xaml on every entry row via a DataTrigger.</summary>
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

    /// <summary>One elevated job's Pause/Resume failing (a dead pipe, a job just ended) must not stop the rest of the batch; logged, never thrown.</summary>
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

    /// <summary>The language changed. This view model outlives its window on purpose, so it re-reads its own texts, which it builds in code, plus those of the areas and history rows.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(SelectedOfFoundText));
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
        RefreshStatusText();
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(StatusFreedText));
        OnPropertyChanged(nameof(HistoryEmptyOverviewText));
        OnPropertyChanged(nameof(RestorePointText));
        OnPropertyChanged(nameof(MeasuredAtText));
        OnPropertyChanged(nameof(ShowAllCategoriesText));
        OnPropertyChanged(nameof(DriveCaptionText));
        OnPropertyChanged(nameof(ComponentStoreSizeText));
        OnPropertyChanged(nameof(ComponentStoreRecommendedText));
        OnPropertyChanged(nameof(LastDoneText));
        OnPropertyChanged(nameof(AreaOfTotalText));
        OnPropertyChanged(nameof(FreedText));
        OnPropertyChanged(nameof(LiveFreedText));

        foreach (var item in Items)
            item.RefreshLocalizedText();

        foreach (var group in Groups)
            group.RefreshLocalizedText();

        foreach (var entry in History)
            entry.RefreshLocalizedText();

        // The category names and the run-count sentence read localized text on every get; re-raising is enough.
        RaiseHistoryOverviewChanged();
    }

    /// <summary>The dialog is closing, but closing cancels nothing. The elevated helper stays; it belongs to the app.</summary>
    public void WindowClosed()
    {
        // A run carries on behind the closed dialog and costs nothing extra: there is nothing to mark up while nobody can see it.
        IsWindowVisible = false;
        _windowAttached = false;

        // Deliberately not unsubscribed from the elevated helper here: a still-busy instance is reused for the next window, which would otherwise be deaf to the helper starting or stopping. WindowAttached() re-subscribes idempotently; only Discard() unsubscribes for good.

        // Remember what is checked for next time. Recycle bin and leftover-installs are never written; see the matching skip in the constructor.
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

    /// <summary>True between <see cref="WindowAttached"/> and the window closing. Read by scans as each target starts: with no window a scan runs in background thread mode, with one at normal priority.</summary>
    private volatile bool _windowAttached;

    /// <summary>Re-subscribes to the helper's state changes when a window attaches. Idempotent (unsubscribes first), so a reopened window on a reused instance (see <see cref="WindowClosed"/>) never ends up with two handlers.</summary>
    internal void WindowAttached()
    {
        _windowAttached = true;

        // A reused instance would otherwise time the reopened window from the first open.
        _openStopwatch.Restart();

        if (_elevated is null)
            return;

        _elevated.StateChanged -= OnElevatedStateChanged;
        _elevated.StateChanged += OnElevatedStateChanged;
    }

    /// <summary>Called only when this instance is discarded for good, never merely because its window closed: the session-lifetime helper would otherwise keep calling into an unreachable instance and keep it alive.</summary>
    internal void Discard()
    {
        _windowAttached = false;
        if (_elevated is not null)
            _elevated.StateChanged -= OnElevatedStateChanged;
    }

    /// <summary>True while this view model still has work of its own and must not be replaced; the component store analysis counts, so reopening reuses the instance that owns the running DISM call.</summary>
    public bool IsBusy => IsScanning || IsCleaning || IsAnalyzingComponentStore || _runClaimed;

    /// <summary>"Last restore point: {date}", or the "none yet" line; the same read-back <see cref="InstallPreflightWindow"/> shows.</summary>
    public string RestorePointText => RestorePointThrottle.DescribeLastCreated(_logging);

    /// <summary>Test seam: replaces the real rstrui.exe launch so a test never pops a live system dialog.</summary>
    internal Action? OpenRestorePointDialogOverride { get; set; }

    /// <summary>Opens Windows' own "Create a restore point" dialog (System32\rstrui.exe by full path, never through %PATH%).</summary>
    [RelayCommand]
    private void OpenRestorePointDialog()
    {
        if (OpenRestorePointDialogOverride is not null)
        {
            OpenRestorePointDialogOverride();
            return;
        }

        RestorePointService.OpenWindowsDialog(_logging);
    }

    /// <summary>Called by the window's first-rendered-frame event; logs how long the whole open took, catalogue load included.</summary>
    internal void LogFirstFrame() =>
        _logging.Info($"cleanup window first frame after {OpenElapsedMilliseconds} ms");

    internal long OpenElapsedMilliseconds => _openStopwatch.ElapsedMilliseconds;
}
