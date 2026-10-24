using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Models;
using UltimateUpdater.Services;

namespace UltimateUpdater.ViewModels;

public partial class UpdateRowViewModel : ObservableObject
{
    private readonly LoggingService? _logging;
    private readonly Action<UpdateRowViewModel>? _onIgnore;
    private readonly IRowActions? _actions;
    private readonly Action<UpdateRowViewModel>? _onShowErrorDetail;
    private readonly Func<UpdateRowViewModel, Task>? _onShowChangelog;

    public UpdateItem Item { get; }

    [ObservableProperty]
    private bool isSelected;

    /// <summary>Space-key equivalent of clicking the row's own checkbox, a no-op where the checkbox
    /// itself is not shown (<see cref="CanInstall"/> false), same as clicking empty space would be.</summary>
    [RelayCommand]
    private void ToggleSelect()
    {
        if (CanInstall)
            IsSelected = !IsSelected;
    }

    [ObservableProperty]
    private BitmapSource? icon;

    [ObservableProperty]
    private RowInstallState installState;

    /// <summary>How many times in a row the same classified failure has now happened, the exact
    /// count <c>PersistentFailureRecord.Count</c> carries in settings; kept here purely for the hint text
    /// below, since only MainViewModel.Filtering.cs (which owns this sync) can see the settings record
    /// itself.</summary>
    [ObservableProperty]
    private int persistentFailureAttempts;

    partial void OnPersistentFailureAttemptsChanged(int value) => OnPropertyChanged(nameof(PersistentFailureHintText));

    [ObservableProperty]
    private double installPercent;

    [ObservableProperty]
    private string? installStatusText;

    /// <summary>Raw, unlocalized failure text from the driving tool, shown only when the user opens Details.</summary>
    [ObservableProperty]
    private string? installErrorDetail;

    public string Name => Item.Name;

    public string? InstalledVersion => Item.InstalledVersion;

    /// <summary>The newest version: what the source reported, or what enrichment found for it. Blank on a
    /// manual-only row besides Steam, a launcher client's own version says nothing about the update the
    /// row exists for (<see cref="HideManualNumbers"/>).</summary>
    public string? AvailableVersion => HideManualNumbers ? null : Item.AvailableVersion ?? EnrichedAvailableVersion;

    public string SourceId => Item.SourceId;

    /// <summary>The source as a person writes it: "Windows Update", not "windowsupdate".</summary>
    public string SourceDisplay => SourceNames.Display(Item.SourceId);

    /// <summary>The vendor part of a winget-shaped id ("Mozilla" from "Mozilla.Firefox"), read only for
    /// winget rows since only winget ids carry that "Publisher.Product" shape (a pip id such as
    /// "anyio#C:\...\python.exe" has a dot too, but in the interpreter path). Windows Update rows report
    /// "Microsoft" instead (its product name, never translated), except a driver update, whose real
    /// manufacturer WindowsUpdateSource captures from WUA when it can; with none captured it shows none
    /// rather than the wrong vendor. A pip row has no prefix to read: its publisher comes from PyPI's
    /// author/maintainer facts, fetched by the background enrichment pass that also looks up its release
    /// date and size (<see cref="EnrichedPublisher"/>), so it stays blank until that pass reaches the row.</summary>
    public string? Publisher => Item.SourceId switch
    {
        "winget" => Services.Sources.PublisherFromId.Derive(Item.Id),
        "windowsupdate" => WindowsUpdatePublisher,
        "pip" => EnrichedPublisher,
        _ => null,
    };

    /// <summary>The same fact as <see cref="Publisher"/>, shortened for the narrow table column
    /// (<see cref="Services.PublisherShortener"/>); the tooltip on that column, and every other place a
    /// publisher is shown, keeps the full <see cref="Publisher"/> text.</summary>
    public string? PublisherDisplay => Publisher is { } publisher ? Services.PublisherShortener.Shorten(publisher) : null;

    private string? WindowsUpdatePublisher =>
        Item.Meta.TryGetValue("driverManufacturer", out var manufacturer)
            ? manufacturer
            : Item.Meta.ContainsKey("isDriverUpdate") ? null : "Microsoft";

    public bool HasPublisher => Publisher is not null;

    /// <summary>The id as a person should read it: pip's composite "package#python.exe path" form
    /// (see <see cref="Services.Sources.PackageManagers.PipSource"/>) collapses to the plain package name,
    /// since the interpreter path is not an identifier the user asked for, it is plumbing that lets two
    /// interpreters keep separate rows.</summary>
    private string DisplayId => Item.SourceId == "pip"
        ? Services.Sources.PackageManagers.PipSource.PackageName(Item)
        : Item.Id;

    /// <summary>
    /// The row's own hover text, one fact per line, each shown only where known: the full name (for a name
    /// the row trims to an ellipsis), the package id, the source and, where the id carries one, the
    /// publisher; a pip row adds the interpreter it was found on, since two interpreters can offer the
    /// exact same package name with no other way to tell the rows apart.
    /// </summary>
    public string NameTooltip
    {
        get
        {
            var lines = new List<string> { Name, DisplayId, SourceDisplay };
            if (Publisher is { } publisher)
                lines.Add(publisher);

            if (Item.SourceId == "pip" && Item.Meta.TryGetValue("pythonPath", out var pythonPath))
                lines.Add(string.Format(LocalizationService.Instance["PipInterpreterTooltip"], pythonPath));

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Whether this row's install goes through the elevated worker (one shared UAC prompt for
    /// the whole admin batch), shown as a small badge on the queue row so waiting for that prompt is not
    /// a surprise.</summary>
    public bool NeedsElevation => InstallQueue.NeedsElevation(Item.SourceId);

    /// <summary>
    /// The ids of the other sources offering this exact same update, when the table has folded them into
    /// this row. The row's own source is the one that will actually run, and it is named first.
    /// </summary>
    private IReadOnlyList<string> _alternateSources = [];

    /// <summary>
    /// The rows this one stands in for. They are REAL, separate installations, uv's isolated tool venv and
    /// pip's site-packages both had graphify at the same version, so updating only the
    /// best-ranked one leaves the other behind and the row comes straight back on the next scan, from the
    /// source that was skipped. Installing this row therefore installs all of them, best first.
    /// </summary>
    public IReadOnlyList<UpdateRowViewModel> MergedPartners { get; private set; } = [];

    public bool HasAlternateSources => _alternateSources.Count > 0;

    /// <summary>"uv tools · pip", every source that has this update, best first.</summary>
    public string AllSourcesText =>
        _alternateSources.Count == 0
            ? SourceDisplay
            : $"{SourceDisplay} · {string.Join(" · ", _alternateSources.Select(SourceNames.Display))}";

    /// <summary>Spelt out for the hover, where there is room to say what the list means.</summary>
    public string AllSourcesTooltip =>
        _alternateSources.Count == 0
            ? SourceDisplay
            : string.Format(LocalizationService.Instance["RowAlsoInSources"], SourceDisplay,
                string.Join(", ", _alternateSources.Select(SourceNames.Display)));

    /// <summary>
    /// Called by the table when it works out which rows are the same update. Silent when nothing changed,
    /// so re-running the filter does not repaint rows that did not move.
    /// </summary>
    public void SetAlternateSources(IReadOnlyList<UpdateRowViewModel> partners)
    {
        MergedPartners = partners;

        // Ids, not display names: some names are translated, and a language switch must re-read them.
        var sources = partners.Select(p => p.SourceId).ToList();
        if (_alternateSources.SequenceEqual(sources, StringComparer.Ordinal))
            return;

        _alternateSources = sources;
        OnPropertyChanged(nameof(HasAlternateSources));
        OnPropertyChanged(nameof(AllSourcesText));
        OnPropertyChanged(nameof(AllSourcesTooltip));
    }

    /// <summary>
    /// Order of the groups in the table: what is worth seeing first. Applications and the store
    /// before the system, developer tooling after that, games last; they are the least urgent and
    /// the most numerous.
    /// </summary>
    public int CategoryOrder
    {
        get
        {
            // A GitHub-release row gets its OWN group, directly above the generic one,
            // unlike a store/launcher/driver-page row it carries a REAL installed/available version
            // comparison and a real download size (see HideManualNumbers below), so lumping it in with
            // rows that have nothing to compare buried the one manual-update kind worth reading closely.
            if (IsGitHubManual)
                return 98;

            // Every other "you open it yourself" row, a store, a game launcher, Steam, a vendor driver
            // page, is collected into ONE "Manual update" category that always sits at the very bottom,
            // whatever else is present.
            if (IsManualOnly)
                return 99;

            return Item.Category switch
            {
                UpdateCategory.Software => 0,
                UpdateCategory.Store => 1,
                UpdateCategory.SystemAndDrivers => 2,
                UpdateCategory.DeveloperTools => 3,
                UpdateCategory.Games => 4,
                _ => 8,
            };
        }
    }

    /// <summary>
    /// Rows this program cannot install: a store or a launcher that only hands the job to its own client.
    /// They are grouped at the bottom rather than mixed in with the ones a click actually updates, since
    /// "I can do this for you" versus "you have to do this" is the most important thing about the row.
    /// Public so the row's context menu (updates only, not stores/launchers) can be suppressed for these.
    /// </summary>
    public bool IsManualOnly => !Item.CanInstall && !string.IsNullOrEmpty(Item.OpenTarget);

    /// <summary>
    /// True for a row <see cref="GitHubReleaseSource"/> found, an installed program whose Add/Remove
    /// Programs entry names a GitHub repository with a newer release, in its own category.
    /// It carries a real installed/available version and often a real size, unlike a bare store or
    /// launcher row, which is exactly why it gets its own group instead of sharing "Manual update".
    /// </summary>
    public bool IsGitHubManual => IsManualOnly && Item.SourceId == "github-release";

    /// <summary>
    /// Date, version and size are blanked on a manual-only row, except Steam (whose games carry real build
    /// ids and a real download size) and a GitHub-release row (whose point is a real version comparison
    /// against the repo's latest release), and except any row with children (an aggregate like the Steam
    /// launcher row has no numbers of its own). Internal, not private: also the exact membership test for
    /// the compact block at the table's end (<see cref="BelongsInCompactBlock"/>), since the "no real
    /// values" rows are the rows a category group has nothing useful to show for.
    /// </summary>
    internal bool HideManualNumbers => IsManualOnly && Item.SourceId is not ("steam" or "github-release") && !HasChildren;

    /// <summary>
    /// True for the rows MainViewModel.ManualOnlyView collects into the compact block below the table
    /// instead of leaving them full-height inside their category group. Steam and any row with children
    /// (Steam's own launcher row) are excluded because they either carry real values or have their own
    /// nested child list, neither fits a flat row of compact chips.
    /// </summary>
    internal bool BelongsInCompactBlock => HideManualNumbers;

    /// <summary>
    /// The small source line under the name is shown for real update rows but dropped for manual-open
    /// rows (a launcher/store), where it just repeats the name, the name then centres on its own.
    /// </summary>
    public bool ShowSourceLine => !IsManualOnly;

    /// <summary>
    /// The user asked not to be told about this update. It stays on screen but marked, is not counted,
    /// cannot be ticked or installed, and can be un-ignored from its context menu or its inline button.
    /// Set by the main view model from the persisted exclude list.
    /// </summary>
    [ObservableProperty]
    private bool isIgnored;

    /// <summary>
    /// True when the whole package is ignored rather than just the version on screen. The two read
    /// differently to the user ("Alle ignoriert" vs "Ignoriert") and offer different menu entries, so the
    /// row has to know which one it is.
    /// </summary>
    [ObservableProperty]
    private bool isIgnoredAllVersions;

    /// <summary>
    /// "Not now", the row leaves the default view until <see cref="SnoozedUntil"/>, without the
    /// permanence of Ignore. Mutually exclusive with <see cref="IsIgnored"/>: the main view model clears
    /// whichever one already applied before setting the other. Set from the persisted <c>Snoozed</c>
    /// dictionary, same as <see cref="IsIgnored"/> is from <c>Excludes</c>.
    /// </summary>
    [ObservableProperty]
    private bool isSnoozed;

    [ObservableProperty]
    private DateTimeOffset? snoozedUntil;

    /// <summary>
    /// The operation this row is running (or last ran) is a REMOVAL, not an update, so its result reads
    /// "Uninstalled", in red, and a Retry repeats the removal instead of installing the thing the user
    /// just asked to have taken off the machine. Cleared by <see cref="BeginInstall"/>.
    /// </summary>
    [ObservableProperty]
    private bool wasUninstalled;

    public bool IsCritical => Item.IsCritical;

    /// <summary>
    /// Already done in this session. The row keeps its result on screen so the user can see what
    /// happened, but it must not be installable a second time; the next scan is what removes it.
    /// </summary>
    public bool IsInstalled => InstallState == RowInstallState.Succeeded;

    /// <summary>
    /// Installable RIGHT NOW. A row that is queued or already running is not: a mid-install row must drop
    /// out of the select-all totals, or "select all" would offer to install what is already installing.
    /// </summary>
    public bool CanInstall =>
        Item.CanInstall && !IsIgnored && !IsSnoozed
        && InstallState is RowInstallState.Idle or RowInstallState.Failed or RowInstallState.Cancelled;

    public bool CanOpen => !string.IsNullOrEmpty(Item.OpenTarget);

    /// <summary>The "Ignored" pill next to the name, it says whether ONE version or the package is ignored.</summary>
    public string IgnoredBadgeLabel =>
        LocalizationService.Instance[IsIgnoredAllVersions ? "IgnoredAllBadge" : "IgnoredBadge"];

    public string UnignoreHeader => LocalizationService.Instance["UnignoreButton"];

    /// <summary>
    /// Rows shown as a collapsible sub-list ON this row instead of as rows of their own, Steam's
    /// pending game updates under the Steam launcher row. Each child keeps its own context menu, so a
    /// game can still be started or removed from here.
    /// </summary>
    public IReadOnlyList<UpdateRowViewModel> Children { get; }

    public bool HasChildren => Children.Count > 0;

    public string ChildCountText => string.Format(
        LocalizationService.Instance[Children.Count == 1 ? "SubListCountOne" : "SubListCount"], Children.Count);

    /// <summary>
    /// The expanded state lives in the main view model, the same store the category groups use,
    /// because the row object itself is rebuilt every time a scan streams new results into the table,
    /// and a state kept on the row would silently reopen whatever the user had just collapsed.
    /// </summary>
    private string SubListKey => $"sublist:{Item.SourceId}:{Item.Id}";

    public bool AreChildrenExpanded
    {
        get => _actions?.IsSubListExpanded(SubListKey) ?? true;
        set
        {
            if (value == AreChildrenExpanded)
                return;

            _actions?.SetSubListExpanded(SubListKey, value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Filled in after the scan by the background enrichment pass (a package's own registry), for the
    /// many sources whose outdated listing carries neither a date nor a size. Prefer these over the
    /// snapshot values on the immutable <see cref="Item"/>; both start null.
    /// </summary>
    [ObservableProperty]
    private DateTimeOffset? enrichedPublishDate;

    [ObservableProperty]
    private long? enrichedDownloadSize;

    private DateTimeOffset? EffectivePublishDate => EnrichedPublishDate ?? Item.PublishDate;

    private long? EffectiveDownloadSize => EnrichedDownloadSize ?? Item.DownloadSize;

    /// <summary>Download size, where the source or the later enrichment pass knew it. Blank on a
    /// manual-only row besides Steam (<see cref="HideManualNumbers"/>).</summary>
    public string? DownloadSizeText =>
        !HideManualNumbers && EffectiveDownloadSize is > 0 ? ByteSize.Format(EffectiveDownloadSize.Value) : null;

    /// <summary>Publish/deployment date, where known. Local, date only, in the chosen UI language's own
    /// short-date order. Blank on a manual-only row besides Steam (<see cref="HideManualNumbers"/>).</summary>
    public string? PublishDateText =>
        !HideManualNumbers && EffectivePublishDate is { } date
            ? date.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture)
            : null;

    public bool HasDownloadSize => DownloadSizeText is not null;

    public bool HasPublishDate => PublishDateText is not null;

    /// <summary>
    /// Merges a background enrichment result onto the row. Only fills what is still unknown and never
    /// overwrites a real figure the source already reported, so a good up-front value always wins.
    /// </summary>
    public void ApplyEnrichment(
        DateTimeOffset? date, long? size, string? projectUrl = null, string? availableVersion = null,
        string? changelogBody = null, string? changelogUrl = null, string? publisher = null)
    {
        if (date is not null && Item.PublishDate is null)
            EnrichedPublishDate = date;
        if (size is > 0 && Item.DownloadSize is null or 0)
            EnrichedDownloadSize = size;
        if (!string.IsNullOrEmpty(projectUrl))
            EnrichedProjectUrl = projectUrl;
        // only fills a gap: a version the source itself reported is never overwritten
        if (!string.IsNullOrEmpty(availableVersion) && string.IsNullOrEmpty(Item.AvailableVersion))
            EnrichedAvailableVersion = availableVersion;
        // Item.Meta wins when a source already populated it at scan time (GitHubReleaseSource), this
        // only fills the gap for sources whose changelog only shows up on the later enrichment pass
        // (winget's `winget show`).
        if (!string.IsNullOrEmpty(changelogBody) && string.IsNullOrEmpty(ChangelogText))
            EnrichedChangelogBody = changelogBody;
        if (!string.IsNullOrEmpty(changelogUrl) && string.IsNullOrEmpty(ChangelogUrl))
            EnrichedChangelogUrl = changelogUrl;
        if (!string.IsNullOrEmpty(publisher) && EnrichedPublisher is null)
            EnrichedPublisher = publisher;
    }

    /// <summary>The newest version, for sources whose own listing does not report one (see UpdateEnrichment).</summary>
    [ObservableProperty]
    private string? enrichedAvailableVersion;

    partial void OnEnrichedAvailableVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(AvailableVersion));
        OnPropertyChanged(nameof(HasVersions));
        OnPropertyChanged(nameof(SortVersion));
        OnPropertyChanged(nameof(VersionChangeTooltip));
    }

    /// <summary>A "learn more" link (changelog/homepage) the enrichment pass found; http(s) only.</summary>
    [ObservableProperty]
    private string? enrichedProjectUrl;

    /// <summary>A pip row's publisher, found by the same background enrichment pass as the date/size
    /// above (PyPI's own author/maintainer, or a linked repository's owner); see <see cref="Publisher"/>.</summary>
    [ObservableProperty]
    private string? enrichedPublisher;

    partial void OnEnrichedPublisherChanged(string? value)
    {
        OnPropertyChanged(nameof(Publisher));
        OnPropertyChanged(nameof(PublisherDisplay));
        OnPropertyChanged(nameof(HasPublisher));
        OnPropertyChanged(nameof(NameTooltip));
    }

    public bool HasInfoLink => !string.IsNullOrEmpty(EnrichedProjectUrl);

    public string InfoLinkHeader => LocalizationService.Instance["InfoLinkButton"];

    partial void OnEnrichedProjectUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(HasInfoLink));
        OpenInfoLinkCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens the project/changelog page. The URL was validated http(s) at parse time; re-checked here.</summary>
    [RelayCommand(CanExecute = nameof(HasInfoLink))]
    private void OpenInfoLink()
    {
        if (EnrichedProjectUrl is not { } url || _logging is null)
            return;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            UriLauncher.LaunchDetached(url, _logging);
        }
    }

    /// <summary>
    /// The directory this update is installed in, resolved in the background after the scan (the
    /// uninstall registry, Steam's game folder, or a launcher's own directory). Null until then, or
    /// when none can be found, in which case the row offers no "open folder" action at all.
    /// </summary>
    [ObservableProperty]
    private string? installLocationPath;

    public bool CanOpenFolder => !string.IsNullOrEmpty(InstallLocationPath);

    public string OpenFolderHeader => LocalizationService.Instance["OpenFolderButton"];

    partial void OnInstallLocationPathChanged(string? value)
    {
        OnPropertyChanged(nameof(CanOpenFolder));
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens the install directory in Explorer. The path was confirmed to exist when resolved;
    /// UriLauncher refuses anything that is not a real rooted path, so nothing user-typed reaches here.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        if (InstallLocationPath is { Length: > 0 } path && _logging is not null)
            UriLauncher.LaunchDetached(path, _logging);
    }

    partial void OnEnrichedPublishDateChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(PublishDateText));
        OnPropertyChanged(nameof(HasPublishDate));
        // so the table's live sorting re-orders this row when the date arrives, no full refresh needed
        OnPropertyChanged(nameof(SortDate));
    }

    partial void OnEnrichedDownloadSizeChanged(long? value)
    {
        OnPropertyChanged(nameof(DownloadSizeText));
        OnPropertyChanged(nameof(HasDownloadSize));
        OnPropertyChanged(nameof(SortSize));
        // DownloadSizeBytes itself never announced its own change, so nothing that sums it
        // across selected rows (MainViewModel.SelectionSummary) had a signal to recompute when the size
        // arrived after the fact via background enrichment rather than up front from the scan.
        OnPropertyChanged(nameof(DownloadSizeBytes));
    }

    /// <summary>Version columns stay hidden for rows where no real version is known (Tier C).</summary>
    public bool HasVersions => !string.IsNullOrEmpty(AvailableVersion);

    /// <summary>Windows Update cannot say what is installed, so those rows show the new version alone.</summary>
    public bool HasInstalledVersion => !string.IsNullOrEmpty(Item.InstalledVersion);

    /// <summary>One tooltip for the whole version lane (installed → available), rather than one
    /// per text block, so the gaps and the arrow between the two numbers are hit-testable too. Installed
    /// plus available when both exist, the single value otherwise.</summary>
    public string? VersionChangeTooltip => HasInstalledVersion
        ? $"{InstalledVersion} → {AvailableVersion}"
        : AvailableVersion;

    /// <summary>
    /// Open owns the action cell only on a row that has nothing to install: every button in that cell is
    /// drawn in the SAME grid cell, so two visible at once means the later one covers the earlier (the
    /// NVIDIA driver row, whose Open target is its release-notes page, had "Aktualisieren" painted over by
    /// "Öffnen"). An installable row still reaches its link through the context menu.
    /// </summary>
    public bool ShowOpenButton => IsManualOnly && !IsIgnored && InstallState == RowInstallState.Idle;

    /// <summary>
    /// The inline "show again" on an ignored row, same cell as everything else, so it must not be drawn on
    /// top of a state label or an Open button either. Shown for EVERY ignored row regardless of install
    /// state, including one stuck in <see cref="RowInstallState.NeedsManualWork"/>.
    /// <see cref="ShowStateLabel"/> excludes ignored rows for exactly this reason.
    /// </summary>
    public bool ShowUnignoreButton => IsIgnored;

    /// <summary>A per-row Install button on an installable row that has not been queued yet.</summary>
    public bool ShowInstallButton => Item.CanInstall && !IsIgnored && !IsSnoozed && InstallState == RowInstallState.Idle;

    public string InstallHeader => LocalizationService.Instance["InstallButton"];

    /// <summary>
    /// The version a rollback would install: the FromVersion of this package's own last successful
    /// "install" history entry (the version it upgraded FROM), so "back to it" undoes exactly the last real
    /// change, not some older release. Null when the source cannot pin an exact version
    /// (<see cref="Services.Sources.IUpdateSource.SupportsVersionPinning"/>), no successful install of this
    /// package is on record, or that install did not know its own previous version. Set by
    /// <see cref="MainViewModel"/> after every scan (like <see cref="IsIgnored"/>/<see cref="IsSnoozed"/>,
    /// applied from the outside rather than computed on the row).
    /// </summary>
    [ObservableProperty]
    private string? downgradeTargetVersion;

    /// <summary>The row menu's "back to the previous version" entry: offered only while the row is
    /// genuinely idle, same gate as <see cref="ShowInstallButton"/>, so a queued/running/finished row
    /// never offers a second, conflicting action.</summary>
    public bool ShowDowngradeButton =>
        DowngradeTargetVersion is { Length: > 0 } && !IsIgnored && InstallState == RowInstallState.Idle;

    public string DowngradeHeader =>
        string.Format(LocalizationService.Instance["RowDowngrade"], DowngradeTargetVersion);

    partial void OnDowngradeTargetVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowDowngradeButton));
        OnPropertyChanged(nameof(DowngradeHeader));
        DowngradeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Confirmed by the caller (<see cref="IRowActions.Downgrade"/>) before anything runs.</summary>
    [RelayCommand(CanExecute = nameof(ShowDowngradeButton))]
    private void Downgrade() => _actions?.Downgrade(this);

    /// <summary>A failed row offers a retry, which re-queues it through the same path as a fresh install;
    /// a row the user stopped themselves offers the very same button, for the very same reason.</summary>
    public bool ShowRetryButton =>
        Item.CanInstall && !IsIgnored && InstallState is RowInstallState.Failed or RowInstallState.Cancelled;

    public string RetryHeader => LocalizationService.Instance["RetryButton"];

    /// <summary>The interactive-retry menu entry, offered only for a Recoverable-classified failure,
    /// outside an unattended run.</summary>
    public bool ShowRetryInteractiveButton =>
        InstallState == RowInstallState.Failed
        && !App.IsUnattended
        // Only WingetSource implements the interactive retry itself (WingetSource.InstallInteractiveAsync);
        // without this, a coincidental code match on another source's own text (a plain "1602" in some
        // other tool's output) would offer a button that silently does nothing when clicked.
        && SourceId == "winget"
        && Services.Sources.InstallErrorExplainer.Explain(SourceId, InstallErrorDetail)?.AllowsInteractiveRetry == true;

    public string RetryInteractiveHeader => LocalizationService.Instance["RetryInteractiveHeader"];

    [RelayCommand(CanExecute = nameof(ShowRetryInteractiveButton))]
    private void RetryInteractive() => _actions?.RetryInteractive(this);

    /// <summary>
    /// Set by the main view model while the interactive retry is actually running this row, winget's own
    /// UI is driving, so the percentage is meaningless; the row shows "läuft interaktiv…" instead of
    /// the normal percent/bar pair rather than a number that cannot mean anything.
    /// </summary>
    [ObservableProperty]
    private bool isRunningInteractively;

    public bool ShowInteractiveRunningLabel => IsInstallRunning && IsRunningInteractively;

    public bool ShowRunningProgress => IsInstallRunning && !IsRunningInteractively;

    public string InteractiveRunningText => LocalizationService.Instance["RowRunningInteractive"];

    partial void OnIsRunningInteractivelyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInteractiveRunningLabel));
        OnPropertyChanged(nameof(ShowRunningProgress));
    }

    public bool IsInstallRunning => InstallState == RowInstallState.Running;

    /// <summary>This row's own stop button, shown only while it is actually running; stopping it
    /// leaves the rest of the queue, and the rest of an admin batch it might be part of, running.</summary>
    public bool ShowStopSingleButton => IsInstallRunning;

    public string StopSingleTooltip => LocalizationService.Instance["StopSingleTooltip"];

    [RelayCommand(CanExecute = nameof(ShowStopSingleButton))]
    private void StopSingle() => _actions?.CancelSingle(this);

    /// <summary>A failed row can show why: the real tool output, on deliberate request (SECURITY.md).</summary>
    public bool HasInstallError =>
        InstallState is RowInstallState.Failed or RowInstallState.HandedOff or RowInstallState.Stuck
            or RowInstallState.NeedsManualWork
        && !string.IsNullOrWhiteSpace(InstallErrorDetail);

    /// <summary>Fallback glyph so the icon gutter never looks ragged when no icon resolves.</summary>
    public string InitialLetter => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();

    /// <summary>
    /// A bundled brand mark, for rows whose own icon cannot be found: an npm or pip package has no
    /// installed application on disk to take one from at all. Never preferred over the real icon,
    /// the Image on top of the tile covers this whenever one was extracted.
    /// </summary>
    public Geometry? BrandGeometry { get; }

    public bool UsesBrandTile => BrandGeometry is not null;

    /// <summary>
    /// The coloured tile is a stand-in, so it must not sit behind a real icon: most application icons
    /// are round or have transparent corners, and the square tile showed through them as a border.
    /// </summary>
    public bool UsesTile => Icon is null;

    /// <summary>
    /// A letter identifies a named application, but it is meaningless for the rest: a Windows update
    /// showed up as "2" for "2024-08 Kumulatives Update…". Those rows say what KIND of thing they are
    /// instead. Nothing here is ever preferred over the application's real icon.
    /// </summary>
    public bool UsesLetterTile => !UsesBrandTile && Item.Category is UpdateCategory.Software or UpdateCategory.Games;

    public bool UsesGlyphTile => !UsesBrandTile && !UsesLetterTile;

    /// <summary>
    /// Muted, deliberately unbranded tile colours. A stable colour per name makes rows without a real
    /// icon tellable apart at a glance without pretending to be anyone's logo.
    /// </summary>
    private static readonly Brush[] FallbackTiles =
    [
        Frozen(0x3A, 0x5A, 0x7A), Frozen(0x5A, 0x3A, 0x5F), Frozen(0x3F, 0x5F, 0x4A),
        Frozen(0x6B, 0x4A, 0x33), Frozen(0x44, 0x4A, 0x6B), Frozen(0x6B, 0x3A, 0x44),
        Frozen(0x35, 0x5C, 0x5C), Frozen(0x5C, 0x55, 0x33),
    ];

    /// <summary>Stable across runs: derived from the name, never from a hash that varies per process.</summary>
    public Brush InitialBrush
    {
        get
        {
            var seed = Name.Where(char.IsLetterOrDigit)
                .Aggregate(17, (current, c) => (current * 31 + char.ToLowerInvariant(c)) & 0x7FFFFFF);

            return FallbackTiles[seed % FallbackTiles.Length];
        }
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public string CategoryDisplay
    {
        get
        {
            // A GitHub-release row reads as its own group, ahead of the generic manual-update one.
            if (IsGitHubManual)
                return LocalizationService.Instance["CategoryGitHubManual"];

            // Stores, launchers, Steam and vendor-page rows all read as one "Manual update" group.
            if (IsManualOnly)
                return LocalizationService.Instance["CategoryManualUpdate"];

            return LocalizationService.Instance[$"Category{Item.Category}"];
        }
    }

    /// <summary>The same grouping <see cref="CategoryDisplay"/> reads, but the language-independent
    /// identifier that remembers collapsed state.</summary>
    public string CategoryKey => IsGitHubManual ? "GitHubManual" : IsManualOnly ? "ManualUpdate" : Item.Category.ToString();

    public string CriticalLabel => LocalizationService.Instance["CriticalBadge"];

    /// <summary>Toggles wording: ignore THIS version, or show an already-ignored one again.</summary>
    public string IgnoreHeader =>
        LocalizationService.Instance[IsIgnored ? "UnignoreButton" : "IgnoreButton"];

    public string IgnoreAllHeader => LocalizationService.Instance["IgnoreAllButton"];

    /// <summary>The plain "ignore" wording used for manual rows, which have no per-version distinction.</summary>
    public string IgnoreSimpleHeader =>
        LocalizationService.Instance[IsIgnored ? "UnignoreButton" : "IgnoreSimpleButton"];

    /// <summary>
    /// Which ignore entries the menu offers, by state, the two entries read as nonsense
    /// once the whole package was already ignored:
    /// nothing ignored → "ignore this version" + "ignore all versions";
    /// this version ignored → "show again" + "ignore all versions";
    /// the package ignored → "show again" alone.
    /// </summary>
    public bool ShowVersionIgnore => !IsManualOnly;

    public bool ShowIgnoreVersionEntry => !IsManualOnly;

    public bool ShowIgnoreAllEntry => !IsManualOnly && !IsIgnoredAllVersions;

    public string SnoozeHeader => LocalizationService.Instance["SnoozeButton"];

    public string SnoozeOneDayHeader => LocalizationService.Instance["SnoozeOneDay"];

    public string SnoozeOneWeekHeader => LocalizationService.Instance["SnoozeOneWeek"];

    public string SnoozeOneMonthHeader => LocalizationService.Instance["SnoozeOneMonth"];

    public string UnsnoozeHeader => LocalizationService.Instance["UnsnoozeButton"];

    /// <summary>The "Remind me later" submenu offers nothing useful on a row already ignored or already
    /// snoozed, the first has no snooze concept until un-ignored, the second already has one running.</summary>
    public bool ShowSnoozeMenu => !IsIgnored && !IsSnoozed;

    public bool ShowUnsnoozeEntry => IsSnoozed;

    /// <summary>The pill next to the name on a snoozed row, mirroring <see cref="IgnoredBadgeLabel"/>.</summary>
    public string SnoozedBadgeLabel => SnoozedUntil is { } until
        ? string.Format(
            LocalizationService.Instance["SnoozedBadge"],
            until.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture))
        : string.Empty;

    public string ResetPersistentFailureHeader => LocalizationService.Instance["TryNormallyAgainButton"];

    /// <summary>Only offered once the row is actually flagged, the whole point is that the normal
    /// Install/Retry buttons stop being offered until this explicit reset is used instead.</summary>
    public bool ShowResetPersistentFailureEntry => InstallState == RowInstallState.NeedsManualWork;

    public string OpenHeader => LocalizationService.Instance["OpenButton"];

    /// <summary>
    /// The ROW MENU's wording for Open. "OpenButton" is shared with the tray menu and the folder action, so
    /// it stays the bare word, but in a row's menu, between Start, Uninstall and "Open folder", a bare
    /// "Open" says nothing about what would open. Where the target is a web page (a driver's release notes,
    /// a vendor page) it says so; where it is a launcher or an application it stays the plain word,
    /// because "open website" would be a lie.
    /// </summary>
    public string OpenTargetHeader =>
        LocalizationService.Instance[OpensAWebsite ? "ContextMenuOpenWebsite" : "OpenButton"];

    private bool OpensAWebsite =>
        Item.OpenTarget is { Length: > 0 } target
        && Uri.TryCreate(target, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public string DetailsHeader => LocalizationService.Instance["ShowErrorDetailButton"];

    public string InstallStateText => InstallState switch
    {
        RowInstallState.Queued => LocalizationService.Instance["RowQueued"],
        // A removal says so rather than "done", the difference matters when reading back what happened.
        RowInstallState.Succeeded when WasUninstalled => LocalizationService.Instance["RowUninstalled"],
        RowInstallState.Succeeded => LocalizationService.Instance["RowDone"],
        RowInstallState.Failed => LocalizationService.Instance["RowFailed"],
        RowInstallState.HandedOff => LocalizationService.Instance["RowHandedOff"],
        RowInstallState.Stuck => LocalizationService.Instance["RowStuck"],
        RowInstallState.NeedsManualWork => LocalizationService.Instance["RowNeedsManualWork"],
        RowInstallState.Cancelled => LocalizationService.Instance["RowCancelled"],
        // Running shows a real progress bar with the percentage BESIDE it (InstallPercentText),
        // never the number written into the bar itself.
        _ => string.Empty,
    };

    /// <summary>
    /// Hover text for <see cref="InstallStateText"/>: for Stuck/HandedOff/NeedsManualWork (single-word in
    /// the column) the explanatory sentence, with the source's own raw status line appended on its own
    /// line where one came in. Every other state just shows the raw status line.
    /// </summary>
    public string? InstallStateTooltip => InstallState switch
    {
        RowInstallState.Stuck => WithRawStatus(LocalizationService.Instance["RowStuckDetail"]),
        RowInstallState.HandedOff => WithRawStatus(LocalizationService.Instance["RowHandedOffDetail"]),
        RowInstallState.NeedsManualWork => WithRawStatus(LocalizationService.Instance["RowNeedsManualWorkDetail"]),
        RowInstallState.Cancelled => CancelledReasonLine ?? LocalizationService.Instance["RowCancelledTooltip"],
        _ => InstallStatusText,
    };

    // A status that already is the explanatory sentence (the queue reports a stuck row with exactly that
    // line) is not repeated under itself.
    private string WithRawStatus(string detail) =>
        string.IsNullOrWhiteSpace(InstallStatusText) || InstallStatusText == detail ? detail : $"{detail}\n{InstallStatusText}";

    /// <summary>The one-line nudge shown once the three-strikes tally flags this row, wired to
    /// the same ignore action as the row's own context menu (<see cref="IgnoreCommand"/>).</summary>
    public bool ShowPersistentFailureHint => InstallState == RowInstallState.NeedsManualWork;

    public string PersistentFailureHintText =>
        string.Format(LocalizationService.Instance["PersistentFailureHint"], PersistentFailureAttempts);

    /// <summary>The percentage shown next to a running row's bar.</summary>
    public string InstallPercentText => PercentFormat.Format(InstallPercent);

    /// <summary>The state word shows only when there is no running bar to show in its place, and never on
    /// an ignored row: that row shows "show again" instead (<see cref="ShowUnignoreButton"/>), whatever
    /// its install state.</summary>
    public bool ShowStateLabel =>
        !IsIgnored &&
        InstallState is RowInstallState.Queued or RowInstallState.Succeeded or RowInstallState.Failed
            or RowInstallState.HandedOff or RowInstallState.Stuck or RowInstallState.NeedsManualWork
            or RowInstallState.Cancelled;

    public bool IsInstallFailed => InstallState == RowInstallState.Failed;

    /// <summary>Queued but not started, the row shows a turning ring beside the word.</summary>
    public bool IsInstallQueued => InstallState == RowInstallState.Queued;

    /// <summary>
    /// The state word ("queued"/"done") is shown for those states; a failure shows a red warning icon
    /// instead of the easily-missed "failed" text, since the retry/details buttons already sit beside it.
    /// </summary>
    /// <summary>A failed row shows its icon instead of the word; so does a row with the visible retry
    /// (<see cref="ShowResetAndRetryButton"/>), whose word, details button and retry would not fit together.</summary>
    public bool ShowStateWord => ShowStateLabel && !IsInstallFailed && !ShowResetAndRetryButton;

    /// <summary>
    /// Hover text for the failure icon: a plain-language line when a code was recognized in
    /// <see cref="InstallErrorDetail"/>, falling back to the raw status line or the bare "failed" word
    /// when nothing was recognized.
    /// </summary>
    public string InstallFailedTooltip =>
        Services.Sources.InstallErrorExplainer.Describe(SourceId, InstallErrorDetail, key => LocalizationService.Instance[key])
        ?? InstallStatusText
        ?? LocalizationService.Instance["RowFailed"];

    /// <summary>
    /// The one-line reason a failed row shows under its name instead of the source: only the translated
    /// explainer text (or the bare "failed" word when no code was recognised), NEVER the raw tool output;
    /// that stays behind the deliberate Details click. Deliberately not <see cref="InstallFailedTooltip"/>,
    /// whose last fallback is the raw status line.
    /// </summary>
    public string FailureLine =>
        Services.Sources.InstallErrorExplainer.Describe(SourceId, InstallErrorDetail, key => LocalizationService.Instance[key])
        ?? LocalizationService.Instance["RowFailed"];

    /// <summary>The reason a cancelled row carries (a blocked row the user skipped), shown under the name
    /// and as the state tooltip. Null for a row the user stopped himself: that one has no reason text.</summary>
    public string? CancelledReasonLine =>
        InstallState == RowInstallState.Cancelled && !string.IsNullOrWhiteSpace(InstallStatusText)
            ? InstallStatusText
            : null;

    public bool ShowCancelledReason => CancelledReasonLine is not null;

    /// <summary>The source line under the name; a failed row shows <see cref="FailureLine"/> in its place.</summary>
    public bool ShowSourceText => ShowSourceLine && !IsInstallFailed && !ShowCancelledReason;

    public Brush InstallStateBrush => InstallState switch
    {
        // "Uninstalled" is red: something is gone from the machine, which is worth reading as such.
        RowInstallState.Succeeded when WasUninstalled => Brush("SystemFillColorCriticalBrush"),
        RowInstallState.Succeeded => Brush("SystemFillColorSuccessBrush"),
        RowInstallState.Failed => Brush("SystemFillColorCriticalBrush"),
        // Neither green nor red: genuinely unknown, not a claim either way.
        RowInstallState.HandedOff or RowInstallState.Stuck => Brush("SystemFillColorCautionBrush"),
        // Not red either, the same repeated failure has already been explained; red is reserved for
        // "something just went wrong", which this row saying so for the fourth time is not.
        RowInstallState.NeedsManualWork => Brush("SystemFillColorCautionBrush"),
        // Caution coloured like HandedOff/Stuck (by design): the user chose to stop it, so it is
        // neither the green of a real success nor the red of a real failure.
        RowInstallState.Cancelled => Brush("SystemFillColorCautionBrush"),
        _ => Brush("TextFillColorTertiaryBrush"),
    };

    /// <summary>What a screen reader announces for the row, without it, the class name is read out.</summary>
    public override string ToString() => Name;

    public UpdateRowViewModel(
        UpdateItem item,
        LoggingService? logging = null,
        Action<UpdateRowViewModel>? onIgnore = null,
        IRowActions? actions = null,
        Action<UpdateRowViewModel>? onShowErrorDetail = null,
        Func<UpdateRowViewModel, Task>? onShowChangelog = null)
    {
        Item = item;
        _logging = logging;
        _onIgnore = onIgnore;
        _actions = actions;
        _onShowErrorDetail = onShowErrorDetail;
        _onShowChangelog = onShowChangelog;
        BrandGeometry = BrandIcons.Resolve(item.Name, item.SourceId, item.Id);

        // Children are rows in their own right (same actions, so Start/Uninstall still work on a game),
        // they are just presented inside their parent instead of in the table. The ignore callback is handed
        // down too: without it a child's Ignore would flip nothing and persist nothing at all. So are the
        // Details and changelog callbacks: the child template shows Details on a failed game, and without the
        // callback that button did nothing.
        Children = item.Children.Count == 0
            ? []
            : item.Children.Select(child => new UpdateRowViewModel(
                child, logging, onIgnore, actions, onShowErrorDetail, onShowChangelog)).ToList();
    }

    [RelayCommand]
    private void Open()
    {
        if (Item.OpenTarget is { Length: > 0 } target && _logging is not null)
            UriLauncher.LaunchDetached(target, _logging);
    }

    /// <summary>Toggle: ignore this update, or (when already ignored) show it again.</summary>
    [RelayCommand]
    private void Ignore() => _onIgnore?.Invoke(this);

    [RelayCommand]
    private void IgnoreAllVersions() => _actions?.IgnoreAllVersions(this);

    /// <summary>The submenu's three fixed durations: "day"/"week"/"month" as the CommandParameter.</summary>
    [RelayCommand]
    private void Snooze(string? duration) => _actions?.Snooze(this, duration ?? "day");

    /// <summary>"Show again now" on an already-snoozed row.</summary>
    [RelayCommand]
    private void Unsnooze() => _actions?.Unsnooze(this);

    /// <summary>"Try normally again", the only way out of <see cref="RowInstallState.NeedsManualWork"/>.</summary>
    [RelayCommand]
    private void ResetPersistentFailure() => _actions?.ResetPersistentFailure(this);

    /// <summary>
    /// Re-run whatever failed on this row: a failed REMOVAL must retry as a removal too, never as an
    /// install, or the row would end up reporting "Uninstalled" for a package it just reinstalled.
    /// </summary>
    [RelayCommand(CanExecute = nameof(ShowRetryButton))]
    private void Retry() => RunRetry();

    /// <summary>The visible way out of <see cref="RowInstallState.NeedsManualWork"/>: the context menu's
    /// "try normally again" alone left the row idle and needed a second click to install.</summary>
    public bool ShowResetAndRetryButton => ShowResetPersistentFailureEntry && Item.CanInstall && !IsIgnored;

    /// <summary>Clears the persistent-failure flag, then runs the same retry as a Failed row.</summary>
    [RelayCommand(CanExecute = nameof(ShowResetAndRetryButton))]
    private void ResetAndRetry()
    {
        _actions?.ResetPersistentFailure(this);
        RunRetry();
    }

    private void RunRetry()
    {
        if (WasUninstalled)
            _actions?.Uninstall(this);
        else if (LastDowngradeTarget is { Length: > 0 } target)
            _actions?.RetryDowngrade(this, target); // a plain install would upgrade: the opposite of the ask
        else
            _actions?.InstallSingle(this);
    }

    /// <summary>The version the last run on this row rolled back to, or null when it was an ordinary install;
    /// set after <see cref="BeginInstall"/> by the rollback path, so Retry repeats what actually failed.</summary>
    internal string? LastDowngradeTarget { get; set; }

    /// <summary>
    /// The executable that starts this program, resolved in the background from the uninstall registry.
    /// Ordinary winget and Chocolatey rows carry no launch URI, so without this they could not offer Start.
    /// Null until resolved, or when nothing reliable was found.
    /// </summary>
    [ObservableProperty]
    private string? launchTargetPath;

    partial void OnLaunchTargetPathChanged(string? value)
    {
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Whether the row can launch the installed program, either the source knows how (a launcher
    /// client, a Steam game) or an executable was resolved for it.</summary>
    public bool CanStart => (_actions?.CanStart(Item) ?? false) || !string.IsNullOrEmpty(LaunchTargetPath);

    /// <summary>Whether the source can remove this item (every package manager, Steam, launcher clients).</summary>
    public bool CanUninstall => _actions?.CanUninstall(Item) ?? false;

    public string StartHeader => LocalizationService.Instance["StartButton"];

    public string UninstallHeader => LocalizationService.Instance["UninstallButton"];

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => _actions?.Start(this);

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private void Uninstall() => _actions?.Uninstall(this);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private void Install() => _actions?.InstallSingle(this);

    /// <summary>Enter-key equivalent of the row's own primary button, whichever ONE is actually
    /// showing right now, in the same precedence the action column itself renders them (they are
    /// mutually exclusive by row state). A Steam child game has no action button of its own at all, so
    /// it falls through to <see cref="CanStart"/>, the same command its context menu's "Start" offers.</summary>
    public void ExecuteDefaultAction()
    {
        if (ShowInstallButton && InstallCommand.CanExecute(null))
            InstallCommand.Execute(null);
        else if (ShowOpenButton && OpenCommand.CanExecute(null))
            OpenCommand.Execute(null);
        else if (ShowRetryButton && RetryCommand.CanExecute(null))
            RetryCommand.Execute(null);
        else if (ShowUnignoreButton && IgnoreCommand.CanExecute(null))
            IgnoreCommand.Execute(null);
        else if (CanStart && StartCommand.CanExecute(null))
            StartCommand.Execute(null);
    }

    /// <summary>
    /// The theme changed. Brushes this row resolved from the resource dictionary by hand are not
    /// DynamicResource references, so nothing would re-read them and the row would keep the old theme's
    /// colour until its state changed. Re-raising is enough: no relayout, no flicker.
    /// </summary>
    public void RefreshThemeBrushes()
    {
        OnPropertyChanged(nameof(InstallStateBrush));

        foreach (var child in Children)
            child.RefreshThemeBrushes();
    }

    /// <summary>
    /// The language changed. A <c>Loc[...]</c> binding refreshes through the localization singleton, but
    /// every property below builds its text in code and nothing would re-read it, so a row kept the old
    /// language until its state happened to change, and the window sat in two languages at once.
    /// </summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(PersistentFailureHintText));
        OnPropertyChanged(nameof(InstallStateTooltip));
        OnPropertyChanged(nameof(NameTooltip));
        OnPropertyChanged(nameof(DownloadSizeText));
        OnPropertyChanged(nameof(InstallStateText));
        OnPropertyChanged(nameof(InstallFailedTooltip));
        OnPropertyChanged(nameof(FailureLine));
        OnPropertyChanged(nameof(CriticalLabel));
        OnPropertyChanged(nameof(IgnoredBadgeLabel));
        OnPropertyChanged(nameof(RebootMarkerText));
        OnPropertyChanged(nameof(VersionUnchangedText));
        OnPropertyChanged(nameof(InstallHeader));
        OnPropertyChanged(nameof(RetryHeader));
        OnPropertyChanged(nameof(RetryInteractiveHeader));
        OnPropertyChanged(nameof(InteractiveRunningText));
        OnPropertyChanged(nameof(OpenHeader));
        OnPropertyChanged(nameof(OpenTargetHeader));
        OnPropertyChanged(nameof(DetailsHeader));
        OnPropertyChanged(nameof(StartHeader));
        OnPropertyChanged(nameof(UninstallHeader));
        OnPropertyChanged(nameof(IgnoreHeader));
        OnPropertyChanged(nameof(IgnoreSimpleHeader));
        OnPropertyChanged(nameof(IgnoreAllHeader));
        OnPropertyChanged(nameof(UnignoreHeader));
        OnPropertyChanged(nameof(SnoozeHeader));
        OnPropertyChanged(nameof(SnoozeOneDayHeader));
        OnPropertyChanged(nameof(SnoozeOneWeekHeader));
        OnPropertyChanged(nameof(SnoozeOneMonthHeader));
        OnPropertyChanged(nameof(UnsnoozeHeader));
        OnPropertyChanged(nameof(SnoozedBadgeLabel));
        OnPropertyChanged(nameof(PublishDateText));
        OnPropertyChanged(nameof(ResetPersistentFailureHeader));
        OnPropertyChanged(nameof(InfoLinkHeader));
        OnPropertyChanged(nameof(OpenFolderHeader));
        OnPropertyChanged(nameof(AllSourcesTooltip));
        OnPropertyChanged(nameof(AllSourcesText));
        OnPropertyChanged(nameof(SourceDisplay));
        OnPropertyChanged(nameof(ChangelogHeader));
        OnPropertyChanged(nameof(DowngradeHeader));
        // The category group header and a Steam-style parent row's "3 games" sub-list count both build
        // their text in code like everything above, omitting either here leaves it in the old language
        // until something else (a rescan, a filter change) happens to re-raise it.
        OnPropertyChanged(nameof(CategoryDisplay));
        OnPropertyChanged(nameof(ChildCountText));

        foreach (var child in Children)
            child.RefreshLocalizedText();
    }

    public void BeginInstall()
    {
        LastDowngradeTarget = null;
        InstallState = RowInstallState.Queued;
        InstallPercent = 0;
        InstallStatusText = null;
        InstallErrorDetail = null;
        _hasRealProgress = false;
        // A retry starts from nothing: the last attempt's byte counts and throughput sample must neither
        // stay on the row nor feed the first speed reading of this attempt.
        BytesReceived = null;
        BytesTotal = null;
        BytesPerSecond = 0;
        _lastByteSampleAt = default;
        _lastByteSample = 0;
        NeedsReboot = false;
        VersionUnchanged = false;
        // An install claims the row: a failed removal that is now being installed must not still report
        // itself as "Uninstalled" when it succeeds. The uninstall path sets the flag again right after.
        WasUninstalled = false;
    }

    /// <summary>
    /// The row was queued but never got its turn; the batch was stopped first. Back to idle so it can be
    /// picked again, with the reason on it rather than a progress bar that will never move.
    /// </summary>
    public void MarkNotInstalled(string reason)
    {
        InstallState = RowInstallState.Idle;
        InstallPercent = 0;
        InstallStatusText = reason;
        _hasRealProgress = false;
    }

    /// <summary>Download size in bytes (source or enrichment), for the pre-install batch preview.</summary>
    public long? DownloadSizeBytes => EnrichedDownloadSize ?? Item.DownloadSize;

    // Sort keys for the column headers. Rows without a date/size sort as the oldest/smallest (MinValue/0):
    // last in the default descending sort, first in an ascending one. Version sorts as text, good enough next to a
    // full semver comparer, which the mixed version strings here do not warrant.
    public DateTimeOffset SortDate => EffectivePublishDate ?? DateTimeOffset.MinValue;

    public long SortSize => EffectiveDownloadSize ?? 0L;

    public string SortVersion => AvailableVersion ?? string.Empty;

    /// <summary>
    /// Opens the raw failure text on request in the error-details window. The winget source in particular hands
    /// back the underlying installer's message and code, which is the one thing that tells the user WHY
    /// an update would not apply. The row raises it; the main view model owns showing the window.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasInstallError))]
    private void ShowErrorDetail()
    {
        if (string.IsNullOrWhiteSpace(InstallErrorDetail))
            return;

        _onShowErrorDetail?.Invoke(this);
    }

    /// <summary>
    /// The real changelog text and/or release-page link, where a source actually found one, Item.Meta
    /// when the source's own scan already knows it (<c>GitHubReleaseSource</c>), otherwise
    /// whatever the later background enrichment pass found (winget's `winget show`). Never
    /// fabricated: no icon shows when neither is present.
    /// </summary>
    public string? ChangelogText => Item.Meta.GetValueOrDefault("changelogBody") ?? EnrichedChangelogBody;

    public string? ChangelogUrl => Item.Meta.GetValueOrDefault("changelogUrl") ?? EnrichedChangelogUrl;

    /// <summary>
    /// True only for npm: its registry enrichment deliberately never fills the changelog fields (see
    /// <c>NpmEnrichment</c>'s doc comment), so unlike every other source it needs the button to light up
    /// BEFORE any text or link is known, so a click can go look one up
    /// (<c>MainViewModel.TryFetchNpmChangelogAsync</c>). Winget needs no equivalent flag: its release notes
    /// arrive automatically once the row's post-scan enrichment runs (the same `winget show` call that
    /// fetches the release date also reads the release-notes fields), so <see cref="HasChangelog"/> is
    /// already true there by the time the button would render.
    /// </summary>
    public bool CanFetchChangelogOnClick => SourceId == "npm";

    public bool HasChangelog =>
        !string.IsNullOrWhiteSpace(ChangelogText) || !string.IsNullOrWhiteSpace(ChangelogUrl) || CanFetchChangelogOnClick;

    public string ChangelogHeader => LocalizationService.Instance["ChangelogButtonTooltip"];

    /// <summary>
    /// True only while a click-triggered GitHub/GitLab release fetch is in flight, the button
    /// swaps its icon for the same waiting ring other per-row async actions use instead of doing nothing.
    /// AsyncRelayCommand already refuses re-entry on its own, so this exists purely for the visual.
    /// </summary>
    [ObservableProperty]
    private bool isChangelogLoading;

    /// <summary>The button shows either the info icon or the spinner, never both.</summary>
    public bool ShowChangelogIcon => !IsChangelogLoading;

    partial void OnIsChangelogLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowChangelogIcon));

    [RelayCommand(CanExecute = nameof(HasChangelog))]
    private async Task ShowChangelog()
    {
        if (_onShowChangelog is null)
            return;

        IsChangelogLoading = true;
        try
        {
            await _onShowChangelog(this);
        }
        finally
        {
            IsChangelogLoading = false;
        }
    }

    /// <summary>Filled by the background enrichment pass when the source's own scan left Item.Meta empty.</summary>
    [ObservableProperty]
    private string? enrichedChangelogBody;

    [ObservableProperty]
    private string? enrichedChangelogUrl;

    partial void OnEnrichedChangelogBodyChanged(string? value) => NotifyChangelogChanged();

    partial void OnEnrichedChangelogUrlChanged(string? value) => NotifyChangelogChanged();

    private void NotifyChangelogChanged()
    {
        OnPropertyChanged(nameof(ChangelogText));
        OnPropertyChanged(nameof(ChangelogUrl));
        OnPropertyChanged(nameof(HasChangelog));
        ShowChangelogCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Throughput, measured rather than guessed: it only appears when the driving tool reports bytes,
    /// and it is derived from the change between two of its own reports.
    /// </summary>
    private DateTimeOffset _lastByteSampleAt;
    private long _lastByteSample;

    [ObservableProperty]
    private long? bytesReceived;

    [ObservableProperty]
    private long? bytesTotal;

    [ObservableProperty]
    private double bytesPerSecond;

    public bool HasTransfer => BytesTotal is > 0;

    public string TransferText =>
        HasTransfer ? $"{ByteSize.Format(BytesReceived ?? 0)} / {ByteSize.Format(BytesTotal!.Value)}" : string.Empty;

    public string SpeedText => BytesPerSecond > 0 ? $"{ByteSize.Format((long)BytesPerSecond)}/s" : string.Empty;

    /// <summary>
    /// True once the driving tool has reported a real figure, a non-zero percent or any byte count.
    /// Until then the row shows a slowly advancing indeterminate percentage (AdvanceIndeterminate) so a
    /// tool that prints no progress at all still looks alive instead of frozen at 0.
    /// </summary>
    private bool _hasRealProgress;

    /// <summary>Public read of <see cref="_hasRealProgress"/>, the install band draws a wandering shine
    /// instead of its normal fill while every running row is still on the indeterminate creep.</summary>
    public bool HasRealProgress => _hasRealProgress;

    public void ApplyInstallStatus(InstallItemStatus status)
    {
        // A second attempt starts from nothing: a bare 0 % line is otherwise ignored below, so the row (and
        // the batch's overall percent) kept the failed attempt's figure, e.g. 100 % after a full download
        // followed by an "installer busy" failure, with the indeterminate creep switched off.
        if (status.IsAwaitingRetry)
        {
            _hasRealProgress = false;
            InstallPercent = 0;
            BytesReceived = null;
            BytesTotal = null;
            BytesPerSecond = 0;
            _lastByteSampleAt = default;
            _lastByteSample = 0;
        }

        InstallStatusText = status.Error ?? status.Status;
        if (status.Detail is not null)
            InstallErrorDetail = status.Detail;
        // Stuck/HandedOff carry their explanation in Status, not Error/Detail (InstallQueue reports them
        // with only a Status line), without this the row's error-detail button never had anything to show.
        else if (status.IsManualHandoff || status.IsNotApplicable)
            InstallErrorDetail = status.Error ?? status.Status;
        UpdateTransfer(status);

        // A bare status line (percent 0, no bytes) must not stamp the row back to 0 and undo the
        // indeterminate creep; only a real reading takes over the bar.
        if (status.Percent > 0 || status.BytesTotal is > 0 || status.BytesReceived is > 0)
        {
            _hasRealProgress = true;
            InstallPercent = status.Percent;
        }

        InstallState = status.IsManualHandoff
            ? RowInstallState.HandedOff
            : status.IsNotApplicable
            ? RowInstallState.Stuck
            : status.IsCancelled
            ? RowInstallState.Cancelled
            : status.Success switch
            {
                true => RowInstallState.Succeeded,
                false => RowInstallState.Failed,
                null => RowInstallState.Running,
            };

        if (status.Success == true || status.IsCancelled)
            InstallPercent = 100;

        // sticky: a later bare status must not clear a reboot the install already reported
        if (status.RebootRequired)
            NeedsReboot = true;

        // sticky, same reason: the "result" message carries this, a later bare progress line must not clear it
        if (status.VersionUnchanged)
            VersionUnchanged = true;
    }

    /// <summary>The item installed but a reboot is pending (winget 3010/1641). Not a failure, a note.</summary>
    [ObservableProperty]
    private bool needsReboot;

    public string RebootMarkerText => LocalizationService.Instance["RebootRequiredBadge"];

    /// <summary>
    /// The source's own post-install re-check found the same version as before the run. Not a
    /// failure, sticky like <see cref="NeedsReboot"/>: a later bare status line must not clear it.
    /// </summary>
    [ObservableProperty]
    private bool versionUnchanged;

    public string VersionUnchangedText => LocalizationService.Instance["InstallVersionUnchanged"];

    /// <summary>
    /// Fallback progress for a tool that reports nothing measurable: while the row is running and no
    /// real reading has arrived, the percentage creeps toward, but never reaches, 90, slowing as it
    /// climbs, so "download → install" reads as movement. A real reading (or completion) takes over
    /// the moment it arrives. Driven by the install timer.
    /// </summary>
    public void AdvanceIndeterminate()
    {
        if (InstallState != RowInstallState.Running || _hasRealProgress)
            return;

        var step = InstallPercent < 55 ? 2.5 : InstallPercent < 80 ? 1.0 : 0.25;
        InstallPercent = Math.Min(90, InstallPercent + step);
    }

    private void UpdateTransfer(InstallItemStatus status)
    {
        if (status.BytesTotal is null && status.BytesReceived is null)
            return;

        var now = DateTimeOffset.UtcNow;
        var received = status.BytesReceived ?? 0;

        if (_lastByteSampleAt != default && received > _lastByteSample)
        {
            var seconds = (now - _lastByteSampleAt).TotalSeconds;
            if (seconds > 0.2)
            {
                BytesPerSecond = (received - _lastByteSample) / seconds;
                _lastByteSampleAt = now;
                _lastByteSample = received;
            }
        }
        else
        {
            _lastByteSampleAt = now;
            _lastByteSample = received;
        }

        BytesReceived = status.BytesReceived;
        BytesTotal = status.BytesTotal;
    }

    partial void OnIsIgnoredChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(ShowInstallButton));
        OnPropertyChanged(nameof(ShowOpenButton));
        OnPropertyChanged(nameof(ShowUnignoreButton));
        OnPropertyChanged(nameof(ShowRetryButton));
        OnPropertyChanged(nameof(ShowDowngradeButton));
        OnPropertyChanged(nameof(ShowStateLabel));
        OnPropertyChanged(nameof(ShowStateWord));
        OnPropertyChanged(nameof(IgnoreHeader));
        OnPropertyChanged(nameof(IgnoreSimpleHeader));
        OnPropertyChanged(nameof(ShowSnoozeMenu));
        InstallCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowResetAndRetryButton));
        OnPropertyChanged(nameof(ShowStateWord));
        ResetAndRetryCommand.NotifyCanExecuteChanged();
        DowngradeCommand.NotifyCanExecuteChanged();
        if (value)
            IsSelected = false;
    }

    partial void OnIsIgnoredAllVersionsChanged(bool value)
    {
        OnPropertyChanged(nameof(IgnoredBadgeLabel));
        OnPropertyChanged(nameof(ShowIgnoreAllEntry));
    }

    partial void OnIsSnoozedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(ShowInstallButton));
        OnPropertyChanged(nameof(ShowSnoozeMenu));
        OnPropertyChanged(nameof(ShowUnsnoozeEntry));
        InstallCommand.NotifyCanExecuteChanged();
        if (value)
            IsSelected = false;
    }

    partial void OnSnoozedUntilChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(SnoozedBadgeLabel));

    partial void OnWasUninstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(InstallStateText));
        OnPropertyChanged(nameof(InstallStateBrush));
    }

    partial void OnIconChanged(BitmapSource? value) => OnPropertyChanged(nameof(UsesTile));

    partial void OnBytesReceivedChanged(long? value) => OnPropertyChanged(nameof(TransferText));

    partial void OnBytesTotalChanged(long? value)
    {
        OnPropertyChanged(nameof(TransferText));
        OnPropertyChanged(nameof(HasTransfer));
    }

    partial void OnBytesPerSecondChanged(double value) => OnPropertyChanged(nameof(SpeedText));

    partial void OnInstallPercentChanged(double value) => OnPropertyChanged(nameof(InstallPercentText));

    partial void OnInstallStateChanged(RowInstallState value)
    {
        OnPropertyChanged(nameof(InstallStateText));
        OnPropertyChanged(nameof(InstallStateTooltip));
        OnPropertyChanged(nameof(InstallStateBrush));
        OnPropertyChanged(nameof(IsInstallRunning));
        OnPropertyChanged(nameof(ShowStateLabel));
        OnPropertyChanged(nameof(ShowStateWord));
        OnPropertyChanged(nameof(IsInstallFailed));
        OnPropertyChanged(nameof(IsInstallQueued));
        OnPropertyChanged(nameof(InstallFailedTooltip));
        OnPropertyChanged(nameof(FailureLine));
        OnPropertyChanged(nameof(ShowSourceText));
        OnPropertyChanged(nameof(CancelledReasonLine));
        OnPropertyChanged(nameof(ShowCancelledReason));
        OnPropertyChanged(nameof(ShowOpenButton));
        OnPropertyChanged(nameof(ShowInstallButton));
        OnPropertyChanged(nameof(ShowUnignoreButton));
        OnPropertyChanged(nameof(ShowRetryButton));
        OnPropertyChanged(nameof(ShowDowngradeButton));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(CanInstall));
        InstallCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        DowngradeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasInstallError));
        ShowErrorDetailCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowRetryInteractiveButton));
        OnPropertyChanged(nameof(ShowInteractiveRunningLabel));
        OnPropertyChanged(nameof(ShowRunningProgress));
        RetryInteractiveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowResetPersistentFailureEntry));
        OnPropertyChanged(nameof(ShowResetAndRetryButton));
        OnPropertyChanged(nameof(ShowStateWord));
        ResetAndRetryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowPersistentFailureHint));
        OnPropertyChanged(nameof(ShowStopSingleButton));
        StopSingleCommand.NotifyCanExecuteChanged();

        // Untick what is already done (or handed off - it is not going to run again on its own either),
        // or the selection count keeps claiming it is still pending.
        if (value is RowInstallState.Succeeded or RowInstallState.HandedOff or RowInstallState.Stuck
            or RowInstallState.NeedsManualWork or RowInstallState.Cancelled)
        {
            IsSelected = false;
        }
    }

    partial void OnInstallStatusTextChanged(string? value)
    {
        OnPropertyChanged(nameof(InstallFailedTooltip));
        OnPropertyChanged(nameof(InstallStateTooltip));
        OnPropertyChanged(nameof(CancelledReasonLine));
        OnPropertyChanged(nameof(ShowCancelledReason));
        OnPropertyChanged(nameof(ShowSourceText));
    }

    partial void OnInstallErrorDetailChanged(string? value)
    {
        OnPropertyChanged(nameof(HasInstallError));
        ShowErrorDetailCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(InstallFailedTooltip));
        OnPropertyChanged(nameof(FailureLine));
        OnPropertyChanged(nameof(ShowRetryInteractiveButton));
        RetryInteractiveCommand.NotifyCanExecuteChanged();
    }

    private static Brush Brush(string resourceKey) =>
        System.Windows.Application.Current?.TryFindResource(resourceKey) as Brush ?? Brushes.Gray;
}
