using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

/// <summary>One row in the cleanup dialog: a tick box, what it frees, and how the scan is going.</summary>
public sealed partial class CleanupItemViewModel : ObservableObject
{
    private readonly Action _onSelectionChanged;

    /// <summary>Ticked: what every total, the red confirmation text and the run itself act on. Every card
    /// in the list is shown (there is no filter that could hide a ticked one), so what is ticked is exactly
    /// what runs.</summary>
    [ObservableProperty]
    private bool isSelected;

    /// <summary>What the scan found. Unticking a detail entry never lowers it: the found figures (row,
    /// group header, dialog total) stay put, only <see cref="SelectedEntryBytes"/> follows the ticks.</summary>
    [ObservableProperty]
    private long bytes;

    [ObservableProperty]
    private int files;

    private long _unselectedEntryBytes;
    private int _unselectedEntryFiles;

    /// <summary>What a run would actually clean from this target: <see cref="Bytes"/> without the detail
    /// entries the user unticked.</summary>
    public long SelectedEntryBytes => Math.Max(0, Bytes - _unselectedEntryBytes);

    public int SelectedEntryFiles => Math.Max(0, Files - _unselectedEntryFiles);

    private void RaiseSelectedEntryFigures()
    {
        OnPropertyChanged(nameof(SelectedEntryBytes));
        OnPropertyChanged(nameof(SelectedEntryFiles));
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

    /// <summary>
    /// Picked for this run but not started yet. The row shows a turning ring on the right, so a list of
    /// targets reads as a queue rather than as a set of rows that are doing nothing.
    /// </summary>
    [ObservableProperty]
    private bool isWaiting;

    /// <summary>
    /// Where this target stands in the run, as one quiet word beside its size. Kept as its own state rather
    /// than read off <see cref="IsWaiting"/>/<see cref="IsCleaning"/>/<see cref="IsDone"/> because two of the
    /// words those three cannot express (a run the user stopped, and a target the run never reached) are
    /// exactly the ones worth saying.
    /// </summary>
    [ObservableProperty]
    private CleanupTargetState runState;

    /// <summary>Whether the detail list is open. The first of the three gates on per-entry marking: a closed
    /// list has nothing to show, so nothing is matched for it at all.</summary>
    [ObservableProperty]
    private bool isDetailsExpanded;

    /// <summary>Never true for <see cref="CleanupTargetState.Done"/>: <see cref="RunStateText"/> is empty
    /// for that state (the green tick already says it), and a visible capsule with nothing in it read as a
    /// stray empty box beside the tick on a finished row.</summary>
    public bool ShowsRunState => RunState != CleanupTargetState.Idle && !string.IsNullOrEmpty(RunStateText);

    /// <summary>Colour is spent on exactly two of the five words. Never red: red is reserved for genuine
    /// Windows Update security severity, the same rule the risk dot above already follows.</summary>
    public bool RunStateIsSuccess => RunState == CleanupTargetState.Done;

    public bool RunStateIsCaution => RunState == CleanupTargetState.Stopped;

    /// <summary>A finished row already shows the green tick (<c>IsDone</c>); the state text next
    /// to it repeated the same information as a redundant "Fertig"/"Done", so <see
    /// cref="CleanupTargetState.Done"/> returns empty BEFORE the localization lookup rather than feeding a
    /// key into it, the one state this switch does not resolve to text.</summary>
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
    }

    /// <summary>
    /// What this target is doing right now, in as much detail as it can give: the file being deleted, the
    /// repair command and its step, the phase a Windows tool is reporting.
    /// </summary>
    [ObservableProperty]
    private string detailText = string.Empty;

    public bool HasDetail => DetailText.Length > 0;

    partial void OnDetailTextChanged(string value) => OnPropertyChanged(nameof(HasDetail));

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

    /// <summary>"12,4 MB / 310 MB": what has gone of what was measured.</summary>
    public string ProgressText =>
        SelectedEntryBytes > 0 ? $"{ByteSize.Format(FreedBytes)} / {ByteSize.Format(SelectedEntryBytes)}" : ByteSize.Format(FreedBytes);

    /// <summary>"3,2 MB in use, 1,1 MB protected": why this row freed less than it promised.</summary>
    public string SkippedText =>
        FormatSkippedShares(LocalizationService.Instance["CleanupInUse"], SkippedBytes, ProtectedBytes);

    /// <summary>Splits what stayed into its in-use and refused shares, each named only when present.
    /// Shared with the history window, which words the in-use share with its own key.</summary>
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

    /// <summary>Only ever passed for the recycle bin row (the one that carries the age-filter sub-row):
    /// persists the new value to settings and re-measures this one row. Set directly onto the backing
    /// field rather than through the property, so wiring the initial settings value during construction
    /// does not itself trigger a save and a rescan before the window has even opened.</summary>
    private readonly Action<int>? _onRecycleBinMinAgeDaysChanged;

    /// <summary>The window's "rescan just this row" command, handed in by the window's view model (it owns
    /// the scan); the card's right-click menu runs it with this row as the parameter. Null on a row built
    /// without a window, whose menu entry then stays disabled.</summary>
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

    /// <summary>Thin pass-through so <see cref="CleanupEntryViewModel"/> never needs to know about
    /// settings: only the window (<see cref="CleanupViewModel"/>) does.</summary>
    private void OnEntryPermanentlyExcluded(CleanupEntryViewModel entry) => _onPermanentlyExcludePath?.Invoke(entry.Model.Path);

    public CleanupTarget Target { get; }

    /// <summary>
    /// Plain key lookup for every ordinary, statically-titled target; formatted when the target carries
    /// arguments of its own (the per-drive defragmentation rows: drive letter, volume label), which
    /// is the one thing that makes several rows of the SAME kind readable at once.
    /// </summary>
    public string Title => Target.TitleArguments.Count == 0
        ? LocalizationService.Instance[Target.TitleKey]
        : string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            LocalizationService.Instance[Target.TitleKey],
            Target.TitleArguments.Cast<object>().ToArray());

    public override string ToString() => Title;

    /// <summary>
    /// Plain key lookup, unchanged, for every ordinary target. A drive row instead resolves each
    /// of <see cref="CleanupTarget.DescriptionWordKeys"/> through the same <see cref="LocalizationService"/>
    /// indexer, appends its byte size as the last formatting argument, and formats
    /// <see cref="CleanupTarget.DescriptionKey"/> with those, the same way <see cref="Title"/> formats
    /// <see cref="CleanupTarget.TitleKey"/>.
    /// </summary>
    public string Description => Target.DescriptionWordKeys.Count == 0
        ? LocalizationService.Instance[Target.DescriptionKey]
        : string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            LocalizationService.Instance[Target.DescriptionKey],
            Target.DescriptionWordKeys
                .Select(key => (object)LocalizationService.Instance[key])
                .Append((object)ByteSize.Format(Target.DescriptionBytes ?? 0))
                .ToArray());

    /// <summary>"Das passiert danach": shown on the risk dot's tooltip rather than as
    /// its own always-visible line, so a resting list of ~20 rows stays as quiet as the register asks for.</summary>
    public string ConsequenceText => LocalizationService.Instance[Target.ConsequenceKey];

    public string RiskLevelText => LocalizationService.Instance[RiskLevelKey(Target.RiskLevel)];

    public string RiskTooltip => $"{RiskLevelText}: {ConsequenceText}";

    /// <summary>Shared with <see cref="CleanupEntryViewModel"/>'s own per-row risk dot so the
    /// same <see cref="CleanupRiskLevel"/> value always reads as the same word wherever it is shown.</summary>
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

    /// <summary>The one card that carries the component store analysis (figures below the description, the
    /// measuring dots in the size cell): the analysis state lives on the window's view model, only this card
    /// shows it.</summary>
    public bool IsComponentCleanup => Target.Id == "component-cleanup";

    /// <summary>The window's view model, set only on the component cleanup card: the analysis block inside
    /// that card binds to it (the analysis state lives there, not on the row).</summary>
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

    /// <summary>The component cleanup card's analysis is running: the size cell shows the measuring dots
    /// then, like any row being measured. Always false on every other row.</summary>
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

    /// <summary>Only items whose deletion timestamp is at least this many days old are listed and,
    /// later, deleted. 0 means every age. Persisted through <see cref="_onRecycleBinMinAgeDaysChanged"/>,
    /// which only the recycle bin row is ever given.</summary>
    [ObservableProperty]
    private int recycleBinMinAgeDays;

    /// <summary>The last positive value the user had, so unchecking the sub-row's checkbox (which sets
    /// <see cref="RecycleBinMinAgeDays"/> to 0) and checking it again restores the number that was there
    /// rather than silently resetting it to the settings default.</summary>
    private int _lastPositiveRecycleBinMinAgeDays = 30;

    /// <summary>The checkbox half of the sub-row: on exactly when <see cref="RecycleBinMinAgeDays"/> is
    /// above zero.</summary>
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

    /// <summary>The one row whose findings the UI must show individually rather than as a single count;
    /// see <see cref="LeftoverCandidateViewModel"/>.</summary>
    public bool IsLeftoverInstalls => Target.Kind == CleanupKind.LeftoverInstalls;

    /// <summary>Never pre-populated with a selection: every entry starts unticked (see
    /// <see cref="LeftoverCandidateViewModel"/>) and the paths actually freed are exactly what the user
    /// ticked here, nothing more.</summary>
    public System.Collections.ObjectModel.ObservableCollection<LeftoverCandidateViewModel> LeftoverCandidates { get; } = [];

    public List<string> SelectedLeftoverPaths => LeftoverCandidates.Where(c => c.IsSelected).Select(c => c.Path).ToList();

    public bool ShowsNoLeftoversFound => IsLeftoverInstalls && !IsScanning && !IsWaiting && LeftoverCandidates.Count == 0;

    public string LeftoverWarningText => LocalizationService.Instance["CleanupLeftoverWarning"];

    /// <summary>The other row whose findings are shown as two individually selectable groups rather than a
    /// single count; see <see cref="DriverDeviceCandidateViewModel"/>/<see cref="DriverPackageCandidateViewModel"/>.</summary>
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
                DriverDeviceCandidates.Add(new DriverDeviceCandidateViewModel(device));

        if (packages is not null)
            foreach (var package in packages)
                DriverPackageCandidates.Add(new DriverPackageCandidateViewModel(package));

        OnPropertyChanged(nameof(ShowsNoDriverFindings));
        OnPropertyChanged(nameof(HasDriverDeviceCandidates));
        OnPropertyChanged(nameof(HasDriverPackageCandidates));
    }

    /// <summary>The one other row whose findings are shown as individually selectable rows rather than a
    /// single count; see <see cref="PythonVersionCandidateViewModel"/>.</summary>
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
                OldPythonVersionCandidates.Add(new PythonVersionCandidateViewModel(candidate));

        OnPropertyChanged(nameof(ShowsNoOldPythonVersionsFound));
    }

    public string SelectAllEntriesText => LocalizationService.Instance["CleanupEntriesSelectAll"];

    /// <summary>What the last scan found for a <see cref="CleanupKind.RegistryCleanup"/> target, carried
    /// through to Clean so it works ONLY this list instead of scanning the category again. Per-entry
    /// exclusions (<see cref="ExcludedEntryPaths"/>) are applied before the clean runs.</summary>
    public IReadOnlyList<RegistryFinding> RegistryFindings { get; private set; } = [];

    public void SetRegistryFindings(IReadOnlyList<RegistryFinding>? findings) => RegistryFindings = findings ?? [];

    /// <summary>
    /// The target's detail list: grouped rows, never one per file. Each starts checked; unchecking one
    /// adjusts <see cref="Bytes"/>/<see cref="Files"/> right away via <see cref="OnEntryToggled"/>, so the
    /// row's own total and every total above it (group, dialog, confirmation text) stay correct through
    /// the SAME propagation the top-level checkbox uses.
    /// </summary>
    /// <remarks>A range collection, like the update table's: a scan can hand this list several thousand
    /// entries, and adding them one at a time made the grouped view regroup itself once per entry.</remarks>
    public RangeObservableCollection<CleanupEntryViewModel> Entries { get; } = [];

    /// <summary>Set while <see cref="ToggleAllEntries"/> is driving every row's <c>IsSelected</c> itself:
    /// <see cref="OnEntryToggled"/> skips its own per-entry work so a list of thousands adjusts
    /// <see cref="Bytes"/>/<see cref="Files"/> once at the end instead of once per row.</summary>
    private bool _suppressEntryToggle;

    /// <summary>Tri-state for the details header checkbox, same reasoning as <see
    /// cref="CleanupGroupViewModel.IsSelected"/>: every entry ticked, none ticked, or a mix.</summary>
    public bool? AllEntriesSelected => Entries.Count(e => e.IsSelected) switch
    {
        0 => false,
        var selected when selected == Entries.Count => true,
        _ => null,
    };

    /// <summary>Ticks every entry when fewer than all of them are selected, clears them all when every
    /// one already is: same "one click always resolves to a definite state" rule the group header uses.</summary>
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
        ApplyDriveNotes();
        OnPropertyChanged(nameof(AllEntriesSelected));
        _onSelectionChanged();
    }

    /// <summary>What the detail list actually binds to: a live view over <see cref="Entries"/>. Flat (no
    /// <see cref="ListCollectionView.GroupDescriptions"/>) unless the target's rows come from several drives
    /// (see <see cref="GroupsByDrive"/>); <see cref="CollectionViewSource.GetDefaultView"/> keeps tracking
    /// <see cref="Entries"/> automatically, including the wholesale replace <see cref="SetEntries"/> does on
    /// every scan.</summary>
    public ListCollectionView EntriesView { get; }

    /// <summary>
    /// The recycle bin's rows are single files spread over several drives, so they group by drive: a flat
    /// list of hundreds of file names says nothing about where they came from.
    /// </summary>
    private bool GroupsByDrive => _recycleBinDrives.Count > 0;

    private void ApplyGrouping()
    {
        EntriesView.GroupDescriptions.Clear();
        if (GroupsByDrive)
            EntriesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CleanupEntryViewModel.DriveLabel)));

        ApplyDriveNotes();
    }

    /// <summary>
    /// The "…and N more" line, per drive and in that drive's own group header rather than once for the
    /// whole target, with a budget split across drives, one figure for all of them would belong to none of
    /// them. When rows on a drive are unticked as well, only the listed selection is cleaned and the note
    /// says so (<see cref="CleanupService.PlanRecycleBinEmpty"/> makes that true).
    /// </summary>
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

    /// <summary>What Clean must leave untouched: every entry the user unchecked. Empty means "clean
    /// everything the scan found".</summary>
    public IReadOnlyCollection<string> ExcludedEntryPaths =>
        Entries.Where(e => !e.IsSelected).Select(e => e.Model.Path).ToList();

    /// <summary>The rows the user saw and left ticked, handed to a recycle-bin clean so it deletes nothing that
    /// reached the bin after the scan. Null when the target keeps no such list (every other kind).</summary>
    public IReadOnlyCollection<string>? ConfirmedEntryPathsForRun =>
        Target.Kind == CleanupKind.RecycleBin
            ? Entries.Where(e => e.IsSelected).Select(e => e.Model.Path).ToList()
            : null;

    /// <summary>The row the last applied report landed on, so the next one can retire it.</summary>
    private CleanupEntryViewModel? _runningEntry;

    /// <summary>What the target had freed when that row became the current one, and at the last report
    /// applied: a report carries no per-row verdict of its own, and the growth of this figure is the only
    /// thing that says whether anything actually went.</summary>
    private long _freedWhenEntryStarted;
    private long _freedAtLastReport;

    /// <summary>Every row back to the start of a run. A row the user unticked is settled before the run even
    /// begins: it is handed to Clean as an exclusion (<see cref="ExcludedEntryPaths"/>), so "skipped" is a
    /// fact about it, not a forecast.</summary>
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

    /// <summary>
    /// Marks the row one progress report belongs to. The recycle bin reports each file's ORIGINAL path, so
    /// its rows match EXACTLY: their own <see cref="CleanupEntry.Path"/> is the transient <c>$R</c> file in
    /// the bin, which no report ever names. Every other target reports a file somewhere inside one of its
    /// grouped folders, so the row is the one whose path is the LONGEST matching prefix; a shorter folder
    /// would otherwise swallow a deeper one nested inside it.
    /// This is the first of the three gates: a closed detail list matches nothing at all; the caller adds
    /// the other two (dialog on screen, at most one report per 200 ms).
    /// </summary>
    public void ApplyEntryProgress(string currentPath, long freedSoFar)
    {
        if (!IsDetailsExpanded || Entries.Count == 0 || string.IsNullOrEmpty(currentPath))
            return;

        var match = MatchEntry(currentPath, out var exact);
        var freedSinceLastReport = freedSoFar - _freedAtLastReport;
        _freedAtLastReport = freedSoFar;

        if (match is null || match.State is EntryState.Skipped || ReferenceEquals(match, _runningEntry))
            return;

        // The one per-row verdict a report really carries. The recycle bin reports once per item and its
        // freed figure grows only for an item CleanupService.DeleteRecycledItem actually removed, so nothing
        // freed since the previous report means this item stayed behind, still true when the 200 ms gate
        // dropped reports in between, because an unchanged figure covers those too. A prefix match cannot say
        // the same: its target reports every tenth file, and a folder full of empty files frees nothing while
        // being emptied perfectly well.
        if (exact && freedSinceLastReport <= 0 && match.Model.Bytes > 0)
        {
            FinishRunningEntry(EntryState.Cleaned, freedSoFar);
            match.State = EntryState.Skipped;
            return;
        }

        // A different row is being worked on, so the one before it is behind us.
        FinishRunningEntry(EntryState.Cleaned, freedSoFar);

        match.State = EntryState.Running;
        _runningEntry = match;
        _freedWhenEntryStarted = freedSoFar;
    }

    /// <summary>
    /// The target is finished, so no row of it may still read "running". Rows the user unticked, and every
    /// row of a target that was stopped or never got through, are genuinely still on disk and say so. A
    /// target that ran to the end removed what it was handed, so whatever is left over reads as cleaned;
    /// what stayed behind in use is a figure only the target itself can give (<see cref="SkippedText"/>),
    /// never attributable to one row.
    /// </summary>
    public void ResolveEntryStates(bool completed)
    {
        FinishRunningEntry(completed ? EntryState.Cleaned : EntryState.Skipped, _freedAtLastReport);

        foreach (var entry in Entries)
        {
            if (entry.State is EntryState.Cleaned or EntryState.Skipped)
                continue;

            var cleaned = completed && entry.IsSelected;
            entry.State = cleaned ? EntryState.Cleaned : EntryState.Skipped;
            entry.FreedBytes = cleaned ? entry.Model.Bytes : 0;
        }
    }

    private void FinishRunningEntry(EntryState state, long freedSoFar)
    {
        if (_runningEntry is not { } entry)
            return;

        // Capped at what the row was measured at: the boundary between two rows is only as sharp as the
        // reports are frequent, and a row must never be credited with its neighbour's bytes.
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

    /// <summary>The one line under the whole list, suppressed while every drive states its own share of it
    /// in its group header, so the same figure is never claimed twice.</summary>
    public bool HasOmittedEntries => _omittedEntryCount > 0 && !GroupsByDrive;

    public string OmittedEntriesText => string.Format(
        LocalizationService.Instance["CleanupMoreEntries"], _omittedEntryCount, ByteSize.Format(_omittedEntryBytes));

    /// <summary>Replaces the detail list wholesale from a fresh scan: every entry starts checked, matching
    /// the target's own scanned total exactly (the sum of a fresh scan's entries always equals its Bytes).
    /// <paramref name="recycleBinDrives"/> comes with a recycle-bin scan only: it carries each drive's
    /// label for the grouping and what that drive left out of the list.</summary>
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
            // Built as a plain list first and handed over in one go: one notification for the whole
            // scan instead of one per entry.
            Entries.AddRange(entries
                .Select(entry => new CleanupEntryViewModel(
                    entry, OnEntryToggled, OnEntryPermanentlyExcluded, Target.RiskLevel, () => ConsequenceText)
                {
                    DriveLabel = DriveLabelFor(entry),
                })
                .ToList());
        }

        ApplyGrouping();
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(DetailsHeaderText));
        OnPropertyChanged(nameof(HasOmittedEntries));
        OnPropertyChanged(nameof(OmittedEntriesText));
        OnPropertyChanged(nameof(AllEntriesSelected));
    }

    /// <summary>The drive group a row belongs to, named as the scan saw the drive (volume label included).
    /// A row whose root matches no summary keeps the plain root it was built with.</summary>
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

        // The drive's own note depends on whether anything on it is unticked, so it follows every toggle.
        ApplyDriveNotes();
        OnPropertyChanged(nameof(AllEntriesSelected));
        _onSelectionChanged();
    }

    /// <summary>Replaces the candidate list wholesale from a fresh scan: every new entry starts
    /// unselected, same as the first scan ever did.</summary>
    public void SetLeftoverCandidates(IEnumerable<LeftoverCandidateDto>? candidates)
    {
        LeftoverCandidates.Clear();
        if (candidates is null)
            return;

        foreach (var candidate in candidates)
            LeftoverCandidates.Add(new LeftoverCandidateViewModel(candidate));

        OnPropertyChanged(nameof(ShowsSize));
        OnPropertyChanged(nameof(ShowsNothingFound));
        OnPropertyChanged(nameof(HasSizeCell));
        OnPropertyChanged(nameof(ShowsNoLeftoversFound));
    }

    public string SizeText => IsRegistryCleanup
        ? string.Format(LocalizationService.Instance[Files == 1 ? "CleanupRegistryEntryCountOne" : "CleanupRegistryEntryCount"], Files)
        : ByteSize.Format(Bytes);

    /// <summary>
    /// "Nothing found", said quietly. It
    /// still has to be said at all, because a blank cell reads as "not measured" when the honest answer is
    /// "measured, and there is nothing here".
    /// </summary>
    public string NothingText => LocalizationService.Instance["CleanupNothingToClean"];

    /// <summary>A real figure, once there is one to show, and not while the waiting ring has the cell.</summary>
    public bool ShowsSize => !HasUnknownSize && !IsScanning && !IsWaiting && !IsAnalyzingComponentStore && (IsRegistryCleanup ? Files > 0 : Bytes > 0);

    /// <summary>Not on the driver and old Python rows: they list their findings as candidates without a byte
    /// total and say "nothing found" in their own list, so the size cell would contradict a filled list and
    /// repeat itself on an empty one.</summary>
    public bool ShowsNothingFound => !HasUnknownSize && !IsScanning && !IsWaiting && !IsAnalyzingComponentStore && !IsDriverCleanup && !IsOldPythonVersions
        && (IsRegistryCleanup ? Files <= 0 : Bytes <= 0);

    /// <summary>
    /// Whether the row's own size/count column has anything to show at all: a figure, "nothing found", the
    /// measuring dots or the wait spinner. False exactly for a <see cref="HasUnknownSize"/> target once it
    /// is neither scanning nor waiting its turn. The column collapses then, so its MinWidth is not
    /// reserved as an empty box beside the row's risk dot for a figure that kind never has.
    /// </summary>
    public bool HasSizeCell => ShowsSize || ShowsNothingFound || IsScanning || IsWaiting || IsAnalyzingComponentStore;

    /// <summary>
    /// Targets that cannot report a size in advance: Disk Cleanup decides for itself what it will remove,
    /// and a repair pass deletes nothing at all. Their cell stays EMPTY rather than explaining why on
    /// every single run.
    /// </summary>
    public bool HasUnknownSize => Target.Kind is CleanupKind.DiskCleanup or CleanupKind.SystemRepair or CleanupKind.Defragmentation;

    /// <summary>Marks the target as needing admin, which explains why some of it may be skipped.</summary>
    public bool NeedsElevation => Target.NeedsElevation;

    /// <summary>
    /// True only while this target needs admin and the run does NOT have it, with the elevated helper
    /// running there is nothing to warn about, so the mark disappears.
    /// </summary>
    [ObservableProperty]
    private bool showsElevationHint;

    partial void OnShowsElevationHintChanged(bool value) => OnPropertyChanged(nameof(ShowsElevationCapsule));

    public string ElevationHint => LocalizationService.Instance["CleanupNeedsAdmin"];

    /// <summary>Said once, on the capsule itself: without admin this area is measured and cleaned only in part.</summary>
    public string ElevationTooltip => LocalizationService.Instance["CleanupAdminMissing"];

    /// <summary>Same empty-capsule guard as <see cref="ShowsRunState"/>: a capsule bound only to a raw flag
    /// would still render if its text ever resolved empty, so the capsule's own visibility checks the text too.</summary>
    public bool ShowsElevationCapsule => ShowsElevationHint && !string.IsNullOrEmpty(ElevationHint);

    /// <summary>The language changed; every text here is read from the resources in code, not through a binding.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(ConsequenceText));
        OnPropertyChanged(nameof(RiskLevelText));
        OnPropertyChanged(nameof(RiskTooltip));
        OnPropertyChanged(nameof(RescanHeader));
        OnPropertyChanged(nameof(NothingText));
        OnPropertyChanged(nameof(ElevationHint));
        OnPropertyChanged(nameof(ElevationTooltip));
        OnPropertyChanged(nameof(ShowsElevationCapsule));
        OnPropertyChanged(nameof(RunStateText));
        OnPropertyChanged(nameof(ShowsRunState));
        OnPropertyChanged(nameof(SkippedText));
        OnPropertyChanged(nameof(SizeText));
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

        // Regroups from the entries' now-current DriveLabel: a PropertyGroupDescription does not
        // move an item to a new group on its own PropertyChanged, so a stale group name/membership would
        // otherwise survive the language switch that just changed it.
        ApplyGrouping();

        // Built as text rather than read through a binding, so it has to be rebuilt, not just re-raised.
        ApplyDriveNotes();
    }

    partial void OnIsSelectedChanged(bool value)
    {
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

    /// <summary>Only matters for a registry row (see <see cref="IsRegistryCleanup"/>) but cheap enough
    /// to raise unconditionally rather than branch.</summary>
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
