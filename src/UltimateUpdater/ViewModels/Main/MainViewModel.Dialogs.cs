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
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.ViewModels.Main;

// The child windows this view model opens: history, cleanup, settings, error detail and changelog.
public partial class MainViewModel
{
    /// <summary>The history window's view model, kept so a language switch can reach its rows while it is open; they read their result word from the resources in code.</summary>
    private HistoryViewModel? _historyViewModel;

    /// <summary>Set while the history file is being read, so a second click does not open a second window.</summary>
    private bool _historyOpening;

    [RelayCommand]
    private async Task ShowHistoryAsync()
    {
        if (IsOpen<HistoryWindow>() || _historyOpening)
            return;

        // Which sources can pin an exact version: the history window's rollback button uses the same capability check as the live row's.
        var pinningCapableSourceIds = _sources
            .Where(s => s.SupportsVersionPinning)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var openStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _historyOpening = true;
        try
        {
            _historyViewModel = await HistoryViewModel.CreateAsync(
                _history,
                pinningCapableSourceIds,
                entry => DowngradeFromHistory(entry.SourceId, entry.ItemId, entry.Name, entry.ToVersion, entry.FromVersion!),
                _settings);
        }
        finally
        {
            _historyOpening = false;
        }

        // Released with its window: it holds every parsed history row.
        ShowOwned(new HistoryWindow(_historyViewModel, _logging), onClosed: () => _historyViewModel = null, openStarted);
    }

    /// <summary>The cleanup dialog. It measures on open and deletes only after its own second, red confirmation; nothing here starts a cleanup and there is no automatic one. The view model outlives the window on purpose: closing the dialog mid-run leaves the cleaning going (the toolbar broom shows it) and reopening picks the run back up. A finished run, or none, starts fresh so the dialog never opens on a stale result.</summary>
    [RelayCommand]
    private void ShowCleanup()
    {
        if (IsOpen<CleanupWindow>())
            return;

        var openStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var cleanupViewModel = EnsureCleanupViewModel();

        // Covers a fresh instance (subscribed once by its constructor) and a reused one whose earlier window close left it subscribed; this is the one place a window attaches.
        cleanupViewModel.WindowAttached();

        // Independent, not owned: an owned window floats above the main one, minimises with it and closes on a click in the main window, so starting a scan there would take the cleanup away. Unowned, both windows work side by side with their own taskbar buttons.
        ShowOwned(new CleanupWindow(cleanupViewModel), openStarted: openStarted, independent: true);
    }

    /// <summary>The uninstaller window. Unowned and independent like the cleanup window: a removal can run for minutes and the main window stays usable beside it. The view model outlives the window, so closing it mid-run keeps the run going and reopening shows it again; a finished or idle one is rebuilt so the list never opens stale.</summary>
    [RelayCommand]
    private void ShowUninstaller()
    {
        if (IsOpen<UninstallerWindow>())
            return;

        var openStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_uninstallerViewModel is null || !_uninstallerViewModel.IsBusy)
        {
            _uninstallerViewModel?.Discard();
            _uninstallerViewModel = new UninstallerViewModel(
                UninstallerBackendOverride?.Invoke() ?? new UninstallerBackend(_logging, _elevated, _settings), _logging);
        }

        ShowOwned(new UninstallerWindow(_uninstallerViewModel), openStarted: openStarted, independent: true);
    }

    private UninstallerViewModel? _uninstallerViewModel;

    /// <summary>Lets a test open the window on a fake machine.</summary>
    internal Func<IUninstallerBackend>? UninstallerBackendOverride { get; set; }

    /// <summary>The view model the dialog opens on. Busy, not merely cleaning: a dialog reopened while still measuring must reuse it, or its scan would race the old one over the same folders. Whatever is still going is picked back up; only a finished dialog, or none, starts fresh. A prescanned one is kept while its result is fresh and untouched, so opening it costs no new measuring (the admin targets the prescan left out are measured on open).</summary>
    internal CleanupViewModel EnsureCleanupViewModel()
    {
        if (_cleanupViewModel is null || (!_cleanupViewModel.IsBusy && !IsPrescanReusable(_cleanupViewModel)))
        {
            _cleanupViewModel?.CancelScan();
            _cleanupViewModel?.Discard();
            _cleanupViewModel = CreateCleanupViewModel();
        }

        return _cleanupViewModel;
    }

    private CleanupViewModel? _cleanupViewModel;

    /// <summary>How long a startup prescan's figures stay good for an opening dialog.</summary>
    internal static readonly TimeSpan CleanupPrescanFreshness = TimeSpan.FromMinutes(30);

    /// <summary>Set by the app once the session is a real, attended one. Off by default so a view model built in a test never measures the machine it runs on.</summary>
    internal bool CleanupPrescanAllowed { get; set; }

    /// <summary>"Now" for the prescan's freshness and stamp; a test replaces it.</summary>
    internal Func<DateTime> CleanupClock { get; set; } = () => DateTime.Now;

    /// <summary>Test seam for the power check; null reads the real power status.</summary>
    internal Func<bool>? OnAcPowerOverride { get; set; }

    /// <summary>The running startup prescan, or null when none was started.</summary>
    internal Task? CleanupPrescanTask { get; private set; }

    /// <summary>Test seam: the measuring service a new cleanup view model gets; null is the real one.</summary>
    internal Func<Services.Cleanup.CleanupService>? CleanupServiceOverride { get; set; }

    private bool _cleanupPrescanStarted;

    private bool IsPrescanReusable(CleanupViewModel viewModel)
    {
        if (viewModel.IsPrescanRunning)
            return true;

        return viewModel.Phase == CleanupPhase.Choosing
            && viewModel.PrescanFinishedAt is { } at
            && CleanupClock() - at < CleanupPrescanFreshness;
    }

    /// <summary>After the first finished update scan, starts measuring what a cleanup could free, so the dialog opens with figures instead of a spinner. Quiet: only on mains power, only with the setting on, user-scope targets (plus admin ones if the elevated helper already runs), never an elevation prompt and never a cleanup. Once per session.</summary>
    private void StartCleanupPrescanIfWanted()
    {
        if (_cleanupPrescanStarted)
            return;

        _cleanupPrescanStarted = true;

        if (!CleanupPrescanAllowed || !_settings.Current.CleanupPrescanAtStartup || _installQueue.IsMockSession)
            return;

        if (!(OnAcPowerOverride ?? Services.Scan.PowerStatus.IsOnAcPower)())
            return;

        if (_cleanupViewModel is not null)
            return;

        _cleanupViewModel = CreateCleanupViewModel();
        CleanupPrescanTask = _cleanupViewModel.PrescanAsync();
    }

    /// <summary>True while a cleanup is running, whether or not its dialog is open.</summary>
    [ObservableProperty]
    private bool isCleanupRunning;

    partial void OnIsCleanupRunningChanged(bool value)
    {
        InstallSelfUpdateCommand.NotifyCanExecuteChanged();
        ApplyWaitingSelfUpdateWhenIdle();
        OnPropertyChanged(nameof(ToolbarCleanupText));
    }

    private int _cleanupRunPercent;

    /// <summary>The cleanup run's progress in whole percent, the same figure as the ring in its window.</summary>
    internal int CleanupRunPercent
    {
        get => _cleanupRunPercent;
        private set
        {
            if (!SetProperty(ref _cleanupRunPercent, value))
                return;

            if (IsCleanupRunning)
                OnPropertyChanged(nameof(ToolbarCleanupText));
        }
    }

    /// <summary>The toolbar button's word: "Bereinigen" at rest, "Bereinigen 47 %" while a cleanup runs, also with its window closed.</summary>
    public string ToolbarCleanupText => IsCleanupRunning
        ? string.Format(Loc["ToolbarCleanupRunning"], CleanupRunPercent)
        : Loc["ToolbarCleanupLabel"];

    private CleanupViewModel CreateCleanupViewModel()
    {
        var viewModel = new CleanupViewModel(
            CleanupServiceOverride?.Invoke() ?? new Services.Cleanup.CleanupService(_logging), _logging, _settings, _elevated)
        {
            Clock = () => CleanupClock(),
        };

        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CleanupViewModel.IsCleaning))
            {
                CleanupRunPercent = (int)Math.Round(viewModel.CleanPercent);
                IsCleanupRunning = viewModel.IsCleaning;
            }
            else if (args.PropertyName == nameof(CleanupViewModel.CleanPercent))
                CleanupRunPercent = (int)Math.Round(viewModel.CleanPercent);
        };

        return viewModel;
    }

    /// <summary>Whether a window of this kind is already up; asked before a dialog's state is replaced.</summary>
    private static bool IsOpen<TWindow>()
        where TWindow : Window
    {
        var existing = Application.Current?.Windows.OfType<TWindow>().FirstOrDefault(open => open.IsLoaded);
        if (existing is null)
            return false;

        existing.Activate();
        return true;
    }

    /// <summary>Opens the raw output behind a failed row on request. The row raises this and the window is owned here, so the row view model stays free of view types and testable without one.</summary>
    private void ShowErrorDetail(UpdateRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(row.InstallErrorDetail))
            return;

        var versionChange = row.HasVersions
            ? row.HasInstalledVersion ? $"{row.InstalledVersion} → {row.AvailableVersion}" : row.AvailableVersion
            : null;

        var explanation = InstallErrorExplainer.Describe(
            row.SourceId, row.InstallErrorDetail, key => LocalizationService.Instance[key]);

        ShowOwned(new ErrorDetailWindow(
            new ErrorDetailViewModel(
                row.Name, row.SourceDisplay, versionChange, row.InstallErrorDetail!, _logging,
                explanation, row.ShowRetryInteractiveButton,
                reinstall: row.ShowReinstallButton ? () => row.ReinstallCommand.Execute(null) : null)));
    }

    /// <summary>Opens the changelog text a source found for this row. Where only a release link is known and it is a GitHub or GitLab release page, a click-triggered fetch of the release body is tried first. Text still missing after that falls back to a small "open the vendor's page" confirmation instead of the big window.</summary>
    internal async Task ShowChangelogAsync(UpdateRowViewModel row)
    {
        if (!row.HasChangelog)
            return;

        // An npm row's button exists only on the strength of CanFetchChangelogOnClick (its registry enrichment never fills these fields, see NpmChangelogLookup), so this is the one place the click-triggered `npm view` runs, never during a scan or background enrichment.
        if (row.CanFetchChangelogOnClick
            && string.IsNullOrWhiteSpace(row.ChangelogText)
            && string.IsNullOrWhiteSpace(row.ChangelogUrl))
        {
            await TryFetchNpmChangelogAsync(row);
        }

        if (string.IsNullOrWhiteSpace(row.ChangelogText) && row.ChangelogUrl is { Length: > 0 } releaseUrl)
            await TryEnrichChangelogFromLinkAsync(row, releaseUrl);

        if (!string.IsNullOrWhiteSpace(row.ChangelogText))
        {
            var versionText = row.HasVersions ? row.AvailableVersion : null;
            ShowOwned(new ChangelogWindow(
                new ChangelogViewModel(row.Name, versionText, row.ChangelogText, row.ChangelogUrl, _logging)));
            return;
        }

        if (row.ChangelogUrl is not { Length: > 0 } linkUrl
            || !Uri.TryCreate(linkUrl, UriKind.Absolute, out var linkUri)
            || (linkUri.Scheme != Uri.UriSchemeHttp && linkUri.Scheme != Uri.UriSchemeHttps))
        {
            // A row whose button exists only because it might have something to fetch (npm) is told explicitly when the click found neither text nor a link, rather than the spinner just stopping.
            if (row.CanFetchChangelogOnClick)
            {
                var versionText = row.HasVersions ? row.AvailableVersion : null;
                ShowOwned(new ChangelogWindow(
                    new ChangelogViewModel(row.Name, versionText, Loc["ChangelogNone"], null, _logging)));
            }

            return;
        }

        var confirmed = Dialogs.Confirm(
            row.Name,
            string.Format(Loc["ChangelogNoTextDialogBody"], linkUri.Host),
            Loc["ChangelogOpenSite"],
            Loc["DialogCancel"]);

        if (confirmed)
            UriLauncher.LaunchDetached(linkUrl, _logging);
    }

    /// <summary>The npm lookup half. `npm view &lt;package&gt; homepage repository --json` names the repository and homepage; a GitHub repository becomes that repo's release-page URL and goes to the same release-link enrichment as above, so the notes come from one fetch path. A non-GitHub or absent repository falls back to the plain homepage link, without inventing text. Cached under the same EnrichmentCache key as <see cref="TryEnrichChangelogFromLinkAsync"/> (an empty, non-null cached ChangelogUrl means "tried this version, found nothing"), so a repeat click never shells out to npm again.</summary>
    private async Task TryFetchNpmChangelogAsync(UpdateRowViewModel row)
    {
        await _enrichmentCache.LoadAsync();
        var cacheKey = EnrichmentCache.Key(row.Item.SourceId, row.Item.Id, row.Item.AvailableVersion);
        var hasCached = _enrichmentCache.TryGet(cacheKey, out var cached);

        string? url = hasCached ? cached.ChangelogUrl : null;

        if (url is null)
        {
            var found = await _npmChangelogLookup(row.Item.Id, CancellationToken.None);

            url = NpmChangelogLookup.TryBuildGitHubReleasesUrl(found.RepositoryUrl) ?? UsableHomepage(found.Homepage);

            _enrichmentCache.Set(cacheKey, new UpdateEnrichment(
                hasCached ? cached.PublishDate : null,
                hasCached ? cached.DownloadSize : null,
                hasCached ? cached.ProjectUrl : null,
                hasCached ? cached.AvailableVersion : null,
                hasCached ? cached.ChangelogBody : null,
                url ?? string.Empty,
                hasCached ? cached.Publisher : null));
            await Task.Run(_enrichmentCache.Save); // the whole cache, atomically: not on the UI thread
        }

        if (!string.IsNullOrWhiteSpace(url))
            row.ApplyEnrichment(null, null, changelogUrl: url);
    }

    /// <summary>A homepage npm reports is free-form text, not a validated link: only an absolute http(s) URL reaches the row (as in <see cref="ChangelogViewModel.OpenRelease"/>).</summary>
    private static string? UsableHomepage(string? homepage) =>
        Uri.TryCreate(homepage, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? homepage
            : null;

    /// <summary>The default implementation behind <see cref="_npmChangelogLookup"/>.</summary>
    private Task<NpmChangelogLookup.NpmChangelogSource> LookupNpmChangelogAsync(
        string packageName, CancellationToken cancellationToken) =>
        NpmChangelogLookup.LookupAsync(packageName, _logging, cancellationToken);

    [RelayCommand]
    private void ShowSettings()
    {
        // An open settings window is only raised: a second view model would subscribe to the main window for nothing and its window would be thrown away by ShowOwned.
        if (IsOpen<SettingsWindow>())
            return;

        // Non-modal, so the follow-up work belongs in the Closed callback: the line after ShowOwned does not mean the window is gone.
        var openStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        ShowOwned(new SettingsWindow(CreateSettingsViewModel()), onClosed: OnSettingsClosed, openStarted);
    }

    internal SettingsViewModel CreateSettingsViewModel()
    {
        var settingsViewModel = new SettingsViewModel(
            _settings, _logging, _themeService, _autostart, _scanOrchestrator.SourceIds,
            CheckForUpdatesInteractiveAsync, IgnoredPackageNames(), _scanOrchestrator.IsKnownNotInstalled);

        // The language is switched in that window, and the main window's computed text, rows and other dialogs have no binding to it, so they are re-raised as soon as it changes, not when the window closes.
        settingsViewModel.LanguageChanged += RefreshLocalizedText;

        // Removing an entry in the ignore list dialog takes effect in the table at once.
        settingsViewModel.IgnoreListChanged += ResyncIgnoreState;
        settingsViewModel.DataReset += OnDataReset;

        // An ignore made in the table while the ignore list dialog is open shows up there at once. The forwarder is removed when the settings window closes, so this view model never keeps a dead settings view model alive.
        if (_forwardIgnoreMarks is not null)
            IgnoreMarksChanged -= _forwardIgnoreMarks;
        _forwardIgnoreMarks = settingsViewModel.RefreshOpenIgnoreList;
        IgnoreMarksChanged += _forwardIgnoreMarks;
        return settingsViewModel;
    }

    /// <summary>A factory reset deletes the files and resets settings.json, but this view model still holds the old state in memory: the enrichment cache would write itself back, rollbacks would stay offered for deleted history, and the view state and column layout written on exit would undo the reset.</summary>
    private void OnDataReset()
    {
        _enrichmentCache.Clear();
        _history.InvalidateRollbackIndex();
        ShowIgnored = _settings.Current.Ui.ShowIgnored;
        HideManual = _settings.Current.Ui.HideManual;
        ResetMainTableLayout();
    }

    /// <summary>Package key to display name for every row and child row, so the ignore list can show names instead of raw settings keys.</summary>
    internal IReadOnlyDictionary<string, string> IgnoredPackageNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in Updates)
        {
            names[PackageKey(row.Item)] = row.Name;
            foreach (var child in row.Children)
                names[PackageKey(child.Item)] = child.Name;
        }

        return names;
    }

    private Action? _forwardIgnoreMarks;

    private void OnSettingsClosed()
    {
        if (_forwardIgnoreMarks is not null)
        {
            IgnoreMarksChanged -= _forwardIgnoreMarks;
            _forwardIgnoreMarks = null;
        }

        // The settings window can change theme and language, so mirror them into the main window's own pickers.
        SelectedTheme = _settings.Current.Ui.Theme;
        SelectedLanguage = _settings.Current.Ui.Language;

        // Sources are switched from the settings window too, and the pills read their state only at creation.
        foreach (var (sourceId, pill) in _providerStatusById)
            pill.IsEnabled = IsSourceEnabled(sourceId);

        ResyncIgnoreState();
    }

    /// <summary>Re-syncs the rows' ignore and snooze marks to the possibly changed settings lists.</summary>
    private void ResyncIgnoreState()
    {
        foreach (var row in Updates)
        {
            row.IsIgnored = IsIgnored(row.Item);
            row.IsIgnoredAllVersions = _settings.Current.Excludes.Contains(PackageKey(row.Item));
            ApplySnoozeState(row);
        }

        ApplyFilter();

        // It also changes what the headline counts.
        RefreshHeader();
    }

    /// <summary>Tells every row to re-read the brushes it resolved by hand, on the UI thread.</summary>
    private void RefreshRowThemeBrushes()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(RefreshRowThemeBrushes, DispatcherPriority.Background);
            return;
        }

        foreach (var row in Updates)
            row.RefreshThemeBrushes();
    }

    /// <summary>The owner is raised again when an owned window closes only if it is shown, not minimised and the app is not on its way out.</summary>
    internal static bool ShouldActivateOwnerOnClose(bool ownerVisible, WindowState ownerState, bool appExiting) =>
        ownerVisible && ownerState != WindowState.Minimized && !appExiting;

    /// <summary>Opens an unowned window centred over <paramref name="anchor"/> while that one is in its normal state; a maximised, minimised or hidden anchor has no useful frame, so the screen is used. DialogSizing.FitToWorkArea still pulls the result onto the monitor.</summary>
    private static void PlaceOver(Window window, Window? anchor)
    {
        if (anchor is null || !anchor.IsVisible || anchor.WindowState != WindowState.Normal)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var origin = CenterOver(new System.Windows.Rect(anchor.Left, anchor.Top, anchor.ActualWidth, anchor.ActualHeight),
            new System.Windows.Size(window.Width, window.Height));
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = origin.X;
        window.Top = origin.Y;
    }

    /// <summary>Top-left corner that centres a window of <paramref name="size"/> over <paramref name="anchor"/>.</summary>
    internal static System.Windows.Point CenterOver(System.Windows.Rect anchor, System.Windows.Size size) =>
        new(anchor.Left + (anchor.Width - size.Width) / 2, anchor.Top + (anchor.Height - size.Height) / 2);

    /// <summary>Shows an owned dialog non-modally, so a click in the main window can close it (a modal dialog disables its owner and there would be no such click). One window per kind: a second click on the same toolbar button raises the open one instead of stacking another. <paramref name="independent"/> opens it as a window of its own, with no owner and centred over the main window, so neither hides, closes or minimises the other.</summary>
    private void ShowOwned(Window window, Action? onClosed = null, long? openStarted = null, bool independent = false)
    {
        var existing = Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(open => open.GetType() == window.GetType() && open.IsLoaded);

        if (existing is not null)
        {
            // These two show one particular row's content: raising the old window would keep showing the previous row's text under the click that asked for another.
            if (window is not (ErrorDetailWindow or ChangelogWindow))
            {
                existing.Activate();
                return;
            }

            existing.Close();
        }

        var owner = Application.Current?.MainWindow;
        if (independent)
        {
            PlaceOver(window, owner);
        }
        else
        {
            window.Owner = owner;

            // When an owned window closes while not active (after a UAC prompt or a confirm dialog), Windows hands activation to the next window in z-order and the owner can fall behind another application. Raising it while the closing window still exists keeps it in front.
            window.Closing += (_, args) =>
            {
                if (args.Cancel || owner is null)
                    return;

                var exiting = Application.Current is App { IsExiting: true };
                if (ShouldActivateOwnerOnClose(owner.IsVisible, owner.WindowState, exiting))
                    owner.Activate();
            };
        }

        window.Closed += (_, _) => _logging.Info($"Owned window closed: {window.GetType().Name}");

        if (onClosed is not null)
            window.Closed += (_, _) => onClosed();

        FirstFrameRevealFor(window, openStarted);
        window.Show();
    }

    /// <summary>Keeps the window hidden until its first frame is drawn (no empty frame flashing up) and logs how long opening took, split into data read and window built versus first frame.</summary>
    private void FirstFrameRevealFor(Window window, long? openStarted)
    {
        FirstFrameReveal.Attach(window, message => _logging.Info(message));

        if (openStarted is not { } started)
            return;

        var built = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var logged = false;
        window.ContentRendered += (_, _) =>
        {
            if (logged)
                return;

            logged = true;
            var total = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _logging.Info($"{window.GetType().Name} opened: built after {built:0} ms, first frame after {total:0} ms");
        };
    }
}
