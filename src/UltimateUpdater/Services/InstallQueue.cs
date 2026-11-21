using System.IO;
using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services;

/// <summary>
/// The source is part of the identity: item ids are unique per source only, and chocolatey, scoop, pip and
/// npm all offer packages under the same bare names (git, python, 7zip). Keyed on the id alone, two such
/// rows would cross-wire.
/// </summary>
public sealed record InstallItemStatus(string SourceId, string ItemId, double Percent, string? Status, bool? Success, string? Error)
{
    /// <summary>Only set where the driving tool reports bytes; the UI hides throughput without them.</summary>
    public long? BytesReceived { get; init; }

    public long? BytesTotal { get; init; }

    /// <summary>Download or install stage as far as the tool's output shows it; Unknown when it does not.</summary>
    public Models.InstallPhase Phase { get; init; }

    /// <summary>Raw, unlocalized diagnostic for a failure, shown only when the user asks for it
    /// (SECURITY.md).</summary>
    public string? Detail { get; init; }

    /// <summary>The item installed but a reboot is needed to finish (winget's 3010/1641). Not a failure.</summary>
    public bool RebootRequired { get; init; }

    /// <summary>
    /// The source could only open a vendor's own confirmation dialog and cannot see what happened next (see
    /// <see cref="Sources.ManualHandoffException"/>). Neither success nor failure: <see cref="Success"/> is
    /// meaningless when this is true.
    /// </summary>
    public bool IsManualHandoff { get; init; }

    /// <summary>The item failed its first attempt and now waits for the batch's automatic second one: the
    /// row's progress starts over instead of keeping the failed attempt's figure.</summary>
    public bool IsAwaitingRetry { get; init; }

    /// <summary>
    /// The package manager offers no version that fits this installation (see
    /// <see cref="Sources.NotApplicableException"/>). Terminal but not a failure: like
    /// <see cref="IsManualHandoff"/>, it makes <see cref="Success"/> meaningless.
    /// </summary>
    public bool IsNotApplicable { get; init; }

    /// <summary>
    /// The user stopped just this row with its own stop button; the rest of the queue keeps running. A
    /// terminal non-failure like <see cref="IsManualHandoff"/> and <see cref="IsNotApplicable"/>:
    /// <see cref="Success"/> is meaningless when this is true.
    /// </summary>
    public bool IsCancelled { get; init; }

    /// <summary>
    /// The source's post-install re-check found the version that was installed before this run, on an
    /// otherwise successful item. A quiet note, not a failure; <see cref="Success"/> keeps its meaning,
    /// unlike the three terminal non-failures above.
    /// </summary>
    public bool VersionUnchanged { get; init; }

    /// <summary>With <see cref="VersionUnchanged"/>: how many times this item has ended unchanged for the same
    /// versions, this one included (read from the history before this result is recorded). 0 when not
    /// unchanged.</summary>
    public int VersionUnchangedCount { get; init; }

    /// <summary>The item's outcome is decided: a result was reported, or it ended as a handoff, as not
    /// applicable or as stopped. A plain progress tick is not terminal. An <see cref="Error"/> text on its
    /// own does not make a status terminal here; a caller that treats it so adds that check itself.</summary>
    public bool IsTerminal => Success is not null || IsManualHandoff || IsNotApplicable || IsCancelled;
}

/// <summary>
/// Items are split by whether their source needs admin; the user-scope batch runs in-process, the admin
/// batch goes to a single elevated worker so the user sees ONE UAC prompt for the whole batch.
/// Within the user-scope batch, different lanes run concurrently; two installs from the same lane
/// serialize, since that is where a package manager's global state could collide (see
/// <see cref="InstallLaneSequentiallyAsync"/>). A lane is a source id, except pip, which has one lane per
/// Python interpreter (<see cref="LaneKey(string, string, IReadOnlyDictionary{string, string}?)"/>).
/// The exception: winget may run several of its own items at once, bounded by
/// <see cref="Models.Settings.InstallSettings.MaxParallelInstalls"/>.
/// The admin batch always stays one connection, one job, one UAC prompt; its items run the same
/// grouped/bounded way (<see cref="InstallWorker"/>). The pipe-auth and no-job-file invariants matter
/// more than speed.
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
    /// Read-only access to the per-session flag <see cref="RequiresElevation"/> uses, so a caller with no
    /// routing decision of its own (the winget bootstrap offer, which must never touch the real winget in a
    /// mock session) can ask whether this session is real.
    /// </summary>
    public bool IsMockSession => _isMockSession;

    public event Action<InstallItemStatus>? StatusChanged;

    /// <summary>The reference the elevated worker gets for one item: only what it needs to rebuild the item.</summary>
    internal static InstallJobItem ToJobItem(UpdateItem item, InstallAction action) => new()
    {
        SourceId = item.SourceId,
        ItemId = item.Id,
        Name = item.Name,
        InstalledVersion = item.InstalledVersion,
        AvailableVersion = item.AvailableVersion,
        PinnedVersion = item.PinnedVersion,
        Scope = action == InstallAction.FreshInstall ? item.InstallScope : null,
        PackageSource = item.SourceId == "winget" ? Sources.WingetSource.SafeSource(item) : null,
    };

    /// <summary>
    /// Fires once, right before the batch's automatic second attempt starts (when something is eligible to
    /// retry). Purely a UI cue so the queue panel can show the retry round as its own section. Install runs only.
    /// </summary>
    public event Action? SecondAttemptStarted;

    /// <param name="isMockSession">
    /// True only for a session started with --mock-sources, set once by App.xaml.cs. Decided per session
    /// rather than per source id: a mock row carries the same real ids <see cref="NeedsElevation"/>
    /// recognises (winget, chocolatey, windowsupdate), so nothing about an item tells mock from real. While
    /// set, every item goes through the in-process, user-scope half (see <see cref="RequiresElevation"/>) and
    /// the elevated worker, which builds its own provider set and knows nothing of mock mode, is never started.
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
    /// Guards what the parallel source groups share: the reported set and the history file.
    /// </summary>
    private readonly object _reportLock = new();

    /// <summary>
    /// Guards every <see cref="RunState"/> in <see cref="_activeRuns"/>: the queue panel's reorder and the
    /// per-run pending/removed bookkeeping. Not <see cref="_reportLock"/>: reordering must never wait behind
    /// a history write.
    /// </summary>
    private readonly object _queueLock = new();

    /// <summary>One gate per lane key (case-insensitive), shared by every concurrent run, so two runs never
    /// touch the same package manager's global state at once. See <see cref="LaneKey(UpdateItem)"/>.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _laneGates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test seam: runs between finding a row's stop source and cancelling it, the window in which
    /// the owner could otherwise have released it.</summary>
    internal Action<CancellationTokenSource>? BeforeItemCancelForTest { get; set; }

    /// <summary>Test seam: runs inside the declined-prompt branch, right after a row was recorded as
    /// accounted for.</summary>
    internal Action<UpdateItem>? DeclinedItemRecordedForTest { get; set; }

    /// <summary>Test seam: runs in <see cref="CancelItem"/> after a waiting row was taken out of the queue and
    /// before it is settled, the window in which the run could otherwise end and report the row as well.</summary>
    internal Action<UpdateItem>? AfterPendingStopForTest { get; set; }

    /// <summary>Test seam: runs right after an in-process row left the pending list, with its stop source
    /// already registered.</summary>
    internal Action<UpdateItem>? AfterItemTakenForTest { get; set; }

    /// <summary>Test seam: runs right after an admin batch took its rows out of the pending list, before the
    /// restore point decision, the settings flush and the elevation prompt.</summary>
    internal Action<IReadOnlyList<UpdateItem>>? AfterAdminRowsTakenForTest { get; set; }

    /// <summary>Test seam: whether the calling thread holds the lock that guards the shared reported set.</summary>
    internal bool HoldsReportLock => Monitor.IsEntered(_reportLock);

    /// <summary>
    /// One <see cref="RunState"/> per in-flight <see cref="RunAsync"/> call. A single shared pending-list
    /// field would let a second concurrent call clobber the first's.
    /// <see cref="Reorder"/> and <see cref="Remove"/> search every active run for the row and act on whichever
    /// holds it; a row belongs to exactly one run, since item ids are unique per source and a source's items
    /// never split across two calls.
    /// </summary>
    private readonly List<RunState> _activeRuns = [];

    /// <summary>The state one <see cref="RunAsync"/> call owns: its own not-yet-started items and its own
    /// removed-from-queue keys, never shared with a concurrent call on the same queue.</summary>
    private sealed class RunState
    {
        public List<UpdateItem> Pending = [];
        public readonly HashSet<string> RemovedFromQueue = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The per-item stop, in-process half: one linked <see cref="CancellationTokenSource"/> per running
        /// user-scope item, keyed like <see cref="Key(UpdateItem)"/>. An entry is removed as soon as the item's
        /// try/finally in <see cref="InstallInProcessAsync"/> is done with it.
        /// </summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> ItemCancellations = new();

        /// <summary>Set once this run actually sends an admin batch; <see cref="CancelItem"
        /// forwards to it for anything not found in this run's own <see cref="ItemCancellations"/>.</summary>
        public ElevatedJobHandle? ElevatedHandle;

        /// <summary>
        /// Items whose first-round failure the explainer table marks safe to retry blindly. Populated only
        /// during the first round (InstallInProcessCoreAsync and HandleWorkerLine take an isRetryRound flag for
        /// this), so a second failure is never queued for a third pass.
        /// </summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, UpdateItem> AutomaticRetryCandidates =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The set of keys this run has accounted for, and the action it runs; set by
        /// <see cref="RunAsync"/> so a stop on a row waiting for the retry can settle it here.</summary>
        public ISet<string>? Reported;
        public InstallAction Action;

        /// <summary>Rows <see cref="CancelItem"/> took out of the queue or the retry lists and settles itself
        /// (guarded by the queue lock, set in the same block that removes them), so the end-of-run pass does
        /// not report them a second time in the moment before the stop adds them to the reported set.</summary>
        public readonly HashSet<string> CancelledPending = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Set (under the queue lock) the moment the retry round's items go back into
        /// <see cref="Pending"/>: from then on a candidate missing from Pending is already being installed
        /// again, and a stop must reach it there rather than being settled as "stopped while waiting".</summary>
        public bool RetryRoundStarted;

        /// <summary>The current elevated batch's items the helper refused as per-user packages, waiting (rows
        /// still showing Running) for their unelevated retry after the job ends. Guarded by the queue lock;
        /// here rather than only local so a stop click can take one out before that retry starts.</summary>
        public List<UpdateItem>? UserScopeRetries;

        /// <summary>The delayed version re-checks of the current elevated batch (one task per result that read
        /// "unchanged" right away). Guarded by the queue lock; awaited when the batch ends.</summary>
        public readonly List<Task> Rechecks = [];

        /// <summary>The delayed version re-checks of in-process installs, run after the item let go of its slot
        /// and lane. Guarded by the queue lock; awaited when the run ends.</summary>
        public readonly List<Task> InProcessRechecks = [];

        /// <summary>The run's stop token, for the delayed re-checks started from the pipe's reader thread.</summary>
        public CancellationToken CancellationToken;
    }

    /// <summary>
    /// How often, and how many times, a version that still reads "unchanged" right after a successful install
    /// is looked up again before the note is kept (some installers finish after they exit, or hand the work
    /// to a background updater). Settable for tests only.
    /// </summary>
    internal TimeSpan VersionRecheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    internal int VersionRecheckAttempts { get; set; } = 6;

    /// <summary>
    /// Moves not-yet-started items into the given order, within whichever active run holds them. Items
    /// already installing or finished, or not in any run, stay where they are, so a stale or partial order
    /// from the UI can never drop or duplicate an item.
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

                // Only the slice of the caller's order that belongs to this run; other runs' rows are not found here.
                var reordered = new List<UpdateItem>(run.Pending.Count);
                foreach (var (sourceId, itemId) in order)
                {
                    if (byKey.Remove($"{sourceId}:{itemId}", out var item))
                        reordered.Add(item);
                }

                // Anything the caller's order did not mention (a stale snapshot) keeps its old spot.
                reordered.AddRange(byKey.Values);
                run.Pending = reordered;
            }
        }
    }

    /// <summary>
    /// Drops a not-yet-started item from the queue, the "remove" (X) button on the queue panel. A no-op if the
    /// item already started or was never queued in any active run.
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

    /// <summary>Sources whose installs write outside the user's profile and therefore need admin.</summary>
    public static bool NeedsElevation(string sourceId) =>
        sourceId is "winget" or "chocolatey" or "windowsupdate";

    /// <summary>
    /// Installs sharing a lane key run strictly one after another. It is the source id, except pip, which has
    /// one lane per Python interpreter (path compared without regard to case).
    /// </summary>
    internal static string LaneKey(string sourceId, string itemId, IReadOnlyDictionary<string, string>? meta = null) =>
        string.Equals(sourceId, "pip", StringComparison.OrdinalIgnoreCase)
            ? $"{sourceId}|{Sources.PackageManagers.PipSource.LaneInterpreter(itemId, meta).ToLowerInvariant()}"
            : sourceId;

    internal static string LaneKey(UpdateItem item) => LaneKey(item.SourceId, item.Id, item.Meta);

    /// <summary>
    /// What this queue does with a source id, as opposed to what <see cref="NeedsElevation"/> says about the
    /// id in the abstract. Identical outside a mock session; a mock session never elevates (see the
    /// constructor's note on <c>isMockSession</c>). Every internal split goes through this; the bare static
    /// check stays for callers with no session of their own (the admin badge, <c>InstallWorker</c>'s
    /// allowlist, the row's elevation icon).
    /// </summary>
    private bool RequiresElevation(string sourceId) => !_isMockSession && NeedsElevation(sourceId);

    /// <summary>The same for one item of a run of <paramref name="action"/>. A fresh install into the USER
    /// scope stays in this process: the elevated copy would use the administrator's profile (and winget
    /// refuses a user-scope package to an administrator anyway).</summary>
    internal bool RequiresElevation(UpdateItem item, InstallAction action) =>
        RequiresElevation(item.SourceId)
        && !(action == InstallAction.FreshInstall && item.InstallScope == InstallScopes.User);

    public async Task RunAsync(
        IReadOnlyList<UpdateItem> items,
        CancellationToken cancellationToken,
        InstallAction action = InstallAction.Install)
    {
        var userScope = items.Where(i => !RequiresElevation(i, action)).ToList();
        var adminScope = items.Where(i => RequiresElevation(i, action)).ToList();

        // What has been accounted for. Anything still missing at the end never ran and is reported as such,
        // so a cancelled row is never left showing "installing".
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Each call's own not-yet-started items live in its own RunState, not one field shared across calls.
        var run = new RunState { Pending = items.ToList(), Reported = reported, Action = action };
        lock (_queueLock)
            _activeRuns.Add(run);

        try
        {
            // Different package managers do not share state, so npm, pip, scoop and the rest run at the same
            // time. Within one manager it stays sequential: each keeps a single global install state that two of
            // its own installs would corrupt. Each group pulls its next item from this run's own pending list, so
            // a reorder on the queue panel changes what runs next.
            // One shared semaphore, sized by the same setting as the elevated worker's budget, bounds the total
            // across every source group here; the two halves are separate processes, so the worker keeps its own
            // budget.
            var maxParallel = Math.Clamp(_settings.Current.Install.MaxParallelInstalls, 1, 5);
            using var throttle = new SemaphoreSlim(maxParallel, maxParallel);

            var userScopeTask = Task.WhenAll(userScope
                .Select(LaneKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(laneKey => InstallLaneSequentiallyAsync(laneKey, action, reported, throttle, run, cancellationToken)));

            // Started together and awaited together: a slow user-scope package must not delay an admin batch, and
            // the two only share a package manager's global state within one manager, never across the elevation
            // boundary.
            var adminScopeTask = adminScope.Count > 0
                ? RunElevatedBatchAsync(adminScope, action, reported, throttle, run, cancellationToken)
                : Task.CompletedTask;

            await Task.WhenAll(userScopeTask, adminScopeTask);

            // One automatic retry round, still inside this run's throttle: the batch is done only once this has
            // had its chance. Skipped once the whole run was stopped, since a cancellation is already unwinding
            // every row.
            if (!cancellationToken.IsCancellationRequested)
            {
                // A result still waiting on its delayed look-up is not settled; the retry round must not start next
                // to an install whose outcome (and lane) is open.
                await AwaitInProcessRechecksAsync(run);
                await RunAutomaticRetryRoundAsync(action, reported, throttle, run, cancellationToken);
            }
        }
        finally
        {
            // Results still waiting on their delayed look-up are settled first (a stop ends each wait at once),
            // so the unreported-rows pass below never closes a row whose install actually succeeded.
            await AwaitInProcessRechecksAsync(run);

            lock (_queueLock)
                _activeRuns.Remove(run);

            var cancelled = cancellationToken.IsCancellationRequested;
            var reason = cancelled
                ? LocalizationService.Instance["InstallCancelled"]
                : LocalizationService.Instance["InstallWorkerStopped"];

            // A cancelled batch says nothing about what happened: the running package is killed mid-install and
            // whether it counted is not ours to guess. Writing "failed" into the history, the only record, would be
            // a claim we cannot make, so the rows are closed out without one. An item the user removed from the
            // queue is neither: it never ran because it was deliberately taken out.
            FailRemaining(items, action, reported, reason, cancelled, run);
        }
    }

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

    private static readonly TimeSpan LaneRecheckInterval = TimeSpan.FromMilliseconds(250);

    private bool HasPendingOnLane(RunState run, string laneKey)
    {
        lock (_queueLock)
            return run.Pending.Exists(i => string.Equals(LaneKey(i), laneKey, StringComparison.OrdinalIgnoreCase));
    }

    private async Task InstallLaneSequentiallyAsync(
        string laneKey, InstallAction action, ISet<string> reported, SemaphoreSlim throttle,
        RunState run, CancellationToken cancellationToken, bool isRetryRound = false)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The lane before the slot: the lane is queue-wide, so another run (a batch started earlier, an
            // uninstall beside it) may hold it for minutes. Waiting here keeps the item in Pending, where Remove,
            // reorder and Stop still reach it, and holds none of this run's slots. Always lane, then slot: no cycle.
            // Then the slot, then the item: an item taken out of Pending before waiting for a slot would be in
            // neither Pending nor ItemCancellations and could not be removed, reordered or stopped.
            // InstallInProcessAsync registers its stop token before its first await, so from here on the item is
            // reachable. Checked again while waiting: an item removed or stopped while it waited must not keep
            // this run open until the other run lets go of the lane.
            var lane = _laneGates.GetOrAdd(laneKey, _ => new SemaphoreSlim(1, 1));
            if (!HasPendingOnLane(run, laneKey))
                return;
            if (!await lane.WaitAsync(LaneRecheckInterval, cancellationToken))
                continue;

            UpdateItem item;
            try
            {
                await throttle.WaitAsync(cancellationToken);
                try
                {
                    CancellationTokenSource itemCts;
                    lock (_queueLock)
                    {
                        var index = run.Pending.FindIndex(i => string.Equals(LaneKey(i), laneKey, StringComparison.OrdinalIgnoreCase));
                        if (index < 0)
                            return;

                        item = run.Pending[index];
                        run.Pending.RemoveAt(index);

                        // Registered in the block that takes the row out of Pending: a stop between the two would find
                        // the row in neither place and the install would start anyway.
                        itemCts = RegisterItemCancellation(run, item, cancellationToken);
                    }

                    AfterItemTakenForTest?.Invoke(item);
                    await InstallInProcessAsync(item, action, run, cancellationToken, isRetryRound, laneHeld: true, itemCts);
                }
                finally
                {
                    throttle.Release();
                }
            }
            finally
            {
                lane.Release();
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

    /// <summary>Creates the row's own stop source (linked to the run's) and registers it under the key
    /// <see cref="CancelItem"/> looks up.</summary>
    private CancellationTokenSource RegisterItemCancellation(RunState run, UpdateItem item, CancellationToken cancellationToken)
    {
        var itemCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        run.ItemCancellations[Key(item)] = itemCts;
        return itemCts;
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

        var job = new InstallJobFile
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
