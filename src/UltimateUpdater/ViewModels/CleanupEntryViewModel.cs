using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

/// <summary>Where one detail row stands in a run. <see cref="Pending"/> is also the resting state: with no
/// run going every row is Pending and shows no mark at all, so a list at rest looks exactly as it did
/// before a run could mark it.</summary>
public enum EntryState
{
    Pending,
    Running,
    Cleaned,
    Skipped,
}

/// <summary>
/// One row in a target's detail list: a program's cache folder, a registry finding, a
/// leftover candidate. Starts checked: the target's own total already counts it, so an unchecked row must
/// be a deliberate choice, not a silent default. Registry rows have no meaningful size/date: the view
/// hides those cells the same way <see cref="CleanupItemViewModel.HasUnknownSize"/> already does for a row.
/// </summary>
public sealed partial class CleanupEntryViewModel : ObservableObject
{
    private readonly Action<CleanupEntryViewModel> _onToggled;
    private readonly Action<CleanupEntryViewModel>? _onPermanentlyExclude;
    private readonly Func<string>? _consequenceTextProvider;

    public CleanupEntry Model { get; }

    /// <summary>The STRONGER of the owning target's own risk level and the entry TYPE's own
    /// level (crash reports, logs and install leftovers count as considered, everything else as safe), so a
    /// row never reads weaker than the target it belongs to. Fixed at construction: neither input can
    /// change for the lifetime of one scan's row.</summary>
    public CleanupRiskLevel RiskLevel { get; }

    public string RiskLevelText => LocalizationService.Instance[CleanupItemViewModel.RiskLevelKey(RiskLevel)];

    /// <summary>Same wording function the owning target's own risk dot uses (<see
    /// cref="CleanupItemViewModel.RiskTooltip"/>), so both dots read identically wherever they appear; only
    /// the level word can differ per row, the consequence sentence always comes from the target.</summary>
    public string RiskTooltip => $"{RiskLevelText}: {_consequenceTextProvider?.Invoke() ?? string.Empty}";

    private static CleanupRiskLevel EntryTypeRiskLevel(CleanupEntryType type) => type switch
    {
        CleanupEntryType.CrashReports or CleanupEntryType.Logs or CleanupEntryType.InstallLeftovers
            => CleanupRiskLevel.Considered,
        _ => CleanupRiskLevel.Safe,
    };

    [ObservableProperty]
    private bool isSelected = true;

    /// <summary>Where this row stands in the running cleanup. Only ever moved by the owning target, and only
    /// while its detail list is actually open and on screen; see <see
    /// cref="CleanupItemViewModel.ApplyEntryProgress"/>.</summary>
    [ObservableProperty]
    private EntryState state;

    /// <summary>What this row actually freed. Stays 0 for a row that was unticked or never removed, so it
    /// never claims a figure the run did not deliver.</summary>
    [ObservableProperty]
    private long freedBytes;

    /// <summary>Nothing is drawn before the name at rest: a permanent bullet in front of every row would be
    /// decoration on a list that is usually just being read.</summary>
    public bool HasRunMark => State != EntryState.Pending;

    public bool IsCleaned => State == EntryState.Cleaned;

    /// <summary>The two states that are a dot rather than a tick.</summary>
    public bool ShowsStateDot => State is EntryState.Running or EntryState.Skipped;

    /// <summary>The checkbox and the run mark share one cell and are never both meaningful at
    /// once: a selection can only change before a run starts, so the box hides itself the moment there is
    /// a mark to show instead.</summary>
    public bool IsCheckboxVisible => !HasRunMark;

    partial void OnStateChanged(EntryState value)
    {
        OnPropertyChanged(nameof(HasRunMark));
        OnPropertyChanged(nameof(IsCleaned));
        OnPropertyChanged(nameof(ShowsStateDot));
        OnPropertyChanged(nameof(IsCheckboxVisible));
    }

    public CleanupEntryViewModel(
        CleanupEntry model, Action<CleanupEntryViewModel> onToggled, Action<CleanupEntryViewModel>? onPermanentlyExclude = null,
        CleanupRiskLevel targetRiskLevel = CleanupRiskLevel.Safe, Func<string>? consequenceTextProvider = null)
    {
        Model = model;
        _onToggled = onToggled;
        _onPermanentlyExclude = onPermanentlyExclude;
        _consequenceTextProvider = consequenceTextProvider;
        DriveLabel = System.IO.Path.GetPathRoot(model.Path) ?? string.Empty;
        RiskLevel = (CleanupRiskLevel)Math.Max((int)targetRiskLevel, (int)EntryTypeRiskLevel(model.Type));
    }

    /// <summary>Right-click "permanently exclude"; unchecks itself (same visible effect
    /// as unticking it by hand) and asks the target to remember this path so a future scan starts it
    /// unchecked too (<see cref="CleanupItemViewModel.OnEntryPermanentlyExcluded"/>).</summary>
    [RelayCommand]
    private void PermanentlyExclude()
    {
        IsSelected = false;
        _onPermanentlyExclude?.Invoke(this);
    }

    public string DisplayName => Model.DisplayName;

    public override string ToString() => DisplayName;

    public string SizeText => ByteSize.Format(Model.Bytes);

    /// <summary>A single deleted file in the recycle bin rather than a grouped folder row: it knows where
    /// it came from, and its own <see cref="CleanupEntry.Path"/> (the bin's <c>$R</c> file) is of no use to
    /// the user at all.</summary>
    public bool IsRecycled => Model.OriginalPath is { Length: > 0 };

    /// <summary>The folder the file was recycled from, shown under its name (trimmed by the view, full in the
    /// tooltip). Empty for every other kind of row.</summary>
    public string OriginalFolder => IsRecycled
        ? System.IO.Path.GetDirectoryName(Model.OriginalPath!) ?? Model.OriginalPath!
        : string.Empty;

    public bool HasOriginalFolder => OriginalFolder.Length > 0;

    /// <summary>What the row's tooltip and "copy path" mean by "the path": for a recycled file that is
    /// where it came from, never the transient <c>$R</c> name in the bin.</summary>
    public string PathText => IsRecycled ? Model.OriginalPath! : Model.Path;

    /// <summary>What "open folder" opens: the original folder for a recycled file, the row's own path for
    /// everything else.</summary>
    public string OpenPath => IsRecycled ? OriginalFolder : Model.Path;

    /// <summary>Which drive this row sits on, as the detail list's default grouping shows it ("C:\ (System)").
    /// Set from the scan's own per-drive summary, which already read the volume label; the path's root is
    /// the fallback for a row that arrived without one.</summary>
    public string DriveLabel { get; set; }

    /// <summary>A permanent exclusion remembers a PATH, and a recycled file's <c>$R</c> path is gone the
    /// moment the bin is emptied; remembering it would only leave rubbish in the settings, so the row
    /// hides that menu item instead of storing something meaningless.</summary>
    public bool CanPermanentlyExclude => !IsRecycled;

    /// <summary>A registry finding carries no real size or timestamp; its row hides both
    /// cells instead of showing "0 B" or a nonsensical 0001-01-01. Every other entry kind always has one.</summary>
    public bool HasDate => Model.NewestWriteTime != DateTimeOffset.MinValue;

    /// <summary>Separate from <see cref="HasDate"/>: a per-drive recycle-bin entry has a
    /// real, known size but genuinely no per-item date (its size is a shell-reported total, not summed
    /// from individually-timestamped files): the size cell must stay visible even though the date cell
    /// still correctly hides. A registry finding has neither, so both stay hidden for it.</summary>
    public bool HasSize => Model.Bytes > 0;

    /// <summary>
    /// Whether "Open folder" makes sense: every real filesystem entry (a directory finding, or a
    /// per-drive recycle-bin entry) has a date, a size, or both, while a registry
    /// finding's Path is a registry key and has neither. Must check HasDate OR HasSize, never HasDate
    /// alone: a recycle-bin entry has a real size but no per-item date. A recycled file answers
    /// for its ORIGINAL folder, which may well be gone; offering to open a folder that no longer
    /// exists would do nothing at all.
    /// </summary>
    public bool HasOpenablePath => IsRecycled ? OriginalFolderExists : HasDate || HasSize;

    private bool? _originalFolderExists;

    private bool OriginalFolderExists
    {
        get
        {
            if (_originalFolderExists is { } known)
                return known;

            try
            {
                return (_originalFolderExists = OriginalFolder.Length > 0 && Directory.Exists(OriginalFolder)).Value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (_originalFolderExists = false).Value;
            }
        }
    }

    public string DateText => IsRecycled
        ? string.Format(
            LocalizationService.Instance["CleanupEntryDeleted"],
            Model.NewestWriteTime.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture))
        : string.Format(
            LocalizationService.Instance["CleanupEntryDateRange"],
            Model.OldestWriteTime.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture), Model.NewestWriteTime.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture));

    /// <summary>
    /// The note under the drive this row belongs to ("…and N more"), set by the owning target because only
    /// it knows what the scan left out per drive. Carried on the row rather than on a group object because
    /// a <see cref="System.Windows.Data.CollectionViewGroup"/> is built by WPF and has nowhere to put it;
    /// the group header reads it off its first item. Empty whenever the list is not grouped by drive.
    /// </summary>
    [ObservableProperty]
    private string groupNoteText = string.Empty;

    public bool HasGroupNote => GroupNoteText.Length > 0;

    partial void OnGroupNoteTextChanged(string value) => OnPropertyChanged(nameof(HasGroupNote));

    /// <summary>Right-click menu headers; same "computed property per VM" shape the
    /// main table's own row context menu already uses (<c>StartHeader</c>/<c>OpenTargetHeader</c> etc. on
    /// <see cref="UpdateRowViewModel"/>), not the indexer binding the WINDOW-level view models use for
    /// their own chrome text.</summary>
    public string OpenFolderHeader => LocalizationService.Instance["CleanupEntryOpenFolder"];

    public string CopyPathHeader => LocalizationService.Instance["CleanupEntryCopyPath"];

    public string PermanentlyExcludeHeader => LocalizationService.Instance["CleanupEntryPermanentlyExclude"];

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(DateText));
        OnPropertyChanged(nameof(OpenFolderHeader));
        OnPropertyChanged(nameof(CopyPathHeader));
        OnPropertyChanged(nameof(PermanentlyExcludeHeader));
        OnPropertyChanged(nameof(RiskLevelText));
        OnPropertyChanged(nameof(RiskTooltip));
    }

    partial void OnIsSelectedChanged(bool value) => _onToggled(this);
}
