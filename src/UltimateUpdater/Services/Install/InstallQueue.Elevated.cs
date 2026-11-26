using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services.Install;

public sealed partial class InstallQueue
{
    /// <summary>
    /// The job travels over the pipe rather than through a file in %TEMP%: a file would sit there between
    /// being written and being read by the elevated copy, where any other process running as this user could
    /// rewrite it and have its own packages installed with admin rights on the back of the user's UAC prompt.
    /// </summary>
    private async Task RunElevatedBatchAsync(
        IReadOnlyList<UpdateItem> items,
        InstallAction action,
        HashSet<string> reported,
        SemaphoreSlim throttle,
        RunState run,
        CancellationToken cancellationToken,
        bool isRetryRound = false)
    {
        // Re-read the current order (dropping anything the user removed) right before the batch is built: a
        // queue-panel reorder or removal can land between this run being registered and this line.
        // The handle is registered in the same step that takes the rows out of Pending: until the job is out,
        // a stop on one of them can only be remembered by this handle and replayed once it is.
        var jobHandle = new ElevatedJobHandle();
        lock (_queueLock)
        {
            var adminKeys = new HashSet<string>(items.Select(Key), StringComparer.OrdinalIgnoreCase);
            items = run.Pending.Where(i => adminKeys.Contains(Key(i))).ToList();
            run.Pending.RemoveAll(i => adminKeys.Contains(Key(i)));
            if (items.Count > 0)
                run.ElevatedHandle = jobHandle;
        }

        if (items.Count == 0)
            return;

        AfterAdminRowsTakenForTest?.Invoke(items);

        // Same reset as the in-process path, per item (a retry round's batch starts here too).
        foreach (var item in items)
            ResetTransfer(item);

        _logging.Event(ActivityLog.Install, ActivityLog.BatchStarted(items.Count, elevated: true));

        // The decision is made here, before the job goes out, and travels as a plain flag; the worker obeys it
        // and never reads the timestamp file itself (it does no file I/O). "Toggle on, but one was already made
        // today" is not a failure, only the one calm log line below.
        var wantsRestorePoint = action == InstallAction.Install && _settings.Current.Install.CreateRestorePoint;
        // Decided and reserved in one step: a second batch deciding while this restore point is being created
        // must not read the old timestamp and ask for another. The lease lasts for the whole batch and is handed
        // back however it ends (a job that never went out, a declined prompt, a failure).
        using var restorePointLease = wantsRestorePoint
            ? RestorePointReservation.Shared.TryReserve(() => RestorePointThrottle.TryReadLastCreated(_logging), DateTimeOffset.UtcNow)
            : null;
        var shouldCreateRestorePoint = restorePointLease is not null;
        if (wantsRestorePoint && !shouldCreateRestorePoint)
        {
            _logging.Info("Restore point skipped: one was already created within the last 24 hours.");
            _logging.Event(ActivityLog.Install, LocalizationService.Instance["ActivityRestorePointSkipped"]);
        }

        var job = new InstallJob
        {
            Action = action,
            // a restore point belongs before an install; there is nothing to roll forward on a removal
            CreateRestorePoint = shouldCreateRestorePoint,
            // the worker is a separate process and would otherwise report in the OS language
            Language = _settings.Current.Ui.Language,
            // The worker keeps its own budget, not reduced by what this half runs, since the two are separate
            // processes; it clamps the value itself regardless of what is sent.
            MaxParallelInstalls = _settings.Current.Install.MaxParallelInstalls,
            Items = items.Select(i => ToJobItem(i, action)).ToList(),
        };

        // Flushed so the file on disk is current for the next app start.
        _settings.Flush();

        // The prompt, if this is the first admin job of the session; later batches go to the same helper.
        bool started;
        try
        {
            started = await _elevated.EnsureStartedAsync(cancellationToken);
        }
        catch
        {
            run.ElevatedHandle = null;
            throw;
        }

        if (!started)
        {
            run.ElevatedHandle = null;

            // a declined UAC prompt is a decision, not a failed install, so it stays out of the history
            var declined = LocalizationService.Instance["InstallElevationDeclined"];
            foreach (var item in items)
            {
                Report(new InstallItemStatus(item.SourceId, item.Id, 0, null, false, declined)
                {
                    Detail = $"{item.SourceId}/{item.Id}: {action} needs administrator rights, but the elevation " +
                        "prompt was declined or the elevated helper failed to start.",
                });

                lock (_reportLock)
                {
                    reported.Add(Key(item));
                    DeclinedItemRecordedForTest?.Invoke(item);
                }
            }

            return;
        }

        // Per call, not a field: the reply handler runs on the pipe's reader thread, and one batch's retries
        // must not be visible to another's.
        var userScopeRetries = new List<UpdateItem>();
        lock (_queueLock)
            run.UserScopeRetries = userScopeRetries;

        // This run's stop-single button forwards here once nothing in its in-process ItemCancellations
        // matches; cleared when the batch is over so a stale handle is never mistaken for a live one.
        run.CancellationToken = cancellationToken;
        try
        {
            await _elevated.RunAsync(
                "install", job, line => HandleWorkerLine(line, items, action, reported, userScopeRetries, run, isRetryRound),
                cancellationToken, handle: jobHandle);
        }
        finally
        {
            run.ElevatedHandle = null;

            // Results waiting on their delayed version look-up are settled before this batch is over (a stop ends
            // each wait), so the caller's "anything left unreported" pass never sees them open.
            Task[] rechecks;
            lock (_queueLock)
            {
                rechecks = [.. run.Rechecks];
                run.Rechecks.Clear();
            }

            try
            {
                await Task.WhenAll(rechecks);
            }
            catch (Exception ex)
            {
                _logging.Warn($"Delayed version re-check ended with an error: {ex.Message}");
            }
        }

        // Jobs the elevated helper refused because the package belongs to this user rather than the machine.
        // Retried here, unelevated, once: if the in-process attempt fails too, that is the answer.
        await RetryUserScopeInProcessAsync(userScopeRetries, action, reported, throttle, run, cancellationToken);
    }

    /// <summary>
    /// One line back from the elevated worker. Returns false once the batch is finished, which tells the
    /// host this conversation is over.
    /// </summary>
    private bool HandleWorkerLine(
        string line, IReadOnlyList<UpdateItem> items, InstallAction action, ISet<string> reported,
        List<UpdateItem> userScopeRetries, RunState run, bool isRetryRound)
    {
        InstallProgressMessage? message = null;
        try
        {
            message = JsonSerializer.Deserialize<InstallProgressMessage>(line, JsonOptions);
        }
        catch (JsonException)
        {
            // hostile-input rule: a malformed worker line is ignored, never fatal
        }

        if (message is null)
            return true;

        if (message.Type == "done")
            return false;

        // The elevated worker writes no files of its own (its directories are controlled by the unelevated
        // user), so its log lines come back here and land in the app's log.
        if (message.Type == "log")
        {
            _logging.Info($"worker: {message.Status}");
            return true;
        }

        // The worker only reports this; this side owns every file the app writes.
        if (message.Type == "restorePointCreated")
        {
            RestorePointThrottle.TryWriteCreated(_logging, message.RestorePointCreatedAt ?? DateTimeOffset.UtcNow);
            return true;
        }

        // A handed-off, not-applicable or cancelled result is neither success nor failure, so Success must
        // arrive as null on this side however the wire spells it.
        var terminalNonFailure = message.IsManualHandoff || message.IsNotApplicable || message.IsCancelled;

        // A package installed per-user refuses to be touched by an administrator, which the elevated helper is.
        // The job is retried in this process, once, for installs as well as removals (winget's error says
        // "the requested operation" for an upgrade of a user-scope package too). Decided before the result is
        // reported: reported first, the row would flash Failed, count as done and feed the three-strike record
        // with a refusal that is not the final answer.
        if (message.Type == "result" && message.Success == false && WingetSource.NeedsUserScopeRetry(message.Detail)
            && items.FirstOrDefault(i => i.Id == message.ItemId && i.SourceId == message.SourceId) is { } retryItem)
        {
            lock (_queueLock)
                userScopeRetries.Add(retryItem);
            return true;
        }

        var item = message.Type == "result"
            ? items.FirstOrDefault(i => i.Id == message.ItemId && i.SourceId == message.SourceId)
            : null;

        // A genuine failure only (not the handed-off/not-applicable/cancelled outcomes), only from the first
        // round, and only for a cause the explainer table marks safe to retry blindly. Decided before anything
        // is reported, as for the user-scope refusal above: the retry round gives the final answer.
        var awaitsRetry = item is not null && message.Success == false && !terminalNonFailure && !isRetryRound
            && InstallErrorExplainer.Explain(item.SourceId, message.Detail)?.AllowsAutomaticRetry == true;

        // Installed fine but the version still reads old: look again for a while before the note stays. Done
        // here, never in the elevated worker, which holds its exclusive slot until the job ends. The result is
        // reported once the lookups are over (RunElevatedBatchAsync awaits them).
        if (item is not null && action == InstallAction.Install && message.Type == "result" && message.Success
            && !terminalNonFailure && message.VersionUnchanged
            && _sources.FirstOrDefault(s => s.Id == item.SourceId) is { } recheckSource)
        {
            // Registered before the task starts, so a row stop at any moment of the wait reaches it through
            // CancelItem's per-row lookup.
            var recheckKey = Key(item);
            var recheckCts = CancellationTokenSource.CreateLinkedTokenSource(run.CancellationToken);
            lock (_queueLock)
                run.ItemCancellations[recheckKey] = recheckCts;

            var recheck = Task.Run(() => FinishAfterVersionRecheckAsync(recheckSource, message, item, action, reported, run, recheckKey, recheckCts));
            lock (_queueLock)
                run.Rechecks.Add(recheck);
            return true;
        }

        ApplyWorkerResult(message, item, action, reported, run, awaitsRetry, terminalNonFailure);

        return true;
    }

    /// <summary>Reports one worker message to the row and, for a result, records it (log, history, accounted-for
    /// set).</summary>
    private void ApplyWorkerResult(
        InstallProgressMessage message, UpdateItem? item, InstallAction action, ISet<string> reported, RunState run,
        bool awaitsRetry, bool terminalNonFailure)
    {
        if (awaitsRetry)
        {
            ReportAwaitingRetry(item!);
        }
        else
        {
            Report(new InstallItemStatus(
                message.SourceId ?? string.Empty,
                message.ItemId ?? string.Empty,
                message.Percent,
                message.Status,
                message.Type == "result" && !terminalNonFailure ? message.Success : null,
                message.Error)
            {
                BytesReceived = message.BytesReceived,
                BytesTotal = message.BytesTotal,
                Phase = Enum.IsDefined(message.Phase) ? message.Phase : Models.InstallPhase.Unknown,
                Detail = message.Detail,
                RebootRequired = message.RebootRequired,
                IsManualHandoff = message.IsManualHandoff,
                IsNotApplicable = message.IsNotApplicable,
                IsCancelled = message.IsCancelled,
                VersionUnchanged = message.VersionUnchanged,
                VersionUnchangedCount = message.VersionUnchanged && item is not null ? CountUnchanged(item) : 0,
            });
        }

        if (item is not null)
        {
            // The elevated worker's own outcome, recorded here because this side owns the log file.
            if (message.IsCancelled)
                _logging.Info($"Elevated {action} for '{item.Id}' was stopped by its own stop button.");
            else if (terminalNonFailure)
                _logging.EventFailed(ActivityLog.Install, ActivityLog.InstallStuck(item, message.Error));
            else if (message.Success)
                _logging.Event(ActivityLog.Install, ActivityLog.InstallSucceeded(item, action, duration: null));
            else
                _logging.EventFailed(ActivityLog.Install, ActivityLog.InstallFailed(item, action, null, message.Detail));

            if (awaitsRetry)
            {
                // Left out of reported, so FailRemaining still closes the row if the retry round never runs.
                run.AutomaticRetryCandidates[Key(item)] = item;
                DeferFailure(item, message.Error, message.Detail);
            }
            else
            {
                RecordHistory(
                    item, action, message.Success && !terminalNonFailure, message.Error,
                    message.IsManualHandoff, message.IsNotApplicable, detail: message.Detail,
                    isCancelled: message.IsCancelled, versionUnchanged: message.VersionUnchanged);

                // This callback runs on the elevated pipe's reader thread (ElevatedHost's pump task) while the
                // in-process source groups mutate the same reported set from their own threads. reported is a plain
                // HashSet, so every write needs this lock.
                lock (_reportLock)
                    reported.Add(Key(item));
            }
        }
    }

    private void FailRemaining(
        IReadOnlyList<UpdateItem> items, InstallAction action, ISet<string> reported, string reason,
        bool cancelled, RunState run)
    {
        // An item the user removed from the queue was taken out on purpose: no status report and no history
        // line, as if it had never been selected. A row a stop is settling itself is skipped too: its key
        // reaches the reported set a moment after it left the queue, and reporting it here would count it twice.
        HashSet<string> removedFromQueue, removed;
        lock (_queueLock)
        {
            removedFromQueue = new HashSet<string>(run.RemovedFromQueue, StringComparer.OrdinalIgnoreCase);
            removed = new HashSet<string>(removedFromQueue, StringComparer.OrdinalIgnoreCase);
            removed.UnionWith(run.CancelledPending);
        }

        HashSet<string> accountedFor;
        lock (_reportLock)
            accountedFor = new HashSet<string>(reported, StringComparer.OrdinalIgnoreCase);

        // A row taken out of the queue is not reported, but a failure that was held back for its retry
        // really happened.
        foreach (var item in items.Where(i => removedFromQueue.Contains(Key(i))))
            FlushDeferredFailure(item, action);

        foreach (var item in items.Where(i => !accountedFor.Contains(Key(i)) && !removed.Contains(Key(i))))
        {
            // A cancelled batch never ran these rows, so they end as stopped (neither success nor failure), like a
            // row stopped with its own button.
            Report(new InstallItemStatus(item.SourceId, item.Id, cancelled ? 100 : 0, null, cancelled ? null : false, reason)
            {
                Detail = $"{item.SourceId}/{item.Id}: {action} did not run; {reason}",
                IsCancelled = cancelled,
            });

            // The real failure of a first attempt that never got its retry beats the generic reason, and is a fact
            // even in a stopped batch.
            if (!FlushDeferredFailure(item, action) && !cancelled)
                RecordHistory(item, action, success: false, error: reason);
        }
    }
}
