using System.IO;
using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services;

/// <summary>
/// The source is part of the identity, not decoration: item ids are unique per source only, and
/// chocolatey, scoop, pip and npm all offer packages under the same bare names (git, python, 7zip).
/// Keyed on the id alone, two such rows cross-wire; both installs report onto one row and the other
/// stays "queued" for good.
/// </summary>
public sealed record InstallItemStatus(string SourceId, string ItemId, double Percent, string? Status, bool? Success, string? Error)
{
    /// <summary>Only set where the driving tool reports bytes; the UI hides throughput without them.</summary>
    public long? BytesReceived { get; init; }

    public long? BytesTotal { get; init; }

    /// <summary>Raw, unlocalized diagnostic for a failure, shown only when the user asks for it (SECURITY.md).</summary>
    public string? Detail { get; init; }

    /// <summary>The item installed but a reboot is needed to finish (winget's 3010/1641). Not a failure.</summary>
    public bool RebootRequired { get; init; }

    /// <summary>
    /// The source could only open a vendor's own confirmation dialog and cannot see what happened next
    /// (see <see cref="Sources.ManualHandoffException"/>). Neither success nor failure, <see cref="Success"/>
    /// is meaningless when this is true and should not be read.
    /// </summary>
    public bool IsManualHandoff { get; init; }

    /// <summary>The item failed its first attempt and now waits for the batch's automatic second one: the
    /// row's progress starts over instead of keeping the failed attempt's figure.</summary>
    public bool IsAwaitingRetry { get; init; }

    /// <summary>
    /// The package manager offers no version that fits this installation (see
    /// <see cref="Sources.NotApplicableException"/>). Terminal, but not a failure, like
    /// <see cref="IsManualHandoff"/>, it makes <see cref="Success"/> meaningless.
    /// </summary>
    public bool IsNotApplicable { get; init; }

    /// <summary>
    /// The user stopped just this one row with its own stop button; the rest of the queue kept
    /// running. A third terminal non-failure, same rule as <see cref="IsManualHandoff"/> and
    /// <see cref="IsNotApplicable"/>: <see cref="Success"/> is meaningless when this is true.
    /// </summary>
    public bool IsCancelled { get; init; }

    /// <summary>
    /// The source's post-install re-check found the same version that was installed before this
    /// run, on an otherwise successful item. Not a failure, a quiet note; <see cref="Success"/> keeps
    /// its normal meaning when this is true, unlike the three terminal non-failures above.
    /// </summary>
    public bool VersionUnchanged { get; init; }

    /// <summary>The item's outcome is decided: a result was reported, or it ended as a handoff, as not
    /// applicable or as stopped. A plain progress tick is not terminal. An <see cref="Error"/> text on its
    /// own does not make a status terminal here; a caller that treats it so adds that check itself.</summary>
    public bool IsTerminal => Success is not null || IsManualHandoff || IsNotApplicable || IsCancelled;
}

/// <summary>
/// Items are split by whether their source needs admin; the user-scope batch runs in-process, the admin
/// batch is handed to a single elevated worker so the user sees ONE UAC prompt for the whole batch.
/// Within the user-scope batch, different lanes run CONCURRENTLY; only two installs from the SAME
/// lane serialize against each other, since that is where a package manager's own global state could
/// collide (see <see cref="InstallLaneSequentiallyAsync"/>). A lane is a source id, except pip, which has
/// one lane per Python interpreter (<see cref="LaneKey(string, string, IReadOnlyDictionary{string, string}?)"/>).
/// The one exception: winget may run several
/// of its own items at once, bounded like everything else by
/// <see cref="Models.Settings.InstallSettings.MaxParallelInstalls"/>.
/// The admin batch itself always stays one connection, one job, one UAC prompt; the items
/// inside that job run the same grouped/bounded way (<see cref="InstallWorker"/>). The pipe-auth and
/// no-job-file invariants matter more than speed.
/// </summary>
public sealed class InstallQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IReadOnlyList<IUpdateSource> _sources;
    private readonly SettingsService _settings;
    private readonly HistoryService _history;
    private readonly LoggingService _logging;
    private readonly ElevatedHost _elevated;
    private readonly bool _isMockSession;

    /// <summary>
    /// Read-only access to the same per-session flag <see cref="RequiresElevation"/> already
    /// uses, so a caller that has no queue-routing decision of its own to make (the winget bootstrap
    /// offer, which must never probe or touch the real winget in a mock session) can still ask "is this
    /// session real" without duplicating the session's own knowledge of how it was started.
    /// </summary>
    public bool IsMockSession => _isMockSession;

    public event Action<InstallItemStatus>? StatusChanged;

    /// <summary>
    /// Fires once, right before the batch's own automatic second attempt actually starts (there
    /// is something eligible to retry). Purely a UI cue, so the queue panel can show the retry round as
    /// its own visibly separate section.
    /// </summary>
    public event Action? SecondAttemptStarted;

    /// <param name="isMockSession">
    /// True only for a session started with --mock-sources, set once by App.xaml.cs where it decides
    /// whether to build <see cref="Sources.MockUpdateSource.CreateAll"/> instead of the real registry.
    /// Decided per SESSION rather than per source id: a mock row carries the SAME real ids
    /// <see cref="NeedsElevation"/> recognises (winget, chocolatey, windowsupdate), so nothing about a
    /// single item tells a mock row from a real one. While set, every item is routed through the
    /// in-process, user-scope half (see <see cref="RequiresElevation"/>) and the real elevated worker,
    /// which builds its OWN provider set from <see cref="Sources.SourceRegistry"/> and knows nothing of
    /// mock mode, is never started.
    /// </param>
    public InstallQueue(
        IReadOnlyList<IUpdateSource> sources,
        SettingsService settings,
        HistoryService history,
        LoggingService logging,
        ElevatedHost? elevated = null,
        bool isMockSession = false)
    {
        _sources = sources;
        _settings = settings;
        _history = history;
        _logging = logging;
        _isMockSession = isMockSession;
        _elevated = elevated ?? new ElevatedHost(logging);
    }

    /// <summary>
    /// Guards what the parallel source groups share: the reported set and the history file. Everything
    /// else they touch is per-item.
    /// </summary>
    private readonly object _reportLock = new();

    /// <summary>
    /// Guards every <see cref="RunState"/> in <see cref="_activeRuns"/>: the queue panel's drag-and-drop
    /// reorder, and the per-run pending/removed bookkeeping every concurrent <see cref="RunAsync"/> call
    /// owns one of. Not <see cref="_reportLock"/>: reordering must never wait behind a history write.
    /// </summary>
    private readonly object _queueLock = new();

    /// <summary>Test seam: runs between finding a row's stop source and cancelling it, the window in which
    /// the owner could otherwise have released it.</summary>
    internal Action<CancellationTokenSource>? BeforeItemCancelForTest { get; set; }

    /// <summary>Test seam: runs inside the declined-prompt branch, right after a row was recorded as
    /// accounted for.</summary>
    internal Action<UpdateItem>? DeclinedItemRecordedForTest { get; set; }

    /// <summary>Test seam: whether the calling thread holds the lock that guards the shared reported set.</summary>
    internal bool HoldsReportLock => Monitor.IsEntered(_reportLock);

    /// <summary>
    /// One <see cref="RunState"/> per IN-FLIGHT <see cref="RunAsync"/> call. The app makes one call per
    /// batch, but nothing in this class requires that: with a single shared "pending list" field, a second
    /// concurrent call's <c>items.ToList()</c> would clobber the first's, and then dereference a field the
    /// first call's <c>finally</c> had nulled out.
    /// <see cref="Reorder"/> and <see cref="Remove"/> search every active run for the row they were asked
    /// about and act on whichever holds it; a row belongs to exactly one run, since item ids are unique
    /// per source and a source's items never split across two calls.
    /// </summary>
    private readonly List<RunState> _activeRuns = [];

    /// <summary>The state one <see cref="RunAsync"/> call owns: its own not-yet-started items and its own
    /// removed-from-queue keys, never shared with a concurrent call on the same queue.</summary>
    private sealed class RunState
    {
        public List<UpdateItem> Pending = [];
        public readonly HashSet<string> RemovedFromQueue = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The per-item stop, in-process half: one linked <see cref="CancellationTokenSource"/> per
        /// currently-running user-scope item, keyed the same way <see cref="Key(UpdateItem)"/> already
        /// does. Only ever holds items actually in flight; an entry is removed the moment the item's own
        /// try/finally in <see cref="InstallInProcessAsync"/> is done with it.
        /// </summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> ItemCancellations = new();

        /// <summary>Set once this run actually sends an admin batch; <see cref="CancelItem"
        /// forwards to it for anything not found in this run's own <see cref="ItemCancellations"/>.</summary>
        public ElevatedJobHandle? ElevatedHandle;

        /// <summary>
        /// Items whose FIRST-round failure the explainer table marks safe to retry blindly,
        /// keyed the same way every other per-run lookup here is. Populated only during the first round
        /// (both InstallInProcessCoreAsync and HandleWorkerLine take an isRetryRound flag for exactly
        /// this), so a SECOND failure is never queued for a third pass.
        /// </summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, UpdateItem> AutomaticRetryCandidates =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The set of keys this run has accounted for, and the action it runs; set by
        /// <see cref="RunAsync"/> so a stop on a row waiting for the retry can settle it here.</summary>
        public ISet<string>? Reported;
        public InstallAction Action;

        /// <summary>Set (under the queue lock) the moment the retry round's items go back into
        /// <see cref="Pending"/>: from then on a candidate missing from Pending is already being installed
        /// again, and a stop must reach it there rather than being settled as "stopped while waiting".</summary>
        public bool RetryRoundStarted;

        /// <summary>The current elevated batch's items the helper refused as per-user packages, waiting (rows
        /// still showing Running) for their unelevated retry after the job ends. Guarded by the queue lock;
        /// here rather than only local so a stop click can take one out before that retry starts.</summary>
        public List<UpdateItem>? UserScopeRetries;
    }

    /// <summary>
    /// Moves not-yet-started items into the given order, within whichever active run currently holds them.
    /// Items already installing, already finished, or not part of any run at all are left exactly where
    /// they are; this only ever reshuffles what is still sitting in that run's own pending list, so a
    /// stale or partial order from the UI can never drop or duplicate an item.
    /// </summary>
    public void Reorder(IReadOnlyList<(string SourceId, string ItemId)> order)
    {
        lock (_queueLock)
        {
            foreach (var run in _activeRuns)
            {
                if (run.Pending.Count == 0)
                    continue;

                var byKey = run.Pending.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);

                // Only the slice of the caller's order that belongs to THIS run, in the order given;
                // rows belonging to a different run are simply not found here and left untouched.
                var reordered = new List<UpdateItem>(run.Pending.Count);
                foreach (var (sourceId, itemId) in order)
                {
                    if (byKey.Remove($"{sourceId}:{itemId}", out var item))
                        reordered.Add(item);
                }

                // Anything this run holds that the caller's order did not mention (should not happen, the
                // panel always sends every queued row, but a stale snapshot must not silently lose items)
                // keeps its old spot.
                reordered.AddRange(byKey.Values);
                run.Pending = reordered;
            }
        }
    }

    /// <summary>
    /// Drops a not-yet-started item from the queue entirely, the "remove" (X) button on the queue panel.
    /// Silently a no-op if the item already started or was never queued in any active run; the row is the
    /// caller's own source of truth for whether removal makes sense.
    /// </summary>
    public bool Remove(string sourceId, string itemId)
    {
        lock (_queueLock)
        {
            foreach (var run in _activeRuns)
            {
                var index = run.Pending.FindIndex(i =>
                    string.Equals(i.SourceId, sourceId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    continue;

                run.Pending.RemoveAt(index);
                run.RemovedFromQueue.Add($"{sourceId}:{itemId}");
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Stops just this one row, the "stop single" button; the rest of the queue, and the rest of an
    /// admin batch the item might be part of, keeps running. Tried in-process first (this queue's own
    /// user-scope half), then forwarded to whichever active run's elevated job actually holds the item; a
    /// row that already finished, or was never running, is simply ignored, not an error, the item may have
    /// finished between the user's click and this call arriving.
    /// </summary>
    public void CancelItem(string sourceId, string itemId)
    {
        var key = $"{sourceId}:{itemId}";

        List<ElevatedJobHandle> handles;
        RunState? retryRun = null;
        UpdateItem? retryItem = null;
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
                        // The row finished and released its source between the lookup and this call:
                        // nothing is left to stop, which is the "already finished" no-op.
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

            // A row waiting for the automatic retry is in neither handle: it is only a candidate. Taking it
            // out of the candidates (and out of the retry round's pending list, if that already started)
            // is what stops the second attempt.
            foreach (var run in retryRun is null ? _activeRuns : Enumerable.Empty<RunState>())
            {
                if (!run.AutomaticRetryCandidates.ContainsKey(key))
                    continue;

                var pendingIndex = run.Pending.FindIndex(i => string.Equals(Key(i), key, StringComparison.OrdinalIgnoreCase));

                // Already handed to a source group or the elevated job for its second attempt: settling it as
                // cancelled here would not stop it (it would report its real result, and a second history
                // entry, afterwards). It falls through to the elevated handles below instead.
                if (run.RetryRoundStarted && pendingIndex < 0)
                    continue;

                run.AutomaticRetryCandidates.TryRemove(key, out var candidate);
                if (pendingIndex >= 0)
                    run.Pending.RemoveAt(pendingIndex);

                retryRun = run;
                retryItem = candidate;
                break;
            }

            handles = _activeRuns.Where(r => r.ElevatedHandle is not null).Select(r => r.ElevatedHandle!).ToList();
        }

        if (retryRun is not null && retryItem is not null)
        {
            lock (_reportLock)
                retryRun.Reported?.Add(key);

            _logging.Info($"{retryRun.Action} for '{retryItem.Id}' was stopped while waiting for its automatic retry.");
            Report(new InstallItemStatus(retryItem.SourceId, retryItem.Id, 100, null, null, null) { IsCancelled = true });
            RecordHistory(retryItem, retryRun.Action, success: false, error: null, isCancelled: true);
            return;
        }

        foreach (var handle in handles)
            _ = handle.CancelItemAsync(sourceId, itemId);
    }

    /// <summary>Sources whose installs write outside the user's profile and therefore need admin.</summary>
    public static bool NeedsElevation(string sourceId) =>
        sourceId is "winget" or "chocolatey" or "windowsupdate";

    /// <summary>
    /// Installs sharing a lane key run strictly one after another. It is the source id, except pip: each
    /// Python interpreter has its own packages, so pip is one lane per interpreter (path compared without
    /// regard to case).
    /// </summary>
    internal static string LaneKey(string sourceId, string itemId, IReadOnlyDictionary<string, string>? meta = null) =>
        string.Equals(sourceId, "pip", StringComparison.OrdinalIgnoreCase)
            ? $"{sourceId}|{Sources.PackageManagers.PipSource.LaneInterpreter(itemId, meta).ToLowerInvariant()}"
            : sourceId;

    internal static string LaneKey(UpdateItem item) => LaneKey(item.SourceId, item.Id, item.Meta);

    /// <summary>
    /// What THIS queue does with a source id, as opposed to what <see cref="NeedsElevation"/> says about
    /// the id in the abstract. Identical outside a mock session; a mock session never elevates (see the
    /// constructor's note on <c>isMockSession</c>). Every internal split goes through this, never the bare
    /// static check, which stays for its other callers (the queue panel's admin badge,
    /// <c>InstallWorker</c>'s allowlist, the row's elevation icon) that have no session of their own to ask.
    /// </summary>
    private bool RequiresElevation(string sourceId) => !_isMockSession && NeedsElevation(sourceId);

    public async Task RunAsync(
        IReadOnlyList<UpdateItem> items,
        CancellationToken cancellationToken,
        InstallAction action = InstallAction.Install)
    {
        var userScope = items.Where(i => !RequiresElevation(i.SourceId)).ToList();
        var adminScope = items.Where(i => RequiresElevation(i.SourceId)).ToList();

        // What has been accounted for, either way. Anything still missing at the end never ran, and
        // must be reported as such; a cancelled row must never be left showing "installing" forever.
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Each call's own not-yet-started items live in its OWN RunState rather than one field shared
        // (and clobbered) across concurrent calls on this queue instance.
        var run = new RunState { Pending = items.ToList(), Reported = reported, Action = action };
        lock (_queueLock)
            _activeRuns.Add(run);

        try
        {
            // Different package managers do not share state, so npm, pip, scoop and the rest can run at the
            // same time; on a batch that spans several of them this is most of the wall clock.
            // Within ONE manager it stays strictly sequential: npm, pip and scoop each keep a single global
            // install state, and two of their own installs at once corrupt it. Each group pulls its next
            // item from this run's own pending list rather than a fixed snapshot, so a drag-and-drop
            // reorder on the queue panel changes what runs next.
            // One shared semaphore, sized by the same setting as the elevated worker's budget, bounds the
            // TOTAL across every source group here too; the two halves are separate PROCESSES, so the
            // worker keeps its own budget rather than sharing this one.
            var maxParallel = Math.Clamp(_settings.Current.Install.MaxParallelInstalls, 1, 5);
            using var throttle = new SemaphoreSlim(maxParallel, maxParallel);

            var userScopeTask = Task.WhenAll(userScope
                .Select(LaneKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(laneKey => InstallLaneSequentiallyAsync(laneKey, action, reported, throttle, run, cancellationToken)));

            // Started together and awaited together: a slow user-scope package must never delay
            // an admin batch that cannot possibly collide with it, the two halves only ever share a
            // package manager's global state WITHIN one manager, never across the elevation boundary.
            var adminScopeTask = adminScope.Count > 0
                ? RunElevatedBatchAsync(adminScope, action, reported, throttle, run, cancellationToken)
                : Task.CompletedTask;

            await Task.WhenAll(userScopeTask, adminScopeTask);

            // One automatic retry round, still inside this run's own throttle: the batch is
            // "done" from the caller's own point of view only once this has had its chance too. Skipped
            // outright once the whole run was stopped: a cancellation is already unwinding every row the
            // same way, retrying into that would just fight the shutdown.
            if (!cancellationToken.IsCancellationRequested)
                await RunAutomaticRetryRoundAsync(action, reported, throttle, run, cancellationToken);
        }
        finally
        {
            lock (_queueLock)
                _activeRuns.Remove(run);

            var cancelled = cancellationToken.IsCancellationRequested;
            var reason = cancelled
                ? LocalizationService.Instance["InstallCancelled"]
                : LocalizationService.Instance["InstallWorkerStopped"];

            // A cancelled batch says nothing about what actually happened: the package that was running is
            // killed mid-install, and whether it got far enough to count is not ours to guess. Writing
            // "failed" into the history, the only record there is, would be a claim we cannot make, so
            // the rows are closed out without one. An item the user removed from the queue is neither: it
            // never ran because it was deliberately taken out, not because the batch stopped.
            FailRemaining(items, action, reported, reason, cancelled, run);
        }
    }

    /// <summary>
    /// The automatic retry's own second pass: everything <see cref="RunState.AutomaticRetryCandidates"/> collected
    /// during the FIRST round, minus anything the user removed from the queue in the meantime, run once
    /// more through the exact same in-process/elevated split - reusing THIS run's own throttle and
    /// pending list, so a drag-and-drop reorder or a stop mid-retry behaves exactly like the first round.
    /// </summary>
    private async Task RunAutomaticRetryRoundAsync(
        InstallAction action, HashSet<string> reported, SemaphoreSlim throttle, RunState run, CancellationToken cancellationToken)
    {
        List<UpdateItem> retryItems;
        lock (_queueLock)
        {
            var removed = run.RemovedFromQueue;
            retryItems = run.AutomaticRetryCandidates.Values.Where(i => !removed.Contains(Key(i))).ToList();

            // Snapshot, flag and enqueue in ONE lock, so CancelItem never sees a started round whose items
            // are not in Pending yet.
            if (retryItems.Count > 0)
            {
                run.RetryRoundStarted = true;
                run.Pending.AddRange(retryItems);
            }
        }

        if (retryItems.Count == 0)
            return;

        _logging.Info($"Retrying {retryItems.Count} failed package(s) once, as the batch's own second attempt.");
        SecondAttemptStarted?.Invoke();

        var retryAdminScope = retryItems.Where(i => RequiresElevation(i.SourceId)).ToList();

        var retryUserScopeTask = Task.WhenAll(retryItems
            .Where(i => !RequiresElevation(i.SourceId))
            .Select(LaneKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(laneKey => InstallLaneSequentiallyAsync(laneKey, action, reported, throttle, run, cancellationToken, isRetryRound: true)));

        var retryAdminScopeTask = retryAdminScope.Count > 0
            ? RunElevatedBatchAsync(retryAdminScope, action, reported, throttle, run, cancellationToken, isRetryRound: true)
            : Task.CompletedTask;

        await Task.WhenAll(retryUserScopeTask, retryAdminScopeTask);
    }

    private async Task InstallLaneSequentiallyAsync(
        string laneKey, InstallAction action, ISet<string> reported, SemaphoreSlim throttle,
        RunState run, CancellationToken cancellationToken, bool isRetryRound = false)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The slot first, THEN the item: an item taken out of Pending before waiting for a slot sat in
            // neither Pending nor ItemCancellations while it waited (four or more sources against the default
            // of three slots), so its row could not be removed, reordered or stopped, and installed anyway.
            // InstallInProcessAsync registers its stop token before its first await, so from here on the
            // item is always reachable.
            await throttle.WaitAsync(cancellationToken);
            UpdateItem item;
            try
            {
                lock (_queueLock)
                {
                    var index = run.Pending.FindIndex(i => string.Equals(LaneKey(i), laneKey, StringComparison.OrdinalIgnoreCase));
                    if (index < 0)
                        return;

                    item = run.Pending[index];
                    run.Pending.RemoveAt(index);
                }

                await InstallInProcessAsync(item, action, run, cancellationToken, isRetryRound);
            }
            finally
            {
                throttle.Release();
            }

            // A first-round failure waiting for the retry round has no final outcome yet. Left out of
            // reported, so FailRemaining still closes its row if the batch ends before that round runs.
            if (isRetryRound || !run.AutomaticRetryCandidates.ContainsKey(Key(item)))
            {
                lock (_reportLock)
                    reported.Add(Key(item));
            }
        }
    }

    private async Task InstallInProcessAsync(
        UpdateItem item, InstallAction action, RunState run, CancellationToken cancellationToken, bool isRetryRound = false)
    {
        // This item's own stop button, layered on top of the run's whole-batch token via a linked
        // CTS, so either one ends the same await; registered under the same key CancelItem looks up, and
        // deregistered in every exit path via the outer try/finally.
        var key = Key(item);
        var itemCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        run.ItemCancellations[key] = itemCts;
        var itemToken = itemCts.Token;

        try
        {
            await InstallInProcessCoreAsync(item, action, cancellationToken, itemToken, run, isRetryRound);
        }
        finally
        {
            // Removed and disposed under the lock CancelItem cancels under, so a stop click can never reach
            // a source that was disposed a moment earlier.
            lock (_queueLock)
            {
                run.ItemCancellations.TryRemove(key, out _);
                itemCts.Dispose();
            }
        }
    }

    private async Task InstallInProcessCoreAsync(
        UpdateItem item, InstallAction action, CancellationToken batchToken, CancellationToken cancellationToken,
        RunState run, bool isRetryRound = false)
    {
        // A fresh attempt of this item (the retry round, the user-scope retry after the admin
        // side refuses it) must not inherit a transfer entry an earlier attempt already marked finished.
        ResetTransfer(item);

        // A Progress<T> without a captured context delivers through the thread pool, so a callback from an
        // earlier attempt of this row can still be in flight when this one starts. It carries its attempt
        // number and is dropped once the row has moved on to a later one.
        var attemptKey = ActivityLog.Item(item.SourceId, item.Id);
        var attempt = _attempts.AddOrUpdate(attemptKey, 1, (_, previous) => previous + 1);

        var source = _sources.FirstOrDefault(s => s.Id == item.SourceId);
        if (source is null)
        {
            // Recorded like any other outcome: the elevated path already does, and the history is the only
            // place the user can go back and see that this row was attempted at all.
            var unknown = $"Unknown source '{item.SourceId}'.";
            var unknownDetail = $"{item.SourceId}/{item.Id}: {action} failed; no source named '{item.SourceId}' is registered.";
            Report(new InstallItemStatus(item.SourceId, item.Id, 0, null, false, unknown)
            {
                // Same rule as a real tool failure: an error state without a usable Detail leaves the row's
                // error-detail button opening on nothing.
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
            });
        });

        var started = System.Diagnostics.Stopwatch.StartNew();
        _logging.Event(ActivityLog.Install, ActivityLog.InstallStarted(item, action, elevated: false));

        try
        {
            if (action == InstallAction.Uninstall)
                await source.UninstallAsync(item, progress, cancellationToken);
            else
                await source.InstallAsync(item, progress, cancellationToken);

            // Only meaningful for an install (there is no "installed version" to re-check after
            // a removal), and only a quiet note that never turns a real success into anything else.
            // A source that cannot answer (null) says nothing at all, exactly like the interface default.
            var versionUnchanged = action == InstallAction.Install
                && await IsVersionUnchangedAsync(source, item, item.InstalledVersion, _logging, cancellationToken);

            // Report() below closes this item's own download line (if it had one) and writes
            // its "dl done" log line; it runs BEFORE the "ok" line so the log reads download-then-install
            // rather than the other way round, which would leave a finished row with a
            // part-filled bar.
            Report(new InstallItemStatus(item.SourceId, item.Id, 100, null, true, null) { VersionUnchanged = versionUnchanged });
            _logging.Event(ActivityLog.Install, ActivityLog.InstallSucceeded(item, action, started.Elapsed));
            RecordHistory(item, action, success: true, error: null, versionUnchanged: versionUnchanged);
        }
        catch (OperationCanceledException) when (batchToken.IsCancellationRequested)
        {
            // Only the USER's whole-queue cancellation propagates and ends the batch. A package manager's
            // own HTTP timeout also surfaces as a cancellation (TaskCanceledException) with our tokens
            // untouched, rethrowing that would abandon every remaining package because one download ran
            // long.
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // This item's own stop button; the rest of the queue keeps running. A terminal
            // non-failure like the two below, not a real failure, the user just chose to stop this one.
            _logging.Info($"{action} for '{item.Id}' was stopped by its own stop button.");
            Report(new InstallItemStatus(item.SourceId, item.Id, 100, null, null, null) { IsCancelled = true });
            RecordHistory(item, action, success: false, error: null, isCancelled: true);
        }
        catch (Sources.ManualHandoffException ex)
        {
            // Genuinely unknown, not a guess in either direction: the source opened a vendor dialog that
            // reports nothing back. Recorded and shown as its own outcome, never folded into success
            // (that already happened once, and reported a cancelled Steam removal as completed) or into
            // failure (nothing is actually known to have gone wrong).
            _logging.EventFailed(ActivityLog.Install, ActivityLog.InstallStuck(item, "handed off"));
            Report(new InstallItemStatus(item.SourceId, item.Id, 100, ex.Message, null, null) { IsManualHandoff = true });
            RecordHistory(item, action, success: false, error: ex.Message, isManualHandoff: true);
        }
        catch (Sources.NotApplicableException ex)
        {
            // Nothing was changed and nothing went wrong: the manager simply has no version that fits this
            // installation. Its own outcome for the same reason as the handoff above. The source's text is
            // English diagnostic output, so the user gets the translated line and the raw text stays the
            // Detail, as for a failure.
            _logging.EventFailed(ActivityLog.Install, ActivityLog.InstallStuck(item, "no applicable version"));
            var stuck = LocalizationService.Instance["RowStuckDetail"];
            Report(new InstallItemStatus(item.SourceId, item.Id, 100, null, null, stuck) { IsNotApplicable = true, Detail = ex.Message });
            RecordHistory(item, action, success: false, error: stuck, isNotApplicable: true, detail: ex.Message);
        }
        catch (Exception ex)
        {
            // One failing package must never abort the rest of the queue. The exception text is raw
            // tool output; it is English and routinely contains the user's own paths, so it goes to
            // the log and the user gets a translated line instead, plus the raw detail for the row's
            // "Details" affordance, which the user opens deliberately (SECURITY.md).
            _logging.EventFailed(ActivityLog.Install, ActivityLog.InstallFailed(item, action, started.Elapsed, ex.Message));
            _logging.Error($"{action} failed for '{item.Id}'", ex);
            var message = LocalizationService.Instance[action == InstallAction.Uninstall ? "UninstallFailed" : "InstallFailed"];

            // Only from the FIRST round (a second failure is final, no third pass), and only for
            // a cause the explainer table itself marks safe to retry blindly. Such a failure is not the
            // row's final outcome yet, so it is not reported as one.
            if (!isRetryRound && InstallErrorExplainer.Explain(item.SourceId, ex.Message)?.AllowsAutomaticRetry == true)
            {
                run.AutomaticRetryCandidates[Key(item)] = item;
                ReportAwaitingRetry(item);
            }
            else
            {
                Report(new InstallItemStatus(item.SourceId, item.Id, 0, null, false, message) { Detail = ex.Message });
            }

            RecordHistory(item, action, success: false, error: message, detail: ex.Message);
        }
    }

    /// <summary>
    /// The job travels over the pipe rather than through a file in %TEMP%. A file would sit there
    /// between being written and being read by the ELEVATED copy, where any other process running as
    /// this user could rewrite it and have its own choice of packages installed with admin rights on
    /// the back of a UAC prompt the user granted to this app.
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
        // Re-read the current order (and drop anything the user removed from the queue in the meantime)
        // right before this batch is built: this now starts alongside the user-scope call rather than
        // after it, but a queue-panel reorder or removal can still land in the brief window
        // between this run being registered and this line running.
        lock (_queueLock)
        {
            var adminKeys = new HashSet<string>(items.Select(Key), StringComparer.OrdinalIgnoreCase);
            items = run.Pending.Where(i => adminKeys.Contains(Key(i))).ToList();
            run.Pending.RemoveAll(i => adminKeys.Contains(Key(i)));
        }

        if (items.Count == 0)
            return;

        // Same reset as the in-process path, per item, at the start of this batch (which is
        // also where a retry round's own batch begins).
        foreach (var item in items)
            ResetTransfer(item);

        _logging.Event(ActivityLog.Install, ActivityLog.BatchStarted(items.Count, elevated: true));

        // The DECISION is made here, before the job goes out, and travels as a plain flag; the
        // worker obeys it and never reads the timestamp file itself: it does no file I/O of its own at all.
        // "The toggle is on, but one was already made today" is not a failure, just nothing left to say to
        // the user beyond the one calm log line below.
        var wantsRestorePoint = action == InstallAction.Install && _settings.Current.Install.CreateRestorePoint;
        // Decided AND reserved in one step: a second batch deciding while this one's restore point is still
        // being created must not read the old timestamp and ask for another. The lease lasts for the whole
        // batch and is handed back however it ends (a job that never went out, a declined prompt, a failure).
        using var restorePointLease = wantsRestorePoint
            ? RestorePointReservation.Shared.TryReserve(() => RestorePointThrottle.TryReadLastCreated(_logging), DateTimeOffset.UtcNow)
            : null;
        var shouldCreateRestorePoint = restorePointLease is not null;
        if (wantsRestorePoint && !shouldCreateRestorePoint)
        {
            _logging.Info("Restore point skipped: one was already created within the last 24 hours.");
            _logging.Event(ActivityLog.Install, LocalizationService.Instance["ActivityRestorePointSkipped"]);
        }

        var job = new InstallJobFile
        {
            Action = action,
            // a restore point belongs before an install; there is nothing to roll forward on a removal
            CreateRestorePoint = shouldCreateRestorePoint,
            // the worker is a separate process and would otherwise report in the OS language
            Language = _settings.Current.Ui.Language,
            // The worker keeps its OWN budget rather than sharing the user-scope half's semaphore, the two
            // are separate processes running side by side, so the worker gets the FULL setting value here,
            // not reduced by anything this half is running at the same time; it clamps this itself
            // regardless of what is sent.
            MaxParallelInstalls = _settings.Current.Install.MaxParallelInstalls,
            Items = items.Select(i => new InstallJobItem
            {
                SourceId = i.SourceId,
                ItemId = i.Id,
                Name = i.Name,
                InstalledVersion = i.InstalledVersion,
                AvailableVersion = i.AvailableVersion,
                PinnedVersion = i.PinnedVersion,
            }).ToList(),
        };

        // The elevated helper is a separate process: whatever it reads, it reads from disk. Settings the
        // user changed a moment ago may still be sitting in the deferred save, so they go out first.
        _settings.Flush();

        // The prompt, if this is the first admin job of the session. After that the same elevated helper
        // takes every later batch without asking again.
        if (!await _elevated.EnsureStartedAsync(cancellationToken))
        {
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

        // Per call, not a field: the reply handler runs on the pipe's reader thread, and one batch's
        // retries must never be visible to another's.
        var userScopeRetries = new List<UpdateItem>();
        lock (_queueLock)
            run.UserScopeRetries = userScopeRetries;

        // This run's own stop-single button forwards here once nothing in its in-process
        // ItemCancellations dictionary matches; cleared again once the batch is over so a stale handle
        // from a finished job is never mistaken for a live one.
        var jobHandle = new ElevatedJobHandle();
        run.ElevatedHandle = jobHandle;
        try
        {
            await _elevated.RunAsync(
                "install", job, line => HandleWorkerLine(line, items, action, reported, userScopeRetries, run, isRetryRound),
                cancellationToken, handle: jobHandle);
        }
        finally
        {
            run.ElevatedHandle = null;
        }

        // Jobs the elevated helper had to refuse because the package belongs to this user rather than to
        // the machine. Retried here, unelevated, which is the one thing that can actually work, and only
        // once: if the in-process attempt fails too, that is the answer.
        await RetryUserScopeInProcessAsync(userScopeRetries, action, reported, throttle, run, cancellationToken);
    }

    private async Task RetryUserScopeInProcessAsync(
        List<UpdateItem> userScopeRetries, InstallAction action, ISet<string> reported, SemaphoreSlim throttle,
        RunState run, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // An in-process install like any other, so it takes a slot of the same budget the user-scope
            // source groups share; otherwise it would run on top of a full set of them. The slot first, THEN
            // the item (same order as the source groups): taken out of the list before waiting, a row stopped
            // during the wait was in neither the list nor ItemCancellations, and installed anyway.
            await throttle.WaitAsync(cancellationToken);
            UpdateItem item;
            try
            {
                // Taken one at a time under the lock CancelItem uses, so a row stopped while it waited is gone.
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
                }

                _logging.Info($"Retrying '{item.Id}' ({action}) without administrator rights.");

                // Flagged as a retry round: this is already the item's second attempt, so a failure here is
                // final and must not enrol it in the automatic retry pass for a third one.
                await InstallInProcessAsync(item, action, run, cancellationToken, isRetryRound: true);
            }
            finally
            {
                throttle.Release();
            }

            lock (_reportLock)
                reported.Add(Key(item));
        }
    }

    /// <summary>
    /// One line back from the elevated worker. Returns false once the batch is finished, which is what
    /// tells the host this conversation is over.
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

        // The elevated worker writes no files of its own (it would be writing into directories the
        // unelevated user controls), so its log lines come back here and land in the app's log.
        if (message.Type == "log")
        {
            _logging.Info($"worker: {message.Status}");
            return true;
        }

        // The worker only ever REPORTS this, it never decides or writes anything from it; this
        // side owns every file the app ever writes.
        if (message.Type == "restorePointCreated")
        {
            RestorePointThrottle.TryWriteCreated(_logging, message.RestorePointCreatedAt ?? DateTimeOffset.UtcNow);
            return true;
        }

        // A handed-off, not-applicable or cancelled result is neither success nor failure, so Success must
        // arrive as null on this side however the wire spells it.
        var terminalNonFailure = message.IsManualHandoff || message.IsNotApplicable || message.IsCancelled;

        // A package installed per-user refuses to be touched by an administrator, and the whole
        // point of the elevated helper is that it IS one. Rather than leaving the user with a
        // message they can do nothing about, the job is retried in this process, once. Decided
        // BEFORE the result is reported: reported first, the row would flash Failed, count as done and
        // feed the three-strike record with a refusal that is not the final answer.
        // Installs as well as removals: winget's error for this says "the
        // requested operation", and an upgrade of a user-scope package fails exactly the same way,
        // which is why Bitcoin Core never updated.
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

        // A genuine failure only (not the handed-off/not-applicable/cancelled outcomes above), only from
        // the FIRST round, and only for a cause the explainer table itself marks safe to retry blindly.
        // Decided before anything is reported, for the same reason as the user-scope refusal above: the
        // retry round, not this attempt, gives the final answer.
        var awaitsRetry = item is not null && message.Success == false && !terminalNonFailure && !isRetryRound
            && InstallErrorExplainer.Explain(item.SourceId, message.Detail)?.AllowsAutomaticRetry == true;

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
                Detail = message.Detail,
                RebootRequired = message.RebootRequired,
                IsManualHandoff = message.IsManualHandoff,
                IsNotApplicable = message.IsNotApplicable,
                IsCancelled = message.IsCancelled,
                VersionUnchanged = message.VersionUnchanged,
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

            RecordHistory(
                item, action, message.Success && !terminalNonFailure, message.Error,
                message.IsManualHandoff, message.IsNotApplicable, detail: message.Detail,
                isCancelled: message.IsCancelled, versionUnchanged: message.VersionUnchanged);

            if (awaitsRetry)
            {
                // Left out of reported, so FailRemaining still closes the row if the retry round never runs.
                run.AutomaticRetryCandidates[Key(item)] = item;
            }
            else
            {
                // This callback runs on the elevated pipe's own reader thread (ElevatedHost's pump task),
                // while the in-process source groups mutate the SAME reported set from their own Task
                // threads at the same time (RunAsync starts both halves together). reported is a plain
                // HashSet, not thread-safe, so every write to it needs this same lock.
                lock (_reportLock)
                    reported.Add(Key(item));
            }
        }

        return true;
    }

    private void FailRemaining(
        IReadOnlyList<UpdateItem> items, InstallAction action, ISet<string> reported, string reason,
        bool cancelled, RunState run)
    {
        // An item the user removed from the queue is not "remaining", it was taken out on purpose, so it
        // gets neither a status report nor a history line, exactly as if it had never been selected.
        HashSet<string> removed;
        lock (_queueLock)
            removed = new HashSet<string>(run.RemovedFromQueue, StringComparer.OrdinalIgnoreCase);

        foreach (var item in items.Where(i => !reported.Contains(Key(i)) && !removed.Contains(Key(i))))
        {
            // A cancelled batch never ran these rows, so they end as stopped (neither success nor failure),
            // the same terminal state a row stopped with its own button gets.
            Report(new InstallItemStatus(item.SourceId, item.Id, cancelled ? 100 : 0, null, cancelled ? null : false, reason)
            {
                Detail = $"{item.SourceId}/{item.Id}: {action} did not run; {reason}",
                IsCancelled = cancelled,
            });

            if (!cancelled)
                RecordHistory(item, action, success: false, error: reason);
        }
    }

    /// <summary>Identity of a row across the whole queue, see the note on <see cref="InstallItemStatus"/>.</summary>
    private static string Key(UpdateItem item) => $"{item.SourceId}:{item.Id}";

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
            // Thrown on, it turned a completed upgrade into "Cancelled" (or into no history entry at all),
            // and that success is what a later rollback is built from.
            return false;
        }
        catch (Exception ex)
        {
            // Same reasoning for a lookup that fails afterwards (a refused tool path, a timeout): the
            // install is done, the re-check only decides whether a note is added.
            logging.Warn($"Version re-check for '{item.Id}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The re-check comparison, shared with <see cref="InstallWorker"/> (the elevated path) so both read the
    /// same answer to the same question. Null from the source means "no statement", never "unchanged":
    /// only an ACTUAL match against the pre-install value counts.
    /// </summary>
    internal static bool IsVersionUnchanged(string? previousInstalledVersion, string? recheckedVersion) =>
        recheckedVersion is not null && string.Equals(recheckedVersion, previousInstalledVersion, StringComparison.OrdinalIgnoreCase);

    private void RecordHistory(
        UpdateItem item, InstallAction action, bool success, string? error, bool isManualHandoff = false,
        bool isNotApplicable = false, string? detail = null, bool isCancelled = false, bool versionUnchanged = false)
    {
        var entry = HistoryEntry.ForItem(
            item, action, success, error, isManualHandoff, isNotApplicable, detail, isCancelled, versionUnchanged);

        lock (_reportLock)
            _history.Append(entry);
    }

    /// <summary>
    /// What a download of this item has cost so far, and whether it is already finished. Every status
    /// report, in-process and from the elevated worker alike, passes through <see cref="Report"/>, the one
    /// place that can see a transfer begin, progress and end without each source reporting it separately.
    /// Kept, marked finished, rather than removed once a transfer ends: a stray progress report for the
    /// SAME item can still arrive afterward (Progress&lt;T&gt; with no captured synchronization context
    /// posts through the thread pool, so a callback queued before the item's own result can be delivered
    /// after it), and only a still-present, marked entry lets that report be recognised as stale rather
    /// than read as a brand new transfer.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TransferState> _transfers = new();

    /// <summary>How many times each row has been started in-process; see the progress callback in
    /// <see cref="InstallInProcessCoreAsync"/>.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _attempts = new();

    private sealed record TransferState(DateTime Started, long Total, bool Finished, long Received = 0);

    /// <summary>
    /// Clears any tracked transfer for this item before a new attempt of it starts (the in-process path,
    /// and each item of a fresh elevated batch, including a retry round). Otherwise a retry of the same
    /// item would find its old entry still marked <c>Finished</c> and read every progress report as
    /// stale, so the row would show no percentage or byte count the second time around.
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
            // This item's own result already went out; a progress line that still arrives after
            // that is stale by construction. Applying it would drag a row that already reads "done" back
            // to a part-filled bar, and logging it would print a later download line for a transfer that
            // already has its "dl done" line. Dropped outright: neither reported to the row nor logged.
            return;
        }
        else if (status.BytesTotal is > 0)
        {
            // The first report that carries a real size is the start of a download; later ones keep the
            // same started time but still update the received count, so a download failing partway
            // through has a real, non-zero byte count to fall back on in CloseTransfer below.
            // One lookup: a second one after ContainsKey could miss an entry ResetTransfer removed in
            // between on another thread, and the throw would end the process from a Progress callback.
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
    /// A first-round failure the automatic retry round will run again. Reported as still in progress,
    /// never as Failed: a row that read as finished here could be retried by hand while the queue retries
    /// it too, be replaced by a rescan before the second attempt reports, and count its failure twice
    /// toward the three-strikes tally. The attempt's transfer is closed as a failure would close it, so a
    /// late progress line from it is still recognised as stale.
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
    /// Closes a tracked transfer in the SAME step as the item's own completion, logging its "dl done" line
    /// and, when the terminal status carries no bytes of its own (a plain success never does, see the
    /// in-process callers of <see cref="Report"/>), filling in BytesReceived/BytesTotal so the row's bar
    /// reaches full together with the row reporting finished, rather than waiting on a progress line a
    /// successful install never sends. An item with nothing tracked (it never reported a size, or its
    /// transfer already closed) is returned unchanged.
    /// The fill-in only claims the FULL size for a genuine success. A failed/cancelled/handed-off status
    /// without bytes keeps the last real byte count, so a download that failed at 50 % still reads
    /// roughly 50 %, and the batch total only counts bytes actually received.
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
