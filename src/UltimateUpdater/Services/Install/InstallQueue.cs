using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services.Install;

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
public sealed partial class InstallQueue
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
        PackageSource = item.SourceId == "winget" ? WingetSource.SafeSource(item) : null,
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

    /// <summary>Identity of a row across the whole queue, see the note on <see cref="InstallItemStatus"/>.</summary>
    private static string Key(UpdateItem item) => $"{item.SourceId}:{item.Id}";
}