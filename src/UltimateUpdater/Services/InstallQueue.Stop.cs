using UltimateUpdater.Models;

namespace UltimateUpdater.Services;

public sealed partial class InstallQueue
{
    /// <summary>
    /// Stops just this one row; the rest of the queue, and the rest of an admin batch it is part of, keeps
    /// running. Tried in-process first, then forwarded to whichever active run's elevated job holds the
    /// item; a row that already finished or never ran is ignored, since it may have finished between the
    /// click and this call.
    /// </summary>
    public void CancelItem(string sourceId, string itemId)
    {
        var key = $"{sourceId}:{itemId}";

        List<ElevatedJobHandle> handles;
        RunState? retryRun = null;
        UpdateItem? retryItem = null;
        var waitingForLane = false;
        lock (_queueLock)
        {
            foreach (var run in _activeRuns)
            {
                if (run.ItemCancellations.TryGetValue(key, out var cts))
                {
                    BeforeItemCancelForTest?.Invoke(cts);
                    try
                    {
                        cts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The row finished and released its source between the lookup and this call: nothing left to
                        // stop.
                    }

                    return;
                }
            }

            // A row the helper refused as a per-user package waits (showing Running) for its unelevated retry
            // after the job ends; the helper has already finished with it, so a stop must be settled here.
            foreach (var run in _activeRuns)
            {
                var waiting = run.UserScopeRetries?.FindIndex(i => string.Equals(Key(i), key, StringComparison.OrdinalIgnoreCase)) ?? -1;
                if (waiting < 0)
                    continue;

                retryRun = run;
                retryItem = run.UserScopeRetries![waiting];
                run.UserScopeRetries.RemoveAt(waiting);
                break;
            }

            // A row waiting for the automatic retry is in neither handle, only a candidate. Taking it out of the
            // candidates (and out of the retry round's pending list, if that started) stops the second attempt.
            foreach (var run in retryRun is null ? _activeRuns : Enumerable.Empty<RunState>())
            {
                if (!run.AutomaticRetryCandidates.ContainsKey(key))
                    continue;

                var pendingIndex = run.Pending.FindIndex(i => string.Equals(Key(i), key, StringComparison.OrdinalIgnoreCase));

                // Already handed to a source group or the elevated job for its second attempt: settling it as
                // cancelled here would not stop it, and it would report its real result afterwards. It falls through
                // to the elevated handles below instead.
                if (run.RetryRoundStarted && pendingIndex < 0)
                    continue;

                run.AutomaticRetryCandidates.TryRemove(key, out var candidate);
                if (pendingIndex >= 0)
                    run.Pending.RemoveAt(pendingIndex);

                retryRun = run;
                retryItem = candidate;
                break;
            }

            // Still in a run's pending list, waiting for its lane (busy in another run) or a slot: it never
            // started, so taking it out and settling it as stopped is the whole stop.
            foreach (var run in retryRun is null ? _activeRuns : Enumerable.Empty<RunState>())
            {
                var pendingIndex = run.Pending.FindIndex(i => string.Equals(Key(i), key, StringComparison.OrdinalIgnoreCase));
                if (pendingIndex < 0)
                    continue;

                retryRun = run;
                retryItem = run.Pending[pendingIndex];
                run.Pending.RemoveAt(pendingIndex);
                waitingForLane = true;
                break;
            }

            retryRun?.CancelledPending.Add(key);
            handles = _activeRuns.Where(r => r.ElevatedHandle is not null).Select(r => r.ElevatedHandle!).ToList();
        }

        if (retryRun is not null && retryItem is not null)
        {
            AfterPendingStopForTest?.Invoke(retryItem);
            lock (_reportLock)
                retryRun.Reported?.Add(key);

            _logging.Info(waitingForLane
                ? $"{retryRun.Action} for '{retryItem.Id}' was stopped before it started."
                : $"{retryRun.Action} for '{retryItem.Id}' was stopped while waiting for its automatic retry.");
            Report(new InstallItemStatus(retryItem.SourceId, retryItem.Id, 100, null, null, null) { IsCancelled = true });
            // A real first failure held for the retry stays the row's entry, as when the whole batch stops,
            // so a stop never hides a failure from the keeps-failing count.
            if (!FlushDeferredFailure(retryItem, retryRun.Action))
                RecordHistory(retryItem, retryRun.Action, success: false, error: null, isCancelled: true);
            return;
        }

        foreach (var handle in handles)
            _ = handle.CancelItemAsync(sourceId, itemId);
    }

    /// <summary>Creates the row's own stop source (linked to the run's) and registers it under the key
    /// <see cref="CancelItem"/> looks up.</summary>
    private CancellationTokenSource RegisterItemCancellation(RunState run, UpdateItem item, CancellationToken cancellationToken)
    {
        var itemCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        run.ItemCancellations[Key(item)] = itemCts;
        return itemCts;
    }

    /// <summary>Drops <paramref name="cts"/> from the run's stop map (only while it is still the registered
    /// one) and disposes it, both under the lock <see cref="CancelItem"/> cancels under.</summary>
    private void ReleaseItemCancellation(RunState run, string key, CancellationTokenSource cts)
    {
        lock (_queueLock)
        {
            if (run.ItemCancellations.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
                run.ItemCancellations.TryRemove(key, out _);
            cts.Dispose();
        }
    }
}
