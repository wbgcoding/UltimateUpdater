using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

// A read-only card showing what DISM's own AnalyzeComponentStore reports about the WinSxS
// component store. No detail list, no selection, nothing this dialog deletes from here; the existing
// Windows Disk Cleanup entry stays the only way to act on what it says.
public partial class CleanupViewModel
{
    [ObservableProperty]
    private bool isAnalyzingComponentStore;

    /// <summary>Null before the first analysis of this session; every field of the record is
    /// independently null after one that ran but could not read (or understand) a given line.</summary>
    [ObservableProperty]
    private ComponentStoreAnalysis? componentStoreResult;

    /// <summary>Null while nothing has gone wrong. Set only when a press could not even get the
    /// analysis started (the elevated helper is not running and could not be, or the user declined the
    /// UAC prompt just now); a real DISM run's own numbers, or lack of them, never touch this - that is
    /// <see cref="ComponentStoreSizeText"/>'s job (and <c>CleanupComponentStoreNoNumbers</c>). Kept as a
    /// resource key and resolved on every read, so a language switch only has to re-raise it.</summary>
    public string? ComponentStoreStatusText => _componentStoreStatusKey is null ? null : LocalizationService.Instance[_componentStoreStatusKey];

    private string? _componentStoreStatusKey;

    private void SetComponentStoreStatus(string? key)
    {
        _componentStoreStatusKey = key;
        OnPropertyChanged(nameof(ComponentStoreStatusText));
        OnPropertyChanged(nameof(HasComponentStoreStatus));
    }

    public bool HasComponentStoreStatus => _componentStoreStatusKey is not null;

    partial void OnComponentStoreResultChanged(ComponentStoreAnalysis? value)
    {
        OnPropertyChanged(nameof(HasComponentStoreResult));
        OnPropertyChanged(nameof(ComponentStoreSizeText));
        OnPropertyChanged(nameof(ShowComponentStoreRecommended));
        OnPropertyChanged(nameof(ShowComponentStoreTimedOut));
    }

    partial void OnIsAnalyzingComponentStoreChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
        AnalyzeComponentStoreCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ComponentStoreAnalyzeButtonText));
    }

    /// <summary>Switches to a "running" wording while the DISM call is in flight, since the button
    /// disabling itself alone gave no visible sign that anything had started at all.</summary>
    public string ComponentStoreAnalyzeButtonText =>
        Loc[IsAnalyzingComponentStore ? "CleanupComponentStoreAnalyzing" : "CleanupComponentStoreAnalyze"];

    public string ComponentStoreAnalyzeHintText => Loc["CleanupComponentStoreAnalyzeHint"];

    public string ComponentStoreRecommendedText => Loc["CleanupComponentStoreRecommended"];

    /// <summary>Whether a result (however incomplete) has come back at least once; the button keeps
    /// reading "needs administrator rights" either way, only the numbers below it depend on this.</summary>
    public bool HasComponentStoreResult => ComponentStoreResult is not null;

    /// <summary>"{0} on disk, {1} shared with Windows"; either half falls back to a dash when DISM's own
    /// output did not carry that one field, never a guess. When NEITHER carried, a run that
    /// genuinely reported no sizes at all (rather than one this parser merely failed to read), the whole
    /// line becomes a sentence instead of two dashes, which would read like a parsing failure.</summary>
    public string ComponentStoreSizeText =>
        ComponentStoreResult is { ActualSizeBytes: null, SharedWithWindowsBytes: null }
            ? Loc["CleanupComponentStoreNoNumbers"]
            : string.Format(
                Loc["CleanupComponentStoreSize"],
                ComponentStoreResult?.ActualSizeBytes is { } actual ? ByteSize.Format(actual) : "-",
                ComponentStoreResult?.SharedWithWindowsBytes is { } shared ? ByteSize.Format(shared) : "-");

    public bool ShowComponentStoreRecommended => ComponentStoreResult?.CleanupRecommended == true;

    /// <summary>DISM was killed after its own 5-minute deadline rather than answering; distinct from a
    /// completed run that simply had nothing to report (<see cref="ComponentStoreSizeText"/>'s
    /// "CleanupComponentStoreNoNumbers" case), which is not an error at all.</summary>
    public bool ShowComponentStoreTimedOut => ComponentStoreResult?.TimedOut == true;

    public string ComponentStoreTimedOutText => Loc["CleanupComponentStoreTimedOut"];

    /// <summary>
    /// Runs DISM's own AnalyzeComponentStore inside the elevated cleanup job: a "cleanup" job on
    /// the pipe exactly like a scan or a clean, carrying no delete targets at all, so the worker path, its
    /// validation and the one-lane-per-job-kind rule never see anything new. Asks for administrator rights
    /// first when nothing is elevated yet; declining leaves the card exactly as it was, a normal answer, not
    /// an error. Non-exclusive on the elevated pipe (see <see cref="RunElevatedAsync"/>'s own "analyze"
    /// case): this only ever reads, so a running file/registry cleanup must never make this queue silently
    /// behind it with no visible reason (a button that stays disabled with no state text looks
    /// exactly like a hang).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAnalyzeComponentStore))]
    private Task AnalyzeComponentStoreAsync() => AnalyzeComponentStoreCoreAsync(quiet: false);

    /// <summary>
    /// Starts the analysis on its own, without a click, but only where it costs nothing to ask: the process
    /// is elevated or the app-wide helper already runs. Never raises a prompt and never starts the helper
    /// (a helper that dropped out a moment ago just means no analysis now). A result already present is not
    /// measured again (the button still re-runs it), and one in flight is not doubled.
    /// </summary>
    private void StartQuietComponentStoreAnalysis()
    {
        if (ComponentStoreResult is not null || IsAnalyzingComponentStore || !HasElevationAvailable())
            return;

        _ = AnalyzeComponentStoreCoreAsync(quiet: true);
    }

    /// <param name="quiet">True for the automatic start: no prompt, no helper launch, and a failure is only
    /// logged, not shown on the card.</param>
    private async Task AnalyzeComponentStoreCoreAsync(bool quiet)
    {
        IsAnalyzingComponentStore = true;
        SetComponentStoreStatus(null);
        try
        {
            if (!await EnsureElevationForComponentStoreAsync(CancellationToken.None, quiet))
                return;

            ComponentStoreAnalysis? result = null;
            var completed = await RunElevatedAsync(
                "analyze",
                [],
                message =>
                {
                    if (message.Type == "componentStore")
                        result = ApplyComponentStoreMessage(message);
                },
                CancellationToken.None,
                allowStart: !quiet);

            // The worker session itself could not be reached at all (never started, or died mid-call): a
            // silent fallback to Unknown here would show the same quiet dashes a genuinely empty DISM run
            // shows, with no way to tell the two apart.
            if (!completed)
            {
                if (!quiet)
                    SetComponentStoreStatus("CleanupComponentStoreUnavailable");
                else
                    _logging.Info("Component store analysis skipped: the elevated helper was not reachable.");
                return;
            }

            ComponentStoreResult = result ?? ComponentStoreAnalysis.Unknown;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The command runs fire-and-forget from the button; an escaping exception (a worker launch that
            // failed for a reason other than the declined prompt, say) would reach the process-wide handler.
            _logging.Warn($"Component store analysis failed: {ex.Message}");
            if (!quiet)
                SetComponentStoreStatus("CleanupComponentStoreUnavailable");
        }
        finally
        {
            IsAnalyzingComponentStore = false;
        }
    }

    private bool CanAnalyzeComponentStore() => !IsAnalyzingComponentStore;

    /// <summary>
    /// Pure mapping from one "componentStore" worker message to the analysis it carries; kept
    /// apart from <see cref="AnalyzeComponentStoreAsync"/> so the mapping is directly testable with a
    /// fabricated message, no pipe or running worker needed.
    /// </summary>
    internal static ComponentStoreAnalysis ApplyComponentStoreMessage(CleanupWorkerMessage message) => new(
        message.ComponentStoreActualSizeBytes,
        message.ComponentStoreSharedWithWindowsBytes,
        message.ComponentStoreBackupsBytes,
        message.ComponentStoreCleanupRecommended,
        message.ComponentStoreTimedOut);

    /// <summary>
    /// Asks for administrator rights before the one DISM call this card ever makes. Mirrors
    /// <see cref="EnsureElevationAsync"/> but unconditionally: an analysis needs admin every single time,
    /// there is no unelevated fallback for it to fall back to. A press ALWAYS tries to get the
    /// rights again, even if a previous press was declined - nothing here remembers a refusal - and only
    /// when this second attempt still fails does it leave a status line behind instead of returning quietly.
    /// </summary>
    private async Task<bool> EnsureElevationForComponentStoreAsync(CancellationToken cancellationToken, bool quiet = false)
    {
        if (_elevated is null)
        {
            if (!quiet)
                SetComponentStoreStatus("CleanupComponentStoreUnavailable");
            return false;
        }

        if (ElevationService.IsElevated)
            return true;

        // The automatic start asks nothing: no helper running means no analysis now.
        if (quiet)
            return _elevated.IsRunning;

        var force = false;
        if (!_elevated.IsRunning)
        {
            var accepted = Dialogs.Confirm(
                Loc["CleanupAdminHeadline"],
                Loc["CleanupAdminBody"],
                confirmText: Loc["CleanupAdminConfirm"],
                cancelText: Loc["CleanupAdminDecline"]);

            if (!accepted)
            {
                SetComponentStoreStatus("CleanupComponentStoreNeedsAdmin");
                return false;
            }

            force = true;
        }

        if (await _elevated.EnsureStartedAsync(cancellationToken, force))
            return true;

        SetComponentStoreStatus("CleanupComponentStoreUnavailable");
        return false;
    }
}
