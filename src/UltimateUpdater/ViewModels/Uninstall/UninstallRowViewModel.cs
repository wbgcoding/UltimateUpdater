using System.Globalization;
using System.Windows.Media.Imaging;
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

    /// <summary>What the row shows. Replaced in place by <see cref="Update"/> when a fresh read changes the entry, so the row (its tick, its place in the list) survives.</summary>
    public UninstallListing Listing { get; private set; }

    /// <summary>False for a row taken from the saved listing that the cheap recheck has not confirmed yet: shown, but greyed and not tickable.</summary>
    public bool IsVerified { get; private set; } = true;

    public InstalledProgramEntry Entry => Listing.Entry;

    public string Name => Entry.Name;

    /// <summary>The app's own icon once the background read resolved it; null until then or when the entry has none (the row then shows <see cref="KindSymbol"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private BitmapSource? icon;

    public bool HasIcon => Icon is not null;

    /// <summary>The Fluent symbol drawn in the icon's place: one per kind of entry.</summary>
    public Wpf.Ui.Controls.SymbolRegular KindSymbol => UninstallKindSymbol.For(Entry.Kind);

    /// <summary>Only programs and Store apps carry an icon of their own; the other kinds always show their symbol.</summary>
    internal bool WantsIcon => Entry.Kind is UninstallEntryKind.Program or UninstallEntryKind.StoreApp;

    /// <summary>True once the owner queued this row's icon read, so a later merge does not read it again.</summary>
    internal bool IconRequested { get; set; }

    public string Publisher => Entry.Publisher;

    public string Version => Entry.Version;

    public long? SizeBytes => Entry.SizeBytes;

    public DateOnly? InstallDate => Entry.InstallDate;

    public string SizeText => SizeBytes is { } bytes and > 0 ? ByteSize.Format(bytes) : string.Empty;

    public string InstalledText => InstallDate?.ToString("d", CultureInfo.CurrentCulture) ?? string.Empty;

    public CleanupRiskLevel RiskLevel => Listing.Risk.Level;

    /// <summary>False for the entries the window shows greyed and without a checkbox (this app itself, entries with no safe way to remove).</summary>
    public bool Selectable => IsVerified && Listing.Risk.Selectable && Listing.Plan.IsRunnable;

    public bool IsRisky => RiskLevel == CleanupRiskLevel.Risky;

    public string RiskLevelText => LocalizationService.Instance[UninstallRowText.RiskLevelKey(RiskLevel)];

    public string ReasonText => LocalizationService.Instance[Listing.Risk.ReasonKey];

    public string RiskTooltip => $"{RiskLevelText}: {ReasonText}";

    public string MethodText => LocalizationService.Instance[UninstallRowText.MethodKey(Listing.Plan)];

    /// <summary>Why a row cannot be ticked, when the plan says (a driver a device uses); empty for every other row.</summary>
    public string NoteText => UninstallRowText.Note(Listing.Plan, Entry);

    public bool HasNote => NoteText.Length > 0;

    /// <summary>Why the row cannot be ticked (the hover on its lock); empty for a selectable row.</summary>
    public string UnavailableReason => UninstallRowText.Unavailable(this);

    /// <summary>The game launcher the entry came through (Steam, EA app, Epic Games, GOG, Ubisoft Connect, Battle.net), empty when none.</summary>
    public string SourceLabel => UninstallEntryOrigin.Label(Entry) ?? string.Empty;

    /// <summary>The install folder shortened for the row; the whole path is the tooltip (<see cref="FolderPath"/>).</summary>
    public string FolderText => UninstallEntryOrigin.ShortFolder(Entry);

    public string FolderPath => Entry.Kind == UninstallEntryKind.Program ? Entry.InstallLocation : string.Empty;

    /// <summary>True while another visible row has the same name: the row then always shows where it comes from. Set by the owner.</summary>
    public bool SharesNameWithVisibleRow { get; private set; }

    /// <summary>The line under the details that tells look-alike entries apart: the install folder. Shown for an entry with a launcher, and for
    /// any entry that shares its name with another visible row; empty otherwise. (The launcher itself is part of <see cref="DetailText"/>.)</summary>
    public string OriginText => SourceLabel.Length > 0 || SharesNameWithVisibleRow ? FolderText : string.Empty;

    public bool HasOrigin => OriginText.Length > 0;

    /// <summary>The quiet second line: publisher, launcher and version, whichever the entry has, joined with a middle dot.</summary>
    public string DetailText => string.Join(" \u00B7 ", new[] { Publisher, SourceLabel, Version }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public bool HasDetail => DetailText.Length > 0;

    /// <summary>True for an uninstaller that opens its own window and cannot run unattended: the only method worth a tag on the row.</summary>
    public bool ShowsMethodTag => Listing.Plan.Method == UninstallMethod.Interactive;

    /// <summary>The hover on the row: why it cannot be ticked, otherwise how it is removed.</summary>
    public string RowTooltip => Selectable ? MethodText : UnavailableReason;

    internal void SetSharesNameWithVisibleRow(bool shares)
    {
        if (SharesNameWithVisibleRow == shares)
            return;

        SharesNameWithVisibleRow = shares;
        OnPropertyChanged(nameof(SharesNameWithVisibleRow));
        OnPropertyChanged(nameof(OriginText));
        OnPropertyChanged(nameof(HasOrigin));
    }

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

    /// <summary>True for a few seconds after a suggestion's "Show" pointed at this row, so the eye finds it in the list.</summary>
    [ObservableProperty]
    private bool isHighlighted;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !Selectable)
        {
            IsSelected = false;
            return;
        }

        _selectionChanged?.Invoke(this);
    }

    /// <summary>A row of the saved listing, not yet confirmed by the recheck: greyed, no checkbox.</summary>
    internal UninstallRowViewModel AsUnverified()
    {
        IsVerified = false;
        return this;
    }

    /// <summary>The recheck (or the full read) confirmed the row: it becomes selectable.</summary>
    internal void MarkVerified()
    {
        if (IsVerified)
            return;

        IsVerified = true;
        OnPropertyChanged(nameof(Selectable));
        OnPropertyChanged(nameof(UnavailableReason));
        OnPropertyChanged(nameof(RowTooltip));
    }

    /// <summary>Takes a fresh read's data over, keeping the row itself. A ticked row that stops being selectable is unticked.</summary>
    internal void Update(UninstallListing listing)
    {
        if (Equals(Listing, listing))
            return;

        // A changed icon source (a new DisplayIcon or folder after an update) means the icon is read again.
        if (!string.Equals(Entry.DisplayIcon, listing.Entry.DisplayIcon, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Entry.InstallLocation, listing.Entry.InstallLocation, StringComparison.OrdinalIgnoreCase))
        {
            IconRequested = false;
        }

        Listing = listing;
        foreach (var name in DerivedProperties)
            OnPropertyChanged(name);

        if (IsSelected && !Selectable)
            IsSelected = false;
    }

    private static readonly string[] DerivedProperties =
    [
        nameof(Listing), nameof(Entry), nameof(Name), nameof(KindSymbol), nameof(Publisher), nameof(Version), nameof(SizeBytes), nameof(InstallDate),
        nameof(SizeText), nameof(InstalledText), nameof(RiskLevel), nameof(Selectable), nameof(IsRisky), nameof(RiskLevelText),
        nameof(ReasonText), nameof(RiskTooltip), nameof(MethodText), nameof(NoteText), nameof(HasNote), nameof(UnavailableReason), nameof(SortName),
        nameof(SourceLabel), nameof(FolderText), nameof(FolderPath), nameof(OriginText), nameof(HasOrigin),
        nameof(DetailText), nameof(HasDetail), nameof(ShowsMethodTag), nameof(RowTooltip),
    ];

    /// <summary>Re-reads every localized text after a language change.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(RiskLevelText));
        OnPropertyChanged(nameof(ReasonText));
        OnPropertyChanged(nameof(RiskTooltip));
        OnPropertyChanged(nameof(MethodText));
        OnPropertyChanged(nameof(NoteText));
        OnPropertyChanged(nameof(UnavailableReason));
        OnPropertyChanged(nameof(RowTooltip));
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

    /// <summary>The note under a row: why the plan refused it when that is worth saying (a driver in use, an update Windows does not let go), or that
    /// a Steam game's folder is empty. Empty otherwise (the method column already says "cannot remove").</summary>
    internal static string Note(UninstallPlan plan, InstalledProgramEntry entry) => plan.Refusal switch
    {
        UninstallRefusal.DriverInUse => string.Format(
            LocalizationService.Instance["UninstallerDriverInUse"], entry.InUseBy),
        UninstallRefusal.UpdateNotRemovable => LocalizationService.Instance["UninstallerUpdateKept"],
        _ => entry.InstallFolderEmpty ? LocalizationService.Instance["UninstallerSteamFolderEmpty"] : string.Empty,
    };

    /// <summary>Why a row cannot be ticked: not confirmed yet, the risk rating bars it (this app), the plan refused it (a driver in use, an update Windows keeps), or
    /// there is simply no way to remove it. Empty when the row is selectable.</summary>
    internal static string Unavailable(UninstallRowViewModel row)
    {
        if (row.Selectable)
            return string.Empty;

        var loc = LocalizationService.Instance;
        if (!row.IsVerified)
            return loc["UninstallerUnavailableChecking"];

        if (!row.Listing.Risk.Selectable)
            return row.ReasonText;

        return row.HasNote ? row.NoteText : loc["UninstallerUnavailableNoMethod"];
    }

    internal static string MethodKey(UninstallPlan plan) => plan.Method switch
    {
        UninstallMethod.Interactive => "UninstallerMethodOwnWindow",
        UninstallMethod.Refused => "UninstallerMethodCannot",
        _ => plan.IsRunnable ? "UninstallerMethodSilent" : "UninstallerMethodCannot",
    };
}
