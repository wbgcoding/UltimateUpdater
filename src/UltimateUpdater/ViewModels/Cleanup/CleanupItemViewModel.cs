using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels.Cleanup;

/// <summary>One row in the cleanup dialog: a tick box, what it frees, and how the scan is going.</summary>
public sealed partial class CleanupItemViewModel : ObservableObject
{
    private readonly Action _onSelectionChanged;

    /// <summary>Ticked: what every total, the red confirmation text and the run itself act on. Every card is shown, so what is ticked is exactly what runs.</summary>
    [ObservableProperty]
    private bool isSelected;

    /// <summary>What the scan found. Unticking a detail entry never lowers it; only <see cref="SelectedEntryBytes"/> follows the ticks.</summary>
    [ObservableProperty]
    private long bytes;

    [ObservableProperty]
    private int files;

    private long _unselectedEntryBytes;
    private int _unselectedEntryFiles;

    /// <summary>What a run would clean from this target: <see cref="Bytes"/> without the unticked detail entries. The leftover and old-Python rows start with every candidate unticked, so there it is the sum of the ticked ones.</summary>
    public long SelectedEntryBytes => IsLeftoverInstalls
        ? LeftoverCandidates.Where(c => c.IsSelected).Sum(c => c.SizeBytes)
        : IsOldPythonVersions
            ? OldPythonVersionCandidates.Where(c => c.IsSelected).Sum(c => c.SizeBytes)
            : Math.Max(0, Bytes - _unselectedEntryBytes);

    public int SelectedEntryFiles => IsLeftoverInstalls
        ? LeftoverCandidates.Count(c => c.IsSelected)
        : IsOldPythonVersions
            ? OldPythonVersionCandidates.Count(c => c.IsSelected)
            : IsDriverCleanup
                ? DriverDeviceCandidates.Count(c => c.IsSelected) + DriverPackageCandidates.Count(c => c.IsSelected)
                : Math.Max(0, Files - _unselectedEntryFiles);

    /// <summary>A candidate tick changes what the run would free, so the selection-following figures are refreshed as for an entry tick.</summary>
    private void OnCandidateSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LeftoverCandidateViewModel.IsSelected))
            return;

        RaiseSelectedEntryFigures();
        _onSelectionChanged();
    }

    private void RaiseSelectedEntryFigures()
    {
        OnPropertyChanged(nameof(SelectedEntryBytes));
        OnPropertyChanged(nameof(SelectedEntryFiles));
        OnPropertyChanged(nameof(ChosenSizeText));
    }

    /// <summary>Still being measured: the row shows a quiet "scanning" hint rather than "0 B".</summary>
    [ObservableProperty]
    private bool isScanning = true;

    /// <summary>Already cleaned in this run, so the row can show a tick.</summary>
    [ObservableProperty]
    private bool isDone;

    /// <summary>This target is the one being cleaned right now: its row shows a live bar.</summary>
    [ObservableProperty]
    private bool isCleaning;

    /// <summary>Picked for this run but not started yet; the row shows a turning ring so the list reads as a queue.</summary>
    [ObservableProperty]
    private bool isWaiting;

    /// <summary>Where this target stands in the run, as one quiet word beside its size. Its own state because two of the words (a run the user stopped, a target the run never reached) cannot be read off <see cref="IsWaiting"/>, <see cref="IsCleaning"/> and <see cref="IsDone"/>.</summary>
    [ObservableProperty]
    private CleanupTargetState runState;

    /// <summary>Whether the detail list is open. The first of three gates on per-entry marking: a closed list has nothing to show, so nothing is matched for it.</summary>
    [ObservableProperty]
    private bool isDetailsExpanded;

    /// <summary>Never true for <see cref="CleanupTargetState.Done"/>: its text is empty and an empty capsule would look like a stray box beside the tick.</summary>
    public bool ShowsRunState => RunState != CleanupTargetState.Idle && !string.IsNullOrEmpty(RunStateText);

    /// <summary>Colour is spent on two of the five words. Never red: red is reserved for Windows Update security severity.</summary>
    public bool RunStateIsSuccess => RunState == CleanupTargetState.Done;

    public bool RunStateIsCaution => RunState == CleanupTargetState.Stopped;

    /// <summary>A finished row already shows the green tick, so <see cref="CleanupTargetState.Done"/> returns empty before the localization lookup.</summary>
    public string RunStateText => RunState == CleanupTargetState.Done
        ? string.Empty
        : LocalizationService.Instance[RunState switch
        {
            CleanupTargetState.Waiting => "CleanupStateWaiting",
            CleanupTargetState.Running => "CleanupStateCleaning",
            CleanupTargetState.Stopped => "CleanupStateStopped",
            CleanupTargetState.Skipped => "CleanupStateSkipped",
            // Idle falls here too, but is unreachable while shown: ShowsRunState hides this text for Idle.
            _ => "CleanupStateWaiting",
        }];

    partial void OnRunStateChanged(CleanupTargetState value)
    {
        OnPropertyChanged(nameof(ShowsRunState));
        OnPropertyChanged(nameof(RunStateIsSuccess));
        OnPropertyChanged(nameof(RunStateIsCaution));
        OnPropertyChanged(nameof(RunStateText));
        OnPropertyChanged(nameof(ShowsRunNote));
    }

    /// <summary>What this target is doing right now, in as much detail as it can give: the file being deleted, the repair step, the phase a Windows tool reports.</summary>
    [ObservableProperty]
    private string detailText = string.Empty;

    public bool HasDetail => DetailText.Length > 0;

    partial void OnDetailTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(ShowsRunNote));
    }

    partial void OnIsCleaningChanged(bool value) => OnPropertyChanged(nameof(ShowsRunNote));

    /// <summary>The reason a finished target freed nothing (admin rights missing, time limit), kept in <see cref="DetailText"/> after the run.</summary>
    public bool ShowsRunNote => !IsCleaning && RunState == CleanupTargetState.Skipped && HasDetail;

    /// <summary>How far this target has got, 0-100.</summary>
    [ObservableProperty]
    private double percent;

    /// <summary>How much this target has actually freed so far.</summary>
    [ObservableProperty]
    private long freedBytes;

    /// <summary>What stayed behind, in use or refused: shown so the row's figures add up.</summary>
    [ObservableProperty]
    private long skippedBytes;

    /// <summary>The refused part of <see cref="SkippedBytes"/> (no access rather than in use).</summary>
    [ObservableProperty]
    private long protectedBytes;

    /// <summary>How a system file or image check ended; null for every other row.</summary>
    public RepairResult? RepairResult { get; set; }

    /// <summary>Why this row was not done in the last run; null when it was done or no reason is known. Recorded in the history.</summary>
    internal CleanupSkipReason? SkipReason { get; set; }

    /// <summary>"12,4 MB / 310 MB": what has gone of what was measured.</summary>
    public string ProgressText =>
        SelectedEntryBytes > 0 ? $"{ByteSize.Format(FreedBytes)} / {ByteSize.Format(SelectedEntryBytes)}" : ByteSize.Format(FreedBytes);

    /// <summary>"3,2 MB in use, 1,1 MB protected": why this row freed less than it promised.</summary>
    public string SkippedText =>
        FormatSkippedShares(LocalizationService.Instance["CleanupInUse"], SkippedBytes, ProtectedBytes);

    /// <summary>Splits what stayed into its in-use and refused shares, each named only when present. Shared with the history window, which words the in-use share with its own key.</summary>
    internal static string FormatSkippedShares(string inUseFormat, long skippedBytes, long protectedBytes)
    {
        var inUse = Math.Max(0, skippedBytes - protectedBytes);
        var parts = new List<string>(2);
        if (inUse > 0)
            parts.Add(string.Format(inUseFormat, ByteSize.Format(inUse)));
        if (protectedBytes > 0)
            parts.Add(string.Format(LocalizationService.Instance["CleanupProtected"], ByteSize.Format(protectedBytes)));
        return string.Join(", ", parts);
    }

    public bool HasSkipped => SkippedBytes > 0;

    partial void OnSkippedBytesChanged(long value)
    {
        OnPropertyChanged(nameof(SkippedText));
        OnPropertyChanged(nameof(HasSkipped));
    }

    partial void OnProtectedBytesChanged(long value) => OnPropertyChanged(nameof(SkippedText));

    public string PercentText => PercentFormat.Format(Percent);

    partial void OnPercentChanged(double value) => OnPropertyChanged(nameof(PercentText));

    partial void OnFreedBytesChanged(long value) => OnPropertyChanged(nameof(ProgressText));

    private readonly Action<string>? _onPermanentlyExcludePath;

    /// <summary>Only passed for the recycle bin row: persists the new value and re-measures that row. Set on the backing field, so wiring the initial value during construction does not save or rescan.</summary>
    private readonly Action<int>? _onRecycleBinMinAgeDaysChanged;

    /// <summary>The window's "rescan just this row" command, run by the card's right-click menu with this row as parameter. Null on a row built without a window, whose entry stays disabled.</summary>
    public System.Windows.Input.ICommand? RescanCommand { get; internal set; }

    public string RescanHeader => LocalizationService.Instance["CleanupRescanMenu"];

    public CleanupItemViewModel(
        CleanupTarget target, Action onSelectionChanged, Action<string>? onPermanentlyExcludePath = null,
        int initialRecycleBinMinAgeDays = 0, Action<int>? onRecycleBinMinAgeDaysChanged = null)
    {
        Target = target;
        isSelected = target.DefaultEnabled;
        showsElevationHint = target.NeedsElevation;
        _onSelectionChanged = onSelectionChanged;
        _onPermanentlyExcludePath = onPermanentlyExcludePath;
        recycleBinMinAgeDays = initialRecycleBinMinAgeDays;
        _lastPositiveRecycleBinMinAgeDays = initialRecycleBinMinAgeDays > 0 ? initialRecycleBinMinAgeDays : 30;
        _onRecycleBinMinAgeDaysChanged = onRecycleBinMinAgeDaysChanged;
        EntriesView = (ListCollectionView)CollectionViewSource.GetDefaultView(Entries);
    }

    /// <summary>Pass-through so <see cref="CleanupEntryViewModel"/> never needs to know about settings.</summary>
    private void OnEntryPermanentlyExcluded(CleanupEntryViewModel entry) => _onPermanentlyExcludePath?.Invoke(entry.Model.Path);

    public CleanupTarget Target { get; }

    /// <summary>A key lookup for ordinary targets; formatted with the target's own arguments for the per-drive defragmentation rows (drive letter, volume label), so several rows of the same kind stay readable.</summary>
    public string Title => Target.TitleArguments.Count == 0
        ? LocalizationService.Instance[Target.TitleKey]
        : string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            LocalizationService.Instance[Target.TitleKey],
            Target.TitleArguments.Cast<object>().ToArray());

    public override string ToString() => Title;

    /// <summary>A key lookup for ordinary targets. A drive row resolves each of <see cref="CleanupTarget.DescriptionWordKeys"/>, appends its byte size as the last argument and formats <see cref="CleanupTarget.DescriptionKey"/> with those.</summary>
    public string Description => Target.DescriptionWordKeys.Count == 0
        ? LocalizationService.Instance[Target.DescriptionKey]
        : string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            LocalizationService.Instance[Target.DescriptionKey],
            Target.DescriptionWordKeys
                .Select(key => (object)LocalizationService.Instance[key])
                .Append((object)ByteSize.Format(Target.DescriptionBytes ?? 0))
                .ToArray());

    /// <summary>What happens afterwards: shown on the risk dot's tooltip rather than as an always-visible line, so a resting list stays quiet.</summary>
    public string ConsequenceText => LocalizationService.Instance[Target.ConsequenceKey];

    public string RiskLevelText => LocalizationService.Instance[RiskLevelKey(Target.RiskLevel)];

    public string RiskTooltip => $"{RiskLevelText}: {ConsequenceText}";

    /// <summary>The worst level among the ticked detail rows, Safe when none is. Kept as a value because the dot bindings read it on every selection change.</summary>
    private CleanupRiskLevel _selectedEntryRisk = CleanupRiskLevel.Safe;

    /// <summary>The worst level this row puts into a run: its own plus its ticked detail rows', only while the row itself is ticked. Feeds the category header's dot.</summary>
    public CleanupRiskLevel SelectedRiskLevel =>
        IsSelected ? (CleanupRiskLevel)Math.Max((int)Target.RiskLevel, (int)_selectedEntryRisk) : CleanupRiskLevel.Safe;

    /// <summary>The worst level among the ticked detail rows, shown on the expander's header; live only while the row is ticked.</summary>
    public CleanupRiskLevel SelectedEntryRiskLevel => IsSelected ? _selectedEntryRisk : CleanupRiskLevel.Safe;

    /// <summary>Shown only above Safe: a green dot would claim a safety the ticks do not prove.</summary>
    public bool ShowsSelectedEntryRisk => SelectedEntryRiskLevel > CleanupRiskLevel.Safe;

    public string SelectedEntryRiskHint => SelectedRiskHint(SelectedEntryRiskLevel);

    /// <summary>"At least one selected entry has level X", the dot's tooltip and automation name.</summary>
    internal static string SelectedRiskHint(CleanupRiskLevel level) => string.Format(
        LocalizationService.Instance["CleanupSelectedRiskHint"],
        LocalizationService.Instance[RiskLevelKey(level)]);

    private void RefreshSelectedEntryRisk()
    {
        var worst = CleanupRiskLevel.Safe;
        foreach (var entry in Entries)
        {
            if (!entry.IsSelected || entry.RiskLevel <= worst)
                continue;

            worst = entry.RiskLevel;
            if (worst == CleanupRiskLevel.Risky)
                break;
        }

        _selectedEntryRisk = worst;
        RaiseSelectedRisk();
    }

    private void RaiseSelectedRisk()
    {
        OnPropertyChanged(nameof(SelectedEntryRiskLevel));
        OnPropertyChanged(nameof(ShowsSelectedEntryRisk));
        OnPropertyChanged(nameof(SelectedEntryRiskHint));
        OnPropertyChanged(nameof(SelectedRiskLevel));
    }

    /// <summary>Shared with <see cref="CleanupEntryViewModel"/>'s risk dot so a level reads as the same word everywhere.</summary>
    internal static string RiskLevelKey(CleanupRiskLevel level) => level switch
    {
        CleanupRiskLevel.Safe => "CleanupRiskSafe",
        CleanupRiskLevel.Considered => "CleanupRiskConsidered",
        _ => "CleanupRiskRisky",
    };

    /// <summary>Registry rows have no meaningful byte size; they show how many broken entries were found.</summary>
    public bool IsRegistryCleanup => Target.Kind == CleanupKind.RegistryCleanup;

    /// <summary>The one row that carries the age-filter sub-row.</summary>
    public bool IsRecycleBin => Target.Kind == CleanupKind.RecycleBin;

    /// <summary>The one card that carries the component store analysis (figures below the description, measuring dots in the size cell).</summary>
    public bool IsComponentCleanup => Target.Id == "component-cleanup";

    /// <summary>The window's view model, set only on the component cleanup card; the analysis state lives there, not on the row.</summary>
    public CleanupViewModel? ComponentStoreHost
    {
        get => _componentStoreHost;
        set
        {
            if (ReferenceEquals(_componentStoreHost, value))
                return;

            if (_componentStoreHost is not null)
                _componentStoreHost.PropertyChanged -= OnComponentStoreHostPropertyChanged;

            _componentStoreHost = value;
            if (value is not null)
                value.PropertyChanged += OnComponentStoreHostPropertyChanged;

            RaiseAnalyzingChanged();
        }
    }

    private CleanupViewModel? _componentStoreHost;

    /// <summary>The analysis is running: the size cell shows the measuring dots. Always false on other rows.</summary>
    public bool IsAnalyzingComponentStore => IsComponentCleanup && ComponentStoreHost?.IsAnalyzingComponentStore == true;

    private void OnComponentStoreHostPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupViewModel.IsAnalyzingComponentStore))
            RaiseAnalyzingChanged();
    }

    private void RaiseAnalyzingChanged()
    {
        OnPropertyChanged(nameof(IsAnalyzingComponentStore));
        OnPropertyChanged(nameof(ShowsSize));
        OnPropertyChanged(nameof(ShowsNothingFound));
        OnPropertyChanged(nameof(HasSizeCell));
    }

    public string RecycleAgeLabelText => LocalizationService.Instance["CleanupRecycleAgeLabel"];

    public string RecycleAgeDaysText => LocalizationService.Instance["CleanupRecycleAgeDays"];

    /// <summary>Only items deleted at least this many days ago are listed and later removed; 0 means every age. Persisted through <see cref="_onRecycleBinMinAgeDaysChanged"/>.</summary>
    [ObservableProperty]
    private int recycleBinMinAgeDays;

    /// <summary>The last positive value, so unchecking and re-checking the sub-row restores the number that was there instead of the default.</summary>
    private int _lastPositiveRecycleBinMinAgeDays = 30;

    /// <summary>The checkbox half of the sub-row: on exactly when <see cref="RecycleBinMinAgeDays"/> is above zero.</summary>
    public bool RecycleBinAgeFilterEnabled
    {
        get => RecycleBinMinAgeDays > 0;
        set
        {
            if (value == RecycleBinAgeFilterEnabled)
                return;

            if (value)
            {
                RecycleBinMinAgeDays = _lastPositiveRecycleBinMinAgeDays;
            }
            else
            {
                if (RecycleBinMinAgeDays > 0)
                    _lastPositiveRecycleBinMinAgeDays = RecycleBinMinAgeDays;

                RecycleBinMinAgeDays = 0;
            }
        }
    }

    partial void OnRecycleBinMinAgeDaysChanged(int value)
    {
        OnPropertyChanged(nameof(RecycleBinAgeFilterEnabled));
        _onRecycleBinMinAgeDaysChanged?.Invoke(value);
    }

    /// <summary>The row whose findings the UI shows individually rather than as one count; see <see cref="LeftoverCandidateViewModel"/>.</summary>
    public bool IsLeftoverInstalls => Target.Kind == CleanupKind.LeftoverInstalls;

    /// <summary>Never pre-populated: every entry starts unticked and exactly the ticked ones are freed.</summary>
    public System.Collections.ObjectModel.ObservableCollection<LeftoverCandidateViewModel> LeftoverCandidates { get; } = [];

    public List<string> SelectedLeftoverPaths => LeftoverCandidates.Where(c => c.IsSelected).Select(c => c.Path).ToList();

    public bool ShowsNoLeftoversFound => IsLeftoverInstalls && !IsScanning && !IsWaiting && LeftoverCandidates.Count == 0;

    public string LeftoverWarningText => LocalizationService.Instance["CleanupLeftoverWarning"];

    /// <summary>A row whose findings are shown as two selectable groups; see <see cref="DriverDeviceCandidateViewModel"/> and <see cref="DriverPackageCandidateViewModel"/>.</summary>
    public bool IsDriverCleanup => Target.Kind == CleanupKind.DriverCleanup;

    /// <summary>Group A: devices no longer connected. Never pre-populated with a selection, same rule as
    /// <see cref="LeftoverCandidates"/>.</summary>
    public System.Collections.ObjectModel.ObservableCollection<DriverDeviceCandidateViewModel> DriverDeviceCandidates { get; } = [];

    /// <summary>Group B: old driver packages. Never pre-populated with a selection.</summary>
    public System.Collections.ObjectModel.ObservableCollection<DriverPackageCandidateViewModel> DriverPackageCandidates { get; } = [];

    public List<string> SelectedDriverDeviceInstanceIds =>
        DriverDeviceCandidates.Where(c => c.IsSelected).Select(c => c.InstanceId).ToList();

    public List<string> SelectedDriverPackageNames =>
        DriverPackageCandidates.Where(c => c.IsSelected).Select(c => c.PackageName).ToList();

    public bool ShowsNoDriverFindings => IsDriverCleanup && !IsScanning && !IsWaiting
        && DriverDeviceCandidates.Count == 0 && DriverPackageCandidates.Count == 0;

    public bool HasDriverDeviceCandidates => DriverDeviceCandidates.Count > 0;

    public bool HasDriverPackageCandidates => DriverPackageCandidates.Count > 0;

    public string DriverGroupDisconnectedText => LocalizationService.Instance["CleanupDriverGroupDisconnected"];

    public string DriverGroupOldPackagesText => LocalizationService.Instance["CleanupDriverGroupOldPackages"];

    public void SetDriverCandidates(
        IEnumerable<DriverDeviceCandidateDto>? devices, IEnumerable<DriverPackageCandidateDto>? packages)
    {
        DriverDeviceCandidates.Clear();
        DriverPackageCandidates.Clear();

        if (devices is not null)
            foreach (var device in devices)
            {
                var row = new DriverDeviceCandidateViewModel(device);
                row.PropertyChanged += OnCandidateSelectionChanged;
                DriverDeviceCandidates.Add(row);
            }

        if (packages is not null)
            foreach (var package in packages)
            {
                var row = new DriverPackageCandidateViewModel(package);
                row.PropertyChanged += OnCandidateSelectionChanged;
                DriverPackageCandidates.Add(row);
            }

        OnPropertyChanged(nameof(ShowsNoDriverFindings));
        OnPropertyChanged(nameof(HasDriverDeviceCandidates));
        OnPropertyChanged(nameof(HasDriverPackageCandidates));
        RaiseSelectedEntryFigures();
    }

    /// <summary>A row whose findings are shown as individually selectable rows; see <see cref="PythonVersionCandidateViewModel"/>.</summary>
    public bool IsOldPythonVersions => Target.Kind == CleanupKind.OldPythonVersions;

    /// <summary>Never pre-populated with a selection, same rule as <see cref="LeftoverCandidates"/>.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PythonVersionCandidateViewModel> OldPythonVersionCandidates { get; } = [];

    public List<string> SelectedOldPythonVersionPaths =>
        OldPythonVersionCandidates.Where(c => c.IsSelected).Select(c => c.PythonExe).ToList();

    public bool ShowsNoOldPythonVersionsFound =>
        IsOldPythonVersions && !IsScanning && !IsWaiting && OldPythonVersionCandidates.Count == 0;

    public void SetOldPythonVersionCandidates(IEnumerable<PythonVersionCandidateDto>? candidates)
    {
        OldPythonVersionCandidates.Clear();

        if (candidates is not null)
            foreach (var candidate in candidates)
            {
                var row = new PythonVersionCandidateViewModel(candidate);
                row.PropertyChanged += OnCandidateSelectionChanged;
                OldPythonVersionCandidates.Add(row);
            }

        OnPropertyChanged(nameof(ShowsNoOldPythonVersionsFound));
        RaiseSelectedEntryFigures();
    }

    public string SelectAllEntriesText => LocalizationService.Instance["CleanupEntriesSelectAll"];

    /// <summary>What the last scan found for a registry target, carried to Clean so it works only this list instead of scanning again. Per-entry exclusions (<see cref="ExcludedEntryPaths"/>) apply before the clean runs.</summary>
    public IReadOnlyList<RegistryFinding> RegistryFindings { get; private set; } = [];

    public void SetRegistryFindings(IReadOnlyList<RegistryFinding>? findings) => RegistryFindings = findings ?? [];

    /// <summary>The target's detail list: grouped rows, never one per file. Each starts checked; unchecking one adjusts <see cref="Bytes"/> and <see cref="Files"/> through <see cref="OnEntryToggled"/>, so every total above it stays correct. A range collection, since a scan can hand it thousands of entries.</summary>
    public RangeObservableCollection<CleanupEntryViewModel> Entries { get; } = [];

    /// <summary>Set while <see cref="ToggleAllEntries"/> drives every row's <c>IsSelected</c>: <see cref="OnEntryToggled"/> then skips its per-entry work so the totals adjust once at the end.</summary>
    private bool _suppressEntryToggle;

    /// <summary>Tri-state for the details header checkbox: every entry ticked, none, or a mix.</summary>
    public bool? AllEntriesSelected => Entries.Count(e => e.IsSelected) switch
    {
        0 => false,
        var selected when selected == Entries.Count => true,
        _ => null,
    };

    /// <summary>Ticks every entry when fewer than all are selected, clears them all when every one already is, so one click always resolves to a definite state.</summary>
    [RelayCommand]
    private void ToggleAllEntries()
    {
        var turnOn = AllEntriesSelected != true;
        long deltaBytes = 0;
        int deltaFiles = 0;

        _suppressEntryToggle = true;
        try
        {
            foreach (var entry in Entries)
            {
                if (entry.IsSelected == turnOn)
                    continue;

                deltaBytes += (turnOn ? -1 : 1) * entry.Model.Bytes;
                deltaFiles += (turnOn ? -1 : 1) * entry.Model.Files;
                entry.IsSelected = turnOn;
            }
        }
        finally
        {
            _suppressEntryToggle = false;
        }

        _unselectedEntryBytes += deltaBytes;
        _unselectedEntryFiles += deltaFiles;
        RaiseSelectedEntryFigures();
        RefreshSelectedEntryRisk();
        ApplyDriveNotes();
        OnPropertyChanged(nameof(AllEntriesSelected));
        _onSelectionChanged();
    }

    /// <summary>What the detail list binds to: a live view over <see cref="Entries"/>, flat unless the target's rows come from several drives (see <see cref="GroupsByDrive"/>). It keeps tracking <see cref="Entries"/> across the wholesale replace <see cref="SetEntries"/> does on every scan.</summary>
    public ListCollectionView EntriesView { get; }

    /// <summary>Recycle bin rows are single files spread over several drives, so they group by drive.</summary>
    private bool GroupsByDrive => _recycleBinDrives.Count > 0;

    private void ApplyGrouping()
    {
        EntriesView.GroupDescriptions.Clear();
        if (GroupsByDrive)
            EntriesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CleanupEntryViewModel.DriveLabel)));

        ApplyDriveNotes();
    }

    /// <summary>The "and N more" line, per drive in that drive's group header, since one figure for all drives would belong to none. When rows on a drive are unticked as well, only the listed selection is cleaned and the note says so (see <see cref="CleanupService.PlanRecycleBinEmpty"/>).</summary>
    private void ApplyDriveNotes()
    {
        foreach (var entry in Entries)
            entry.GroupNoteText = string.Empty;

        if (!GroupsByDrive)
        {
            OnPropertyChanged(nameof(HasOmittedEntries));
            return;
        }

        foreach (var drive in _recycleBinDrives)
        {
            if (drive.OmittedCount <= 0)
                continue;

            var rows = Entries.Where(e => string.Equals(e.DriveLabel, drive.Label, StringComparison.Ordinal)).ToList();
            if (rows.Count == 0)
                continue;

            var key = rows.Any(row => !row.IsSelected) ? "CleanupMoreEntriesNotCleaned" : "CleanupMoreEntries";
            var note = string.Format(
                LocalizationService.Instance[key], drive.OmittedCount, ByteSize.Format(drive.OmittedBytes));

            foreach (var row in rows)
                row.GroupNoteText = note;
        }

        OnPropertyChanged(nameof(HasOmittedEntries));
    }

    public bool HasEntries => Entries.Count > 0;

    public string DetailsHeaderText =>
        string.Format(LocalizationService.Instance["CleanupShowDetails"], Entries.Count);

    /// <summary>What Clean must leave untouched: every entry the user unchecked. Empty means clean everything the scan found.</summary>
    public IReadOnlyCollection<string> ExcludedEntryPaths =>
        Entries.Where(e => !e.IsSelected).Select(e => e.Model.Path).ToList();

    /// <summary>The rows the user saw and left ticked, handed to a recycle-bin clean so it deletes nothing that reached the bin after the scan. Null for every other kind.</summary>
    public IReadOnlyCollection<string>? ConfirmedEntryPathsForRun =>
        Target.Kind == CleanupKind.RecycleBin
            ? Entries.Where(e => e.IsSelected).Select(e => e.Model.Path).ToList()
            : null;

    /// <summary>The row the last applied report landed on, so the next one can retire it.</summary>
    private CleanupEntryViewModel? _runningEntry;

    /// <summary>What the target had freed when that row became current, and at the last report applied: a report has no per-row verdict, and the growth of this figure is the only sign that anything went.</summary>
    private long _freedWhenEntryStarted;
    private long _freedAtLastReport;

    /// <summary>Every row back to the start of a run. A row the user unticked is settled before the run begins, since it goes to Clean as an exclusion.</summary>
    public void ResetEntryStates()
    {
        _runningEntry = null;
        _freedWhenEntryStarted = 0;
        _freedAtLastReport = 0;

        foreach (var entry in Entries)
        {
            entry.State = entry.IsSelected ? EntryState.Pending : EntryState.Skipped;
            entry.FreedBytes = 0;
        }
    }

    /// <summary>Marks the row one progress report belongs to. The recycle bin reports each file's original path, so its rows match exactly (their own path is the transient <c>$R</c> file, which no report names). Every other target reports a file inside a grouped folder, so the row with the longest matching prefix wins. This is the first of three gates: a closed detail list matches nothing; the caller adds the other two (dialog on screen, at most one report per 200 ms).</summary>
    public void ApplyEntryProgress(string currentPath, long freedSoFar)
    {
        if (!IsDetailsExpanded || Entries.Count == 0 || string.IsNullOrEmpty(currentPath))
            return;

        var match = MatchEntry(currentPath, out var exact);
        var freedSinceLastReport = freedSoFar - _freedAtLastReport;
        _freedAtLastReport = freedSoFar;

        if (match is null || match.State is EntryState.Skipped || ReferenceEquals(match, _runningEntry))
            return;

        // The one per-row verdict a report carries. The recycle bin reports once per item and its freed figure grows only for an item actually removed, so no growth means this item stayed behind, even when the 200 ms gate dropped reports in between. A prefix match cannot say the same: it reports every tenth file, and a folder of empty files frees nothing.
        if (exact && freedSinceLastReport <= 0 && match.Model.Bytes > 0)
        {
            FinishRunningEntry(EntryState.Cleaned, freedSoFar);
            match.State = EntryState.Skipped;
            return;
        }

        // A different row is being worked on, so the previous one is done.
        FinishRunningEntry(EntryState.Cleaned, freedSoFar);

        match.State = EntryState.Running;
        _runningEntry = match;
        _freedWhenEntryStarted = freedSoFar;
    }

    /// <summary>The target is finished, so no row may still read "running". Unticked rows, and every row of a stopped or unfinished target, are still on disk and say so. For a target that ran to the end, leftovers read as cleaned; what stayed behind in use is only known for the whole target (<see cref="SkippedText"/>). Those rows share what the target freed beyond what the reports credited, in list order and each capped at its own size.</summary>
    public void ResolveEntryStates(bool completed)
    {
        FinishRunningEntry(completed ? EntryState.Cleaned : EntryState.Skipped, _freedAtLastReport);

        var uncredited = Math.Max(0, FreedBytes - Entries.Sum(e => e.FreedBytes));

        foreach (var entry in Entries)
        {
            if (entry.State is EntryState.Cleaned or EntryState.Skipped)
                continue;

            var cleaned = completed && entry.IsSelected;
            entry.State = cleaned ? EntryState.Cleaned : EntryState.Skipped;
            var credit = cleaned ? Math.Min(uncredited, entry.Model.Bytes) : 0;
            entry.FreedBytes = credit;
            uncredited -= credit;
        }
    }

    private void FinishRunningEntry(EntryState state, long freedSoFar)
    {
        if (_runningEntry is not { } entry)
            return;

        // Capped at the measured size: the boundary between two rows is only as sharp as the reports are frequent, and a row must not be credited with its neighbour's bytes.
        entry.FreedBytes = state == EntryState.Cleaned
            ? Math.Clamp(freedSoFar - _freedWhenEntryStarted, 0, entry.Model.Bytes)
            : 0;
        entry.State = state;
        _runningEntry = null;
    }

    private CleanupEntryViewModel? MatchEntry(string currentPath, out bool exact)
    {
        exact = false;
        CleanupEntryViewModel? best = null;
        var bestLength = 0;

        foreach (var entry in Entries)
        {
            if (entry.Model.OriginalPath is { Length: > 0 } original)
            {
                if (string.Equals(original, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    exact = true;
                    return entry;
                }

                continue;
            }

            var path = entry.Model.Path;
            if (path.Length > bestLength && IsUnder(currentPath, path))
            {
                best = entry;
                bestLength = path.Length;
            }
        }

        return best;
    }

    /// <summary>Prefix on whole path SEGMENTS: "…\Temp" must never claim a file in "…\Temp2".</summary>
    private static bool IsUnder(string path, string folder)
    {
        if (folder.Length == 0 || !path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            return false;

        return path.Length == folder.Length
            || folder[^1] is '\\' or '/'
            || path[folder.Length] is '\\' or '/';
    }

    private int _omittedEntryCount;
    private long _omittedEntryBytes;
    private IReadOnlyList<RecycleBinDriveSummary> _recycleBinDrives = [];

    /// <summary>The line under the whole list, suppressed while every drive states its own share in its group header.</summary>
    public bool HasOmittedEntries => _omittedEntryCount > 0 && !GroupsByDrive;

    public string OmittedEntriesText => string.Format(
        LocalizationService.Instance["CleanupMoreEntries"], _omittedEntryCount, ByteSize.Format(_omittedEntryBytes));

    /// <summary>Replaces the detail list from a fresh scan; every entry starts checked and sums to the target's scanned total. <paramref name="recycleBinDrives"/> comes with a recycle-bin scan only: each drive's label for the grouping and what it left out of the list.</summary>
    public void SetEntries(
        IEnumerable<CleanupEntry>? entries, int omittedCount = 0, long omittedBytes = 0,
        IReadOnlyList<RecycleBinDriveSummary>? recycleBinDrives = null)
    {
        Entries.Clear();
        _unselectedEntryBytes = 0;
        _unselectedEntryFiles = 0;
        RaiseSelectedEntryFigures();
        _omittedEntryCount = omittedCount;
        _omittedEntryBytes = omittedBytes;
        _recycleBinDrives = recycleBinDrives ?? [];

        if (entries is not null)
        {
            // Built as a plain list and added in one go: one notification for the whole scan.
            Entries.AddRange(entries
                .Select(entry => new CleanupEntryViewModel(
                    entry, OnEntryToggled, OnEntryPermanentlyExcluded, Target.RiskLevel, () => ConsequenceText)
                {
                    DriveLabel = DriveLabelFor(entry),
                })
                .ToList());
        }

        ApplyGrouping();
        RefreshSelectedEntryRisk();
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(DetailsHeaderText));
        OnPropertyChanged(nameof(HasOmittedEntries));
        OnPropertyChanged(nameof(OmittedEntriesText));
        OnPropertyChanged(nameof(AllEntriesSelected));
    }

    /// <summary>The drive group a row belongs to, named as the scan saw the drive. A row whose root matches no summary keeps its own root.</summary>
    private string DriveLabelFor(CleanupEntry entry)
    {
        var root = System.IO.Path.GetPathRoot(entry.Path);
        if (string.IsNullOrEmpty(root))
            return string.Empty;

        foreach (var drive in _recycleBinDrives)
        {
            if (string.Equals(drive.Root, root, StringComparison.OrdinalIgnoreCase))
                return drive.Label;
        }

        return root;
    }

    /// <summary>Keeps the selected figures in step with the entry ticks; the found figures are not touched.</summary>
    private void OnEntryToggled(CleanupEntryViewModel entry)
    {
        if (_suppressEntryToggle)
            return;

        var sign = entry.IsSelected ? -1 : 1;
        _unselectedEntryBytes += sign * entry.Model.Bytes;
        _unselectedEntryFiles += sign * entry.Model.Files;
        RaiseSelectedEntryFigures();
        RefreshSelectedEntryRisk();

        // The drive's own note depends on whether anything on it is unticked.
        ApplyDriveNotes();
        OnPropertyChanged(nameof(AllEntriesSelected));
        _onSelectionChanged();
    }

    /// <summary>Replaces the candidate list from a fresh scan; every new entry starts unselected.</summary>
    public void SetLeftoverCandidates(IEnumerable<LeftoverCandidateDto>? candidates)
    {
        LeftoverCandidates.Clear();

        if (candidates is not null)
            foreach (var candidate in candidates)
            {
                var row = new LeftoverCandidateViewModel(candidate);
                row.PropertyChanged += OnCandidateSelectionChanged;
                LeftoverCandidates.Add(row);
            }

        RaiseSelectedEntryFigures();
        OnPropertyChanged(nameof(ShowsSize));
        OnPropertyChanged(nameof(ShowsNothingFound));
        OnPropertyChanged(nameof(HasSizeCell));
        OnPropertyChanged(nameof(ShowsNoLeftoversFound));
    }

    public string SizeText => IsRegistryCleanup
        ? string.Format(LocalizationService.Instance[Files == 1 ? "CleanupRegistryEntryCountOne" : "CleanupRegistryEntryCount"], Files)
        : ByteSize.Format(Bytes);

    /// <summary>The size cell while choosing: "ticked / found"; an unticked row reads "0 B / found". A registry row keeps its plain count.</summary>
    public string ChosenSizeText => IsRegistryCleanup
        ? SizeText
        : $"{ByteSize.Format(IsSelected ? SelectedEntryBytes : 0)} / {ByteSize.Format(Bytes)}";

    /// <summary>"Nothing found", said quietly: a blank cell would read as "not measured".</summary>
    public string NothingText => LocalizationService.Instance["CleanupNothingToClean"];

    /// <summary>A real figure, once there is one, and not while the waiting ring has the cell.</summary>
    public bool ShowsSize => !HasUnknownSize && !IsScanning && !IsWaiting && !IsAnalyzingComponentStore && (IsRegistryCleanup ? Files > 0 : Bytes > 0);

    /// <summary>Not on the driver and old Python rows: they list candidates without a byte total and say "nothing found" in their own list.</summary>
    public bool ShowsNothingFound => !HasUnknownSize && !IsScanning && !IsWaiting && !IsAnalyzingComponentStore && !IsDriverCleanup && !IsOldPythonVersions
        && (IsRegistryCleanup ? Files <= 0 : Bytes <= 0);

    /// <summary>Whether the size column has anything to show: a figure, "nothing found", the measuring dots or the wait spinner. Otherwise the column collapses so its MinWidth is not reserved as an empty box.</summary>
    public bool HasSizeCell => ShowsSize || ShowsNothingFound || IsScanning || IsWaiting || IsAnalyzingComponentStore;

    /// <summary>Targets that cannot report a size in advance: Disk Cleanup decides for itself what it removes, and a repair pass deletes nothing. Their cell stays empty.</summary>
    public bool HasUnknownSize => Target.Kind is CleanupKind.DiskCleanup or CleanupKind.SystemRepair or CleanupKind.Defragmentation;

    /// <summary>Marks the target as needing admin, which explains why some of it may be skipped.</summary>
    public bool NeedsElevation => Target.NeedsElevation;

    /// <summary>True only while this target needs admin and the run does not have it; with the elevated helper running there is nothing to warn about.</summary>
    [ObservableProperty]
    private bool showsElevationHint;

    partial void OnShowsElevationHintChanged(bool value) => OnPropertyChanged(nameof(ShowsElevationCapsule));

    public string ElevationHint => LocalizationService.Instance["CleanupNeedsAdmin"];

    /// <summary>Said once, on the capsule itself: without admin this area is measured and cleaned only in part.</summary>
    public string ElevationTooltip => LocalizationService.Instance["CleanupAdminMissing"];

    /// <summary>Same empty-capsule guard as <see cref="ShowsRunState"/>: the capsule's visibility checks the text too.</summary>
    public bool ShowsElevationCapsule => ShowsElevationHint && !string.IsNullOrEmpty(ElevationHint);

    /// <summary>The language changed; every text here is read from the resources in code, not through a binding.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(ConsequenceText));
        OnPropertyChanged(nameof(RiskLevelText));
        OnPropertyChanged(nameof(RiskTooltip));
        OnPropertyChanged(nameof(SelectedEntryRiskHint));
        OnPropertyChanged(nameof(RescanHeader));
        OnPropertyChanged(nameof(NothingText));
        OnPropertyChanged(nameof(ElevationHint));
        OnPropertyChanged(nameof(ElevationTooltip));
        OnPropertyChanged(nameof(ShowsElevationCapsule));
        OnPropertyChanged(nameof(RunStateText));
        OnPropertyChanged(nameof(ShowsRunState));
        OnPropertyChanged(nameof(SkippedText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ChosenSizeText));
        OnPropertyChanged(nameof(LeftoverWarningText));
        OnPropertyChanged(nameof(DriverGroupDisconnectedText));
        OnPropertyChanged(nameof(DriverGroupOldPackagesText));
        OnPropertyChanged(nameof(DetailsHeaderText));
        OnPropertyChanged(nameof(OmittedEntriesText));
        OnPropertyChanged(nameof(SelectAllEntriesText));
        OnPropertyChanged(nameof(RecycleAgeLabelText));
        OnPropertyChanged(nameof(RecycleAgeDaysText));

        foreach (var candidate in LeftoverCandidates)
            candidate.RefreshLocalizedText();

        foreach (var device in DriverDeviceCandidates)
            device.RefreshLocalizedText();

        foreach (var package in DriverPackageCandidates)
            package.RefreshLocalizedText();

        foreach (var version in OldPythonVersionCandidates)
            version.RefreshLocalizedText();

        foreach (var entry in Entries)
            entry.RefreshLocalizedText();

        // Regroups from the current DriveLabel: a PropertyGroupDescription does not move an item to a new group on its own PropertyChanged, so a stale group name would survive a language switch.
        ApplyGrouping();

        // Built as text, not read through a binding, so it must be rebuilt.
        ApplyDriveNotes();
    }

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ChosenSizeText));
        RaiseSelectedRisk();
        _onSelectionChanged();
    }

    partial void OnBytesChanged(long value)
    {
        RaiseSelectedEntryFigures();
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ShowsSize));
        OnPropertyChanged(nameof(ShowsNothingFound));
        OnPropertyChanged(nameof(HasSizeCell));
    }

    /// <summary>Only matters for a registry row, but cheap to raise unconditionally.</summary>
    partial void OnFilesChanged(int value)
    {
        RaiseSelectedEntryFigures();
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ShowsSize));
        OnPropertyChanged(nameof(ShowsNothingFound));
        OnPropertyChanged(nameof(HasSizeCell));
    }

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsSize));
        OnPropertyChanged(nameof(ShowsNothingFound));
        OnPropertyChanged(nameof(HasSizeCell));
        OnPropertyChanged(nameof(ShowsNoLeftoversFound));
        OnPropertyChanged(nameof(ShowsNoDriverFindings));
        OnPropertyChanged(nameof(ShowsNoOldPythonVersionsFound));
    }

    partial void OnIsWaitingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsSize));
        OnPropertyChanged(nameof(ShowsNothingFound));
        OnPropertyChanged(nameof(HasSizeCell));
        OnPropertyChanged(nameof(ShowsNoLeftoversFound));
        OnPropertyChanged(nameof(ShowsNoDriverFindings));
        OnPropertyChanged(nameof(ShowsNoOldPythonVersionsFound));
    }
}
