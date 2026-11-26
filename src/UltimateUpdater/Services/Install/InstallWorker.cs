using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services.Install;

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

    /// <summary>Hard ceiling on the parallel-install budget, whatever the job claims (see <see
    /// cref="InstallJob.MaxParallelInstalls"/>).</summary>
    private const int MaxParallelInstallsCap = 5;

    /// <summary>The source name from the job, kept only when it is a plain name: it lands on an elevated winget
    /// command line, and <see cref="WingetSource.SafeSource"/> checks it once more right there.</summary>
    private static Dictionary<string, string> PackageSourceMeta(string? name) =>
        name is not null && WingetSource.IsSafeSourceName(name)
            ? new Dictionary<string, string> { [WingetSource.SourceMetaKey] = name }
            : new Dictionary<string, string>();

    /// <summary>
    /// How often the log buffer is flushed on a clock, on top of the explicit drains around each step, so a
    /// single slow step (the restore point, one long install) does not sit on its own log lines until it ends.
    /// </summary>
    private static readonly TimeSpan LogFlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The one source allowed to run several of its own items at once. Every other source keeps a single
    /// global install state (npm, pip, scoop, ...) that two installs at once would corrupt, see
    /// <see cref="InstallQueue"/>'s class doc.
    /// </summary>
    private const string ConcurrentSourceId = "winget";

    /// <summary>
    /// Runs one batch. Returns false once the app has stopped listening: installing the rest with admin
    /// rights where nobody can see it is what this worker must not do.
    /// </summary>
    /// <param name="sources">
    /// Test seam only; production always resolves the full registry. It makes the winget-only concurrency
    /// case reachable from a test.
    /// </param>
    /// <param name="itemCancellations">
    /// The per-item stop: keyed by <c>"sourceId:itemId"</c>, filled in while an item runs and read by
    /// <see cref="ElevatedWorker"/>'s "cancel-item" control message. Null means per-item stop is not wired
    /// up; the whole-job token still works.
    /// </param>
    public static async Task<bool> RunJobAsync(
        InstallJob? job, StreamWriter writer, string jobId, LoggingService logging,
        CancellationToken cancellationToken, IReadOnlyList<IUpdateSource>? sources = null,
        ConcurrentDictionary<string, CancellationTokenSource>? itemCancellations = null)
    {
        // An explicit "items": null overwrites the default initializer, so Items.Count alone would throw.
        if (job is null || job.Items is null || job.Items.Count == 0)
        {
            Send(writer, jobId, new InstallProgressMessage { Type = "done" });
            return true;
        }

        // The unelevated app is the only sender, but the pipe is authenticated by process identity, not by
        // trusting its contents: after one approved UAC prompt this worker stays alive for the whole session,
        // so a job with an unexpected source id or action (bug or compromised medium-integrity parent) must
        // never reach a package manager with admin rights. Action and batch size are checked once up front;
        // each item is checked individually in the main loop, and a bad one gets its own failed "result"
        // instead of losing the batch.
        if (!Enum.IsDefined(job.Action))
        {
            logging.Error($"Elevated worker: rejecting job with unknown action '{job.Action}'.");
            Send(writer, jobId, new InstallProgressMessage { Type = "done" });
            return true;
        }

        // A fresh install removes nothing itself but is only ever the second half of a reinstall the user
        // confirmed for ONE package: a job of several, or one naming another source than winget, is not
        // something the app ever sends, so it is refused whole before anything runs with admin rights.
        if (job.Action == InstallAction.FreshInstall
            && (job.Items.Count != 1 || !string.Equals(job.Items[0]?.SourceId, "winget", StringComparison.Ordinal)))
        {
            logging.Error($"Elevated worker: rejecting a fresh install of {job.Items.Count} item(s); exactly one winget package is allowed.");
            Send(writer, jobId, new InstallProgressMessage { Type = "done" });
            return true;
        }

        if (job.Items.Count > MaxItems)
        {
            logging.Error($"Elevated worker: rejecting job with {job.Items.Count} items (limit {MaxItems}).");
            Send(writer, jobId, new InstallProgressMessage { Type = "done" });
            return true;
        }

        // An explicit JSON null element deserializes to a null entry.
        if (job.Items.Any(i => i is null))
        {
            logging.Error("Elevated worker: rejecting job with a null item.");
            Send(writer, jobId, new InstallProgressMessage { Type = "done" });
            return true;
        }

        // this process never went through App.StartUi, so it has no language of its own yet
        LocalizationService.Instance.Apply(job.Language);

        // Disposed on every exit path. The explicit DrainLog calls still fire at each step's boundary; this
        // covers the time spent inside one step.
        using var logFlushTimer = new System.Threading.Timer(
            _ => DrainLog(writer, jobId), null, LogFlushInterval, LogFlushInterval);

        if (job.CreateRestorePoint)
        {
            // Flushed before the slow step starts, so its own lines are not held back until it finishes.
            DrainLog(writer, jobId);
            logging.Info("Creating a restore point before installing.");

            // Said out loud before it starts: the step takes about 20 seconds, and the strip would show the first
            // package at 0 % and look hung. Reported against the first item, the row the strip already shows.
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
                // A damaged WMI repository or an unregistered SystemRestore provider throws past TryCreate's own
                // catch; losing the approved batch over a missing restore point is the wrong trade.
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
            // Same-source items stay sequential (a package manager's global state, see the class doc), except
            // winget, which may run up to the budget of its own items at once. Different source groups run
            // concurrently; ONE semaphore shared by every group keeps the total in flight within the budget.
            var groupTasks = job.Items
                .GroupBy(i => InstallQueue.LaneKey(i.SourceId ?? string.Empty, i.ItemId ?? string.Empty), StringComparer.Ordinal)
                .Select(group => RunGroupAsync(group.First().SourceId ?? string.Empty, group.ToList(), state, throttle))
                .ToList();

            await Task.WhenAll(groupTasks);
        }

        // Windows Installer allows one classic MSI transaction at a time. A refusal for that reason is an
        // expected side effect of this batch's own concurrency, so each such item gets one retry, alone, after
        // every concurrent group has finished.
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

    private sealed class JobRunState(
        StreamWriter writer, string jobId, IReadOnlyList<IUpdateSource> sources, InstallJob job,
        LoggingService logging, CancellationToken cancellationToken,
        ConcurrentDictionary<string, CancellationTokenSource>? itemCancellations)
    {
        public StreamWriter Writer { get; } = writer;
        public string JobId { get; } = jobId;
        public IReadOnlyList<IUpdateSource> Sources { get; } = sources;
        public InstallJob Job { get; } = job;
        public LoggingService Logging { get; } = logging;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public ConcurrentDictionary<string, CancellationTokenSource>? ItemCancellations { get; } = itemCancellations;

        public volatile bool LostApp;

        private readonly ConcurrentQueue<InstallJobItem> _serialRetries = new();

        public void EnqueueSerialRetry(InstallJobItem item) => _serialRetries.Enqueue(item);

        public IEnumerable<InstallJobItem> DrainSerialRetries()
        {
            while (_serialRetries.TryDequeue(out var item))
                yield return item;
        }
    }

    /// <summary>Runs one source's share of the batch. Sequential for every source but
    /// <see cref="ConcurrentSourceId"/>, bounded by the shared <paramref name="throttle"/>.</summary>
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
    /// Validates, resolves and runs exactly one item, reporting its outcome.
    /// </summary>
    /// <param name="isRetry">
    /// True for the one serial retry after an "another installation was already running" refusal; a second
    /// such refusal on that pass is a real failure and is never queued again.
    /// </param>
    private static async Task RunOneItemAsync(InstallJobItem jobItem, JobRunState state, bool isRetry = false)
    {
        var (writer, jobId, job, logging) = (state.Writer, state.JobId, state.Job, state.Logging);

        // This item's own stop button, linked to the whole-job token so either one ends the same await.
        // Registered under "sourceId:itemId" and deregistered on every exit path.
        var itemKey = ItemCancellationKey(jobItem.SourceId, jobItem.ItemId);
        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(state.CancellationToken);

        // A stop that arrived before this item started applies now. One atomic swap, so a stop landing
        // between a separate look-up and the registration cannot be overwritten unseen.
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
        InstallJobItem jobItem, JobRunState state, StreamWriter writer, string jobId, InstallJob job,
        LoggingService logging, CancellationToken cancellationToken, bool isRetry)
    {
        // Flushed before this item starts: an earlier item can still be mid-install when this one begins.
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

        // Never resolved into a real source until it passes the allowlist: a source that does not need
        // elevation (or does not exist) must never reach InstallAsync/UninstallAsync with admin rights.
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
            || (jobItem.PinnedVersion?.Length ?? 0) > MaxFieldLength
            || (jobItem.PackageSource?.Length ?? 0) > MaxFieldLength)
        {
            logging.Error($"Elevated worker: rejecting item '{jobItem.ItemId}', a field exceeds {MaxFieldLength} characters.");
            if (!Send(writer, jobId, Result(jobItem.SourceId, jobItem.ItemId, false, "Rejected: a field exceeds the allowed length.")))
                MarkLostApp(state);

            return;
        }

        // The scope lands on a winget command line: only the machine scope (or none) is sent here. The user
        // scope runs in the app's own process; an elevated copy would be the wrong account.
        if (jobItem.Scope is not null
            && (job.Action != InstallAction.FreshInstall || jobItem.Scope != InstallScopes.Machine))
        {
            logging.Error($"Elevated worker: rejecting item '{jobItem.ItemId}', the scope '{jobItem.Scope}' is not allowed for this job.");
            if (!Send(writer, jobId, Result(jobItem.SourceId, jobItem.ItemId, false, "Rejected: the install scope is not allowed.")))
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
            PinnedVersion = jobItem.PinnedVersion,
            InstallScope = InstallScopes.Normalize(jobItem.Scope),
            Meta = PackageSourceMeta(jobItem.PackageSource),
        };

        // Not System.Progress<T>: it posts to the captured SynchronizationContext, so a report made just
        // before InstallAsync returns could still be queued when the "result" message is sent, and a late
        // post would flip a finished row back to "running". A synchronous IProgress<T> sends every report
        // in order, before the result that follows it.
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
                Phase = p.Phase,
            }));

        bool delivered;
        try
        {
            if (job.Action == InstallAction.Uninstall)
                await source.UninstallAsync(item, progress, cancellationToken);
            else if (job.Action == InstallAction.FreshInstall)
            {
                // Fails closed: a source without the capability never falls back to a normal install.
                if (source is not IFreshInstallSource fresh)
                    throw new InvalidOperationException($"Source '{item.SourceId}' does not support a fresh install.");
                await fresh.FreshInstallAsync(item, progress, cancellationToken);
            }
            else
                await source.InstallAsync(item, progress, cancellationToken);

            // Only for an install, and only a quiet note on a real success, via the same re-check the in-process
            // path uses (InstallQueue.IsVersionUnchangedAsync). Only the immediate look-up happens here: the
            // worker holds its exclusive slot until the job ends, so the delayed look-ups are done by the app.
            var versionUnchanged = job.Action == InstallAction.Install
                && await InstallQueue.IsVersionUnchangedAsync(source, item, jobItem.InstalledVersion, logging, cancellationToken);

            delivered = Send(writer, jobId, Result(item.SourceId, item.Id, true, null, versionUnchanged: versionUnchanged));
        }
        catch (OperationCanceledException) when (state.CancellationToken.IsCancellationRequested)
        {
            // The whole job was stopped; every other row is unwinding too, so there is nothing to report.
            logging.Info($"Worker {job.Action} for '{item.Id}' was stopped.");
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // This item's own stop button; the job keeps running. ProcessRunner already killed the tool's
            // process tree, and a plain failure would show a deliberate stop as an error.
            logging.Info($"Worker {job.Action} for '{item.Id}' was stopped by its own stop button.");
            delivered = Send(writer, jobId, Result(item.SourceId, item.Id, false, null, cancelled: true));
        }
        catch (ManualHandoffException ex)
        {
            // A handoff to a vendor's installer dialog is not a failure. The flag travels with the result;
            // Success is false on the wire, and the app ignores that when the flag is set.
            logging.Info($"Worker {job.Action} for '{item.Id}' was handed off to an external program.");
            delivered = Send(writer, jobId, Result(item.SourceId, item.Id, false, ex.Message, ex.Message, handoff: true));
        }
        catch (NotApplicableException ex)
        {
            // The package manager knows a newer version but refuses it for this installation; nothing went wrong.
            // The source's text is English diagnostic output, so it travels as Detail behind the translated line.
            logging.Info($"Worker {job.Action} for '{item.Id}': no applicable version.");
            delivered = Send(writer, jobId, Result(
                item.SourceId, item.Id, false, LocalizationService.Instance["RowStuckDetail"], ex.Message, notApplicable: true));
        }
        catch (Exception ex)
        {
            // A first attempt refused because another classic MSI transaction is running is an expected side
            // effect of concurrent installs and gets one serial retry. A second refusal (isRetry) is a failure.
            if (!isRetry && string.Equals(jobItem.SourceId, "winget", StringComparison.Ordinal)
                && WingetSource.IsAnotherInstallationRunning(ex.Message))
            {
                logging.Info($"Worker {job.Action} for '{item.Id}': another installation was already running; retrying alone once every concurrent group has finished.");
                state.EnqueueSerialRetry(jobItem);
                DrainLog(writer, jobId);
                return;
            }

            // The localized summary crosses to the user; the raw tool output (English, often with the user's own
            // paths) goes to the log and travels back as Detail, which the user opens on purpose (SECURITY.md).
            logging.Error($"Worker {job.Action} failed for '{item.Id}'", ex);
            var summary = InstallErrorExplainer.RowText(item.SourceId, ex.Message, key => LocalizationService.Instance[key])
                ?? LocalizationService.Instance[job.Action == InstallAction.Uninstall ? "UninstallFailed" : "InstallFailed"];
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
    /// Like <c>SemaphoreSlim.WaitAsync</c>, except a stop during the wait ends the group quietly (false)
    /// instead of throwing. The caller's foreach checks the token before every item; this covers the moment
    /// between that check and getting a slot.
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

    private static bool Send(StreamWriter writer, string jobId, InstallProgressMessage message) =>
        ElevatedWorker.Send(writer, jobId, message);

    /// <summary>Calls back synchronously, unlike <see cref="Progress{T}"/>; see where it is constructed.</summary>
    private sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
