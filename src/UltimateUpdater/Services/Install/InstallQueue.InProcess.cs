using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services.Install;

public sealed partial class InstallQueue
{
    /// <summary>Waits for the delayed re-checks of in-process results started so far and forgets them; ones
    /// started later (by the retry round) are collected by the next call.</summary>
    private async Task AwaitInProcessRechecksAsync(RunState run)
    {
        Task[] rechecks;
        lock (_queueLock)
        {
            rechecks = [.. run.InProcessRechecks];
            run.InProcessRechecks.Clear();
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

    /// <param name="laneHeld">True when the caller already holds the item's lane (the batch path does).</param>
    private async Task InstallInProcessAsync(
        UpdateItem item, InstallAction action, RunState run, CancellationToken cancellationToken, bool isRetryRound = false,
        bool laneHeld = false, CancellationTokenSource? registeredCts = null)
    {
        // This item's own stop button, linked to the run's whole-batch token so either ends the same await;
        // registered under the key CancelItem looks up and deregistered on every exit path. A caller that took
        // the row out of its list under the queue lock registers the source there and hands it in.
        var key = Key(item);
        var itemCts = registeredCts ?? RegisterItemCancellation(run, item, cancellationToken);
        var itemToken = itemCts.Token;

        try
        {
            await InstallInProcessCoreAsync(item, action, cancellationToken, itemToken, run, isRetryRound, laneHeld);
        }
        finally
        {
            // Removed and disposed under the lock CancelItem cancels under, so a stop click can never reach
            // a source that was disposed a moment earlier.
            ReleaseItemCancellation(run, key, itemCts);
        }
    }

    /// <summary>The success outcome of an in-process install: reported first, so the log reads
    /// download-then-install and a closed download line never trails the "ok".</summary>
    private void CompleteInProcessSuccess(UpdateItem item, InstallAction action, TimeSpan installTime, bool versionUnchanged)
    {
        Report(new InstallItemStatus(item.SourceId, item.Id, 100, null, true, null)
        {
            VersionUnchanged = versionUnchanged,
            VersionUnchangedCount = versionUnchanged ? CountUnchanged(item) : 0,
        });
        _logging.Event(ActivityLog.Install, ActivityLog.InstallSucceeded(item, action, installTime));
        RecordHistory(item, action, success: true, error: null, versionUnchanged: versionUnchanged);
    }

    /// <summary>
    /// Runs the delayed part of the version re-check after the caller released its throttle slot and lane, so
    /// an unrelated queued install is not held back for the whole window. The result is reported once the
    /// look-ups are over; the item stays stoppable meanwhile through a token registered under its key, which
    /// replaces the one <see cref="InstallInProcessAsync"/> deregisters only while it is still its own.
    /// </summary>
    private void DeferInProcessRecheck(
        IUpdateSource source, UpdateItem item, InstallAction action, TimeSpan installTime,
        CancellationToken batchToken, CancellationToken itemToken, RunState run)
    {
        var key = Key(item);

        // Linked to the batch token, not the item's: the item's own source is disposed as soon as the caller
        // returns, which would cut the link to the batch. A stop that already landed carries over by hand.
        var recheckCts = CancellationTokenSource.CreateLinkedTokenSource(batchToken);
        if (itemToken.IsCancellationRequested)
            recheckCts.Cancel();
        Task task;
        lock (_queueLock)
        {
            run.ItemCancellations[key] = recheckCts;
            task = Task.Run(async () =>
            {
                var unchanged = false;
                try
                {
                    unchanged = await StillUnchangedAfterWaitAsync(
                        source, item, item.InstalledVersion, _logging, VersionRecheckInterval, VersionRecheckAttempts, recheckCts.Token);
                }
                catch (Exception ex)
                {
                    _logging.Warn($"Delayed version re-check for '{item.Id}' failed: {ex.Message}");
                }
                finally
                {
                    ReleaseItemCancellation(run, key, recheckCts);
                }

                CompleteInProcessSuccess(item, action, installTime, unchanged);
            });
            run.InProcessRechecks.Add(task);
        }
    }

    private async Task InstallInProcessCoreAsync(
        UpdateItem item, InstallAction action, CancellationToken batchToken, CancellationToken cancellationToken,
        RunState run, bool isRetryRound = false, bool callerHoldsLane = false)
    {
        // A fresh attempt (the retry round, the user-scope retry after the admin side refuses it) must not
        // inherit a transfer entry an earlier attempt marked finished.
        ResetTransfer(item);

        // A Progress<T> without a captured context delivers through the thread pool, so a callback from an
        // earlier attempt can still be in flight when this one starts. It carries its attempt number and is
        // dropped once the row has moved on.
        var attemptKey = ActivityLog.Item(item.SourceId, item.Id);
        var attempt = _attempts.AddOrUpdate(attemptKey, 1, (_, previous) => previous + 1);

        var source = _sources.FirstOrDefault(s => s.Id == item.SourceId);
        if (source is null)
        {
            // Recorded like any other outcome: the history is the only place the user can see this row was attempted.
            var unknown = $"Unknown source '{item.SourceId}'.";
            var unknownDetail = $"{item.SourceId}/{item.Id}: {action} failed; no source named '{item.SourceId}' is registered.";
            Report(new InstallItemStatus(item.SourceId, item.Id, 0, null, false, unknown)
            {
                // Same rule as a real tool failure: an error state without a usable Detail leaves the error-detail
                // button opening on nothing.
                Detail = unknownDetail,
            });
            RecordHistory(item, action, success: false, error: unknown, detail: unknownDetail);
            return;
        }

        var progress = new Progress<Models.InstallProgress>(p =>
        {
            if (_attempts.TryGetValue(attemptKey, out var current) && current != attempt)
                return;

            Report(new InstallItemStatus(item.SourceId, item.Id, p.PercentComplete, p.StatusText, null, null)
            {
                BytesReceived = p.BytesReceived,
                BytesTotal = p.BytesTotal,
                RebootRequired = p.RebootRequired,
                Phase = p.Phase,
            });
        });

        var started = System.Diagnostics.Stopwatch.StartNew();
        var laneGate = _laneGates.GetOrAdd(LaneKey(item), _ => new SemaphoreSlim(1, 1));
        var laneHeld = false;

        try
        {
            // The lane is queue-wide, not per run: a side run (an uninstall started beside a batch) shares the
            // package manager's global state with the batch, so it waits here on the same lane. Waited for inside
            // this try, on the item's own stop token, so a waiting row still reads queued and Stop ends it. The batch
            // path takes the lane itself before the item (InstallLaneSequentiallyAsync). The elevated half needs no
            // gate: the worker runs its install jobs one at a time.
            if (!callerHoldsLane)
            {
                await laneGate.WaitAsync(cancellationToken);
                laneHeld = true;
            }

            // A stop that landed while the row waited for its slot or lane ends it here, before it is announced.
            cancellationToken.ThrowIfCancellationRequested();

            started.Restart();
            _logging.Event(ActivityLog.Install, ActivityLog.InstallStarted(item, action, elevated: false));

            if (action == InstallAction.Uninstall)
                await source.UninstallAsync(item, progress, cancellationToken);
            else if (action == InstallAction.FreshInstall)
            {
                // Only a source that can do a plain install without the upgrade ladder; anything else fails closed.
                if (source is not Sources.IFreshInstallSource fresh)
                    throw new InvalidOperationException($"Source '{item.SourceId}' does not support a fresh install.");
                await fresh.FreshInstallAsync(item, progress, cancellationToken);
            }
            else
                await source.InstallAsync(item, progress, cancellationToken);

            // Only for an install (a removal has no installed version to re-check), and only a quiet note that
            // never turns a real success into anything else. A source that cannot answer (null) says nothing.
            // A value that still reads old right after the installer exits is looked up again for a while; that
            // wait runs after this item let go of its slot and lane, and the row keeps showing the install until
            // it is over.
            var unchangedNow = action == InstallAction.Install
                && await IsVersionUnchangedAsync(source, item, item.InstalledVersion, _logging, cancellationToken);
            var installTime = started.Elapsed;

            if (unchangedNow)
            {
                DeferInProcessRecheck(source, item, action, installTime, batchToken, cancellationToken, run);
                return;
            }

            CompleteInProcessSuccess(item, action, installTime, versionUnchanged: false);
        }
        catch (OperationCanceledException) when (batchToken.IsCancellationRequested)
        {
            // Only the user's whole-queue cancellation propagates and ends the batch. A package manager's own
            // HTTP timeout also surfaces as a cancellation (TaskCanceledException) with our tokens untouched;
            // rethrowing that would abandon every remaining package.
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // This item's own stop button; the rest of the queue keeps running. A terminal non-failure.
            _logging.Info($"{action} for '{item.Id}' was stopped by its own stop button.");
            Report(new InstallItemStatus(item.SourceId, item.Id, 100, null, null, null) { IsCancelled = true });
            RecordHistory(item, action, success: false, error: null, isCancelled: true);
        }
        catch (Sources.ManualHandoffException ex)
        {
            // Genuinely unknown: the source opened a vendor dialog that reports nothing back. Recorded and shown as
            // its own outcome, never folded into success or into failure.
            _logging.EventFailed(ActivityLog.Install, ActivityLog.InstallStuck(item, "handed off"));
            Report(new InstallItemStatus(item.SourceId, item.Id, 100, ex.Message, null, null) { IsManualHandoff = true });
            RecordHistory(item, action, success: false, error: ex.Message, isManualHandoff: true);
        }
        catch (Sources.NotApplicableException ex)
        {
            // Nothing was changed and nothing went wrong: the manager has no version that fits this installation.
            // The source's text is English diagnostic output, so the user gets the translated line and the raw
            // text stays the Detail, as for a failure.
            _logging.EventFailed(ActivityLog.Install, ActivityLog.InstallStuck(item, "no applicable version"));
            var stuck = LocalizationService.Instance["RowStuckDetail"];
            Report(new InstallItemStatus(item.SourceId, item.Id, 100, null, null, stuck) { IsNotApplicable = true, Detail = ex.Message });
            RecordHistory(item, action, success: false, error: stuck, isNotApplicable: true, detail: ex.Message);
        }
        catch (Exception ex)
        {
            // One failing package must never abort the rest of the queue. The exception text is raw tool output,
            // English and routinely containing the user's own paths, so it goes to the log and the user gets a
            // translated line plus the raw detail for the row's "Details" affordance, which the user opens
            // deliberately (SECURITY.md).
            _logging.EventFailed(ActivityLog.Install, ActivityLog.InstallFailed(item, action, started.Elapsed, ex.Message));
            _logging.Error($"{action} failed for '{item.Id}'", ex);
            var message = InstallErrorExplainer.RowText(item.SourceId, ex.Message, key => LocalizationService.Instance[key])
                ?? LocalizationService.Instance[action == InstallAction.Uninstall ? "UninstallFailed" : "InstallFailed"];

            // Only from the first round (a second failure is final), and only for a cause the explainer table
            // marks safe to retry blindly. Such a failure is not the row's final outcome, so it is not reported
            // as one.
            if (!isRetryRound && InstallErrorExplainer.Explain(item.SourceId, ex.Message)?.AllowsAutomaticRetry == true)
            {
                run.AutomaticRetryCandidates[Key(item)] = item;
                DeferFailure(item, message, ex.Message);
                ReportAwaitingRetry(item);
            }
            else
            {
                Report(new InstallItemStatus(item.SourceId, item.Id, 0, null, false, message) { Detail = ex.Message });
                RecordHistory(item, action, success: false, error: message, detail: ex.Message);
            }
        }
        finally
        {
            if (laneHeld)
                laneGate.Release();
        }
    }
}
