using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels.Cleanup;

/// <summary>Where one detail row stands in a run. <see cref="Pending"/> is also the resting state: with no run going every row is Pending and shows no mark.</summary>
public enum EntryState
{
    Pending,
    Running,
    Cleaned,
    Skipped,
}

/// <summary>One row in a target's detail list: a program's cache folder, a registry finding, a leftover candidate. Starts checked, since the target's total already counts it. Registry rows have no meaningful size or date, so the view hides those cells.</summary>
public sealed partial class CleanupEntryViewModel : ObservableObject
{
    private readonly Action<CleanupEntryViewModel> _onToggled;
    private readonly Action<CleanupEntryViewModel>? _onPermanentlyExclude;
    private readonly Func<string>? _consequenceTextProvider;

    public CleanupEntry Model { get; }

    /// <summary>The stronger of the owning target's risk level and the entry type's own (crash reports, logs and install leftovers count as considered, everything else as safe), so a row never reads weaker than its target. Fixed at construction.</summary>
    public CleanupRiskLevel RiskLevel { get; }

    public string RiskLevelText => LocalizationService.Instance[CleanupItemViewModel.RiskLevelKey(RiskLevel)];

    /// <summary>Same wording as the owning target's risk dot (<see cref="CleanupItemViewModel.RiskTooltip"/>); only the level word differs per row, the consequence sentence comes from the target.</summary>
    public string RiskTooltip => $"{RiskLevelText}: {_consequenceTextProvider?.Invoke() ?? string.Empty}";

    private static CleanupRiskLevel EntryTypeRiskLevel(CleanupEntryType type) => type switch
    {
        CleanupEntryType.CrashReports or CleanupEntryType.Logs or CleanupEntryType.InstallLeftovers
            => CleanupRiskLevel.Considered,
        _ => CleanupRiskLevel.Safe,
    };

    [ObservableProperty]
    private bool isSelected = true;

    /// <summary>Where this row stands in the running cleanup. Moved only by the owning target, and only while its detail list is open and on screen; see <see cref="CleanupItemViewModel.ApplyEntryProgress"/>.</summary>
    [ObservableProperty]
    private EntryState state;

    /// <summary>What this row actually freed; stays 0 for a row that was unticked or never removed.</summary>
    [ObservableProperty]
    private long freedBytes;

    /// <summary>Nothing is drawn before the name at rest.</summary>
    public bool HasRunMark => State != EntryState.Pending;

    public bool IsCleaned => State == EntryState.Cleaned;

    /// <summary>The two states that are a dot rather than a tick.</summary>
    public bool ShowsStateDot => State is EntryState.Running or EntryState.Skipped;

    /// <summary>The checkbox and the run mark share one cell: a selection can only change before a run starts, so the box hides once there is a mark.</summary>
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

    /// <summary>Right-click "permanently exclude": unchecks the row and asks the target to remember this path so a future scan starts it unchecked (<see cref="CleanupItemViewModel.OnEntryPermanentlyExcluded"/>).</summary>
    [RelayCommand]
    private void PermanentlyExclude()
    {
        IsSelected = false;
        _onPermanentlyExclude?.Invoke(this);
    }

    public string DisplayName => Model.DisplayName;

    public override string ToString() => DisplayName;

    public string SizeText => ByteSize.Format(Model.Bytes);

    /// <summary>"ticked / found" for the size cell while choosing: the row's own size when ticked, "0 B / size" when not.</summary>
    public string ChosenSizeText => $"{ByteSize.Format(IsSelected ? Model.Bytes : 0)} / {ByteSize.Format(Model.Bytes)}";

    /// <summary>A single deleted file in the recycle bin rather than a grouped folder row: it knows where it came from, and its own <see cref="CleanupEntry.Path"/> (the bin's <c>$R</c> file) means nothing to the user.</summary>
    public bool IsRecycled => Model.OriginalPath is { Length: > 0 };

    /// <summary>The folder the file was recycled from, shown under its name. Empty for every other kind of row.</summary>
    public string OriginalFolder => IsRecycled
        ? System.IO.Path.GetDirectoryName(Model.OriginalPath!) ?? Model.OriginalPath!
        : string.Empty;

    public bool HasOriginalFolder => OriginalFolder.Length > 0;

    /// <summary>What "the path" means for the tooltip and "copy path": for a recycled file where it came from, never the transient <c>$R</c> name.</summary>
    public string PathText => IsRecycled ? Model.OriginalPath! : Model.Path;

    /// <summary>What "open folder" opens: the original folder for a recycled file, the row's own path otherwise.</summary>
    public string OpenPath => IsRecycled ? OriginalFolder : Model.Path;

    /// <summary>Which drive this row sits on ("C:\ (System)"), set from the scan's per-drive summary; the path's root is the fallback.</summary>
    public string DriveLabel { get; set; }

    /// <summary>A permanent exclusion remembers a path, and a recycled file's <c>$R</c> path is gone once the bin is emptied, so the menu item is hidden for it.</summary>
    public bool CanPermanentlyExclude => !IsRecycled;

    /// <summary>A registry finding has no real size or timestamp, so its row hides both cells.</summary>
    public bool HasDate => Model.NewestWriteTime != DateTimeOffset.MinValue;

    /// <summary>Separate from <see cref="HasDate"/>: a per-drive recycle-bin entry has a known size but no per-item date. A registry finding has neither.</summary>
    public bool HasSize => Model.Bytes > 0;

    /// <summary>Whether "Open folder" makes sense: every real filesystem entry has a date, a size or both, a registry key has neither. Checks HasDate or HasSize, never HasDate alone. A recycled file answers for its original folder, which may be gone.</summary>
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

    /// <summary>The note under the drive this row belongs to ("and N more"), set by the owning target because only it knows what the scan left out per drive. Carried on the row because a <see cref="System.Windows.Data.CollectionViewGroup"/> is built by WPF and has nowhere to hold it; the group header reads it off its first item. Empty when the list is not grouped by drive.</summary>
    [ObservableProperty]
    private string groupNoteText = string.Empty;

    public bool HasGroupNote => GroupNoteText.Length > 0;

    partial void OnGroupNoteTextChanged(string value) => OnPropertyChanged(nameof(HasGroupNote));

    /// <summary>Right-click menu headers, as computed properties like the main table's row context menu.</summary>
    public string OpenFolderHeader => LocalizationService.Instance["CleanupEntryOpenFolder"];

    public string CopyPathHeader => LocalizationService.Instance["CleanupEntryCopyPath"];

    public string PermanentlyExcludeHeader => LocalizationService.Instance["CleanupEntryPermanentlyExclude"];

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ChosenSizeText));
        OnPropertyChanged(nameof(DateText));
        OnPropertyChanged(nameof(OpenFolderHeader));
        OnPropertyChanged(nameof(CopyPathHeader));
        OnPropertyChanged(nameof(PermanentlyExcludeHeader));
        OnPropertyChanged(nameof(RiskLevelText));
        OnPropertyChanged(nameof(RiskTooltip));
    }

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ChosenSizeText));
        _onToggled(this);
    }
}
