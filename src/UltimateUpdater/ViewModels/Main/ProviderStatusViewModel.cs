using CommunityToolkit.Mvvm.ComponentModel;
using UltimateUpdater.Models;

namespace UltimateUpdater.ViewModels.Main;

public partial class ProviderStatusViewModel : ObservableObject
{
    public string SourceId { get; }

    [ObservableProperty]
    private ProviderScanState state;

    [ObservableProperty]
    private int foundCount;

    /// <summary>The number on the pill's chip: this source's rows that still offer an update, so rows that finished installing stop counting. <see cref="FoundCount"/> stays what the scan found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenUpdates))]
    private int openCount;

    [ObservableProperty]
    private string? errorMessage;

    /// <summary>Raw, unlocalized reason behind <see cref="ErrorMessage"/>, for the pill's hover tooltip only.</summary>
    [ObservableProperty]
    private string? detail;

    /// <summary>A caveat about a finished scan (not an error), shown under the tooltip text.</summary>
    [ObservableProperty]
    private string? note;

    /// <summary>Percent of a setup download running for this source (winget being installed), null when none. While set, the pill says so and stays visible.</summary>
    [ObservableProperty]
    private int? setupPercent;

    partial void OnSetupPercentChanged(int? value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HasStateText));
        OnPropertyChanged(nameof(IsNoteworthy));
    }

    /// <summary>Whether the user has this source switched on. Clicking its pill flips it: a switched-off source is filtered out of the table and skipped on the next scan. The pill stays, dimmed, so it can be switched back on.</summary>
    [ObservableProperty]
    private bool isEnabled = true;

    private readonly string _groupKey;

    /// <summary>Source ids are lowercase technical names; every surface shows the readable form.</summary>
    public string DisplayName => SourceNames.Display(SourceId);

    /// <summary>The pill's category group and order, mirroring the table: Software, Store, System and drivers, Developer tools, Games, then the "open it yourself" store and launcher rows. Fixed at construction.</summary>
    public int GroupOrder { get; }

    /// <summary>Read live, so a language change is reflected the next time the pills are regrouped.</summary>
    public string GroupName => LocalizationService.Instance[_groupKey];

    private static (int Order, string Key) ComputeGroup(UpdateCategory category, SourceTier tier)
    {
        // A Tier C store or launcher is grouped by what it is, not by its raw category.
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

    public bool HasOpenUpdates => OpenCount > 0;

    public bool HasError => State == ProviderScanState.Error;

    /// <summary>The tooltip: the translated one-liner plus the raw reason underneath where one is known (the hover is the deliberate "tell me more" that raw tool output is gated behind), and a copy hint when there is a raw reason to copy.</summary>
    public string? TooltipText
    {
        get
        {
            var text = Detail is { Length: > 0 }
                ? $"{ErrorMessage}\n{Detail}\n\n{LocalizationService.Instance["TooltipCopyHint"]}"
                : ErrorMessage;

            if (Note is not { Length: > 0 })
                return text;

            return string.IsNullOrEmpty(text) ? Note : $"{text}\n\n{Note}";
        }
    }

    /// <summary>What right-clicking the pill copies: the raw reason alone, without the summary or the hint.</summary>
    public string? CopyableErrorText => Detail is { Length: > 0 } ? Detail : ErrorMessage;

    /// <summary>Dimmed when switched off, so a pill reads as a toggle rather than plain status.</summary>
    public double PillOpacity => IsEnabled ? 1.0 : 0.4;

    /// <summary>A pill is worth showing if it found something, failed, is working, was stopped by its own cancel button, or is switched off. Waiting pills are not included: every source has left Waiting when a scan ends, and while scanning the PillView filter shows them.</summary>
    public bool IsNoteworthy =>
        HasFindings || HasError || State is ProviderScanState.Scanning or ProviderScanState.Cancelled || !IsEnabled
        || Note is { Length: > 0 } || SetupPercent is not null;

    public bool IsCancellable => State == ProviderScanState.Scanning;

    /// <summary>First sort key while scanning: running pills float to the top. Once nothing is scanning it is equal for everyone and the category order remains.</summary>
    public int RunningRank => State == ProviderScanState.Scanning ? 0 : 1;

    /// <summary>The short state word beside the name while the pill has no result yet, after a single-source cancel, or once it failed. A finished pill needs no extra word.</summary>
    public string? StateText
    {
        get
        {
            if (SetupPercent is { } percent)
                return string.Format(LocalizationService.Instance["WingetBootstrapDownloading"], percent);

            return State switch
            {
                ProviderScanState.Waiting => LocalizationService.Instance["ScanStateWaiting"],
                ProviderScanState.Scanning => LocalizationService.Instance[IsSlow ? "ScanStateSlow" : "ScanStateRunning"],
                ProviderScanState.Cancelled => LocalizationService.Instance["ScanStateCancelled"],
                ProviderScanState.Error => LocalizationService.Instance["ScanStateError"],
                _ => null,
            };
        }
    }

    public bool HasStateText => StateText is not null;

    private DateTimeOffset? _scanningSince;

    private static readonly TimeSpan SlowThreshold = TimeSpan.FromSeconds(30);

    /// <summary>Marked "slow" after <see cref="SlowThreshold"/>: long enough that a normal source is nowhere near it, short enough to mean something during a scan.</summary>
    public bool IsSlow { get; private set; }

    /// <summary>Called on the existing 200 ms flush tick for every scanning pill; cheap, and reuses a running timer instead of adding one.</summary>
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
        // TooltipText carries a translated copy-hint suffix, so it is re-raised too.
        OnPropertyChanged(nameof(TooltipText));
        // An Error or Cancelled pill keeps its state word after the scan, so it follows too.
        OnPropertyChanged(nameof(StateText));
    }

    /// <summary>What a screen reader announces for the pill; without it the class name is read.</summary>
    public override string ToString() => DisplayName;

    public ProviderStatusViewModel(
        string sourceId, UpdateCategory category = UpdateCategory.Software, SourceTier tier = SourceTier.A)
    {
        SourceId = sourceId;
        var (order, key) = ComputeGroup(category, tier);
        GroupOrder = order;
        _groupKey = key;
    }

    /// <summary>Tracks when the pill last became <see cref="ProviderScanState.Scanning"/>, for <see cref="RefreshSlowState"/>. Reset on every other state, so a source that scans again is timed fresh.</summary>
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
        Note = status.Note;

        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsNoteworthy));
        OnPropertyChanged(nameof(TooltipText));
    }
}
