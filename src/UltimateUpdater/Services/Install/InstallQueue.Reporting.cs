using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services.Install;

public sealed partial class InstallQueue
{
    private async Task FinishAfterVersionRecheckAsync(
        IUpdateSource source, InstallProgressMessage message, UpdateItem item, InstallAction action,
        ISet<string> reported, RunState run, string key, CancellationTokenSource recheckCts)
    {
        try
        {
            message.VersionUnchanged = await StillUnchangedAfterWaitAsync(
                source, item, item.InstalledVersion, _logging, VersionRecheckInterval, VersionRecheckAttempts, recheckCts.Token);
        }
        catch (Exception ex)
        {
            _logging.Warn($"Delayed version re-check for '{item.Id}' failed: {ex.Message}");
        }
        finally
        {
            // Under the lock CancelItem cancels under, so a stop click never reaches a disposed source.
            ReleaseItemCancellation(run, key, recheckCts);
        }

        ApplyWorkerResult(message, item, action, reported, run, awaitsRetry: false, terminalNonFailure: false);
    }

    /// <summary>The re-check decision, shared by the in-process path and <see cref="InstallWorker"/>.</summary>
    internal static async Task<bool> IsVersionUnchangedAsync(
        IUpdateSource source, UpdateItem item, string? previousInstalledVersion, LoggingService logging, CancellationToken cancellationToken)
    {
        try
        {
            var recheck = await source.GetInstalledVersionAsync(item, cancellationToken);
            return IsVersionUnchanged(previousInstalledVersion, recheck);
        }
        catch (OperationCanceledException)
        {
            // The install itself already finished: a stop during this optional re-check only skips the note.
            // Thrown on, it would turn a completed upgrade into "Cancelled", and that success is what a later
            // rollback is built from.
            return false;
        }
        catch (Exception ex)
        {
            // Same for a lookup that fails afterwards (a refused tool path, a timeout): the re-check only decides
            // whether a note is added.
            logging.Warn($"Version re-check for '{item.Id}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The immediate re-check, then (only when it reads "unchanged") a few more lookups spaced
    /// <paramref name="interval"/> apart. A changed value at any lookup is a normal success.
    /// </summary>
    internal static async Task<bool> IsVersionUnchangedAfterWaitAsync(
        IUpdateSource source, UpdateItem item, string? previousInstalledVersion, LoggingService logging,
        TimeSpan interval, int attempts, CancellationToken cancellationToken)
    {
        if (!await IsVersionUnchangedAsync(source, item, previousInstalledVersion, logging, cancellationToken))
            return false;

        return await StillUnchangedAfterWaitAsync(source, item, previousInstalledVersion, logging, interval, attempts, cancellationToken);
    }

    /// <summary>
    /// The delayed part alone: true only when no lookup within the window showed a different version.
    /// A stop ends the wait quietly with false (the install itself succeeded, so no note is added); a lookup
    /// that fails or has no answer simply counts as "no change seen yet".
    /// </summary>
    internal static async Task<bool> StillUnchangedAfterWaitAsync(
        IUpdateSource source, UpdateItem item, string? previousInstalledVersion, LoggingService logging,
        TimeSpan interval, int attempts, CancellationToken cancellationToken)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                await Task.Delay(interval, cancellationToken);
                var recheck = await source.GetInstalledVersionAsync(item, cancellationToken);
                if (recheck is not null && !IsVersionUnchanged(previousInstalledVersion, recheck))
                    return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                logging.Warn($"Delayed version re-check for '{item.Id}' failed: {ex.Message}");
            }
        }

        return true;
    }

    /// <summary>
    /// The re-check comparison, shared with <see cref="InstallWorker"/> (the elevated path). Null from the
    /// source means "no statement", never "unchanged": only an actual match against the pre-install value
    /// counts.
    /// </summary>
    internal static bool IsVersionUnchanged(string? previousInstalledVersion, string? recheckedVersion) =>
        recheckedVersion is not null && string.Equals(recheckedVersion, previousInstalledVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>This result's place in the item's run of "installed, version unchanged" outcomes: the earlier
    /// ones for the same versions already in the history, plus this one (not recorded yet).</summary>
    private int CountUnchanged(UpdateItem item)
    {
        try
        {
            return _history.CountUnchangedInstalls(item.SourceId, item.Id, item.InstalledVersion, item.AvailableVersion) + 1;
        }
        catch (Exception ex)
        {
            _logging.Warn($"Could not count earlier unchanged installs of '{item.Id}': {ex.Message}");
            return 1;
        }
    }

    /// <summary>First-round failures of rows waiting for the automatic retry, not yet in the history: the
    /// second attempt's result is the row's one entry, so this one is written only when no later entry
    /// replaces it (see <see cref="RecordHistory"/> and <see cref="FlushDeferredFailure"/>).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Error, string? Detail)> _deferredFailures =
        new(StringComparer.OrdinalIgnoreCase);

    private void DeferFailure(UpdateItem item, string? error, string? detail) =>
        _deferredFailures[Key(item)] = (error, detail);

    /// <summary>Writes the held first failure of a row whose retry never produced an entry of its own
    /// (the batch ended first, or the row was taken out of the queue). Returns whether one was held.</summary>
    private bool FlushDeferredFailure(UpdateItem item, InstallAction action)
    {
        if (!_deferredFailures.TryRemove(Key(item), out var failure))
            return false;

        RecordHistory(item, action, success: false, failure.Error, detail: failure.Detail);
        return true;
    }

    private void RecordHistory(
        UpdateItem item, InstallAction action, bool success, string? error, bool isManualHandoff = false,
        bool isNotApplicable = false, string? detail = null, bool isCancelled = false, bool versionUnchanged = false)
    {
        // Any entry for the row supersedes the first failure the retry round was holding back.
        _deferredFailures.TryRemove(Key(item), out _);
        var entry = HistoryEntry.ForItem(
            item, action, success, error, isManualHandoff, isNotApplicable, detail, isCancelled, versionUnchanged);

        lock (_reportLock)
            _history.Append(entry);
    }

    /// <summary>
    /// What a download of this item has cost so far, and whether it is finished. Every status report,
    /// in-process and from the elevated worker, passes through <see cref="Report"/>, the one place that can
    /// see a transfer begin, progress and end without each source reporting it separately. An entry is kept,
    /// marked finished, once the transfer ends: Progress&lt;T&gt; without a captured synchronization context
    /// posts through the thread pool, so a progress report queued before the item's result can arrive after
    /// it, and only a marked entry lets that report be recognised as stale.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TransferState> _transfers = new();

    /// <summary>How many times each row has been started in-process; see the progress callback in
    /// <see cref="InstallInProcessCoreAsync"/>.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _attempts = new();

    private sealed record TransferState(DateTime Started, long Total, bool Finished, long Received = 0);

    /// <summary>
    /// Clears any tracked transfer for this item before a new attempt starts (in-process, and each item of a
    /// fresh elevated batch, including a retry round). Otherwise the old entry, still marked Finished, would
    /// make every progress report of the retry read as stale.
    /// </summary>
    private void ResetTransfer(UpdateItem item) => _transfers.TryRemove(ActivityLog.Item(item.SourceId, item.Id), out _);

    private void Report(InstallItemStatus status)
    {
        var key = ActivityLog.Item(status.SourceId, status.ItemId);
        var isTerminal = status.IsTerminal;

        if (isTerminal)
        {
            status = CloseTransfer(status, key);
        }
        else if (_transfers.TryGetValue(key, out var tracked) && tracked.Finished)
        {
            // This item's own result already went out; a progress line arriving after that is stale. Applying it
            // would drag a row that reads "done" back to a part-filled bar, and logging it would print a download
            // line after the "dl done" line. Dropped: neither reported nor logged.
            return;
        }
        else if (status.BytesTotal is > 0)
        {
            // The first report with a real size starts a download; later ones keep the start time but update the
            // received count, so a download failing partway has a real byte count to fall back on in CloseTransfer.
            // One lookup: a second one after ContainsKey could miss an entry ResetTransfer removed on another
            // thread, and the throw would end the process from a Progress callback.
            var isNewTransfer = !_transfers.TryGetValue(key, out var existing);
            _transfers[key] = new TransferState(
                isNewTransfer ? DateTime.UtcNow : existing!.Started,
                status.BytesTotal.Value,
                Finished: false,
                Received: status.BytesReceived ?? 0);

            if (isNewTransfer)
                _logging.Event(ActivityLog.Download, ActivityLog.DownloadStarted(status.SourceId, status.ItemId, status.BytesTotal));
        }

        StatusChanged?.Invoke(status);
    }

    /// <summary>
    /// A first-round failure the automatic retry round will run again. Reported as still in progress, never
    /// as Failed: a row that read as finished could be retried by hand while the queue retries it too, be
    /// replaced by a rescan before the second attempt reports, and count its failure twice toward the
    /// three-strikes tally. The attempt's transfer is closed as a failure would close it, so a late progress
    /// line from it is still recognised as stale.
    /// </summary>
    private void ReportAwaitingRetry(UpdateItem item)
    {
        var status = new InstallItemStatus(
            item.SourceId, item.Id, 0, LocalizationService.Instance["InstallRetryRound"], null, null)
        {
            IsAwaitingRetry = true,
        };
        _ = CloseTransfer(status, ActivityLog.Item(item.SourceId, item.Id));
        StatusChanged?.Invoke(status);
    }

    /// <summary>
    /// Closes a tracked transfer in the same step as the item's own completion, logging its "dl done" line
    /// and, when the terminal status carries no bytes of its own (a plain success never does), filling in
    /// BytesReceived/BytesTotal so the row's bar reaches full together with the row reporting finished. An
    /// item with nothing tracked is returned unchanged.
    /// The fill-in claims the full size only for a genuine success. A failed/cancelled/handed-off status
    /// without bytes keeps the last real byte count, so a download that failed at 50 % still reads roughly
    /// 50 %, and the batch total only counts bytes actually received.
    /// </summary>
    private InstallItemStatus CloseTransfer(InstallItemStatus status, string key)
    {
        if (!_transfers.TryGetValue(key, out var transfer) || transfer.Finished)
            return status;

        _transfers[key] = transfer with { Finished = true };
        _logging.Event(
            ActivityLog.Download,
            ActivityLog.DownloadFinished(status.SourceId, status.ItemId, transfer.Total, DateTime.UtcNow - transfer.Started));

        if (status.BytesTotal is > 0)
            return status;

        return status.Success == true
            ? status with { BytesReceived = transfer.Total, BytesTotal = transfer.Total }
            : status with { BytesReceived = transfer.Received, BytesTotal = transfer.Total };
    }
}
