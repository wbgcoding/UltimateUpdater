using CommunityToolkit.Mvvm.ComponentModel;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels.Cleanup;

// A read-only card showing what DISM's AnalyzeComponentStore reports about the WinSxS component store. Nothing is deleted from here; Windows Disk Cleanup stays the only way to act on it.
public partial class CleanupViewModel
{
    [ObservableProperty]
    private bool isAnalyzingComponentStore;

    /// <summary>Null before the first analysis of this session; each field is null when an analysis ran but a line could not be read.</summary>
    [ObservableProperty]
    private ComponentStoreAnalysis? componentStoreResult;

    partial void OnComponentStoreResultChanged(ComponentStoreAnalysis? value)
    {
        OnPropertyChanged(nameof(HasComponentStoreResult));
        OnPropertyChanged(nameof(ComponentStoreSizeText));
        OnPropertyChanged(nameof(ShowComponentStoreRecommended));
    }

    partial void OnIsAnalyzingComponentStoreChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBusy));
    }

    public string ComponentStoreRecommendedText => Loc["CleanupComponentStoreRecommended"];

    /// <summary>Whether a result (however incomplete) has come back; an analysis DISM never finished in time counts as none, since a slow Windows is no failure of this app.</summary>
    public bool HasComponentStoreResult => ComponentStoreResult is { TimedOut: false };

    /// <summary>"{0} on disk, {1} shared with Windows". A half DISM did not report falls back to a dash, never a guess; when neither was reported the line becomes a sentence instead of two dashes, which would read like a parse failure.</summary>
    public string ComponentStoreSizeText =>
        ComponentStoreResult is { ActualSizeBytes: null, SharedWithWindowsBytes: null }
            ? Loc["CleanupComponentStoreNoNumbers"]
            : string.Format(
                Loc["CleanupComponentStoreSize"],
                ComponentStoreResult?.ActualSizeBytes is { } actual ? ByteSize.Format(actual) : "-",
                ComponentStoreResult?.SharedWithWindowsBytes is { } shared ? ByteSize.Format(shared) : "-");

    public bool ShowComponentStoreRecommended => ComponentStoreResult?.CleanupRecommended == true;

    /// <summary>Starts the analysis only where it costs nothing to ask: the process is elevated or the helper already runs. Never prompts and never starts the helper. A present result is not measured again and one in flight is not doubled.</summary>
    private void StartQuietComponentStoreAnalysis()
    {
        if (ComponentStoreResult is not null || IsAnalyzingComponentStore || !HasElevationAvailable())
            return;

        _ = AnalyzeComponentStoreAsync();
    }

    /// <summary>Runs DISM's AnalyzeComponentStore inside the elevated cleanup job: a "cleanup" job carrying no delete targets, so the worker path and its validation see nothing new. Quiet: no prompt, a failure is only logged. Non-exclusive on the pipe (see <see cref="RunElevatedAsync"/>'s "analyze" case) so a running cleanup never queues it silently.</summary>
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
                allowStart: MayStartHelperQuietly(ElevationService.IsElevated));

            // The worker session could not be reached (never started, or died mid-call): a silent fallback to Unknown would show the same dashes as a genuinely empty DISM run.
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

    /// <summary>Pure mapping from one "componentStore" worker message to the analysis it carries; separate from <see cref="AnalyzeComponentStoreAsync"/> so it is testable with a fabricated message.</summary>
    internal static ComponentStoreAnalysis ApplyComponentStoreMessage(CleanupWorkerMessage message) => new(
        message.ComponentStoreActualSizeBytes,
        message.ComponentStoreSharedWithWindowsBytes,
        message.ComponentStoreBackupsBytes,
        message.ComponentStoreCleanupRecommended,
        message.ComponentStoreTimedOut);

    /// <summary>An app started with administrator rights still measures through the helper, and starting it raises no prompt.</summary>
    internal static bool MayStartHelperQuietly(bool processElevated) => processElevated;

    /// <summary>Whether the analysis can reach an elevated session without asking: the process is elevated or the helper already runs.</summary>
    private bool HasElevatedHelperForComponentStore() =>
        _elevated is not null && (ElevationService.IsElevated || _elevated.IsRunning);
}
