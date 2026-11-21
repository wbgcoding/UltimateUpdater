using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

/// <summary>One collapsible section of the cleanup dialog: a fixed, ordered subset of <see cref="CleanupViewModel.Items"/>, built once and never reshuffled. A view over the same <see cref="CleanupItemViewModel"/> instances the flat list owns; selecting a target changes nothing about how a run is sent.</summary>
public sealed partial class CleanupGroupViewModel : ObservableObject
{
    private readonly Func<Models.Settings.RegistryCleanupSettings>? _registrySettings;
    private readonly LoggingService? _logging;
    private readonly Func<CleanupItemViewModel, bool> _skipsForMissingAdmin;

    /// <param name="skipsForMissingAdmin">True for a row a run started now would skip for missing admin rights. The window's view model supplies it (it also knows about a running elevated helper); without one the group asks only whether this process is elevated.</param>
    public CleanupGroupViewModel(
        CleanupGroupKey key, IReadOnlyList<CleanupItemViewModel> items,
        Func<Models.Settings.RegistryCleanupSettings>? registrySettings = null, LoggingService? logging = null,
        Func<CleanupItemViewModel, bool>? skipsForMissingAdmin = null)
    {
        Key = key;
        Items = items;
        _registrySettings = registrySettings;
        _logging = logging;
        _skipsForMissingAdmin = skipsForMissingAdmin
            ?? (item => CleanupService.SkipsWithoutAdmin(item.Target, ElevationService.IsElevated));

        foreach (var item in items)
            item.PropertyChanged += OnItemPropertyChanged;
    }

    public CleanupGroupKey Key { get; }

    /// <summary>The window's "rescan just this category" command; the header's right-click menu runs it with this group as parameter.</summary>
    public System.Windows.Input.ICommand? RescanCommand { get; internal set; }

    public string RescanHeader => LocalizationService.Instance["CleanupRescanMenu"];

    public IReadOnlyList<CleanupItemViewModel> Items { get; }

    public string Title => LocalizationService.Instance[CleanupGroups.TitleKey(Key)];

    public override string ToString() => Title;

    /// <summary>The one group whose rows report a count rather than bytes: every member is a <see cref="CleanupKind.RegistryCleanup"/> target.</summary>
    public bool IsRegistryGroup => Key == CleanupGroupKey.Registry;

    /// <summary>The registry group header's inline note: there is no switch for the backup, only a sentence saying where it goes. Read live from <see cref="Models.Settings.RegistryCleanupSettings"/>, since another window may change it.</summary>
    public string RegistryBackupNoteText => _registrySettings is { } settings
        ? string.Format(LocalizationService.Instance["CleanupRegistryBackupNote"],
            settings().BackupFolder, settings().RetentionCount)
        : string.Empty;

    /// <summary>Opens the registry backup folder in Explorer; a no-op, logged and swallowed, if nothing has been backed up yet.</summary>
    [RelayCommand]
    private void OpenRegistryBackupFolder()
    {
        if (_registrySettings is null || _logging is null)
            return;

        UriLauncher.LaunchDetached(_registrySettings().BackupFolder, _logging);
    }

    public long TotalBytes => Items.Sum(i => i.Bytes);

    public int TotalFiles => Items.Sum(i => i.Files);

    public string SizeText => IsRegistryGroup
        ? string.Format(LocalizationService.Instance[TotalFiles == 1 ? "CleanupRegistryEntryCountOne" : "CleanupRegistryEntryCount"], TotalFiles)
        : ByteSize.Format(TotalBytes);

    /// <summary>What the ticked members would free: the first half of <see cref="ChosenSizeText"/>. A row the run skips for missing admin rights adds nothing.</summary>
    public long SelectedBytes => Items.Where(i => i.IsSelected && !_skipsForMissingAdmin(i)).Sum(i => i.SelectedEntryBytes);

    /// <summary>The header's size cell: always "ticked / found", so the user sees what a run would free while choosing. Registry entries are counted and keep the plain text.</summary>
    public string ChosenSizeText => IsRegistryGroup
        ? SizeText
        : $"{ByteSize.Format(SelectedBytes)} / {ByteSize.Format(TotalBytes)}";

    /// <summary>False only when every member has nothing to report (system repair: DISM and sfc are both <see cref="CleanupItemViewModel.HasUnknownSize"/>): summing them gives zero and "0,0 B" would read as a measurement. A mixed group keeps showing the known sum.</summary>
    public bool ShowsSize => !Items.All(i => i.HasUnknownSize);

    /// <summary>What this section freed, shown in its header once the run is over. Set by the dialog when the phase changes: an ancestor binding to the window would not survive the render harness, which photographs this content detached from its Window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsFreed))]
    private bool runFinished;

    /// <summary>A section that freed nothing says nothing: "+0 B" would read as a result where there was none.</summary>
    public bool ShowsFreed => RunFinished && FreedBytes > 0;

    public long FreedBytes => Items.Sum(i => i.FreedBytes);

    public string FreedText => $"+{ByteSize.Format(FreedBytes)}";

    /// <summary>Members eligible for a group-wide toggle; scopes the header checkbox's tri-state and toggle. A drive optimisation row is swept in only by ticking its own row.</summary>
    private int VisibleCount => Items.Count(IsEligibleForGroupToggle);

    /// <summary>Counted over the same eligible rows as <see cref="VisibleCount"/>, or the header would read "8 of 6".</summary>
    public int SelectedCount => Items.Count(i => IsEligibleForGroupToggle(i) && i.IsSelected && !_skipsForMissingAdmin(i));

    /// <summary>The rows the header checkbox acts on: eligible and not skipped for missing admin rights, so ticking them all ends in the checked state.</summary>
    private int TogglableCount => Items.Count(i => IsEligibleForGroupToggle(i) && !_skipsForMissingAdmin(i));

    public string SelectedCountText =>
        string.Format(LocalizationService.Instance[VisibleCount == 1 ? "CleanupSelectedCountOne" : "CleanupSelectedCount"], SelectedCount, VisibleCount);

    /// <summary>The worst level among the ticked rows and their ticked detail rows, Safe when nothing is ticked. Drives the dot beside the title, so a collapsed category still shows that something ticked inside it is not harmless.</summary>
    public CleanupRiskLevel SelectedRiskLevel =>
        Items.Aggregate(CleanupRiskLevel.Safe, (worst, item) => item.SelectedRiskLevel > worst ? item.SelectedRiskLevel : worst);

    /// <summary>Never true for Safe: the dot only warns, it does not vouch.</summary>
    public bool ShowsSelectedRisk => SelectedRiskLevel > CleanupRiskLevel.Safe;

    public string SelectedRiskHint => CleanupItemViewModel.SelectedRiskHint(SelectedRiskLevel);

    private static bool IsEligibleForGroupToggle(CleanupItemViewModel item) =>
        item.Target.Kind != CleanupKind.Defragmentation;

    /// <summary>Tri-state for the header checkbox, bound one-way; the toggle is <see cref="ToggleSelectionCommand"/>.</summary>
    public bool? IsSelected => SelectedCount switch
    {
        0 => false,
        var selected when selected == TogglableCount => true,
        _ => null,
    };

    /// <summary>Ticks every eligible row when fewer than all are selected, clears them when all are, so one click always resolves to a definite state. A drive optimisation row is left as it was.</summary>
    [RelayCommand]
    private void ToggleSelection()
    {
        var eligible = Items.Where(i => IsEligibleForGroupToggle(i) && !_skipsForMissingAdmin(i)).ToList();
        var turnOn = eligible.Count(i => i.IsSelected) != eligible.Count;
        foreach (var item in eligible)
            item.IsSelected = turnOn;
    }

    /// <summary>True only once every row has finished measuring and found nothing; never while scanning, and never for a group with an unknown-size target (Disk Cleanup, repair), which has nothing to report, not nothing found.</summary>
    public bool IsEmpty => Items.All(i => !i.IsScanning && !i.IsWaiting && !i.HasUnknownSize && !i.ShowsSize
                                          && !(i.IsDriverCleanup && i.Files > 0));

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CleanupItemViewModel.IsSelected):
                OnPropertyChanged(nameof(IsSelected));
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(SelectedCountText));
                OnPropertyChanged(nameof(ChosenSizeText));
                break;
            case nameof(CleanupItemViewModel.SelectedEntryBytes):
                OnPropertyChanged(nameof(SelectedBytes));
                OnPropertyChanged(nameof(ChosenSizeText));
                break;
            case nameof(CleanupItemViewModel.SelectedRiskLevel):
                OnPropertyChanged(nameof(SelectedRiskLevel));
                OnPropertyChanged(nameof(ShowsSelectedRisk));
                OnPropertyChanged(nameof(SelectedRiskHint));
                break;
            case nameof(CleanupItemViewModel.Bytes):
            case nameof(CleanupItemViewModel.Files):
                OnPropertyChanged(nameof(TotalBytes));
                OnPropertyChanged(nameof(TotalFiles));
                OnPropertyChanged(nameof(SizeText));
                OnPropertyChanged(nameof(ChosenSizeText));
                OnPropertyChanged(nameof(IsEmpty));
                break;
            case nameof(CleanupItemViewModel.IsScanning):
            case nameof(CleanupItemViewModel.IsWaiting):
                OnPropertyChanged(nameof(IsEmpty));
                break;
            case nameof(CleanupItemViewModel.FreedBytes):
                OnPropertyChanged(nameof(FreedBytes));
                OnPropertyChanged(nameof(FreedText));
                OnPropertyChanged(nameof(ShowsFreed));
                break;
        }
    }

    /// <summary>Admin rights appeared or went away, which changes which rows a run would skip, so the figures that leave those rows out are read again.</summary>
    public void RefreshAdminDependentTotals()
    {
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(ChosenSizeText));
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(RescanHeader));
        OnPropertyChanged(nameof(SelectedRiskHint));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ChosenSizeText));
        OnPropertyChanged(nameof(FreedText));
        OnPropertyChanged(nameof(RegistryBackupNoteText));
    }
}
