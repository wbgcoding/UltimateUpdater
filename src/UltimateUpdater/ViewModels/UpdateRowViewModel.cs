using System.Windows.Media.Imaging;
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

    private string? WindowsUpdatePublisher =>
        Item.Meta.TryGetValue("driverManufacturer", out var manufacturer)
            ? manufacturer
            : Item.Meta.ContainsKey("isDriverUpdate") ? null : "Microsoft";

    /// <summary>The id as a person should read it: pip's composite "package#python.exe path" form collapses to the package name.</summary>
    private string DisplayId => Item.SourceId == "pip"
        ? Services.Sources.PackageManagers.PipSource.PackageName(Item)
        : Item.Id;

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

    /// <summary>Rows shown as a collapsible sub-list on this row (Steam's game updates under the launcher row), each with its own context menu.</summary>
    public IReadOnlyList<UpdateRowViewModel> Children { get; }

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
    /// The version a rollback would install: the FromVersion of this package's last successful install
    /// history entry. Null when the source cannot pin a version
    /// (<see cref="Services.Sources.IUpdateSource.SupportsVersionPinning"/>), no such install is on record, or
    /// it did not know its previous version. Set by <see cref="MainViewModel"/> after every scan.
    /// </summary>
    [ObservableProperty]
    private string? downgradeTargetVersion;

    partial void OnDowngradeTargetVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowDowngradeButton));
        OnPropertyChanged(nameof(DowngradeHeader));
        DowngradeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Confirmed by the caller (<see cref="IRowActions.Downgrade"/>) before anything runs.</summary>
    [RelayCommand(CanExecute = nameof(ShowDowngradeButton))]
    private void Downgrade() => _actions?.Downgrade(this);

    [RelayCommand(CanExecute = nameof(ShowRetryInteractiveButton))]
    private void RetryInteractive() => _actions?.RetryInteractive(this);

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

    partial void OnIsRunningInteractivelyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInteractiveRunningLabel));
        OnPropertyChanged(nameof(ShowRunningProgress));
    }

    public bool IsInstallRunning => InstallState == RowInstallState.Running;

    [RelayCommand(CanExecute = nameof(ShowStopSingleButton))]
    private void StopSingle() => _actions?.CancelSingle(this);

    /// <summary>The language-independent identifier behind <see cref="CategoryDisplay"/>, used to remember collapsed state.</summary>
    public string CategoryKey => IsGitHubManual ? "GitHubManual" : IsManualOnly ? "ManualUpdate" : Item.Category.ToString();

    private bool OpensAWebsite =>
        Item.OpenTarget is { Length: > 0 } target
        && Uri.TryCreate(target, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // A status that already is the explanatory sentence is not repeated under itself.
    private string WithRawStatus(string detail) =>
        string.IsNullOrWhiteSpace(InstallStatusText) || InstallStatusText == detail ? detail : $"{detail}\n{InstallStatusText}";

    public bool IsInstallFailed => InstallState == RowInstallState.Failed;

    /// <summary>Queued but not started: the row shows a turning ring beside the word.</summary>
    public bool IsInstallQueued => InstallState == RowInstallState.Queued;

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

    /// <summary>Opens the raw failure text in the error-details window on request. The row raises it; the main view model owns showing the window.</summary>
    [RelayCommand(CanExecute = nameof(HasInstallError))]
    private void ShowErrorDetail()
    {
        if (string.IsNullOrWhiteSpace(InstallErrorDetail))
            return;

        _onShowErrorDetail?.Invoke(this);
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
}