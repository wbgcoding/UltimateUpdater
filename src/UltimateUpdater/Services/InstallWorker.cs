using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services;

/// <summary>
/// The install and uninstall half of the elevated worker (<see cref="ElevatedWorker"/>). Receives a batch
/// over the pipe, optionally creates a restore point, works through it grouped by source (bounded
/// concurrency, see <see cref="RunGroupAsync"/>), and streams progress back on the same pipe. Shows no UI
/// and writes no files.
/// </summary>
public static class InstallWorker
{
    /// <summary>
    /// Hard cap on a single batch and on any string field inside it. Not a real-world limit (nobody selects
    /// thousands of updates at once), it exists so a compromised or bugged unelevated process cannot make
    /// the admin worker allocate or process an unbounded amount of attacker-controlled data.
    /// </summary>
    private const int MaxItems = 500;
    private const int MaxFieldLength = 512;

    /// <summary>Hard ceiling on the parallel-install budget, whatever the job claims (see <see cref="InstallJobFile.MaxParallelInstalls"/>).</summary>
    private const int MaxParallelInstallsCap = 5;

    /// <summary>
    /// How often the buffer is flushed purely on a clock, on top of the explicit drains around each step
    /// below. Those explicit calls cover the boundary between steps; this covers the inside of one, a
    /// single slow step (the restore point, one long install) would otherwise sit on its own log lines for
    /// its entire duration, exactly the gap a fixed set of drain points produced.
    /// </summary>
    private static readonly TimeSpan LogFlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The one source allowed to run several of its own items at once, a deliberate exception: every other
    /// source keeps a single global install state (npm, pip, scoop, ...) and two of its own installs at once
    /// would corrupt it, exactly the reason <see cref="InstallQueue"/>'s class doc gives for the
    /// user-scope half.
    /// </summary>
    private const string ConcurrentSourceId = "winget";

    /// <summary>
    /// Runs one batch. Returns false once the app has stopped listening, installing the rest with admin
    /// rights where nobody can see it is exactly what this worker must not do.
    /// </summary>
    /// <param name="sources">
    /// Test seam only; production always resolves the full registry. Without this the winget-only
    /// concurrency case (an elevation source, so it can never be driven through
    /// <see cref="InstallQueue"/>) would be unreachable from a test.
    /// </param>
    /// <param name="itemCancellations">
    /// The per-item stop: keyed by <c>"sourceId:itemId"</c>, filled in by this method while an item
    /// runs and read by <see cref="ElevatedWorker"/>'s "cancel-item" control message. Null (the production
    /// default from a copy of this worker not yet asked to cancel a single item) simply means per-item stop
    /// is not wired up; the whole-job token still works exactly as before.
    /// </param>
    public static async Task<bool> RunJobAsync(
        InstallJobFile? job, StreamWriter writer, string jobId, LoggingService logging,
        CancellationToken cancellationToken, IReadOnlyList<IUpdateSource>? sources = null,
        ConcurrentDictionary<string, CancellationTokenSource>? itemCancellations = null)
    {
        // A payload deserialized with an explicit "items": null (not merely an empty list) still
        // has to end this job cleanly - Items has a default initializer, but System.Text.Json overwrites it
        // with the literal null the wire carried, so job.Items.Count alone would throw a
        // NullReferenceException and drop the whole job with no reply, leaving the app waiting forever.
        if (job is null || job.Items is null || job.Items.Count == 0)
        {
            Send(writer, jobId, new InstallProgressMessage { Type = "done" });
            return true;
        }

        // The unelevated app is the only thing that ever sends a job, but the pipe is authenticated by
        // process identity, not by trusting its contents: after ONE approved UAC prompt this
        // worker stays alive for the whole session, so a job that slipped an unexpected source id or action
        // through, bug or a compromised medium-integrity parent, must never reach a package manager with
        // admin rights. Action and batch size are checked once, up front, because neither makes sense per
        // item; each ITEM is checked individually, in the main loop below, and a bad one is answered with
        // its own failed "result" (same shape as "unknown source" already was) rather than losing the whole
        // batch over one entry.
        if (!Enum.IsDefined(job.Action))
        {
            logging.Error($"Elevated worker: rejecting job with unknown action '{job.Action}'.");
            Send(writer, jobId, new InstallProgressMessage { Type = "done" });
            return true;
        }

        if (job.Items.Count > MaxItems)
        {
            logging.Error($"Elevated worker: rejecting job with {job.Items.Count} items (limit {MaxItems}).");
            Send(writer, jobId, new InstallProgressMessage { Type = "done" });
            return true;
        }

        // An explicit JSON null element deserializes to a null entry; one of those must not take the
        // whole batch down with a NullReferenceException further below.
        if (job.Items.Any(i => i is null))
        {
            logging.Error("Elevated worker: rejecting job with a null item.");
            Send(writer, jobId, new InstallProgressMessage { Type = "done" });
            return true;
        }

        // this process never went through App.StartUi, so it has no language of its own yet
        LocalizationService.Instance.Apply(job.Language);

        // Runs for the rest of this method (disposed on every exit path, including an exception): the
        // explicit DrainLog calls below still fire immediately at each step's own boundary, this only
        // covers the time spent INSIDE one, so a slow step's own lines do not sit unseen for its whole
        // duration.
        using var logFlushTimer = new System.Threading.Timer(
            _ => DrainLog(writer, jobId), null, LogFlushInterval, LogFlushInterval);

        if (job.CreateRestorePoint)
        {
            // Flushed before the step starts, not only after: this is the slow step (measured ~22 s
            // below), so its own lines are not held back until it finishes.
            DrainLog(writer, jobId);
            logging.Info("Creating a restore point before installing.");

            // Said out loud, and BEFORE it starts: measured at 22 seconds in practice, during which
            // the strip showed the first package at 0 % and the app looked hung. Reported against the
            // first item of the batch, because that is the row the strip is already showing.
            var first = job.Items[0];
            Send(writer, jobId, new InstallProgressMessage
            {
                Type = "progress",
                SourceId = first.SourceId,
                ItemId = first.ItemId,
                Percent = 0,
                Status = LocalizationService.Instance["InstallRestorePointStep"],
            });

            try
            {
                if (RestorePointService.TryCreate("Ultimate Updater, before installing updates", logging))
                {
                    // The app owns every file this program ever writes, this worker writes none,
                    // so the fact travels back as data rather than the worker deciding anything from it.
                    Send(writer, jobId, new InstallProgressMessage
                    {
                        Type = "restorePointCreated",
                        RestorePointCreatedAt = DateTimeOffset.UtcNow,
                    });
                }
            }
            catch (Exception ex)
            {
                // Best effort means best effort: a damaged WMI repository or an unregistered
                // SystemRestore provider throws past TryCreate's own catch, and losing the whole batch
                // the user just approved over a missing restore point is the wrong trade.
                logging.Warn($"Restore point could not be created, installing anyway: {ex.Message}");
            }
        }

        DrainLog(writer, jobId);

        sources ??= SourceRegistry.CreateAll(logging);

        // The app computes this budget from the setting, but the worker never trusts a job field
        // blindly: re-clamped here regardless of what was sent.
        var maxParallel = Math.Clamp(job.MaxParallelInstalls, 1, MaxParallelInstallsCap);

        var state = new JobRunState(writer, jobId, sources, job, logging, cancellationToken, itemCancellations);

        using (var throttle = new SemaphoreSlim(maxParallel, maxParallel))
        {
            // Same-source items stay sequential (a package manager's own global state, see the class doc
            // above), except winget, which may run up to the budget of its own items at once. Different
            // source groups always run concurrently with each other; ONE semaphore, shared by every group,
            // is what keeps the TOTAL in flight at any moment within the budget, whichever groups it comes
            // from.
            var groupTasks = job.Items
                .GroupBy(i => i.SourceId ?? string.Empty, StringComparer.Ordinal)
                .Select(group => RunGroupAsync(group.Key, group.ToList(), state, throttle))
                .ToList();

            await Task.WhenAll(groupTasks);
        }

        // Windows Installer allows only one classic MSI transaction at a time; with several installs now
        // able to run at once, a refusal for that reason is an expected side effect of THIS batch's own
        // concurrency, not a real failure, so every item that hit it gets one retry, alone, after every
        // concurrent group has already finished (the cap is effectively 1 here, on purpose).
        foreach (var retryItem in state.DrainSerialRetries())
        {
            if (state.LostApp || cancellationToken.IsCancellationRequested)
                break;

            await RunOneItemAsync(retryItem, state, isRetry: true);
        }

        ElevatedWorker.StopLogFlushTimer(logFlushTimer);
        DrainLog(writer, jobId);
        Send(writer, jobId, new InstallProgressMessage { Type = "done" });
        return !state.LostApp;
    }

    /// <summary>Shared, read-only view of one job plus the one piece of mutable cross-group state: whether
    /// the connection to the app was lost, checked before every new item starts.</summary>
    private sealed class JobRunState(
        StreamWriter writer, string jobId, IReadOnlyList<IUpdateSource> sources, InstallJobFile job,
        LoggingService logging, CancellationToken cancellationToken,
        ConcurrentDictionary<string, CancellationTokenSource>? itemCancellations)
    {
        public StreamWriter Writer { get; } = writer;
        public string JobId { get; } = jobId;
        public IReadOnlyList<IUpdateSource> Sources { get; } = sources;
        public InstallJobFile Job { get; } = job;
        public LoggingService Logging { get; } = logging;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public ConcurrentDictionary<string, CancellationTokenSource>? ItemCancellations { get; } = itemCancellations;

        public volatile bool LostApp;

        /// <summary>Items refused because another (classic MSI) installation was already running,
        /// collected from every group and worked through serially once every group has finished.</summary>
        private readonly ConcurrentQueue<InstallJobItem> _serialRetries = new();

        public void EnqueueSerialRetry(InstallJobItem item) => _serialRetries.Enqueue(item);

        public IEnumerable<InstallJobItem> DrainSerialRetries()
        {
            while (_serialRetries.TryDequeue(out var item))
                yield return item;
        }
    }

    /// <summary>Runs one source's share of the batch. Sequential for every source but the one named in
    /// <see cref="ConcurrentSourceId"/>, which is allowed to have several of its own items in flight, still
    /// bounded by the shared <paramref name="throttle"/>.</summary>
    private static async Task RunGroupAsync(
        string sourceId, List<InstallJobItem> items, JobRunState state, SemaphoreSlim throttle)
    {
        if (!string.Equals(sourceId, ConcurrentSourceId, StringComparison.Ordinal))
        {
            foreach (var jobItem in items)
            {
                if (state.LostApp || state.CancellationToken.IsCancellationRequested)
                    break;

                if (!await TryEnterAsync(throttle, state.CancellationToken))
                    break;

                try
                {
                    await RunOneItemAsync(jobItem, state);
                }
                finally
                {
                    throttle.Release();
                }
            }

            return;
        }

        var tasks = new List<Task>();
        foreach (var jobItem in items)
        {
            if (state.LostApp || state.CancellationToken.IsCancellationRequested)
                break;

            if (!await TryEnterAsync(throttle, state.CancellationToken))
                break;

            var captured = jobItem;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await RunOneItemAsync(captured, state);
                }
                finally
                {
                    throttle.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Validates, resolves and runs exactly one item, reporting its outcome. Callable once per item from several
    /// groups at once.
    /// </summary>
    /// <param name="isRetry">
    /// True for the one serial retry that gives an "another installation was already running" refusal;
    /// on that pass a second such refusal is a real failure and is reported normally, never queued again.
    /// </param>
    private static async Task RunOneItemAsync(InstallJobItem jobItem, JobRunState state, bool isRetry = false)
    {
        var (writer, jobId, job, logging) = (state.Writer, state.JobId, state.Job, state.Logging);

        // This item's own stop button, layered on top of the whole-job token via a linked CTS,
        // so either one ends the same await. Registered under "sourceId:itemId" for the whole item,
        // deregistered in every exit path below (including the ones that never look at the token again).
        var itemKey = ItemCancellationKey(jobItem.SourceId, jobItem.ItemId);
        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(state.CancellationToken);

        // A stop that arrived before this item started (ElevatedWorker remembers it as a cancelled entry)
        // applies now, instead of being lost and the item installing anyway. One atomic swap, so a stop that
        // lands between a separate look-up and the registration cannot be overwritten unseen.
        state.ItemCancellations?.AddOrUpdate(itemKey, itemCts, (_, earlier) =>
        {
            if (earlier.IsCancellationRequested)
                itemCts.Cancel();
            return itemCts;
        });
        var cancellationToken = itemCts.Token;

        try
        {
            await RunOneItemCoreAsync(jobItem, state, writer, jobId, job, logging, cancellationToken, isRetry);
        }
        finally
        {
            if (state.ItemCancellations is { } map)
                map.TryRemove(itemKey, out _);
        }
    }

    /// <summary>Composite key the per-item cancellation dictionary is keyed by; a plain item id is not
    /// unique across sources (two package managers can each have their own "git").</summary>
    private static string ItemCancellationKey(string sourceId, string itemId) => $"{sourceId}:{itemId}";

    private static async Task RunOneItemCoreAsync(
        InstallJobItem jobItem, JobRunState state, StreamWriter writer, string jobId, InstallJobFile job,
        LoggingService logging, CancellationToken cancellationToken, bool isRetry)
    {
        // Flushed before this item's own work starts, not only after it finishes below: with several
        // groups running at once an earlier item can still be mid-install when this one begins, and
        // without this its lines waited behind whichever item happened to still be running.
        DrainLog(writer, jobId);

        // "required" does not stop an explicit JSON null from reaching these properties; a missing field
        // rejects this one item instead of faulting the whole group.
        if (string.IsNullOrWhiteSpace(jobItem.SourceId) || string.IsNullOrWhiteSpace(jobItem.ItemId)
            || string.IsNullOrWhiteSpace(jobItem.Name))
        {
            logging.Error("Elevated worker: rejecting an item with a missing required field.");
            if (!Send(writer, jobId, Result(jobItem.SourceId ?? string.Empty, jobItem.ItemId ?? string.Empty, false, "Rejected: a required field is missing.")))
                MarkLostApp(state);

            return;
        }

        // Never resolved into a real source until it passes the allowlist, a source that does not
        // need elevation (or does not exist at all) must never reach InstallAsync/UninstallAsync with
        // admin rights, whatever the unelevated app claims about it.
        if (!InstallQueue.NeedsElevation(jobItem.SourceId))
        {
            logging.Error($"Elevated worker: rejecting item '{jobItem.ItemId}', source '{jobItem.SourceId}' does not need elevation and must never reach this worker.");
            if (!Send(writer, jobId, Result(jobItem.SourceId, jobItem.ItemId, false, $"Rejected: source '{jobItem.SourceId}' is not an elevation-requiring source.")))
                MarkLostApp(state);

            return;
        }

        if (jobItem.SourceId.Length > MaxFieldLength || jobItem.ItemId.Length > MaxFieldLength
            || jobItem.Name.Length > MaxFieldLength
            || (jobItem.InstalledVersion?.Length ?? 0) > MaxFieldLength
            || (jobItem.AvailableVersion?.Length ?? 0) > MaxFieldLength
            || (jobItem.PinnedVersion?.Length ?? 0) > MaxFieldLength)
        {
            logging.Error($"Elevated worker: rejecting item '{jobItem.ItemId}', a field exceeds {MaxFieldLength} characters.");
            if (!Send(writer, jobId, Result(jobItem.SourceId, jobItem.ItemId, false, "Rejected: a field exceeds the allowed length.")))
                MarkLostApp(state);

            return;
        }

        var source = state.Sources.FirstOrDefault(s => s.Id == jobItem.SourceId);
        if (source is null)
        {
            if (!Send(writer, jobId, Result(jobItem.SourceId, jobItem.ItemId, false, $"Unknown source '{jobItem.SourceId}'.")))
                MarkLostApp(state);

            return;
        }

        var item = new UpdateItem
        {
            Id = jobItem.ItemId,
            SourceId = jobItem.SourceId,
            Category = source.Category,
            Name = jobItem.Name,
            InstalledVersion = jobItem.InstalledVersion,
            AvailableVersion = jobItem.AvailableVersion,
            // A rollback's exact target version, so the source can honour it the same way it
            // would in-process (WingetSource.InstallAsync).
            PinnedVersion = jobItem.PinnedVersion,
        };

        // NOT System.Progress<T>: it posts its callback to the captured
        // SynchronizationContext rather than calling it inline, so a progress report made just before
        // InstallAsync/UninstallAsync returns could still be QUEUED when the "result" message below is
        // sent right after, a late progress post landing after the result flipped an already-finished
        // row back to "running" on the app side. This worker has no UI thread of its own to marshal to
        // anyway; a plain synchronous IProgress<T> guarantees every report is sent, in order, strictly
        // before the result that follows it.
        var progress = new SyncProgress<Models.InstallProgress>(p =>
            Send(writer, jobId, new InstallProgressMessage
            {
                Type = "progress",
                SourceId = item.SourceId,
                ItemId = item.Id,
                Percent = p.PercentComplete,
                Status = p.StatusText,
                BytesReceived = p.BytesReceived,
                BytesTotal = p.BytesTotal,
                RebootRequired = p.RebootRequired,
            }));

        bool delivered;
        try
        {
            if (job.Action == InstallAction.Uninstall)
                await source.UninstallAsync(item, progress, cancellationToken);
            else
                await source.InstallAsync(item, progress, cancellationToken);

            // Only meaningful for an install, and only a quiet note added to a real success,
            // the same shared re-check the in-process path uses (InstallQueue.IsVersionUnchangedAsync).
            var versionUnchanged = job.Action == InstallAction.Install
                && await InstallQueue.IsVersionUnchangedAsync(source, item, jobItem.InstalledVersion, logging, cancellationToken);

            delivered = Send(writer, jobId, Result(item.SourceId, item.Id, true, null, versionUnchanged: versionUnchanged));
        }
        catch (OperationCanceledException) when (state.CancellationToken.IsCancellationRequested)
        {
            // The whole job was stopped, not just this item; every other row is unwinding the same
            // way right now, so there is nothing useful left to report for this one.
            logging.Info($"Worker {job.Action} for '{item.Id}' was stopped.");
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // This item's own stop button, the job otherwise keeps running. ProcessRunner has
            // already killed the tool's process tree; reporting a plain failure would show it as
            // something having gone wrong, when the user just chose to stop it.
            logging.Info($"Worker {job.Action} for '{item.Id}' was stopped by its own stop button.");
            delivered = Send(writer, jobId, Result(item.SourceId, item.Id, false, null, cancelled: true));
        }
        catch (ManualHandoffException ex)
        {
            // An admin-side source handed off to a vendor's own installer dialog; this is not a
            // failure. The handoff flag travels with the result, Success is false on the wire, but
            // the app ignores that when the flag is set.
            logging.Info($"Worker {job.Action} for '{item.Id}' was handed off to an external program.");
            delivered = Send(writer, jobId, Result(item.SourceId, item.Id, false, ex.Message, ex.Message, handoff: true));
        }
        catch (NotApplicableException ex)
        {
            // The package manager knows a newer version but refuses it for this installation. Nothing
            // was changed and nothing went wrong, the row says so instead of red. The source's text is
            // English diagnostic output, so it travels as Detail behind the translated line.
            logging.Info($"Worker {job.Action} for '{item.Id}': no applicable version.");
            delivered = Send(writer, jobId, Result(
                item.SourceId, item.Id, false, LocalizationService.Instance["RowStuckDetail"], ex.Message, notApplicable: true));
        }
        catch (Exception ex)
        {
            // Windows Installer allows only one classic MSI transaction at a time; a first attempt
            // refused for that reason is not a real failure, it is an expected side effect of running
            // several installs at once, so it gets one serial retry instead of being reported now. A
            // SECOND such refusal (isRetry already true) falls through and is a genuine failure.
            if (!isRetry && string.Equals(jobItem.SourceId, "winget", StringComparison.Ordinal)
                && WingetSource.IsAnotherInstallationRunning(ex.Message))
            {
                logging.Info($"Worker {job.Action} for '{item.Id}': another installation was already running; retrying alone once every concurrent group has finished.");
                state.EnqueueSerialRetry(jobItem);
                DrainLog(writer, jobId);
                return;
            }

            // The localized summary crosses to the user; the raw tool output, English, often with
            // the user's own paths, goes to the log and travels back as Detail for the row's
            // "Details" affordance, which the user opens on purpose (SECURITY.md).
            logging.Error($"Worker {job.Action} failed for '{item.Id}'", ex);
            var summary = LocalizationService.Instance[job.Action == InstallAction.Uninstall ? "UninstallFailed" : "InstallFailed"];
            delivered = Send(writer, jobId, Result(item.SourceId, item.Id, false, summary, ex.Message));
        }

        DrainLog(writer, jobId);

        if (!delivered)
            MarkLostApp(state);
    }

    private static void MarkLostApp(JobRunState state)
    {
        state.Logging.Warn("Lost the connection to the app; stopping before the rest of the batch.");
        state.LostApp = true;
    }

    /// <summary>
    /// Same as <c>SemaphoreSlim.WaitAsync</c>, except a stop during the wait ends the group quietly
    /// (false) instead of throwing out of it: the caller's own foreach already checks the token before
    /// every item, this only covers the moment BETWEEN that check and actually getting a slot.
    /// </summary>
    private static async Task<bool> TryEnterAsync(SemaphoreSlim throttle, CancellationToken cancellationToken)
    {
        try
        {
            await throttle.WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static InstallProgressMessage Result(
        string sourceId, string itemId, bool success, string? error, string? detail = null,
        bool handoff = false, bool notApplicable = false, bool cancelled = false, bool versionUnchanged = false) =>
        new()
        {
            Type = "result",
            SourceId = sourceId,
            ItemId = itemId,
            // A terminal non-failure has run its course, so the row's bar belongs at the end, not at zero.
            Percent = success || handoff || notApplicable || cancelled ? 100 : 0,
            Success = success,
            Error = error,
            Detail = detail,
            IsManualHandoff = handoff,
            IsNotApplicable = notApplicable,
            IsCancelled = cancelled,
            VersionUnchanged = versionUnchanged,
        };

    /// <summary>Hands everything logged so far to the app, which is the only side that writes files.</summary>
    private static void DrainLog(StreamWriter writer, string jobId)
    {
        while (ElevatedWorker.TryDequeueLog(out var line))
        {
            if (!Send(writer, jobId, new InstallProgressMessage { Type = "log", Status = line }))
                return;
        }
    }

    /// <summary>Every reply goes through the worker's single write lock, jobs run concurrently.</summary>
    private static bool Send(StreamWriter writer, string jobId, InstallProgressMessage message) =>
        ElevatedWorker.Send(writer, jobId, message);

    /// <summary>Calls back synchronously, unlike <see cref="Progress{T}"/>, see the comment where this is
    /// constructed.</summary>
    private sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
