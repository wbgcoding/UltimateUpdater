using System.Globalization;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using UltimateUpdater.Models;

namespace UltimateUpdater.ViewModels.Main;

public partial class UpdateRowViewModel
{
    /// <summary>The source as a person writes it: "Windows Update", not "windowsupdate".</summary>
    public string SourceDisplay => SourceNames.Display(Item.SourceId);

    /// <summary>The same fact as <see cref="Publisher"/>, shortened for the narrow table column (<see cref="PublisherShortener"/>).</summary>
    public string? PublisherDisplay => Publisher is { } publisher ? PublisherShortener.Shorten(publisher) : null;

    public bool HasPublisher => Publisher is not null;

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

    /// <summary>The source line under the name is dropped for manual-open rows, where it would repeat the name.</summary>
    public bool ShowSourceLine => !IsManualOnly;

    /// <summary>The "Ignored" pill next to the name: whether one version or the package is ignored.</summary>
    public string IgnoredBadgeLabel =>
        LocalizationService.Instance[IsIgnoredAllVersions ? "IgnoredAllBadge" : "IgnoredBadge"];

    public string UnignoreHeader => LocalizationService.Instance["UnignoreButton"];

    public bool HasChildren => Children.Count > 0;

    public string ChildCountText => string.Format(
        LocalizationService.Instance[Children.Count == 1 ? "SubListCountOne" : "SubListCount"], Children.Count);

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

    public bool HasInfoLink => !string.IsNullOrEmpty(EnrichedProjectUrl);

    public string InfoLinkHeader => LocalizationService.Instance["InfoLinkButton"];

    public string OpenFolderHeader => LocalizationService.Instance["OpenFolderButton"];

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

    /// <summary>The row menu's "back to the previous version" entry: offered only while the row is idle.</summary>
    public bool ShowDowngradeButton =>
        DowngradeTargetVersion is { Length: > 0 } && !IsIgnored && InstallState == RowInstallState.Idle;

    public string DowngradeHeader =>
        string.Format(LocalizationService.Instance["RowDowngrade"], DowngradeTargetVersion);

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
        && InstallErrorExplainer.Explain(SourceId, InstallErrorDetail)?.AllowsInteractiveRetry == true;

    public string RetryInteractiveHeader => LocalizationService.Instance["RetryInteractiveHeader"];

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
        && InstallErrorExplainer.Explain(SourceId, InstallErrorDetail)?.OffersReinstall == true;

    public string ReinstallHeader => LocalizationService.Instance["ReinstallButton"];

    public string ReinstallTooltip => LocalizationService.Instance["ReinstallTooltip"];

    public bool ShowInteractiveRunningLabel => IsInstallRunning && IsRunningInteractively;

    public bool ShowRunningProgress => IsInstallRunning && !IsRunningInteractively;

    public string InteractiveRunningText => LocalizationService.Instance["RowRunningInteractive"];

    /// <summary>This row's own stop button, shown only while it runs; the rest of the queue keeps going.</summary>
    public bool ShowStopSingleButton => IsInstallRunning;

    public string StopSingleTooltip => LocalizationService.Instance["StopSingleTooltip"];

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

    /// <summary>
    /// A failed row shows a warning icon instead of the word, and so does a row with the visible retry
    /// (<see cref="ShowResetAndRetryButton"/>), where word, details button and retry would not fit together.
    /// </summary>
    public bool ShowStateWord => ShowStateLabel && !IsInstallFailed && !ShowResetAndRetryButton;

    /// <summary>Hover text for the failure icon: the plain-language line when a code was recognized in <see cref="InstallErrorDetail"/>, else the raw status line or the bare "failed" word.</summary>
    public string InstallFailedTooltip =>
        PendingFreshInstall ? LocalizationService.Instance["ReinstallInstallFailed"] :
        InstallErrorExplainer.Describe(SourceId, InstallErrorDetail, key => LocalizationService.Instance[key])
        ?? InstallStatusText
        ?? LocalizationService.Instance["RowFailed"];

    /// <summary>
    /// The one-line reason a failed row shows under its name: only the translated explainer text (or the bare
    /// "failed" word), never the raw tool output, which stays behind the Details click.
    /// </summary>
    public string FailureLine =>
        PendingFreshInstall ? LocalizationService.Instance["ReinstallInstallFailed"] :
        InstallErrorExplainer.Describe(SourceId, InstallErrorDetail, key => LocalizationService.Instance[key])
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

    /// <summary>The visible way out of <see cref="RowInstallState.NeedsManualWork"/>: resets the flag and retries in one click.</summary>
    public bool ShowResetAndRetryButton => ShowResetPersistentFailureEntry && Item.CanInstall && !IsIgnored && !IsSnoozed;

    public string StartHeader => LocalizationService.Instance["StartButton"];

    public string UninstallHeader => LocalizationService.Instance["UninstallButton"];

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

    /// <summary>The changelog text and/or release link, from Item.Meta when the scan knew it, else from the enrichment pass. Never fabricated.</summary>
    public string? ChangelogText => Item.Meta.GetValueOrDefault("changelogBody") ?? EnrichedChangelogBody;

    public bool HasChangelog =>
        !string.IsNullOrWhiteSpace(ChangelogText) || !string.IsNullOrWhiteSpace(ChangelogUrl) || CanFetchChangelogOnClick;

    public string ChangelogHeader => LocalizationService.Instance["ChangelogButtonTooltip"];

    /// <summary>The button shows either the info icon or the spinner, never both.</summary>
    public bool ShowChangelogIcon => !IsChangelogLoading;

    public bool HasTransfer => BytesTotal is > 0;

    public string TransferText =>
        HasTransfer ? $"{ByteSize.Format(BytesReceived ?? 0)} / {ByteSize.Format(BytesTotal!.Value)}" : string.Empty;

    public string SpeedText => BytesPerSecond > 0 ? $"{ByteSize.Format((long)BytesPerSecond)}/s" : string.Empty;

    public string RebootMarkerText => LocalizationService.Instance["RebootRequiredBadge"];

    private string MsixDeferredText => string.Format(LocalizationService.Instance["InstallMsixDeferred"], Name);

    public string VersionUnchangedText => IsPackagedApp
        ? MsixDeferredText
        : LocalizationService.Instance["InstallVersionUnchanged"];

    /// <summary>Why the version can stay the same and what to do; from the second identical result on, how often it already happened.</summary>
    public string VersionUnchangedTooltip => IsPackagedApp ? MsixDeferredText : VersionUnchangedCount >= 2
        ? LocalizationService.Instance["InstallVersionUnchangedDetail"] + "\n"
            + string.Format(LocalizationService.Instance["InstallVersionUnchangedRepeat"], VersionUnchangedCount)
        : LocalizationService.Instance["InstallVersionUnchangedDetail"];

    private static Brush Brush(string resourceKey) =>
        System.Windows.Application.Current?.TryFindResource(resourceKey) as Brush ?? Brushes.Gray;
}
