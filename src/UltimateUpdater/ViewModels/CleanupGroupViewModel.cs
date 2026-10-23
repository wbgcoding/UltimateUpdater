using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// One collapsible section of the cleanup dialog: a fixed, ordered subset of
/// <see cref="CleanupViewModel.Items"/>, built once in the constructor and never reshuffled: the group
/// a target belongs to does not change while the dialog is open, so there is nothing here that needs to
/// react to more than its own members' totals and selection. Purely a view over the same
/// <see cref="CleanupItemViewModel"/> instances the flat list already owns: nothing about SELECTING a
/// target changes; the elevated pipe still gets one target id at a time, exactly as before.
/// </summary>
public sealed partial class CleanupGroupViewModel : ObservableObject
{
    private readonly Func<Models.Settings.RegistryCleanupSettings>? _registrySettings;
    private readonly LoggingService? _logging;

    public CleanupGroupViewModel(
        CleanupGroupKey key, IReadOnlyList<CleanupItemViewModel> items,
        Func<Models.Settings.RegistryCleanupSettings>? registrySettings = null, LoggingService? logging = null)
    {
        Key = key;
        Items = items;
        _registrySettings = registrySettings;
        _logging = logging;

        foreach (var item in items)
            item.PropertyChanged += OnItemPropertyChanged;
    }

    public CleanupGroupKey Key { get; }

    public IReadOnlyList<CleanupItemViewModel> Items { get; }

    public string Title => LocalizationService.Instance[CleanupGroups.TitleKey(Key)];

    public override string ToString() => Title;

    /// <summary>The one group whose rows report a count rather than bytes: every member is a
    /// <see cref="CleanupKind.RegistryCleanup"/> target, same convention each row already uses on its
    /// own (<see cref="CleanupItemViewModel.IsRegistryCleanup"/>).</summary>
    public bool IsRegistryGroup => Key == CleanupGroupKey.Registry;

    /// <summary>
    /// The registry group header's own inline note. There is no on/off switch for the backup
    /// anywhere in this app, only this one sentence saying where it goes; read live off
    /// <see cref="Models.Settings.RegistryCleanupSettings"/> rather than a snapshot taken when the dialog
    /// opened, since nothing here can edit it, but another window in the same session might.
    /// </summary>
    public string RegistryBackupNoteText => _registrySettings is { } settings
        ? string.Format(LocalizationService.Instance["CleanupRegistryBackupNote"],
            settings().BackupFolder, settings().RetentionCount)
        : string.Empty;

    /// <summary>Opens the registry backup folder in Explorer; a no-op, logged and swallowed, if nothing has
    /// been backed up there yet.</summary>
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

    /// <summary>False only when EVERY member has nothing to report (Systemreparatur: DISM+sfc are both
    /// <see cref="CleanupItemViewModel.HasUnknownSize"/>); summing unknown-size rows always lands on
    /// exactly zero, and printing "0,0 B" reads as a measurement rather than as "not measurable". A mixed
    /// group (Windows-Wartung: disk-cleanup alongside real byte counts) keeps showing the known sum.</summary>
    public bool ShowsSize => !Items.All(i => i.HasUnknownSize);

    /// <summary>
    /// What this section actually freed, shown in its header once the run is over so the result reads per
    /// section rather than only as one total in the ring. Set by the dialog when the phase changes: an
    /// ancestor binding to the window would not survive the render harness, which photographs this content
    /// detached from its Window.
    /// </summary>
    [ObservableProperty]
    private bool showsFreed;

    public long FreedBytes => Items.Sum(i => i.FreedBytes);

    public string FreedText => $"+{ByteSize.Format(FreedBytes)}";

    /// <summary>Members eligible for a group-wide toggle at all; what the header checkbox's own tri-state
    /// and toggle are scoped to. A drive optimisation row is never swept in by a bulk toggle, group header
    /// included, only by ticking its own row.</summary>
    private int VisibleCount => Items.Count(IsEligibleForGroupToggle);

    /// <summary>Counted over the same eligible rows as <see cref="VisibleCount"/>: counting a ticked drive
    /// optimisation row here but not there made the header read "8 of 6", and kept the count out of step
    /// with the header checkbox beside it.</summary>
    public int SelectedCount => Items.Count(i => IsEligibleForGroupToggle(i) && i.IsSelected);

    public string SelectedCountText =>
        string.Format(LocalizationService.Instance[VisibleCount == 1 ? "CleanupSelectedCountOne" : "CleanupSelectedCount"], SelectedCount, VisibleCount);

    private static bool IsEligibleForGroupToggle(CleanupItemViewModel item) =>
        item.Target.Kind != CleanupKind.Defragmentation;

    /// <summary>Tri-state for the header checkbox, read-only and bound OneWay, never two-way. The actual
    /// toggle is <see cref="ToggleSelectionCommand"/>.</summary>
    public bool? IsSelected => SelectedCount switch
    {
        0 => false,
        var selected when selected == VisibleCount => true,
        _ => null,
    };

    /// <summary>Ticks every eligible row when fewer than all of them are selected, clears them
    /// when all are: the same "one click always resolves to a definite state" rule the header checkbox
    /// needs. A drive optimisation row is left exactly as it was.</summary>
    [RelayCommand]
    private void ToggleSelection()
    {
        var eligible = Items.Where(IsEligibleForGroupToggle).ToList();
        var turnOn = eligible.Count(i => i.IsSelected) != VisibleCount;
        foreach (var item in eligible)
            item.IsSelected = turnOn;
    }

    /// <summary>
    /// True only once every row has actually finished measuring AND found nothing, never while still
    /// scanning (the group must not visibly dim mid-measurement) and never for a group holding an
    /// unknown-size target (Disk Cleanup, repair): those have nothing to REPORT, which is not the same
    /// as having found nothing (the same distinction <see cref="CleanupItemViewModel.ShowsNothingFound"/>
    /// already draws per row).
    /// </summary>
    // The driver row reports findings as a count with 0 bytes, so ShowsSize alone dimmed the Drivers card as
    // "found nothing" while it listed tickable devices and packages.
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
                break;
            case nameof(CleanupItemViewModel.Bytes):
            case nameof(CleanupItemViewModel.Files):
                OnPropertyChanged(nameof(TotalBytes));
                OnPropertyChanged(nameof(TotalFiles));
                OnPropertyChanged(nameof(SizeText));
                OnPropertyChanged(nameof(IsEmpty));
                break;
            case nameof(CleanupItemViewModel.IsScanning):
            case nameof(CleanupItemViewModel.IsWaiting):
                OnPropertyChanged(nameof(IsEmpty));
                break;
            case nameof(CleanupItemViewModel.FreedBytes):
                OnPropertyChanged(nameof(FreedBytes));
                OnPropertyChanged(nameof(FreedText));
                break;
        }
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(FreedText));
        OnPropertyChanged(nameof(RegistryBackupNoteText));
    }
}
