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
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.ViewModels;

// The child windows this view model opens: history, cleanup, settings, error detail and changelog.
public partial class MainViewModel
{
    /// <summary>
    /// The history window's view model, kept only so a language switch can reach its rows while it is open;
    /// they read their result word from the resources in code.
    /// </summary>
    private HistoryViewModel? _historyViewModel;

    /// <summary>Set while the history file is being read, so a second click does not open a second window.</summary>
    private bool _historyOpening;

    [RelayCommand]
    private async Task ShowHistoryAsync()
    {
        if (IsOpen<Views.HistoryWindow>() || _historyOpening)
            return;

        // Which sources can even pin an exact version, so the history window's own rollback
        // button uses the exact same capability check the live row's button already applies.
        var pinningCapableSourceIds = _sources
            .Where(s => s.SupportsVersionPinning)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

        // Released with its window: it holds every parsed history row, for the rest of the session otherwise.
        ShowOwned(new Views.HistoryWindow(_historyViewModel, _logging), onClosed: () => _historyViewModel = null);
    }

    /// <summary>
    /// The cleanup dialog. It measures on open and only ever deletes after its own
    /// second, red confirmation: nothing here starts a cleanup, and there is deliberately no automatic one.
    /// The view model OUTLIVES the window on purpose: closing the dialog mid-run leaves the cleaning going in
    /// the background, the toolbar broom shows that, and opening the dialog again picks the
    /// same run back up. A run that has finished (or none at all) starts fresh, so the dialog never opens
    /// on a stale result.
    /// </summary>
    [RelayCommand]
    private void ShowCleanup()
    {
        if (IsOpen<Views.CleanupWindow>())
            return;

        // Busy, not merely cleaning: a dialog re-opened while still MEASURING must reuse that view model
        // rather than get a fresh one, or its scan would race the old one over the same folders.
        // Whatever is still going is picked back up; only a finished dialog (or none at all) starts fresh.
        if (_cleanupViewModel is null || !_cleanupViewModel.IsBusy)
        {
            _cleanupViewModel?.CancelScan();
            _cleanupViewModel?.Discard();
            _cleanupViewModel = CreateCleanupViewModel();
        }

        // Covers both branches above - a fresh instance (already
        // subscribed once by its own constructor) and a reused, still-busy one whose earlier window close
        // left it subscribed anyway; either way this is the one place a window actually attaches.
        _cleanupViewModel.WindowAttached();
        ShowOwned(new Views.CleanupWindow(_cleanupViewModel));
    }

    private CleanupViewModel? _cleanupViewModel;

    /// <summary>True while a cleanup is running, whether or not its dialog is open.</summary>
    [ObservableProperty]
    private bool isCleanupRunning;

    partial void OnIsCleanupRunningChanged(bool value)
    {
        InstallSelfUpdateCommand.NotifyCanExecuteChanged();
        ApplyWaitingSelfUpdateWhenIdle();
    }

    private CleanupViewModel CreateCleanupViewModel()
    {
        var viewModel = new CleanupViewModel(new Services.Cleanup.CleanupService(_logging), _logging, _settings, _elevated);

        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(CleanupViewModel.IsCleaning))
                IsCleanupRunning = viewModel.IsCleaning;
        };

        return viewModel;
    }

    /// <summary>Is a window of this kind already up? Asked before a dialog's state is replaced.</summary>
    private static bool IsOpen<TWindow>()
        where TWindow : Window
    {
        var existing = Application.Current?.Windows.OfType<TWindow>().FirstOrDefault(open => open.IsLoaded);
        if (existing is null)
            return false;

        existing.Activate();
        return true;
    }

    /// <summary>
    /// Opens the raw output behind a failed row on deliberate request. The row raises this; the window
    /// is owned here so the row view model stays free of view types (and testable without one).
    /// </summary>
    private void ShowErrorDetail(UpdateRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(row.InstallErrorDetail))
            return;

        var versionChange = row.HasVersions
            ? row.HasInstalledVersion ? $"{row.InstalledVersion} → {row.AvailableVersion}" : row.AvailableVersion
            : null;

        var explanation = Services.Sources.InstallErrorExplainer.Describe(
            row.SourceId, row.InstallErrorDetail, key => LocalizationService.Instance[key]);

        ShowOwned(new Views.ErrorDetailWindow(
            new ErrorDetailViewModel(
                row.Name, row.SourceDisplay, versionChange, row.InstallErrorDetail!, _logging,
                explanation, row.ShowRetryInteractiveButton)));
    }

    /// <summary>
    /// Opens the real changelog text a source found for this row, or; where only a release LINK is known
    /// and that link is a GitHub/GitLab release page; tries a click-triggered fetch of the release body
    /// first. Text still missing after that (wrong host, not a release URL, or the fetch itself
    /// found nothing) falls back to a small "open the vendor's page" confirmation instead of the big window.
    /// </summary>
    internal async Task ShowChangelogAsync(UpdateRowViewModel row)
    {
        if (!row.HasChangelog)
            return;

        // An npm row's button exists purely on the strength of CanFetchChangelogOnClick (its own
        // registry enrichment never fills these fields, see NpmChangelogLookup's doc comment), so this is
        // the one place the click-triggered `npm view` itself runs, never during a scan or the background
        // enrichment pass.
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
            ShowOwned(new Views.ChangelogWindow(
                new ChangelogViewModel(row.Name, versionText, row.ChangelogText, row.ChangelogUrl, _logging)));
            return;
        }

        if (row.ChangelogUrl is not { Length: > 0 } linkUrl
            || !Uri.TryCreate(linkUrl, UriKind.Absolute, out var linkUri)
            || (linkUri.Scheme != Uri.UriSchemeHttp && linkUri.Scheme != Uri.UriSchemeHttps))
        {
            // A row whose button exists only because it MIGHT have had something to fetch (npm)
            // is told so explicitly when the click truly found neither text nor even a link, rather than
            // the loading spinner just stopping with no result at all.
            if (row.CanFetchChangelogOnClick)
            {
                var versionText = row.HasVersions ? row.AvailableVersion : null;
                ShowOwned(new Views.ChangelogWindow(
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

    /// <summary>
    /// The npm lookup half. `npm view &lt;package&gt; homepage repository --json` names the package's
    /// repository and homepage; a GitHub repository is turned into that repo's release-page URL and
    /// handed onward to the SAME github/gitlab release-link enrichment above (unchanged), so the real
    /// release-notes text still comes from exactly one fetch path. A non-GitHub (or absent) repository
    /// falls back to the plain homepage link, matching the item's "otherwise show the homepage link
    /// without inventing text". Cached under the same EnrichmentCache key and idiom as
    /// <see cref="TryEnrichChangelogFromLinkAsync"/> (an empty, non-null cached ChangelogUrl means "tried
    /// this version, found nothing", not "never tried"), so a repeat click on the same version never
    /// shells out to npm again.
    /// </summary>
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

    /// <summary>A homepage npm reports is free-form text, not a validated link; only an absolute http(s)
    /// URL is ever handed to the row (same defense-in-depth as <see cref="ChangelogViewModel.OpenRelease"/>).</summary>
    private static string? UsableHomepage(string? homepage) =>
        Uri.TryCreate(homepage, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? homepage
            : null;

    /// <summary>The real, default implementation behind <see cref="_npmChangelogLookup"/>.</summary>
    private Task<NpmChangelogLookup.NpmChangelogSource> LookupNpmChangelogAsync(
        string packageName, CancellationToken cancellationToken) =>
        NpmChangelogLookup.LookupAsync(packageName, _logging, cancellationToken);

    [RelayCommand]
    private void ShowSettings()
    {
        // An open settings window is only raised: a second view model would subscribe to the main window for
        // nothing, and its window would be thrown away by ShowOwned.
        if (IsOpen<Views.SettingsWindow>())
            return;

        // Non-modal, so the follow-up work belongs in the Closed callback: the line after ShowOwned
        // returns does NOT mean "the window is gone".
        ShowOwned(new Views.SettingsWindow(CreateSettingsViewModel()), onClosed: OnSettingsClosed);
    }

    internal SettingsViewModel CreateSettingsViewModel()
    {
        var settingsViewModel = new SettingsViewModel(
            _settings, _logging, _themeService, _autostart, _scanOrchestrator.SourceIds,
            CheckForUpdatesInteractiveAsync, IgnoredPackageNames(), _scanOrchestrator.IsKnownNotInstalled);

        // The language is normally switched in THAT window, and the main window's computed text, its rows and
        // its other dialogs are reached by no binding, so they are re-raised as soon as it changes, not when
        // the window is eventually closed.
        settingsViewModel.LanguageChanged += RefreshLocalizedText;

        // Removing an entry in the ignore list dialog takes effect in the table at once.
        settingsViewModel.IgnoreListChanged += ResyncIgnoreState;
        settingsViewModel.DataReset += OnDataReset;
        return settingsViewModel;
    }

    /// <summary>A factory reset deleted the files and reset settings.json, but this view model still held the
    /// old state in memory: the enrichment cache would have written itself straight back on the next save,
    /// rollbacks stayed offered for deleted history, and the table's view state and column layout (written
    /// back on exit) would have undone the reset.</summary>
    private void OnDataReset()
    {
        _enrichmentCache.Clear();
        _history.InvalidateRollbackIndex();
        ShowIgnored = _settings.Current.Ui.ShowIgnored;
        HideManual = _settings.Current.Ui.HideManual;
        ResetMainTableLayout();
    }

    /// <summary>Package key to display name for every row and child row, so the ignore list can show names
    /// instead of its raw settings keys.</summary>
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

    private void OnSettingsClosed()
    {
        // the settings window can change theme/language, so mirror them back into the main window's own pickers
        SelectedTheme = _settings.Current.Ui.Theme;
        SelectedLanguage = _settings.Current.Ui.Language;

        // sources are switched from the settings window too, and the pills read their state only at creation
        foreach (var (sourceId, pill) in _providerStatusById)
            pill.IsEnabled = IsSourceEnabled(sourceId);

        ResyncIgnoreState();
    }

    /// <summary>Re-syncs the rows' ignore and snooze marks to the (maybe changed) settings lists.</summary>
    private void ResyncIgnoreState()
    {
        foreach (var row in Updates)
        {
            row.IsIgnored = IsIgnored(row.Item);
            row.IsIgnoredAllVersions = _settings.Current.Excludes.Contains(PackageKey(row.Item));
            ApplySnoozeState(row);
        }

        ApplyFilter();

        // it also changes what the headline counts
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

    /// <summary>The owner is raised again when an owned window closes only if it is shown, not minimised and
    /// the app is not on its way out.</summary>
    internal static bool ShouldActivateOwnerOnClose(bool ownerVisible, WindowState ownerState, bool appExiting) =>
        ownerVisible && ownerState != WindowState.Minimized && !appExiting;

    /// <summary>
    /// Shows an owned dialog NON-modally, so a click in the main window can close it: that is what
    /// "beside the popup" means; a modal dialog disables its owner and there is no such click.
    /// One window per kind: a second click on the same toolbar button raises the one already open instead of
    /// stacking another copy, which non-modal windows would otherwise allow.
    /// </summary>
    private void ShowOwned(Window window, Action? onClosed = null)
    {
        var existing = Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(open => open.GetType() == window.GetType() && open.IsLoaded);

        if (existing is not null)
        {
            // These two show the content of one particular row: raising the old window would keep
            // showing the previous row's text under the click that asked for another.
            if (window is not (Views.ErrorDetailWindow or Views.ChangelogWindow))
            {
                existing.Activate();
                return;
            }

            existing.Close();
        }

        var owner = Application.Current?.MainWindow;
        window.Owner = owner;

        // When an owned window closes while it is not the active one (after a UAC prompt or a confirm
        // dialog, say), Windows hands activation to the next window in z-order, and the owner can fall
        // behind another application. Raising it while the closing window still exists keeps it in front.
        window.Closing += (_, args) =>
        {
            if (args.Cancel || owner is null)
                return;

            var exiting = Application.Current is App { IsExiting: true };
            if (ShouldActivateOwnerOnClose(owner.IsVisible, owner.WindowState, exiting))
                owner.Activate();
        };

        window.Closed += (_, _) => _logging.Info($"Owned window closed: {window.GetType().Name}");

        if (onClosed is not null)
            window.Closed += (_, _) => onClosed();

        window.Show();
    }
}
