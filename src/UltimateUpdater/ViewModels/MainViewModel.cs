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
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.ViewModels;

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
    private readonly Services.WingetBootstrapService? _wingetBootstrap;
    private readonly AutostartService _autostart;
    private readonly NotificationService _notifications;
    private readonly Services.SelfUpdate.SelfUpdateService _selfUpdate;
    private readonly EnrichmentCache _enrichmentCache;
    private readonly Func<string, CancellationToken, Task<string?>> _changelogFetcher;

    /// <summary>The click-triggered `npm view` lookup; same swappable-seam idea as
    /// <see cref="_changelogFetcher"/>, defaulting to the real process call so a test can hand in a fake
    /// without ever shelling out to npm.</summary>
    private readonly Func<string, CancellationToken, Task<NpmChangelogLookup.NpmChangelogSource>> _npmChangelogLookup;
    private readonly Services.Sources.InstallLocationResolver _installLocations =
        new(Services.Sources.WindowsRegistryView.Instance);

    /// <summary>The pre-install check; shares the same install-location resolver. Not readonly: a test
    /// can hand in a fake through the constructor's own <c>installBlockers</c> parameter, since the real
    /// one's process probe reads whatever genuinely happens to be running on the host it executes on.</summary>
    private Services.Install.InstallBlockers _installBlockers;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, ProviderStatusViewModel> _providerStatusById = new();

    /// <summary>
    /// Last (whole percent, status text) dispatched per install row; same throttle idea as
    /// <c>CleanupService</c>'s repair-step reporting: a redrawn progress line arrives many times a
    /// second and every report crosses a dispatcher (and, for an elevated install, a pipe first). Only a
    /// REPEAT of what the row already shows is dropped; see <see cref="OnInstallStatusChanged"/>.
    /// </summary>
    private readonly Dictionary<string, (double Percent, string? Status)> _lastDispatchedInstallStatus = new(StringComparer.OrdinalIgnoreCase);

    private readonly System.Collections.Concurrent.ConcurrentQueue<UpdateItem> _pendingItems = new();

    private readonly Func<bool> _canShowWingetOffer;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProviderStatus> _liveStatuses = new();
    private readonly DispatcherTimer _flushTimer;

    // A scan reconciles the table instead of clearing it: rows still present are reused, new ones added,
    // vanished ones removed at the end. Keeping the rows on screen through a rescan is what stops the
    // table blanking and "flashing" every refresh. Keyed by "source:id".
    private readonly Dictionary<string, UpdateRowViewModel> _rowsByKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scanSeenKeys = new(StringComparer.Ordinal);

    /// <summary>Serialises scans; see ScanAsync for why overlapping runs corrupt the view.</summary>
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _installCts;
    private bool _scanCompletedCleanly;

    /// <summary>
    /// two quick clicks on "Search" queued two waiters on <see cref="_scanGate"/>; the
    /// first ran a scan, released the gate, and the second (still queued, wanting nothing more than to
    /// restart what the first click already covered) ran a SECOND full scan automatically. Bumped by every
    /// call before it queues; a waiter that is no longer the newest one by the time it gets the gate has
    /// been superseded and steps aside instead of running its own redundant scan.
    /// </summary>
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
        Services.WingetBootstrapService? wingetBootstrap = null,
        Services.Install.InstallBlockers? installBlockers = null,
        Func<bool>? canShowWingetOffer = null)
    {
        _elevated = elevated;
        _wingetBootstrap = wingetBootstrap;
        _canShowWingetOffer = canShowWingetOffer
            ?? (() => !App.IsUnattended && Application.Current?.MainWindow?.IsVisible == true);
        _installBlockers = installBlockers ?? new Services.Install.InstallBlockers(_installLocations);
        // Swappable seam for tests: the default is the real network fetch, so nothing about the
        // real app changes; a test can hand in a fake to prove the dialog-vs-window choice without ever
        // touching the network.
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
        // Application.Current is briefly null on the render harness's STA thread (a scripted-env timing
        // quirk, never on the real host); fall back to the thread's own dispatcher so the view model can
        // still be constructed. On the UI thread the two are the very same object.
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _scanOrchestrator.ItemFound += OnItemFound;
        _scanOrchestrator.ProviderStatusChanged += OnProviderStatusChanged;
        _installQueue.StatusChanged += OnInstallStatusChanged;
        _installQueue.SecondAttemptStarted += OnSecondAttemptStarted;

        // drains found items into the table in batches while a scan is running
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Raised to SlowFlushInterval once the table is long; see Flush.
            Interval = FastFlushInterval,
        };
        // DispatcherTimer.Stop() does not retract a Tick that was already posted to the dispatcher
        // queue before Stop() ran (a documented WPF gotcha); under heavy load the gap between the
        // timer elapsing and the dispatcher actually processing it can outlast the scan itself,
        // landing here after FinishScanUi already flushed for the last time. Harmless on its own
        // (Flush is idempotent), but a stale call belongs to no scan the user can see, so it is a
        // no-op rather than doing pointless work against a table nothing is watching.
        _flushTimer.Tick += (_, _) =>
        {
            if (_scanUiActive)
                Flush();
        };

        // The view is built over the VISIBLE rows, not over Updates with a filter callback. A filter is
        // re-evaluated by ICollectionView.Refresh(), which raises a Reset; and a Reset throws away every
        // row container in the table, so switching one filter re-created and re-animated rows that had not
        // changed at all. Maintaining the visible set by hand turns a filter change into the handful of Add and
        // Remove notifications it really is, and the rows that stay keep their containers untouched.
        UpdatesView = CollectionViewSource.GetDefaultView(_visible);
        ApplyGrouping();
        // Set once, never reassigned; narrows the grouped/sorted table itself, leaving _visible/SyncVisible
        // untouched; the compact-block rows this excludes appear instead in ManualOnlyView below.
        UpdatesView.Filter = candidate => candidate is UpdateRowViewModel row && !row.BelongsInCompactBlock;

        // Independent view, own Filter/SortDescriptions (name only; the compact block has no columns to
        // sort by), no GroupDescriptions (a flat WrapPanel of chips, so the GroupStyleSelector group=null
        // problem never applies here; there is no group at all).
        ManualOnlyView = new ListCollectionView(_visible)
        {
            Filter = candidate => candidate is UpdateRowViewModel row && row.BelongsInCompactBlock,
        };
        ManualOnlyView.SortDescriptions.Add(new SortDescription(nameof(UpdateRowViewModel.IsIgnored), ListSortDirection.Ascending));
        ManualOnlyView.SortDescriptions.Add(new SortDescription(nameof(UpdateRowViewModel.Name), ListSortDirection.Ascending));

        // Whatever changes Updates (a scan draining its finds, a stale row going, a test), the visible set
        // follows on its own. Nothing has to remember to ask.
        Updates.CollectionChanged += (_, args) =>
        {
            if (args.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                PruneWatchedPersistentFailureRows();

            ApplyFilter();
        };

        // Groups appear in the order their items do, so the group order is always the FIRST sort key
        // (applications, store, system, developer tooling, games); ApplySort sets the within-group key
        // the user chose from the column headers (name by default).
        ApplySort();

        // The pills sit in one flat wrapping row, with no category headers and only the
        // noteworthy ones shown. A dedicated view (not the collection's default) so nothing else is
        // affected by the filter. Still ordered by category then name so related pills stay adjacent.
        // A ListCollectionView of its own, never the View of a throwaway CollectionViewSource: nothing
        // would keep that source alive, and once it is collected WPF's view manager detaches the view
        // from the collection, after which every Refresh throws inside PrepareLocalArray and the pills
        // stop updating for good.
        PillView = new ListCollectionView(ProviderStatuses);
        // While a scan runs, every source shows so its wait/run/done/error state is visible;
        // once it ends this falls back to exactly the old "only what's worth a look" filter, since
        // IsScanning is false and IsNoteworthy alone decides again.
        PillView.Filter = candidate => candidate is ProviderStatusViewModel vm && (IsScanning || vm.IsNoteworthy);
        // Running sources float to the top while scanning; once nothing is running this key is the same
        // for everyone and the category order beneath it is all that is left; the same "quiet view" as
        // before the scan, with no separate code path needed for it.
        PillView.SortDescriptions.Add(
            new SortDescription(nameof(ProviderStatusViewModel.RunningRank), ListSortDirection.Ascending));
        PillView.SortDescriptions.Add(
            new SortDescription(nameof(ProviderStatusViewModel.GroupOrder), ListSortDirection.Ascending));
        PillView.SortDescriptions.Add(
            new SortDescription(nameof(ProviderStatusViewModel.DisplayName), ListSortDirection.Ascending));

        // assign via the generated properties (not the backing fields) so the
        // OnXChanged callbacks fire and actually apply theme/localization on startup
        SelectedLanguage = settings.Current.Ui.Language;
        SelectedTheme = settings.Current.Ui.Theme;

        // restore the two overview toggles; backing-field assignment so their change callbacks (which
        // persist) do not fire during construction
        showIgnored = settings.Current.Ui.ShowIgnored;
        hideManual = settings.Current.Ui.HideManual;

        // The remembered column chooser state and sort, after ApplySort's own default above.
        LoadMainTableLayout();

        // Also without a saved state: a fresh install starts with its default-hidden and still-empty columns
        // collapsed, not only after the first rows arrive.
        RecomputeAutoHiddenMainColumns();
        ApplyMainColumnWidths();
    }

    /// <summary>Raised when the UI language changed, so non-WPF surfaces (the tray menu) can follow.</summary>
    public event Action? LanguageChanged;

    public void SaveWindowPlacement(double left, double top, double width, double height, bool maximized)
    {
        // A window that was never shown reports NaN, and JSON cannot represent that: started with
        // --tray on a fresh profile, saving on exit threw and the app closed with a fatal-error box
        // instead of writing its settings.
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

    /// <summary>
    /// A language switch refreshes every <c>Loc[...]</c> BINDING through the localization singleton, but the
    /// properties that build their text in code have to be re-raised by hand; and the rows, the pills, the
    /// history list and the cleanup dialog each own more of them. Without this the window sat in two
    /// languages at once until something else happened to re-raise them.
    /// </summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(HeadlineText));
        OnPropertyChanged(nameof(SubheadlineText));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(SelectAllTooltip));
        OnPropertyChanged(nameof(FilterButtonText));
        OnPropertyChanged(nameof(NameSortTooltip));
        OnPropertyChanged(nameof(DateSortTooltip));
        OnPropertyChanged(nameof(VersionSortTooltip));
        OnPropertyChanged(nameof(SizeSortTooltip));
        // Recomputed, not just re-raised: the summary is a cached string, so the binding re-read the old text.
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

    /// <summary>Groups by the translated category text. A group keeps the name it was created with, so a
    /// language switch has to rebuild the description for the headers to follow.</summary>
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

    /// <summary>
    /// Called from around twenty places, all of them on the UI thread and most of them a single click
    /// (ignore, snooze, retry, a toggle). The write is therefore deferred and coalesced by the settings
    /// service rather than done here; a synchronous save can hold the calling thread for the better part
    /// of a second when the file is briefly locked. A failed write is logged there; the exit path flushes
    /// whatever is still pending.
    /// </summary>
    private void Persist() => _settings.SaveDeferred();
}
