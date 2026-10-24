using CommunityToolkit.Mvvm.ComponentModel;
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
    }

    public string ComponentStoreRecommendedText => Loc["CleanupComponentStoreRecommended"];

    /// <summary>Whether a result (however incomplete) has come back at least once; the lines below the
    /// description depend on this.</summary>
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
    /// Starts the analysis on its own, but only where it costs nothing to ask: the process is elevated or
    /// the app-wide helper already runs. Never raises a prompt and never starts the helper (a helper that
    /// dropped out a moment ago just means no analysis now). A result already present is not measured
    /// again, and one in flight is not doubled.
    /// </summary>
    private void StartQuietComponentStoreAnalysis()
    {
        if (ComponentStoreResult is not null || IsAnalyzingComponentStore || !HasElevationAvailable())
            return;

        _ = AnalyzeComponentStoreAsync();
    }

    /// <summary>
    /// Runs DISM's own AnalyzeComponentStore inside the elevated cleanup job: a "cleanup" job on the pipe
    /// exactly like a scan or a clean, carrying no delete targets at all, so the worker path, its
    /// validation and the one-lane-per-job-kind rule never see anything new. Quiet: no prompt, no helper
    /// launch, and a failure is only logged. Non-exclusive on the elevated pipe (see
    /// <see cref="RunElevatedAsync"/>'s own "analyze" case): this only ever reads, so a running
    /// file/registry cleanup must never make it queue silently behind it.
    /// </summary>
    private async Task AnalyzeComponentStoreAsync()
    {
        IsAnalyzingComponentStore = true;
        try
        {
            if (!HasElevatedHelperForComponentStore())
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
                allowStart: false);

            // The worker session itself could not be reached at all (never started, or died mid-call): a
            // silent fallback to Unknown here would show the same quiet dashes a genuinely empty DISM run
            // shows, with no way to tell the two apart.
            if (!completed)
            {
                _logging.Info("Component store analysis skipped: the elevated helper was not reachable.");
                return;
            }

            ComponentStoreResult = result ?? ComponentStoreAnalysis.Unknown;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Runs fire-and-forget; an escaping exception would reach the process-wide handler.
            _logging.Warn($"Component store analysis failed: {ex.Message}");
        }
        finally
        {
            IsAnalyzingComponentStore = false;
        }
    }

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

    /// <summary>Whether the analysis can reach an elevated session without asking anything: the process
    /// itself is elevated, or the app-wide helper already runs.</summary>
    private bool HasElevatedHelperForComponentStore() =>
        _elevated is not null && (ElevationService.IsElevated || _elevated.IsRunning);
}
