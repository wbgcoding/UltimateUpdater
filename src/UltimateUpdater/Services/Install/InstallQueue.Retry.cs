using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Install;

public sealed partial class InstallQueue
{
    /// <summary>
    /// The automatic retry's second pass: everything <see cref="RunState.AutomaticRetryCandidates"/> collected
    /// in the first round, minus anything the user removed meanwhile, through the same in-process/elevated
    /// split, reusing this run's throttle and pending list so a reorder or stop behaves as in the first round.
    /// </summary>
    private async Task RunAutomaticRetryRoundAsync(
        InstallAction action, HashSet<string> reported, SemaphoreSlim throttle, RunState run, CancellationToken cancellationToken)
    {
        List<UpdateItem> retryItems;
        lock (_queueLock)
        {
            var removed = run.RemovedFromQueue;
            retryItems = run.AutomaticRetryCandidates.Values.Where(i => !removed.Contains(Key(i))).ToList();

            // Snapshot, flag and enqueue in one lock, so CancelItem never sees a started round whose items are
            // not in Pending yet.
            if (retryItems.Count > 0)
            {
                run.RetryRoundStarted = true;
                run.Pending.AddRange(retryItems);
            }
        }

        if (retryItems.Count == 0)
            return;

        _logging.Info($"Retrying {retryItems.Count} failed package(s) once, as the batch's own second attempt.");
        // An uninstall has a retry round too, but it is nobody's "second attempt" of a batch: announcing it
        // would put a running batch's queue panel into its retry section.
        if (action == InstallAction.Install)
            SecondAttemptStarted?.Invoke();

        var retryAdminScope = retryItems.Where(i => RequiresElevation(i, action)).ToList();

        var retryUserScopeTask = Task.WhenAll(retryItems
            .Where(i => !RequiresElevation(i, action))
            .Select(LaneKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(laneKey => InstallLaneSequentiallyAsync(laneKey, action, reported, throttle, run, cancellationToken, isRetryRound: true)));

        var retryAdminScopeTask = retryAdminScope.Count > 0
            ? RunElevatedBatchAsync(retryAdminScope, action, reported, throttle, run, cancellationToken, isRetryRound: true)
            : Task.CompletedTask;

        await Task.WhenAll(retryUserScopeTask, retryAdminScopeTask);
    }

    private async Task RetryUserScopeInProcessAsync(
        List<UpdateItem> userScopeRetries, InstallAction action, ISet<string> reported, SemaphoreSlim throttle,
        RunState run, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // An in-process install like any other, so it takes a slot of the same budget the user-scope groups
            // share. The slot first, then the item (same order as the source groups): taken out of the list before
            // waiting, a row stopped during the wait would be in neither the list nor ItemCancellations.
            await throttle.WaitAsync(cancellationToken);
            UpdateItem item;
            try
            {
                // Taken one at a time under the lock CancelItem uses, so a row stopped while it waited is gone.
                CancellationTokenSource itemCts;
                lock (_queueLock)
                {
                    if (userScopeRetries.Count == 0)
                    {
                        if (ReferenceEquals(run.UserScopeRetries, userScopeRetries))
                            run.UserScopeRetries = null;
                        return;
                    }

                    item = userScopeRetries[0];
                    userScopeRetries.RemoveAt(0);
                    itemCts = RegisterItemCancellation(run, item, cancellationToken);
                }

                AfterItemTakenForTest?.Invoke(item);
                _logging.Info($"Retrying '{item.Id}' ({action}) without administrator rights.");

                // Flagged as a retry round: this is already the second attempt, so a failure is final and must not
                // enrol the item in the automatic retry pass again.
                await InstallInProcessAsync(item, action, run, cancellationToken, isRetryRound: true, registeredCts: itemCts);
            }
            finally
            {
                throttle.Release();
            }

            lock (_reportLock)
                reported.Add(Key(item));
        }
    }
}
