using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Models;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.ViewModels.Main;

public partial class MainViewModel : ObservableObject, IRowActions
{
    private readonly SettingsService _settings;
    private readonly LoggingService _logging;
    private readonly ThemeService _themeService;
    private readonly ScanOrchestrator _scanOrchestrator;
    private readonly IconCacheService _iconCache;
    private readonly InstallQueue _installQueue;
    private readonly IReadOnlyList<Services.Sources.IUpdateSource> _sources;
    private readonly HistoryService _history;

    /// <summary>The app-wide elevated helper, shared with the install queue so there is one prompt.</summary>
    private readonly ElevatedHost? _elevated;
    private readonly WingetBootstrapService? _wingetBootstrap;
    private readonly AutostartService _autostart;
    private readonly NotificationService _notifications;
    private readonly Services.SelfUpdate.SelfUpdateService _selfUpdate;
    private readonly EnrichmentCache _enrichmentCache;
    private readonly Func<string, CancellationToken, Task<string?>> _changelogFetcher;

    /// <summary>The click-triggered `npm view` lookup; a swappable seam like <see cref="_changelogFetcher"/> so a test can pass a fake without shelling out to npm.</summary>
    private readonly Func<string, CancellationToken, Task<NpmChangelogLookup.NpmChangelogSource>> _npmChangelogLookup;
    private readonly InstallLocationResolver _installLocations =
        new(WindowsRegistryView.Instance);

    /// <summary>The pre-install check, sharing the install-location resolver. Not readonly: a test passes a fake through the constructor's <c>installBlockers</c> parameter, since the real process probe reads whatever runs on the host.</summary>
    private Services.Install.InstallBlockers _installBlockers;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, ProviderStatusViewModel> _providerStatusById = new();

    /// <summary>Last (whole percent, status text) dispatched per install row. A redrawn progress line arrives many times a second and every report crosses a dispatcher (for an elevated install, a pipe first), so only a repeat of what the row already shows is dropped; see <see cref="OnInstallStatusChanged"/>.</summary>
    private readonly Dictionary<string, (double Percent, string? Status)> _lastDispatchedInstallStatus = new(StringComparer.OrdinalIgnoreCase);

    private readonly System.Collections.Concurrent.ConcurrentQueue<UpdateItem> _pendingItems = new();

    private readonly Func<bool> _canShowWingetOffer;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProviderStatus> _liveStatuses = new();
    private readonly DispatcherTimer _flushTimer;

    // A scan reconciles the table instead of clearing it: rows still present are reused, new ones added, vanished ones removed at the end, so the table does not blank and flash on every refresh. Keyed by "source:id".
    private readonly Dictionary<string, UpdateRowViewModel> _rowsByKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scanSeenKeys = new(StringComparer.Ordinal);

    /// <summary>Serialises scans; see ScanAsync for why overlapping runs corrupt the view.</summary>
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _installCts;
    private bool _scanCompletedCleanly;

    /// <summary>Bumped by every scan request before it queues. Two quick clicks on "Search" queue two waiters on <see cref="_scanGate"/>; a waiter that is no longer the newest when it gets the gate has been superseded and steps aside instead of running a redundant second scan.</summary>
    private int _scanGeneration;

    [ObservableProperty]
    private string selectedLanguage;

    [ObservableProperty]
    private string selectedTheme;

    public LocalizationService Loc => LocalizationService.Instance;

    public WindowPlacement? WindowPlacement => _settings.Current.Ui.WindowPlacement;

    public MainViewModel(
        SettingsService settings,
        LoggingService logging,
        ThemeService themeService,
        ScanOrchestrator scanOrchestrator,
        IconCacheService iconCache,
        InstallQueue installQueue,
        HistoryService history,
        AutostartService autostart,
        NotificationService notifications,
        Services.SelfUpdate.SelfUpdateService selfUpdate,
        IReadOnlyList<Services.Sources.IUpdateSource> sources,
        ElevatedHost? elevated = null,
        Func<string, CancellationToken, Task<string?>>? changelogFetcher = null,
        Func<string, CancellationToken, Task<NpmChangelogLookup.NpmChangelogSource>>? npmChangelogLookup = null,
        WingetBootstrapService? wingetBootstrap = null,
        Services.Install.InstallBlockers? installBlockers = null,
        Func<bool>? canShowWingetOffer = null)
    {
        _elevated = elevated;
        _wingetBootstrap = wingetBootstrap;
        _canShowWingetOffer = canShowWingetOffer
            ?? (() => !App.IsUnattended && Application.Current?.MainWindow?.IsVisible == true);
        _installBlockers = installBlockers ?? new Services.Install.InstallBlockers(_installLocations, packagedApps: _packagedApps);
        // Swappable seam for tests: the default is the real network fetch.
        _changelogFetcher = changelogFetcher ?? FetchReleaseBodyAsync;
        _npmChangelogLookup = npmChangelogLookup ?? LookupNpmChangelogAsync;

        // Rows expose a few brushes they looked up themselves, which no DynamicResource is watching.
        themeService.ThemeChanged += RefreshRowThemeBrushes;

        _notifications = notifications;
        _selfUpdate = selfUpdate;
        _settings = settings;
        _logging = logging;
        _enrichmentCache = new EnrichmentCache(logging);
        // The file can be large; read it off the UI thread. The enrichment paths await this before their first lookup.
        _ = _enrichmentCache.LoadAsync();
        _scanResultCache = new ScanResultCache(logging);
        _changelogTrafficHandler.Logging = logging;
        _themeService = themeService;
        _scanOrchestrator = scanOrchestrator;
        _iconCache = iconCache;
        _installQueue = installQueue;
        _sources = sources;
        _history = history;
        _autostart = autostart;
        // Application.Current is briefly null on the render harness's STA thread (never on the real host); fall back to the thread's own dispatcher. On the UI thread they are the same object.
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _scanOrchestrator.ItemFound += OnItemFound;
        _scanOrchestrator.ProviderStatusChanged += OnProviderStatusChanged;
        _installQueue.StatusChanged += OnInstallStatusChanged;
        _installQueue.SecondAttemptStarted += OnSecondAttemptStarted;

        // Drains found items into the table in batches while a scan runs.
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Raised to SlowFlushInterval once the table is long; see Flush.
            Interval = FastFlushInterval,
        };
        // DispatcherTimer.Stop() does not retract a Tick already posted to the dispatcher queue (a documented WPF gotcha). Under load it can land after FinishScanUi flushed for the last time; Flush is idempotent, but a stale call belongs to no visible scan, so it is a no-op.
        _flushTimer.Tick += (_, _) =>
        {
            if (_scanUiActive)
                Flush();
        };

        // The view is built over the visible rows, not over Updates with a filter callback: ICollectionView.Refresh() raises a Reset that discards every row container, so switching one filter would re-create and re-animate unchanged rows. Maintaining the visible set by hand turns a filter change into the few Add and Remove notifications it really is.
        UpdatesView = CollectionViewSource.GetDefaultView(_visible);
        ApplyGrouping();
        // Set once, never reassigned; narrows the grouped and sorted table, leaving _visible and SyncVisible untouched. The compact-block rows it excludes appear in ManualOnlyView below.
        UpdatesView.Filter = candidate => candidate is UpdateRowViewModel row && !row.BelongsInCompactBlock;

        // An independent view with its own Filter and SortDescriptions (name only; the compact block has no columns) and no GroupDescriptions: it is a flat WrapPanel of chips.
        ManualOnlyView = new ListCollectionView(_visible)
        {
            Filter = candidate => candidate is UpdateRowViewModel row && row.BelongsInCompactBlock,
        };
        ManualOnlyView.SortDescriptions.Add(new SortDescription(nameof(UpdateRowViewModel.IsIgnored), ListSortDirection.Ascending));
        ManualOnlyView.SortDescriptions.Add(new SortDescription(nameof(UpdateRowViewModel.Name), ListSortDirection.Ascending));

        // Whatever changes Updates (a scan draining finds, a stale row going, a test), the visible set follows on its own.
        Updates.CollectionChanged += (_, args) =>
        {
            if (args.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                PruneWatchedPersistentFailureRows();

            ApplyFilter();
        };

        // Groups appear in the order their items do, so the group order is always the first sort key (applications, store, system, developer tooling, games); ApplySort sets the within-group key chosen in the column headers.
        ApplySort();

        // The pills sit in one flat wrapping row with no category headers, only the noteworthy ones, ordered by category then name. A ListCollectionView of its own, never the View of a throwaway CollectionViewSource: nothing would keep that source alive, and once collected WPF's view manager detaches the view from the collection and every Refresh throws inside PrepareLocalArray.
        PillView = new ListCollectionView(ProviderStatuses);
        // While a scan runs every source shows, so its wait, run, done or error state is visible; afterwards IsNoteworthy alone decides.
        PillView.Filter = candidate => candidate is ProviderStatusViewModel vm && (IsScanning || vm.IsNoteworthy);
        // Running sources float to the top while scanning; once nothing runs this key is equal for everyone and the category order beneath it remains.
        PillView.SortDescriptions.Add(
            new SortDescription(nameof(ProviderStatusViewModel.RunningRank), ListSortDirection.Ascending));
        PillView.SortDescriptions.Add(
            new SortDescription(nameof(ProviderStatusViewModel.GroupOrder), ListSortDirection.Ascending));
        PillView.SortDescriptions.Add(
            new SortDescription(nameof(ProviderStatusViewModel.DisplayName), ListSortDirection.Ascending));

        // Assign via the generated properties, not the backing fields, so the OnXChanged callbacks apply theme and localization on startup.
        SelectedLanguage = settings.Current.Ui.Language;
        SelectedTheme = settings.Current.Ui.Theme;

        // Restore the two overview toggles; backing-field assignment so their change callbacks (which persist) do not fire during construction.
        showIgnored = settings.Current.Ui.ShowIgnored;
        hideManual = settings.Current.Ui.HideManual;

        // The remembered column chooser state and sort, after ApplySort's default above.
        LoadMainTableLayout();

        // Also without a saved state: a fresh install starts with its default-hidden and still-empty columns collapsed.
        RecomputeAutoHiddenMainColumns();
        ApplyMainColumnWidths();
    }

    /// <summary>Raised when the UI language changed, so non-WPF surfaces (the tray menu) can follow.</summary>
    public event Action? LanguageChanged;

    public void SaveWindowPlacement(double left, double top, double width, double height, bool maximized)
    {
        // A window that was never shown reports NaN, which JSON cannot represent: with --tray on a fresh profile, saving on exit would throw.
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height))
            return;

        _settings.Current.Ui.WindowPlacement = new WindowPlacement
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Maximized = maximized,
        };
        Persist();
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        _settings.Current.Ui.Language = value;
        LocalizationService.Instance.Apply(value);
        Persist();
        RefreshLocalizedText();
        LanguageChanged?.Invoke();
    }

    /// <summary>A language switch refreshes every <c>Loc[...]</c> binding through the localization singleton, but properties that build their text in code must be re-raised by hand, as must those of the rows, pills, history list and cleanup dialog.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(ToolbarCleanupText));
        OnPropertyChanged(nameof(HeadlineText));
        OnPropertyChanged(nameof(HeadlineCriticalText));
        OnPropertyChanged(nameof(SubheadlineText));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(SelectAllTooltip));
        OnPropertyChanged(nameof(PersistentFailureFilterLabel));
        OnPropertyChanged(nameof(PersistentFailureChipLabel));
        OnPropertyChanged(nameof(SelfUpdateDismissLabel));
        OnPropertyChanged(nameof(NameSortTooltip));
        OnPropertyChanged(nameof(DateSortTooltip));
        OnPropertyChanged(nameof(VersionSortTooltip));
        OnPropertyChanged(nameof(SizeSortTooltip));
        // Recomputed, not just re-raised: the summary is a cached string.
        RefreshSelectionState();
        OnPropertyChanged(nameof(InstallCountText));
        OnPropertyChanged(nameof(InstallTotalSizeText));
        OnPropertyChanged(nameof(InstallDownloadedText));
        OnPropertyChanged(nameof(InstallSpeedText));
        OnPropertyChanged(nameof(QueuePanelHeaderText));
        OnPropertyChanged(nameof(RebootInfoText));
        OnPropertyChanged(nameof(CleanupReminderText));

        foreach (var row in Updates)
            row.RefreshLocalizedText();

        foreach (var pill in ProviderStatuses)
            pill.RefreshLocalizedText();

        ApplyGrouping();

        _historyViewModel?.RefreshLocalizedText();
        _cleanupViewModel?.RefreshLocalizedText();
    }

    /// <summary>Groups by the translated category text. A group keeps the name it was created with, so a language switch rebuilds the description.</summary>
    private void ApplyGrouping()
    {
        using (UpdatesView.DeferRefresh())
        {
            UpdatesView.GroupDescriptions.Clear();
            UpdatesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(UpdateRowViewModel.CategoryDisplay)));
        }
    }

    partial void OnSelectedThemeChanged(string value)
    {
        _settings.Current.Ui.Theme = value;
        _themeService.Apply(value);
        Persist();
    }

    /// <summary>Called from around twenty places on the UI thread, most from a single click. The write is deferred and coalesced by the settings service: a synchronous save can block the thread for most of a second when the file is briefly locked. A failed write is logged there and the exit path flushes what is pending.</summary>
    private void Persist() => _settings.SaveDeferred();
}
