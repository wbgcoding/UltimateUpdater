using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Formatting;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.ViewModels.Uninstall;

/// <summary>Where one row stands in an uninstall run.</summary>
public enum UninstallRowState
{
    /// <summary>Not part of a run (or the run has not reached it).</summary>
    Idle,
    Waiting,
    Running,
    Removed,
    Failed,
    NeedsRestart,

    /// <summary>The run was stopped before this row's turn.</summary>
    NotStarted,
}

/// <summary>One installed entry in the uninstaller table: what it is, how it is removed, how much care it needs, whether it is ticked.</summary>
public sealed partial class UninstallRowViewModel : ObservableObject
{
    private readonly Action<UninstallRowViewModel>? _selectionChanged;

    public UninstallRowViewModel(UninstallListing listing, Action<UninstallRowViewModel>? selectionChanged = null)
    {
        Listing = listing;
        _selectionChanged = selectionChanged;
    }

    public UninstallListing Listing { get; }

    public InstalledProgramEntry Entry => Listing.Entry;

    public string Name => Entry.Name;

    public string Publisher => Entry.Publisher;

    public string Version => Entry.Version;

    public long? SizeBytes => Entry.SizeBytes;

    public DateOnly? InstallDate => Entry.InstallDate;

    public string SizeText => SizeBytes is { } bytes and > 0 ? ByteSize.Format(bytes) : string.Empty;

    public string InstalledText => InstallDate?.ToString("d", CultureInfo.CurrentCulture) ?? string.Empty;

    public CleanupRiskLevel RiskLevel => Listing.Risk.Level;

    /// <summary>False for the entries the window shows greyed and without a checkbox (this app itself, entries with no safe way to remove).</summary>
    public bool Selectable => Listing.Risk.Selectable && Listing.Plan.IsRunnable;

    public bool IsRisky => RiskLevel == CleanupRiskLevel.Risky;

    public string RiskLevelText => LocalizationService.Instance[UninstallRowText.RiskLevelKey(RiskLevel)];

    public string ReasonText => LocalizationService.Instance[Listing.Risk.ReasonKey];

    public string RiskTooltip => $"{RiskLevelText}: {ReasonText}";

    public string MethodText => LocalizationService.Instance[UninstallRowText.MethodKey(Listing.Plan)];

    /// <summary>Why a row cannot be ticked, when the plan says (a driver a device uses); empty for every other row.</summary>
    public string NoteText => UninstallRowText.Note(Listing.Plan, Entry);

    public bool HasNote => NoteText.Length > 0;

    /// <summary>The name that sorts and matches: the row's own, case-insensitive, never empty.</summary>
    public string SortName => Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(ShowsState))]
    private UninstallRowState state;

    /// <summary>Why a failed row failed, in the user's words.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private string? reason;

    public bool ShowsState => State != UninstallRowState.Idle;

    public string StateText => State switch
    {
        UninstallRowState.Waiting => LocalizationService.Instance["UninstallerStateWaiting"],
        UninstallRowState.Running => LocalizationService.Instance["UninstallerStateRunning"],
        UninstallRowState.Removed => LocalizationService.Instance["UninstallerStateRemoved"],
        UninstallRowState.NeedsRestart => LocalizationService.Instance["UninstallerStateNeedsRestart"],
        UninstallRowState.NotStarted => LocalizationService.Instance["UninstallerStateNotStarted"],
        UninstallRowState.Failed => string.IsNullOrWhiteSpace(Reason)
            ? LocalizationService.Instance["UninstallerStateFailed"]
            : LocalizationService.Instance["UninstallerStateFailed"] + ": " + Reason,
        _ => string.Empty,
    };

    /// <summary>The checkbox. Only <see cref="UninstallerViewModel"/> sets it for several rows at once; the user's own click on a row may tick any selectable one.</summary>
    [ObservableProperty]
    private bool isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !Selectable)
        {
            IsSelected = false;
            return;
        }

        _selectionChanged?.Invoke(this);
    }

    /// <summary>Re-reads every localized text after a language change.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(RiskLevelText));
        OnPropertyChanged(nameof(ReasonText));
        OnPropertyChanged(nameof(RiskTooltip));
        OnPropertyChanged(nameof(MethodText));
        OnPropertyChanged(nameof(NoteText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(InstalledText));
    }
}

/// <summary>Resource keys of the uninstaller table's texts.</summary>
internal static class UninstallRowText
{
    internal static string RiskLevelKey(CleanupRiskLevel level) => level switch
    {
        CleanupRiskLevel.Safe => "CleanupRiskSafe",
        CleanupRiskLevel.Considered => "CleanupRiskConsidered",
        _ => "CleanupRiskRisky",
    };

    /// <summary>The note under a row the plan refused for a reason worth saying: a driver in use. Empty otherwise
    /// (the method column already says "cannot remove").</summary>
    internal static string Note(UninstallPlan plan, InstalledProgramEntry entry) => plan.Refusal switch
    {
        UninstallRefusal.DriverInUse => string.Format(
            LocalizationService.Instance["UninstallerDriverInUse"], entry.InUseBy),
        _ => string.Empty,
    };

    internal static string MethodKey(UninstallPlan plan) => plan.Method switch
    {
        UninstallMethod.Interactive => "UninstallerMethodOwnWindow",
        UninstallMethod.Refused => "UninstallerMethodCannot",
        _ => plan.IsRunnable ? "UninstallerMethodSilent" : "UninstallerMethodCannot",
    };
}
