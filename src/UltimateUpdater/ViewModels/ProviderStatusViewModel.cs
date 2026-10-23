using CommunityToolkit.Mvvm.ComponentModel;
using UltimateUpdater.Models;
using UltimateUpdater.Services;

namespace UltimateUpdater.ViewModels;

public partial class ProviderStatusViewModel : ObservableObject
{
    public string SourceId { get; }

    [ObservableProperty]
    private ProviderScanState state;

    [ObservableProperty]
    private int foundCount;

    [ObservableProperty]
    private string? errorMessage;

    /// <summary>Raw, unlocalized reason behind <see cref="ErrorMessage"/>, the pill's hover tooltip only.
    /// Never shown inline next to the pill itself.</summary>
    [ObservableProperty]
    private string? detail;

    /// <summary>
    /// Whether the user has this source switched on. Clicking its pill flips it: a switched-off source
    /// is filtered out of the table at once and skipped on the next scan. The pill stays (dimmed) so it
    /// can be switched back on.
    /// </summary>
    [ObservableProperty]
    private bool isEnabled = true;

    private readonly string _groupKey;

    /// <summary>Source ids are lowercase technical names; every surface shows the readable form.</summary>
    public string DisplayName => Services.SourceNames.Display(SourceId);

    /// <summary>
    /// The pill's category group and its order, mirroring the table so the pills at the bottom read the
    /// same way: Software, Store, System &amp; drivers, Developer tools, Games, then the "open it
    /// yourself" store and launcher rows. Fixed at construction from the source's category and tier.
    /// </summary>
    public int GroupOrder { get; }

    /// <summary>Read live, so a language change is reflected the next time the pills are regrouped.</summary>
    public string GroupName => LocalizationService.Instance[_groupKey];

    private static (int Order, string Key) ComputeGroup(UpdateCategory category, SourceTier tier)
    {
        // A Tier C store or launcher is grouped by what it IS, not merely by its raw category.
        if (tier == SourceTier.C && category == UpdateCategory.Store)
            return (6, "CategoryOpenStore");
        if (tier == SourceTier.C && category == UpdateCategory.Games)
            return (7, "CategoryOpenGames");

        return category switch
        {
            UpdateCategory.Software => (0, "CategorySoftware"),
            UpdateCategory.Store => (1, "CategoryStore"),
            UpdateCategory.SystemAndDrivers => (2, "CategorySystemAndDrivers"),
            UpdateCategory.DeveloperTools => (3, "CategoryDeveloperTools"),
            UpdateCategory.Games => (4, "CategoryGames"),
            _ => (8, "CategorySoftware"),
        };
    }

    public bool HasFindings => FoundCount > 0;

    public bool HasError => State == ProviderScanState.Error;

    /// <summary>What the pill's tooltip shows: the translated one-liner, plus the raw reason underneath
    /// it where one is known, the hover IS the deliberate "tell me more" the app otherwise gates raw
    /// tool output behind (SECURITY.md). A copy hint is appended whenever there is a raw reason to copy.</summary>
    public string? TooltipText => Detail is { Length: > 0 }
        ? $"{ErrorMessage}\n{Detail}\n\n{LocalizationService.Instance["TooltipCopyHint"]}"
        : ErrorMessage;

    /// <summary>What right-clicking the pill copies to the clipboard, the raw reason alone, not the
    /// translated summary or the copy hint text.</summary>
    public string? CopyableErrorText => Detail is { Length: > 0 } ? Detail : ErrorMessage;

    /// <summary>Dimmed when switched off, so a pill reads as a toggle rather than plain status.</summary>
    public double PillOpacity => IsEnabled ? 1.0 : 0.4;

    /// <summary>A pill is worth showing if it found something, failed, is working, was deliberately
    /// stopped by its own cancel button (that outcome should stay visible, the same as an
    /// error would), or is switched off. Waiting pills are NOT included: every source has left Waiting
    /// by the time a scan ends, so that state only ever matters while <see cref="MainViewModel.IsScanning"/>
    /// is doing the rest of the showing, in the PillView filter itself.</summary>
    public bool IsNoteworthy =>
        HasFindings || HasError || State is ProviderScanState.Scanning or ProviderScanState.Cancelled || !IsEnabled;

    public bool IsCancellable => State == ProviderScanState.Scanning;

    /// <summary>First sort key while scanning: running pills float to the top. Once nothing is
    /// scanning any more this is the same value for everyone and the category order underneath it is all
    /// that is left, so the view settles back to its quiet state on its own.</summary>
    public int RunningRank => State == ProviderScanState.Scanning ? 0 : 1;

    /// <summary>The short state word next to a pill's name while it has no result yet (waiting for its
    /// turn, actively scanning), after a deliberate single-source cancel, or once it failed. A finished
    /// pill already says so with its badge and needs no extra word.</summary>
    public string? StateText => State switch
    {
        ProviderScanState.Waiting => LocalizationService.Instance["ScanStateWaiting"],
        ProviderScanState.Scanning => LocalizationService.Instance[IsSlow ? "ScanStateSlow" : "ScanStateRunning"],
        ProviderScanState.Cancelled => LocalizationService.Instance["ScanStateCancelled"],
        ProviderScanState.Error => LocalizationService.Instance["ScanStateError"],
        _ => null,
    };

    public bool HasStateText => StateText is not null;

    private DateTimeOffset? _scanningSince;

    private static readonly TimeSpan SlowThreshold = TimeSpan.FromSeconds(30);

    /// <summary>Marked "slow" after <see cref="SlowThreshold"/>, long enough that a normal source is
    /// nowhere near it, short enough to still mean something while the scan is running.</summary>
    public bool IsSlow { get; private set; }

    /// <summary>
    /// Called on MainViewModel's existing 200ms flush tick for every currently-scanning pill, cheap
    /// (one subtraction and a comparison), and reuses a timer that already runs during every scan instead
    /// of adding a second one just for this.
    /// </summary>
    public void RefreshSlowState(DateTimeOffset now)
    {
        var isSlowNow = State == ProviderScanState.Scanning
            && _scanningSince is { } since && now - since >= SlowThreshold;

        if (isSlowNow == IsSlow)
            return;

        IsSlow = isSlowNow;
        OnPropertyChanged(nameof(IsSlow));
        OnPropertyChanged(nameof(StateText));
    }

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(PillOpacity));
        OnPropertyChanged(nameof(IsNoteworthy));
    }

    /// <summary>The language changed: both labels are read from the resources in code, not through a binding.</summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(GroupName));
        // TooltipText carries a translated "copy hint" suffix too, so it must be re-raised here as
        // well or a pill's error tooltip stays half in the old language after a language switch.
        OnPropertyChanged(nameof(TooltipText));
        // An Error or Cancelled pill keeps its state word after the scan, so it has to follow too.
        OnPropertyChanged(nameof(StateText));
    }

    /// <summary>What a screen reader announces for the pill, without it, the class name is read out.</summary>
    public override string ToString() => DisplayName;

    public ProviderStatusViewModel(
        string sourceId, UpdateCategory category = UpdateCategory.Software, SourceTier tier = SourceTier.A)
    {
        SourceId = sourceId;
        var (order, key) = ComputeGroup(category, tier);
        GroupOrder = order;
        _groupKey = key;
    }

    /// <summary>Tracks when this pill last became <see cref="ProviderScanState.Scanning"/>, for
    /// <see cref="RefreshSlowState"/>, reset on every OTHER state so a source that finishes and starts
    /// scanning again (a second source sharing its category aggregate) is timed fresh, not against
    /// whenever it first started.</summary>
    partial void OnStateChanged(ProviderScanState value)
    {
        if (value == ProviderScanState.Scanning)
        {
            _scanningSince = DateTimeOffset.UtcNow;
        }
        else
        {
            _scanningSince = null;
            IsSlow = false;
        }

        OnPropertyChanged(nameof(IsCancellable));
        OnPropertyChanged(nameof(RunningRank));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HasStateText));
    }

    public void Apply(ProviderStatus status)
    {
        State = status.State;
        FoundCount = status.FoundCount;
        ErrorMessage = status.ErrorMessage;
        Detail = status.Detail;

        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsNoteworthy));
        OnPropertyChanged(nameof(TooltipText));
    }
}
