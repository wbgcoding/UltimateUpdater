using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
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

    /// <summary>Space-key equivalent of clicking the row's checkbox; a no-op where the checkbox is not shown.</summary>
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

    /// <summary>How many times in a row the same classified failure has happened (mirrors <c>PersistentFailureRecord.Count</c>), kept for the hint text.</summary>
    [ObservableProperty]
    private int persistentFailureAttempts;

    partial void OnPersistentFailureAttemptsChanged(int value) => OnPropertyChanged(nameof(PersistentFailureHintText));

    /// <summary>The tally behind <see cref="PersistentFailureAttempts"/> counts installs that finished with
    /// the version unchanged, not failures: the hint and the state tooltip then say so.</summary>
    [ObservableProperty]
    private bool persistentTallyIsUnchanged;

    partial void OnPersistentTallyIsUnchangedChanged(bool value)
    {
        OnPropertyChanged(nameof(PersistentFailureHintText));
        OnPropertyChanged(nameof(InstallStateTooltip));
    }

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

    /// <summary>
    /// The vendor part of a winget-shaped id ("Mozilla" from "Mozilla.Firefox"), read only for winget rows.
    /// Windows Update rows report "Microsoft", except a driver update with a captured manufacturer. A pip
    /// row's publisher comes from the background enrichment pass (<see cref="EnrichedPublisher"/>).
    /// </summary>
    public string? Publisher => Item.SourceId switch
    {
        "winget" => Services.Sources.PublisherFromId.Derive(Item.Id),
        "windowsupdate" => WindowsUpdatePublisher,
        "pip" => EnrichedPublisher,
        _ => null,
    };

    /// <summary>The same fact as <see cref="Publisher"/>, shortened for the narrow table column (<see cref="Services.PublisherShortener"/>).</summary>
    public string? PublisherDisplay => Publisher is { } publisher ? Services.PublisherShortener.Shorten(publisher) : null;

    private string? WindowsUpdatePublisher =>
        Item.Meta.TryGetValue("driverManufacturer", out var manufacturer)
            ? manufacturer
            : Item.Meta.ContainsKey("isDriverUpdate") ? null : "Microsoft";

    public bool HasPublisher => Publisher is not null;

    /// <summary>The id as a person should read it: pip's composite "package#python.exe path" form collapses to the package name.</summary>
    private string DisplayId => Item.SourceId == "pip"
        ? Services.Sources.PackageManagers.PipSource.PackageName(Item)
        : Item.Id;

    /// <summary>
    /// The row's hover text, one fact per line where known: full name, package id, source and publisher;
    /// a pip row adds its interpreter, since two interpreters can offer the same package name.
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

    /// <summary>Whether this row's install goes through the elevated worker (one shared UAC prompt for the admin batch).</summary>
    public bool NeedsElevation => InstallQueue.NeedsElevation(Item.SourceId);

    /// <summary>
    /// The ids of the other sources offering this exact same update, when the table has folded them into
    /// this row. The row's own source is the one that will actually run, and it is named first.
    /// </summary>
    private IReadOnlyList<string> _alternateSources = [];

    /// <summary>
    /// The rows this one stands in for. They are real, separate installations (for example uv's tool venv and
    /// pip's site-packages), so installing this row installs all of them, best first.
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

    /// <summary>Called by the table when it groups the same update. Silent when nothing changed.</summary>
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

    /// <summary>Order of the groups in the table: applications and store first, then system, developer tooling, games last.</summary>
    public int CategoryOrder
    {
        get
        {
            // A GitHub-release row gets its own group above the generic one: it carries a real version comparison and size.
            if (IsGitHubManual)
                return 98;

            // Every other "open it yourself" row (store, launcher, Steam, vendor driver page) goes into one "Manual update" category at the very bottom.
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
    /// Rows this program cannot install: a store or launcher that only hands the job to its own client.
    /// Public so the row's context menu can be suppressed for these.
    /// </summary>
    public bool IsManualOnly => !Item.CanInstall && !string.IsNullOrEmpty(Item.OpenTarget);

    /// <summary>
    /// True for a row <see cref="GitHubReleaseSource"/> found: an installed program whose Add/Remove Programs
    /// entry names a GitHub repository with a newer release. It has a real version and often a size, so it
    /// gets its own group.
    /// </summary>
    public bool IsGitHubManual => IsManualOnly && Item.SourceId == "github-release";

    /// <summary>
    /// Date, version and size are blanked on a manual-only row, except Steam (real build ids and size), a
    /// GitHub-release row (a real version comparison) and any row with children. Internal because it is also
    /// the membership test for <see cref="BelongsInCompactBlock"/>.
    /// </summary>
    internal bool HideManualNumbers => IsManualOnly && Item.SourceId is not ("steam" or "github-release") && !HasChildren;

    /// <summary>
    /// True for the rows MainViewModel.ManualOnlyView collects into the compact block below the table.
    /// Steam and rows with children are excluded: they carry real values or a nested list.
    /// </summary>
    internal bool BelongsInCompactBlock => HideManualNumbers;

    /// <summary>The source line under the name is dropped for manual-open rows, where it would repeat the name.</summary>
    public bool ShowSourceLine => !IsManualOnly;

    /// <summary>
    /// The user asked not to be told about this update. It stays on screen but marked, is not counted and
    /// cannot be ticked or installed. Set from the persisted exclude list.
    /// </summary>
    [ObservableProperty]
    private bool isIgnored;

    /// <summary>True when the whole package is ignored rather than just the version on screen (different label and menu entries).</summary>
    [ObservableProperty]
    private bool isIgnoredAllVersions;

    /// <summary>
    /// "Not now": the row leaves the default view until <see cref="SnoozedUntil"/>. Mutually exclusive with
    /// <see cref="IsIgnored"/>; set from the persisted <c>Snoozed</c> dictionary.
    /// </summary>
    [ObservableProperty]
    private bool isSnoozed;

    [ObservableProperty]
    private DateTimeOffset? snoozedUntil;

    /// <summary>
    /// The operation this row runs (or last ran) is a removal: its result reads "Uninstalled" and a Retry
    /// repeats the removal. Cleared by <see cref="BeginInstall"/>.
    /// </summary>
    [ObservableProperty]
    private bool wasUninstalled;

    public bool IsCritical => Item.IsCritical;

    /// <summary>Already done in this session: the row keeps its result on screen but cannot be installed again.</summary>
    public bool IsInstalled => InstallState == RowInstallState.Succeeded;

    /// <summary>Installable right now. A queued or running row is not, so it drops out of the select-all totals.</summary>
    public bool CanInstall =>
        Item.CanInstall && !IsIgnored && !IsSnoozed
        && InstallState is RowInstallState.Idle or RowInstallState.Failed or RowInstallState.Cancelled;

    public bool CanOpen => !string.IsNullOrEmpty(Item.OpenTarget);

    /// <summary>The "Ignored" pill next to the name: whether one version or the package is ignored.</summary>
    public string IgnoredBadgeLabel =>
        LocalizationService.Instance[IsIgnoredAllVersions ? "IgnoredAllBadge" : "IgnoredBadge"];

    public string UnignoreHeader => LocalizationService.Instance["UnignoreButton"];

    /// <summary>Rows shown as a collapsible sub-list on this row (Steam's game updates under the launcher row), each with its own context menu.</summary>
    public IReadOnlyList<UpdateRowViewModel> Children { get; }

    public bool HasChildren => Children.Count > 0;

    public string ChildCountText => string.Format(
        LocalizationService.Instance[Children.Count == 1 ? "SubListCountOne" : "SubListCount"], Children.Count);

    /// <summary>The expanded state lives in the main view model: the row object is rebuilt on every scan result, so state kept here would reopen what the user collapsed.</summary>
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
    /// Filled in after the scan by the background enrichment pass for sources whose listing carries no date
    /// or size. Preferred over the snapshot values on the immutable <see cref="Item"/>.
    /// </summary>
    [ObservableProperty]
    private DateTimeOffset? enrichedPublishDate;

    [ObservableProperty]
    private long? enrichedDownloadSize;

    private DateTimeOffset? EffectivePublishDate => EnrichedPublishDate ?? Item.PublishDate;

    private long? EffectiveDownloadSize => EnrichedDownloadSize ?? Item.DownloadSize;

    /// <summary>Download size, where the source or the enrichment pass knew it. Blank on a manual-only row besides Steam.</summary>
    public string? DownloadSizeText =>
        !HideManualNumbers && EffectiveDownloadSize is > 0 ? ByteSize.Format(EffectiveDownloadSize.Value) : null;

    /// <summary>Publish date, local, date only, in the UI language's short-date order. Blank on a manual-only row besides Steam.</summary>
    public string? PublishDateText =>
        !HideManualNumbers && EffectivePublishDate is { } date
            ? date.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture)
            : null;

    public bool HasDownloadSize => DownloadSizeText is not null;

    public bool HasPublishDate => PublishDateText is not null;

    /// <summary>Merges a background enrichment result onto the row. Only fills what is unknown, never overwrites a reported figure.</summary>
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
        // Item.Meta wins when a source filled it at scan time; this only fills the gap for sources whose changelog arrives with the enrichment pass.
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

    /// <summary>A pip row's publisher, found by the background enrichment pass; see <see cref="Publisher"/>.</summary>
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
    /// The install directory, resolved in the background after the scan. Null until then or when none is
    /// found, in which case the row offers no "open folder" action.
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

    /// <summary>Opens the install directory in Explorer. UriLauncher refuses anything that is not a real rooted path.</summary>
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
        OnPropertyChanged(nameof(SortDate));
    }

    partial void OnEnrichedDownloadSizeChanged(long? value)
    {
        OnPropertyChanged(nameof(DownloadSizeText));
        OnPropertyChanged(nameof(HasDownloadSize));
        OnPropertyChanged(nameof(SortSize));
        // DownloadSizeBytes announces no change of its own; selection sums need a signal when the size arrives from enrichment.
        OnPropertyChanged(nameof(DownloadSizeBytes));
    }

    /// <summary>Version columns stay hidden for rows where no real version is known (Tier C).</summary>
    public bool HasVersions => !string.IsNullOrEmpty(AvailableVersion);

    /// <summary>Windows Update cannot say what is installed, so those rows show the new version alone.</summary>
    public bool HasInstalledVersion => !string.IsNullOrEmpty(Item.InstalledVersion);

    /// <summary>One tooltip for the whole version lane (installed to available), so the gap and arrow are hit-testable too.</summary>
    public string? VersionChangeTooltip => HasInstalledVersion
        ? $"{InstalledVersion} → {AvailableVersion}"
        : AvailableVersion;

    /// <summary>
    /// Open owns the action cell only on a row with nothing to install: every button in that cell shares one
    /// grid cell, so two visible at once would overlap. An installable row reaches its link via the context menu.
    /// </summary>
    public bool ShowOpenButton => IsManualOnly && !IsIgnored && InstallState == RowInstallState.Idle;

    /// <summary>
    /// The inline "show again" on an ignored row. It shares the action cell, so it must not overlap a state
    /// label or an Open button; <see cref="ShowStateLabel"/> excludes ignored rows for that reason.
    /// </summary>
    public bool ShowUnignoreButton => IsIgnored;

    /// <summary>A per-row Install button on an installable row that has not been queued yet.</summary>
    public bool ShowInstallButton => Item.CanInstall && !IsIgnored && !IsSnoozed && InstallState == RowInstallState.Idle;

    public string InstallHeader => LocalizationService.Instance["InstallButton"];

    /// <summary>
    /// The version a rollback would install: the FromVersion of this package's last successful install
    /// history entry. Null when the source cannot pin a version
    /// (<see cref="Services.Sources.IUpdateSource.SupportsVersionPinning"/>), no such install is on record, or
    /// it did not know its previous version. Set by <see cref="MainViewModel"/> after every scan.
    /// </summary>
    [ObservableProperty]
    private string? downgradeTargetVersion;

    /// <summary>The row menu's "back to the previous version" entry: offered only while the row is idle.</summary>
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

    /// <summary>A failed or user-stopped row offers a retry, which re-queues it through the same path as a fresh install.</summary>
    public bool ShowRetryButton =>
        Item.CanInstall && !IsIgnored && !IsSnoozed
        && InstallState is RowInstallState.Failed or RowInstallState.Cancelled;

    public string RetryHeader => LocalizationService.Instance["RetryButton"];

    /// <summary>The interactive-retry menu entry, offered only for a Recoverable-classified failure,
    /// outside an unattended run.</summary>
    public bool ShowRetryInteractiveButton =>
        InstallState == RowInstallState.Failed
        && !App.IsUnattended
        // Only WingetSource implements the interactive retry; without this a coincidental code match in another source's output would offer a dead button.
        && SourceId == "winget"
        && Services.Sources.InstallErrorExplainer.Explain(SourceId, InstallErrorDetail)?.AllowsInteractiveRetry == true;

    public string RetryInteractiveHeader => LocalizationService.Instance["RetryInteractiveHeader"];

    [RelayCommand(CanExecute = nameof(ShowRetryInteractiveButton))]
    private void RetryInteractive() => _actions?.RetryInteractive(this);

    /// <summary>
    /// The "reinstall" entry: a winget update refused because the new version installs differently from the
    /// old one (0x8A15008E), where removing the old version and installing the new one is the fix. Not while
    /// only the fresh install is left (that is <see cref="Retry"/>'s job), and not in an unattended run.
    /// </summary>
    public bool ShowReinstallButton =>
        InstallState is RowInstallState.Failed or RowInstallState.NeedsManualWork
        && !App.IsUnattended
        && !PendingFreshInstall
        && SourceId == "winget"
        && Services.Sources.InstallErrorExplainer.Explain(SourceId, InstallErrorDetail)?.OffersReinstall == true;

    public string ReinstallHeader => LocalizationService.Instance["ReinstallButton"];

    public string ReinstallTooltip => LocalizationService.Instance["ReinstallTooltip"];

    [RelayCommand(CanExecute = nameof(ShowReinstallButton))]
    private void Reinstall() => _actions?.Reinstall(this);

    /// <summary>Set once the old version of a reinstall is gone and only the fresh install is left, so a retry repeats just that install.</summary>
    [ObservableProperty]
    private bool pendingFreshInstall;

    /// <summary>Set when the removal step of a reinstall failed, so Retry reruns the whole flow rather than a plain removal. Cleared by every new run.</summary>
    internal bool ReinstallRemovalFailed { get; set; }

    /// <summary>The scope ("user" or "machine") the program had before its reinstall removed it; null = not known.</summary>
    internal string? PendingFreshInstallScope { get; set; }

    partial void OnPendingFreshInstallChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowReinstallButton));
        OnPropertyChanged(nameof(FailureLine));
        OnPropertyChanged(nameof(InstallFailedTooltip));
        ReinstallCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Set while the interactive retry runs this row: winget's own UI drives, so the percentage is meaningless and the row shows a label instead.</summary>
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

    /// <summary>This row's own stop button, shown only while it runs; the rest of the queue keeps going.</summary>
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

    /// <summary>A bundled brand mark for rows whose own icon cannot be found (an npm or pip package has no application on disk). Never preferred over a real icon.</summary>
    public Geometry? BrandGeometry => _brandMark?.Geometry;

    private readonly BrandMark? _brandMark;

    public bool UsesBrandTile => _brandMark is not null;

    /// <summary>The mark in its brand colour (shaded where the plain colour would be too faint), or the theme's text colour where the brand has no hue. Resolved by hand, so <see cref="RefreshThemeBrushes"/> re-raises it on a theme switch.</summary>
    public Brush BrandBrush => BrandPaint().Mark;

    /// <summary>The tile behind the icon: a faint wash of the brand colour, of the accent for a letter or category glyph, or plain neutral.</summary>
    public Brush TileBackground => BrandPaint().Tile;

    /// <summary>Opacity of the colour wash on a tile: enough to read as tinted, little enough to stay a quiet backdrop.</summary>
    private const byte TileWashAlpha = 0x2A;

    private (Brush Mark, Brush Tile) BrandPaint()
    {
        var neutralTile = Brush("ControlFillColorSecondaryBrush");
        var text = Brush("TextFillColorPrimaryBrush");

        // High contrast is the user's own colour scheme: no wash and no brand colour may compete with it.
        if (System.Windows.SystemParameters.HighContrast)
            return (text, neutralTile);

        if (_brandMark is not { } mark)
            return (text, Wash(ThemeColor("AccentFillColorDefaultBrush")) ?? neutralTile);

        var surface = SurfaceColor();
        var tileColor = surface is { } below
            ? BrandMark.Composite(Color.FromArgb(TileWashAlpha, mark.Color.R, mark.Color.G, mark.Color.B), below)
            : (Color?)null;

        // Without a theme to measure against (no Application) the brand colour is simply used.
        var drawn = tileColor is { } tile ? BrandMark.LegibleOn(mark.Color, tile) : mark.Color;
        if (drawn is not { } drawColor)
            return (text, neutralTile);

        var brush = new SolidColorBrush(drawColor);
        brush.Freeze();
        return (brush, Wash(mark.Color)!);
    }

    private static Brush? Wash(Color? color)
    {
        if (color is not { } value)
            return null;

        var brush = new SolidColorBrush(Color.FromArgb(TileWashAlpha, value.R, value.G, value.B));
        brush.Freeze();
        return brush;
    }

    private static Color? ThemeColor(string resourceKey) =>
        (System.Windows.Application.Current?.TryFindResource(resourceKey) as SolidColorBrush)?.Color;

    /// <summary>The colour the tile sits on in the current theme: the card fill laid over the page where the card is translucent.</summary>
    private static Color? SurfaceColor()
    {
        if (ThemeColor("CardBackgroundFillColorDefaultBrush") is not { } card)
            return null;

        var page = ThemeColor("ApplicationBackgroundBrush") is { A: 255 } opaquePage ? opaquePage : Colors.White;
        return BrandMark.Composite(card, page);
    }

    /// <summary>The coloured tile is a stand-in and must not sit behind a real icon: round or transparent-cornered icons would show the square tile as a border.</summary>
    public bool UsesTile => Icon is null;

    /// <summary>A letter identifies a named application but means nothing for the rest (a Windows update would show "2"); those rows show what kind of thing they are instead.</summary>
    public bool UsesLetterTile => !UsesBrandTile && Item.Category is UpdateCategory.Software or UpdateCategory.Games;

    public bool UsesGlyphTile => !UsesBrandTile && !UsesLetterTile;

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

    /// <summary>The language-independent identifier behind <see cref="CategoryDisplay"/>, used to remember collapsed state.</summary>
    public string CategoryKey => IsGitHubManual ? "GitHubManual" : IsManualOnly ? "ManualUpdate" : Item.Category.ToString();

    public string CriticalLabel => LocalizationService.Instance["CriticalBadge"];

    /// <summary>Toggles wording: ignore this version, or show an already-ignored one again.</summary>
    public string IgnoreHeader =>
        LocalizationService.Instance[IsIgnored ? "UnignoreButton" : "IgnoreButton"];

    public string IgnoreAllHeader => LocalizationService.Instance["IgnoreAllButton"];

    /// <summary>The plain "ignore" wording used for manual rows, which have no per-version distinction.</summary>
    public string IgnoreSimpleHeader =>
        LocalizationService.Instance[IsIgnored ? "UnignoreButton" : "IgnoreSimpleButton"];

    /// <summary>
    /// Which ignore entries the menu offers: nothing ignored gives "ignore this version" and "ignore all
    /// versions"; this version ignored gives "show again" and "ignore all versions"; the package ignored
    /// gives "show again" alone.
    /// </summary>
    public bool ShowVersionIgnore => !IsManualOnly;

    public bool ShowIgnoreVersionEntry => !IsManualOnly;

    public bool ShowIgnoreAllEntry => !IsManualOnly && !IsIgnoredAllVersions;

    public string SnoozeHeader => LocalizationService.Instance["SnoozeButton"];

    public string SnoozeOneDayHeader => LocalizationService.Instance["SnoozeOneDay"];

    public string SnoozeOneWeekHeader => LocalizationService.Instance["SnoozeOneWeek"];

    public string SnoozeOneMonthHeader => LocalizationService.Instance["SnoozeOneMonth"];

    public string UnsnoozeHeader => LocalizationService.Instance["UnsnoozeButton"];

    /// <summary>The "Remind me later" submenu is offered only on a row that is neither ignored nor already snoozed.</summary>
    public bool ShowSnoozeMenu => !IsIgnored && !IsSnoozed;

    public bool ShowUnsnoozeEntry => IsSnoozed;

    /// <summary>The pill next to the name on a snoozed row, mirroring <see cref="IgnoredBadgeLabel"/>.</summary>
    public string SnoozedBadgeLabel => SnoozedUntil is { } until
        ? string.Format(
            LocalizationService.Instance["SnoozedBadge"],
            until.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture))
        : string.Empty;

    public string ResetPersistentFailureHeader => LocalizationService.Instance["TryNormallyAgainButton"];

    /// <summary>Offered once the row is flagged: the normal Install/Retry buttons stop until this explicit reset is used.</summary>
    public bool ShowResetPersistentFailureEntry => InstallState == RowInstallState.NeedsManualWork;

    public string OpenHeader => LocalizationService.Instance["OpenButton"];

    /// <summary>
    /// The row menu's wording for Open. "OpenButton" is shared with the tray menu and the folder action, so
    /// this one says "open website" where the target is a web page, and stays the plain word for a launcher
    /// or an application.
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
        RowInstallState.Succeeded when WasUninstalled => LocalizationService.Instance["RowUninstalled"],
        RowInstallState.Succeeded => LocalizationService.Instance["RowDone"],
        RowInstallState.Failed => LocalizationService.Instance["RowFailed"],
        RowInstallState.HandedOff => LocalizationService.Instance["RowHandedOff"],
        RowInstallState.Stuck => LocalizationService.Instance["RowStuck"],
        RowInstallState.NeedsManualWork => LocalizationService.Instance["RowNeedsManualWork"],
        RowInstallState.Cancelled => LocalizationService.Instance["RowCancelled"],
        // Running shows a progress bar with the percentage beside it (InstallPercentText), never inside the bar.
        _ => string.Empty,
    };

    /// <summary>
    /// Hover text for <see cref="InstallStateText"/>: for Stuck, HandedOff and NeedsManualWork the explanatory
    /// sentence plus the source's raw status line where there is one; every other state shows the raw status line.
    /// </summary>
    public string? InstallStateTooltip => InstallState switch
    {
        RowInstallState.Stuck => WithRawStatus(LocalizationService.Instance["RowStuckDetail"]),
        RowInstallState.HandedOff => WithRawStatus(LocalizationService.Instance["RowHandedOffDetail"]),
        RowInstallState.NeedsManualWork when PersistentTallyIsUnchanged => VersionUnchangedTooltip,
        RowInstallState.NeedsManualWork => WithRawStatus(LocalizationService.Instance["RowNeedsManualWorkDetail"]),
        RowInstallState.Cancelled => CancelledReasonLine ?? LocalizationService.Instance["RowCancelledTooltip"],
        _ => InstallStatusText,
    };

    // A status that already is the explanatory sentence is not repeated under itself.
    private string WithRawStatus(string detail) =>
        string.IsNullOrWhiteSpace(InstallStatusText) || InstallStatusText == detail ? detail : $"{detail}\n{InstallStatusText}";

    /// <summary>The one-line nudge shown once the three-strikes tally flags this row; wired to <see cref="IgnoreCommand"/>.</summary>
    public bool ShowPersistentFailureHint => InstallState == RowInstallState.NeedsManualWork;

    public string PersistentFailureHintText =>
        string.Format(
            LocalizationService.Instance[PersistentTallyIsUnchanged ? "PersistentUnchangedHint" : "PersistentFailureHint"],
            PersistentFailureAttempts);

    /// <summary>The percentage shown next to a running row's bar.</summary>
    public string InstallPercentText => PercentFormat.Format(InstallPercent);

    /// <summary>The state word shows only when no running bar takes its place, and never on an ignored row (that row shows "show again", <see cref="ShowUnignoreButton"/>).</summary>
    public bool ShowStateLabel =>
        !IsIgnored &&
        InstallState is RowInstallState.Queued or RowInstallState.Succeeded or RowInstallState.Failed
            or RowInstallState.HandedOff or RowInstallState.Stuck or RowInstallState.NeedsManualWork
            or RowInstallState.Cancelled;

    public bool IsInstallFailed => InstallState == RowInstallState.Failed;

    /// <summary>Queued but not started: the row shows a turning ring beside the word.</summary>
    public bool IsInstallQueued => InstallState == RowInstallState.Queued;

    /// <summary>
    /// A failed row shows a warning icon instead of the word, and so does a row with the visible retry
    /// (<see cref="ShowResetAndRetryButton"/>), where word, details button and retry would not fit together.
    /// </summary>
    public bool ShowStateWord => ShowStateLabel && !IsInstallFailed && !ShowResetAndRetryButton;

    /// <summary>Hover text for the failure icon: the plain-language line when a code was recognized in <see cref="InstallErrorDetail"/>, else the raw status line or the bare "failed" word.</summary>
    public string InstallFailedTooltip =>
        PendingFreshInstall ? LocalizationService.Instance["ReinstallInstallFailed"] :
        Services.Sources.InstallErrorExplainer.Describe(SourceId, InstallErrorDetail, key => LocalizationService.Instance[key])
        ?? InstallStatusText
        ?? LocalizationService.Instance["RowFailed"];

    /// <summary>
    /// The one-line reason a failed row shows under its name: only the translated explainer text (or the bare
    /// "failed" word), never the raw tool output, which stays behind the Details click.
    /// </summary>
    public string FailureLine =>
        PendingFreshInstall ? LocalizationService.Instance["ReinstallInstallFailed"] :
        Services.Sources.InstallErrorExplainer.Describe(SourceId, InstallErrorDetail, key => LocalizationService.Instance[key])
        ?? LocalizationService.Instance["RowFailed"];

    /// <summary>The reason a cancelled row carries (a blocked row the user skipped), shown under the name and as the state tooltip. Null for a row the user stopped.</summary>
    public string? CancelledReasonLine =>
        InstallState == RowInstallState.Cancelled && !string.IsNullOrWhiteSpace(InstallStatusText)
            ? InstallStatusText
            : null;

    public bool ShowCancelledReason => CancelledReasonLine is not null;

    /// <summary>The source line under the name; a failed row shows <see cref="FailureLine"/> in its place and an
    /// indeterminate run shows <see cref="RunningStatusText"/>.</summary>
    public bool ShowSourceText => ShowSourceLine && !IsInstallFailed && !ShowCancelledReason && !ShowIndeterminateProgress;

    public Brush InstallStateBrush => InstallState switch
    {
        RowInstallState.Succeeded when WasUninstalled => Brush("SystemFillColorCriticalBrush"),
        RowInstallState.Succeeded => Brush("SystemFillColorSuccessBrush"),
        RowInstallState.Failed => Brush("SystemFillColorCriticalBrush"),
        // Neither green nor red: genuinely unknown.
        RowInstallState.HandedOff or RowInstallState.Stuck => Brush("SystemFillColorCautionBrush"),
        // Not red: the same repeated failure was already explained, and red is for "something just went wrong".
        RowInstallState.NeedsManualWork => Brush("SystemFillColorCautionBrush"),
        // Caution coloured like HandedOff and Stuck: a user stop is neither a success nor a failure.
        RowInstallState.Cancelled => Brush("SystemFillColorCautionBrush"),
        _ => Brush("TextFillColorTertiaryBrush"),
    };

    /// <summary>What a screen reader announces for the row.</summary>
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
        _brandMark = BrandIcons.Resolve(item.Name, item.SourceId, item.Id);

        // Children are rows in their own right with the same actions, presented inside their parent. The ignore, Details and changelog callbacks are handed down too, or a child's buttons would do nothing.
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

    /// <summary>Re-runs whatever failed on this row; a failed removal retries as a removal, never as an install.</summary>
    [RelayCommand(CanExecute = nameof(ShowRetryButton))]
    private void Retry() => RunRetry();

    /// <summary>The visible way out of <see cref="RowInstallState.NeedsManualWork"/>: resets the flag and retries in one click.</summary>
    public bool ShowResetAndRetryButton => ShowResetPersistentFailureEntry && Item.CanInstall && !IsIgnored && !IsSnoozed;

    /// <summary>Clears the persistent-failure flag, then runs the same retry as a Failed row.</summary>
    [RelayCommand(CanExecute = nameof(ShowResetAndRetryButton))]
    private void ResetAndRetry()
    {
        _actions?.ResetPersistentFailure(this);
        RunRetry();
    }

    private void RunRetry()
    {
        if (ReinstallRemovalFailed)
            _actions?.Reinstall(this); // the reinstall's removal failed: redo the whole reinstall, not a bare removal
        else if (PendingFreshInstall)
            _actions?.RetryFreshInstall(this); // the old version is already gone: only the install is left
        else if (WasUninstalled)
            _actions?.Uninstall(this);
        else if (LastDowngradeTarget is { Length: > 0 } target)
            _actions?.RetryDowngrade(this, target); // a plain install would upgrade: the opposite of the ask
        else
            _actions?.InstallSingle(this);
    }

    /// <summary>The version the last run rolled back to, or null for an ordinary install; set by the rollback path so Retry repeats what failed.</summary>
    internal string? LastDowngradeTarget { get; set; }

    /// <summary>The executable that starts this program, resolved in the background from the uninstall registry. Null until resolved or when none is found.</summary>
    [ObservableProperty]
    private string? launchTargetPath;

    partial void OnLaunchTargetPathChanged(string? value)
    {
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Whether the row can launch the installed program: the source knows how, or an executable was resolved.</summary>
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

    /// <summary>Enter-key equivalent of the row's primary button: whichever one is showing, in the action column's own precedence. A Steam child falls through to <see cref="CanStart"/>.</summary>
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

    /// <summary>The theme changed. Brushes resolved by hand are not DynamicResource references, so re-raise them; no relayout needed.</summary>
    public void RefreshThemeBrushes()
    {
        OnPropertyChanged(nameof(InstallStateBrush));
        OnPropertyChanged(nameof(BrandBrush));
        OnPropertyChanged(nameof(TileBackground));

        foreach (var child in Children)
            child.RefreshThemeBrushes();
    }

    /// <summary>The language changed. Properties that build their text in code are re-raised; <c>Loc[...]</c> bindings refresh on their own.</summary>
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
        OnPropertyChanged(nameof(VersionUnchangedTooltip));
        OnPropertyChanged(nameof(InstallHeader));
        OnPropertyChanged(nameof(RetryHeader));
        OnPropertyChanged(nameof(RetryInteractiveHeader));
        OnPropertyChanged(nameof(ReinstallHeader));
        OnPropertyChanged(nameof(ReinstallTooltip));
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
        // The category header and a parent row's sub-list count build their text in code too; re-raise them.
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
        ResetPhase();
        // A retry starts from nothing: stale byte counts and throughput must not feed the next speed reading.
        BytesReceived = null;
        BytesTotal = null;
        BytesPerSecond = 0;
        _lastByteSampleAt = default;
        _lastByteSample = 0;
        NeedsReboot = false;
        VersionUnchanged = false;
        VersionUnchangedCount = 0;
        // An install claims the row: a failed removal now being installed must not report "Uninstalled" on success.
        WasUninstalled = false;
        ReinstallRemovalFailed = false;
    }

    /// <summary>The row was queued but never got its turn (the batch was stopped): back to idle with the reason on it.</summary>
    public void MarkNotInstalled(string reason)
    {
        InstallState = RowInstallState.Idle;
        InstallPercent = 0;
        InstallStatusText = reason;
        _hasRealProgress = false;
        ResetPhase();
    }

    /// <summary>Download size in bytes (source or enrichment), for the pre-install batch preview.</summary>
    public long? DownloadSizeBytes => EnrichedDownloadSize ?? Item.DownloadSize;

    // Sort keys for the column headers. The table sorts by HasPublishDate/HasDownloadSize/HasVersions first, so rows without a value stay at the bottom.
    public DateTimeOffset SortDate => EffectivePublishDate ?? DateTimeOffset.MinValue;

    public long SortSize => EffectiveDownloadSize ?? 0L;

    public string SortVersion => VersionSortKey.For(AvailableVersion);

    /// <summary>Opens the raw failure text in the error-details window on request. The row raises it; the main view model owns showing the window.</summary>
    [RelayCommand(CanExecute = nameof(HasInstallError))]
    private void ShowErrorDetail()
    {
        if (string.IsNullOrWhiteSpace(InstallErrorDetail))
            return;

        _onShowErrorDetail?.Invoke(this);
    }

    /// <summary>The changelog text and/or release link, from Item.Meta when the scan knew it, else from the enrichment pass. Never fabricated.</summary>
    public string? ChangelogText => Item.Meta.GetValueOrDefault("changelogBody") ?? EnrichedChangelogBody;

    public string? ChangelogUrl => Item.Meta.GetValueOrDefault("changelogUrl") ?? EnrichedChangelogUrl;

    /// <summary>
    /// True only for npm: its enrichment never fills the changelog fields, so the button must light up before
    /// any text is known and a click goes to look one up (<c>MainViewModel.TryFetchNpmChangelogAsync</c>).
    /// </summary>
    public bool CanFetchChangelogOnClick => SourceId == "npm";

    public bool HasChangelog =>
        !string.IsNullOrWhiteSpace(ChangelogText) || !string.IsNullOrWhiteSpace(ChangelogUrl) || CanFetchChangelogOnClick;

    public string ChangelogHeader => LocalizationService.Instance["ChangelogButtonTooltip"];

    /// <summary>True while a click-triggered release fetch is in flight; the button shows a waiting ring instead.</summary>
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

    /// <summary>Throughput, derived from the change between two byte reports; it only appears when the tool reports bytes.</summary>
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

    /// <summary>True once the driving tool reported a real figure (a non-zero percent or any byte count); until then the bar is indeterminate, never an invented percentage.</summary>
    private bool _hasRealProgress;

    /// <summary>Public read of <see cref="_hasRealProgress"/>: the install band draws a wandering shine while no running row has a real figure.</summary>
    public bool HasRealProgress => _hasRealProgress;

    public void ApplyInstallStatus(InstallItemStatus status)
    {
        // A new attempt starts from nothing: a bare 0 % line would otherwise be ignored and keep the failed attempt's figure.
        if (status.IsAwaitingRetry)
        {
            _hasRealProgress = false;
            InstallPercent = 0;
            BytesReceived = null;
            BytesTotal = null;
            BytesPerSecond = 0;
            _lastByteSampleAt = default;
            _lastByteSample = 0;
            ResetPhase();
            _runStartedAt = Clock();
        }

        status = TrackPhase(status);

        InstallStatusText = status.Error ?? status.Status;
        if (status.Detail is not null)
            InstallErrorDetail = status.Detail;
        // Stuck and HandedOff carry their explanation in Status only; without this the error-detail button has nothing to show.
        else if (status.IsManualHandoff || status.IsNotApplicable)
            InstallErrorDetail = status.Error ?? status.Status;
        UpdateTransfer(status);

        // A bare status line (percent 0, no bytes) must not reset the row to 0; only a real reading takes over the bar.
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
        {
            VersionUnchanged = true;
            VersionUnchangedCount = status.VersionUnchangedCount;
        }

        NotifyPhaseChanged();
    }

    /// <summary>The item installed but a reboot is pending (winget 3010/1641). Not a failure, a note.</summary>
    [ObservableProperty]
    private bool needsReboot;

    public string RebootMarkerText => LocalizationService.Instance["RebootRequiredBadge"];

    /// <summary>The source's post-install re-check found the same version as before the run. Not a failure; sticky like <see cref="NeedsReboot"/>.</summary>
    [ObservableProperty]
    private bool versionUnchanged;

    /// <summary>How many times this item ended unchanged for the same versions, this run included (<see cref="InstallItemStatus.VersionUnchangedCount"/>); 0 while not unchanged.</summary>
    [ObservableProperty]
    private int versionUnchangedCount;

    partial void OnVersionUnchangedCountChanged(int value)
    {
        OnPropertyChanged(nameof(VersionUnchangedTooltip));
        OnPropertyChanged(nameof(InstallStateTooltip));
    }

    /// <summary>The row is a Store (MSIX) app: winget registers the update and Windows applies it once the app is closed, so "version unchanged" is a wait there.</summary>
    [ObservableProperty]
    private bool isPackagedApp;

    partial void OnIsPackagedAppChanged(bool value)
    {
        OnPropertyChanged(nameof(VersionUnchangedText));
        OnPropertyChanged(nameof(VersionUnchangedTooltip));
        OnPropertyChanged(nameof(InstallStateTooltip));
    }

    private string MsixDeferredText => string.Format(LocalizationService.Instance["InstallMsixDeferred"], Name);

    public string VersionUnchangedText => IsPackagedApp
        ? MsixDeferredText
        : LocalizationService.Instance["InstallVersionUnchanged"];

    /// <summary>Why the version can stay the same and what to do; from the second identical result on, how often it already happened.</summary>
    public string VersionUnchangedTooltip => IsPackagedApp ? MsixDeferredText : VersionUnchangedCount >= 2
        ? LocalizationService.Instance["InstallVersionUnchangedDetail"] + "\n"
            + string.Format(LocalizationService.Instance["InstallVersionUnchangedRepeat"], VersionUnchangedCount)
        : LocalizationService.Instance["InstallVersionUnchangedDetail"];

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
        OnPropertyChanged(nameof(ShowRetryButton));
        OnPropertyChanged(nameof(ShowResetAndRetryButton));
        OnPropertyChanged(nameof(ShowStateWord));
        InstallCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        ResetAndRetryCommand.NotifyCanExecuteChanged();
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

    partial void OnInstallPercentChanged(double value)
    {
        OnPropertyChanged(nameof(InstallPercentText));
        OnPropertyChanged(nameof(RunningProgressText));
    }

    partial void OnInstallStateChanged(RowInstallState value)
    {
        OnRunningChanged(value == RowInstallState.Running);
        NotifyPhaseChanged();
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
        OnPropertyChanged(nameof(ShowReinstallButton));
        ReinstallCommand.NotifyCanExecuteChanged();
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

        // Untick what is done or handed off, or the selection count keeps claiming it is pending.
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
        OnPropertyChanged(nameof(ShowReinstallButton));
        ReinstallCommand.NotifyCanExecuteChanged();
    }

    private static Brush Brush(string resourceKey) =>
        System.Windows.Application.Current?.TryFindResource(resourceKey) as Brush ?? Brushes.Gray;
}
