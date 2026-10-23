using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Services;

/// <summary>One instruction from the app, naming which handler should read the payload.</summary>
public sealed class ElevatedJob
{
    /// <summary>Correlates every reply with this job, so several can be in flight at once.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>"install", "cleanup" (and its per-lane variants "cleanup-repair", "cleanup-diskcleanup",
    /// "cleanup-defrag", "cleanup-registry"), or "control" to stop or hold one of those.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Carried on the wire for the sender's own bookkeeping, but the worker never reads it to decide
    /// exclusivity: a message naming its own serialisation would let an elevated install and an elevated
    /// cleanup run at the same time, so the worker derives the answer itself (see
    /// <see cref="ElevatedWorker.IsExclusiveJob"/>). Left on the type, accepted and ignored on purpose,
    /// so nobody re-wires it into that decision.
    /// </summary>
    public bool Exclusive { get; set; } = true;

    /// <summary>The job itself, still as JSON, only the matching handler knows its shape.</summary>
    public JsonElement Payload { get; set; }
}

/// <summary>One reply, tagged with the job it belongs to.</summary>
public sealed class ElevatedReply
{
    public string Id { get; set; } = string.Empty;

    public JsonElement Payload { get; set; }
}

/// <summary>
/// The ELEVATED half of the app (started with --elevated-worker). It connects back to the unelevated main
/// app and then serves jobs on that one pipe until the app closes it: install and uninstall batches,
/// cleanup measurements, cleanup runs.
/// One long-lived worker rather than one per job: a single UAC prompt for the whole session instead of one
/// per admin action. The cost is an administrator process living as long as the app, so the things that
/// keep it honest matter: the pipe is authenticated in both directions, every job is validated against
/// what this app knows how to do, and the worker writes no files of its own (its log lines go back over
/// the pipe). Jobs are answered CONCURRENTLY and every reply carries its job's id, so a slow measurement
/// never blocks an install behind it. Only jobs that change the machine take the exclusive slot.
/// </summary>
public static class ElevatedWorker
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// ONE lock for every write on this pipe. Two jobs run at once and progress callbacks fire from both,
    /// so a lock per handler would let their lines interleave into unparseable text.
    /// </summary>
    private static readonly object WriteLock = new();

    /// <summary>
    /// One lane per kind of machine-changing work, NOT one lane for all of it. Installing serialises
    /// against installing and cleaning against cleaning (two winget batches, or two passes over the same
    /// folders, would fight), but an install and a cleanup run side by side. Measurements take no lane.
    /// The accepted trade: an installer and a cleanup can touch the same temp folder. That stays survivable
    /// because the cleanup skips (and counts) every file another process holds open, and CleanupSafety
    /// protects the extraction roots of running single-file apps. A file the installer has not created yet
    /// is the residual risk, accepted deliberately.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Lanes = new();

    private static SemaphoreSlim LaneFor(string kind) =>
        Lanes.GetOrAdd(kind, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// The cross-category exclusion, on top of the per-kind lane above: an elevated install only waits on
    /// the two cleanup kinds that touch the temp files an install unpacks
    /// (<see cref="ContendingCleanupKinds"/>), the app's own file deletion and Windows disk cleanup.
    /// System repair and drive optimisation never touch that area, so they take NO cross-category slot and
    /// only keep their own per-kind lane (an install once waited 15 minutes behind a DISM repair that could
    /// never have collided with it).
    /// Modelled as a single-writer/many-readers lock: install is the sole writer
    /// (<see cref="InstallVsCleanupWriterLock"/> held outright), each contending cleanup kind is a reader
    /// (they contend for that lock as ONE group, via <see cref="_activeCleanupReaders"/>, guarded by its
    /// own tiny async mutex so the 0-to-1 and 1-to-0 transitions cannot race).
    /// </summary>
    private static readonly SemaphoreSlim InstallVsCleanupWriterLock = new(1, 1);

    private static readonly SemaphoreSlim CleanupReaderGate = new(1, 1);

    private static int _activeCleanupReaders;

    /// <summary>
    /// The only cleanup kinds that contend with an install: the app's own file deletion ("cleanup") and
    /// Windows Disk Cleanup ("cleanup-diskcleanup"), because both can delete a temp file an install is
    /// unpacking. "cleanup-repair" (DISM/sfc), "cleanup-defrag" (drive optimisation) and
    /// "cleanup-registry" touch neither an install's staging area nor each other's, so they are left out
    /// (registry cleanup runs beside installs by product decision).
    /// </summary>
    private static readonly HashSet<string> ContendingCleanupKinds = new(StringComparer.Ordinal)
    {
        "cleanup",
        "cleanup-diskcleanup",
    };

    /// <summary>
    /// Acquires the cross-category slot for one exclusive job: the writer lock outright for
    /// "install", a shared reader slot for a contending cleanup kind, or no slot at all (a no-op disposable)
    /// for every other exclusive kind. Independent of <see cref="LaneFor"/> and of any real install/cleanup
    /// work, so it is directly testable by racing fabricated jobs against each other with no pipe, no real
    /// install, and no real cleanup target.
    /// </summary>
    internal static async Task<IAsyncDisposable> AcquireMachineChangeSlotAsync(string kind, CancellationToken token)
    {
        if (kind == "install")
        {
            await InstallVsCleanupWriterLock.WaitAsync(token);
            return new WriterSlot();
        }

        if (!ContendingCleanupKinds.Contains(kind))
            return NoOpSlot.Instance;

        await EnterCleanupReaderAsync(token);
        return new ReaderSlot();
    }

    private static async Task EnterCleanupReaderAsync(CancellationToken token)
    {
        await CleanupReaderGate.WaitAsync(token);
        try
        {
            // Only the FIRST reader actually waits on the writer lock; every later reader just joins the
            // group already holding it. Doing this while holding CleanupReaderGate is what stops a second
            // reader from reading "a reader is already active" before the first one has genuinely finished
            // acquiring the writer lock.
            if (_activeCleanupReaders == 0)
                await InstallVsCleanupWriterLock.WaitAsync(token);

            _activeCleanupReaders++;
        }
        finally
        {
            CleanupReaderGate.Release();
        }
    }

    private static async Task ExitCleanupReaderAsync()
    {
        await CleanupReaderGate.WaitAsync();
        try
        {
            if (--_activeCleanupReaders == 0)
                InstallVsCleanupWriterLock.Release();
        }
        finally
        {
            CleanupReaderGate.Release();
        }
    }

    private sealed class WriterSlot : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            InstallVsCleanupWriterLock.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReaderSlot : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await ExitCleanupReaderAsync();
    }

    /// <summary>The cross-category slot for a kind that contends with nothing.</summary>
    private sealed class NoOpSlot : IAsyncDisposable
    {
        public static readonly NoOpSlot Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// The ONE extra exclusion beyond the install/cleanup pairing above. "cleanup-repair" (DISM
    /// /RestoreHealth, component cleanup, sfc) takes no cross-category slot, so it runs freely beside an
    /// install (an install once waited 15 minutes behind a repair that could never have collided with it),
    /// except the one pairing that genuinely CAN collide: a repair and an elevated install that contains a
    /// Windows Update item, both of which use the servicing stack and can fail busy running together.
    /// A plain mutual-exclusion lock rather than a reader/writer shape, because exactly two kinds ever take
    /// it and neither is a "many readers" group: whichever side gets there first holds it, the other
    /// waits. Every other pairing (repair alongside a non-Windows-Update install, repair alongside any
    /// cleanup) takes no slot here at all.
    /// </summary>
    private static readonly SemaphoreSlim RepairVsWindowsUpdateLock = new(1, 1);

    internal static async Task<IAsyncDisposable> AcquireRepairVsWindowsUpdateSlotAsync(ElevatedJob job, CancellationToken token)
    {
        if (job.Kind == "cleanup-repair" || (job.Kind == "install" && ContainsWindowsUpdateItem(job)))
        {
            await RepairVsWindowsUpdateLock.WaitAsync(token);
            return new RepairVsWindowsUpdateSlot();
        }

        return NoOpSlot.Instance;
    }

    /// <summary>
    /// Stops a worker's log-flush timer and waits (bounded) for a callback that is already running.
    /// <c>Timer.Dispose()</c> alone returns at once, so a callback still draining the log could write
    /// after the job's final drain and "done" line, or race it.
    /// </summary>
    internal static void StopLogFlushTimer(System.Threading.Timer timer)
    {
        var stopped = new ManualResetEvent(false);
        if (!timer.Dispose(stopped) || stopped.WaitOne(LogFlushStopWait))
        {
            stopped.Dispose();
            return;
        }

        // The callback is still running and signals this handle when it ends. Disposing it now would make
        // that signal throw on a thread-pool thread and take the whole worker down, so the handle is left
        // for the finalizer.
    }

    internal static readonly TimeSpan LogFlushStopWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Takes every slot an exclusive job needs before its own lane. The servicing lock (repair versus a
    /// Windows Update install) comes FIRST and the cross-category slot second: an install that waits for a
    /// repair while already holding the writer slot would keep every file cleanup queued behind the whole
    /// repair plus the install. The repair side takes no cross-category slot, so the order cannot form a
    /// cycle. <paramref name="notifyWaiting"/> is called at most once, before the first wait that will
    /// really block, so the rows say why they sit at 0 %.
    /// </summary>
    internal static async Task<IAsyncDisposable> AcquireJobSlotsAsync(
        ElevatedJob job, Action notifyWaiting, CancellationToken token)
    {
        var told = false;
        if (job.Kind == "install" && ContainsWindowsUpdateItem(job) && RepairVsWindowsUpdateLock.CurrentCount == 0)
        {
            notifyWaiting();
            told = true;
        }

        var repair = await AcquireRepairVsWindowsUpdateSlotAsync(job, token);
        try
        {
            // Only while a contending cleanup reader group really holds the writer lock, not merely
            // whenever it is held: another install running ahead of this one has nothing to do with cleanup.
            if (!told && job.Kind == "install" && _activeCleanupReaders > 0)
                notifyWaiting();

            var cross = await AcquireMachineChangeSlotAsync(job.Kind, token);
            return new CompositeSlot(cross, repair);
        }
        catch
        {
            await repair.DisposeAsync();
            throw;
        }
    }

    /// <summary>Releases the first slot, then the second.</summary>
    private sealed class CompositeSlot(IAsyncDisposable first, IAsyncDisposable second) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    private sealed class RepairVsWindowsUpdateSlot : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            RepairVsWindowsUpdateLock.Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Whether an "install" job's own payload names the Windows Update source (the source id
    /// string itself, never trusted from anywhere else: this is the worker reading the SAME job it is
    /// about to run, not a claim from a separate field). A payload that fails to deserialize, or any job
    /// that is not "install" at all, answers false, and the caller falls through to "no slot needed"
    /// exactly like every other non-matching kind.
    /// </summary>
    private static bool ContainsWindowsUpdateItem(ElevatedJob job)
    {
        if (job.Kind != "install")
            return false;

        InstallJobFile? file;
        try
        {
            file = job.Payload.Deserialize<InstallJobFile>(JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        return file?.Items?.Any(i => string.Equals(i.SourceId, "windowsupdate", StringComparison.Ordinal)) == true;
    }

    /// <summary>
    /// Whether <paramref name="job"/> changes the machine and therefore needs the exclusion above, derived
    /// purely from the job's own KIND and, for a cleanup job, whether its payload's own "command" is
    /// "scan" (read-only, never takes any lane) or "clean" (the wire's <see cref="ElevatedJob.
    /// Exclusive"/> is deliberately never read here). Internal and pure so a fabricated job whose wire
    /// field lies about this is directly testable.
    /// </summary>
    internal static bool IsExclusiveJob(ElevatedJob job) => job.Kind switch
    {
        "install" => true,
        "cleanup" or "cleanup-repair" or "cleanup-diskcleanup" or "cleanup-defrag" or "cleanup-registry" =>
            !IsReadOnlyCleanupCommand(job.Payload),
        _ => true,
    };

    /// <summary>Neither "scan" (measuring what could be freed) nor "analyze" (DISM's own read-only
    /// AnalyzeComponentStore) ever deletes anything, so both run alongside whatever else the worker is doing
    /// rather than taking the exclusive per-kind lane. Every other "cleanup"-kind command reaches this
    /// worker on the "cleanup" job kind ONLY through a "clean" command (see
    /// CleanupViewModel.RunElevatedAsync), which does change the machine and must stay exclusive. Otherwise
    /// "analyze" would queue behind a running cleanup with no visible reason.</summary>
    private static bool IsReadOnlyCleanupCommand(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("command", out var commandValue)
        && commandValue.ValueKind == JsonValueKind.String
        && commandValue.GetString() is "scan" or "analyze";

    /// <summary>
    /// Every job kind this worker will accept; anything else ends the session rather than being guessed at.
    /// "cleanup-repair", "cleanup-diskcleanup", "cleanup-defrag" and "cleanup-registry" exist ONLY so each
    /// gets its own lane above, keeping cleanup's five lanes (file targets, registry, repair, Disk Cleanup,
    /// drive optimisation) from serialising against each other the way five same-kind "cleanup" jobs
    /// otherwise would; all four are still handled by <see cref="RunCleanupAsync"/>, exactly like plain
    /// "cleanup". Extracted so an unrecognised kind can be proven to end the session without a live pipe.
    /// </summary>
    internal static bool IsRecognizedKind(string kind) =>
        kind is "install" or "cleanup" or "cleanup-repair" or "cleanup-diskcleanup" or "cleanup-defrag"
            or "cleanup-registry" or "control";

    /// <summary>
    /// The stop switch and the hold of a running job. Without this the app could only ever stop queueing
    /// MORE work: a stop during an elevated batch waited for every remaining package, which is exactly how
    /// long "stopping takes for ever" lasted.
    /// </summary>
    private sealed class JobControl
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public PauseGate Pause { get; } = new();

        /// <summary>
        /// Per-item cancellation sources for THIS job, keyed <c>"sourceId:itemId"</c>: lets one
        /// item be stopped without touching <see cref="Cancellation"/>, the whole-job switch. Populated by
        /// <see cref="InstallWorker"/> for the span of one item's install/uninstall call and removed again
        /// in its own finally; a key this dictionary does not have is simply ignored by <see cref="Control"/>,
        /// the item may already have finished, which is a race, not an error.
        /// </summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> ItemCancellations { get; } = new();
    }

    private static readonly ConcurrentDictionary<string, JobControl> Active = new();

    /// <summary>
    /// Log lines waiting to go back to the app. This process must not write files, every directory it
    /// could write to belongs to the unelevated user, so the lines are held here and drained by whichever
    /// handler is running, in that handler's own message shape.
    /// </summary>
    private static readonly ConcurrentQueue<string> PendingLog = new();

    public static void CollectLog(string line) => PendingLog.Enqueue(line);

    internal static bool TryDequeueLog(out string line) => PendingLog.TryDequeue(out line!);

    /// <summary>
    /// Writes one reply for <paramref name="jobId"/>. Returns false once the app has stopped listening.
    /// Synchronous and locked: this is called from IProgress callbacks on several threads.
    /// </summary>
    internal static bool Send(StreamWriter writer, string jobId, object message)
    {
        try
        {
            var envelope = new
            {
                id = jobId,
                payload = JsonSerializer.SerializeToElement(message, JsonOptions),
            };

            lock (WriteLock)
                writer.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));

            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    public static async Task<int> RunAsync(string pipeName, int expectedServerProcessId, LoggingService logging)
    {
        // Anonymous impersonation: with the default the pipe SERVER may impersonate its client, and this
        // client holds an administrator token.
        // Asynchronous is not optional here. The read loop now waits for the next job WHILE a running job
        // writes its replies, and on a pipe handle opened without overlapped I/O the write blocks behind
        // the pending read, the worker accepted the job and then never answered it.
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Anonymous);

        try
        {
            await pipe.ConnectAsync(30_000);
        }
        catch (TimeoutException)
        {
            logging.Error("Elevated worker could not connect back to the app; aborting so nothing runs unobserved.");
            logging.Flush();
            return 3;
        }

        // The other half of the handshake. When the app gives up on the pipe the name becomes free again
        // while this elevated process is still connecting, and any process running as this user could then
        // serve it, but it cannot own the process id of the app that started us.
        if (!PipePeer.IsExpectedServer(pipe, expectedServerProcessId))
        {
            logging.Error("The process serving the pipe is not the app that started this worker; aborting.");
            logging.Flush();
            return 5;
        }

        // A runtime load variable that is set here was already acted on before this code ran, and the
        // process cannot tell what got loaded. Same outcome as an unauthenticated peer: no job is run.
        var loadVariables = NativeLoadEnvironment.FindSet();
        if (loadVariables.Count > 0)
        {
            logging.Error(
                $"The environment of this elevated worker sets runtime load variables ({string.Join(", ", loadVariables)}); refusing to run jobs.");
            logging.Flush();
            return 6;
        }

        var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        try
        {
            return await ServeAsync(reader, writer, logging);
        }
        finally
        {
            // A repeat count still pending (the same warning logged over and over near the very end)
            // would otherwise never make it into PendingLog: this is the last line of the worker's own
            // life, nothing after it calls Info/Warn/Error again to flush it out incidentally. Sink mode
            // has no disk write to wait for, so this never blocks on anything.
            logging.Flush();

            // The session ends BECAUSE the app closed the pipe, so the last flush has nowhere to go.
            // Disposing these normally would throw out of the process on the way to a clean exit.
            Close(reader);
            Close(writer);
        }
    }

    private static void Close(IDisposable stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // the pipe is already gone; there is nothing left to flush it to
        }
    }

    /// <summary>
    /// The largest a single job line may be. A batch is capped at 500 items with 512 characters per field
    /// (see <see cref="InstallWorker"/>), so a legitimate job stays far below this, but those limits are
    /// applied AFTER deserialisation, and reading an unbounded line would let a compromised parent make an
    /// administrator process allocate as much memory as it likes before any of them are checked.
    /// </summary>
    private const int MaxJobLineChars = 2 * 1024 * 1024;

    /// <summary>
    /// Reads job lines in blocks of 4 KB instead of one character per call. Characters read past the end
    /// of a line stay in this reader for the next call, so nothing is lost between lines. A line that runs
    /// past <see cref="MaxJobLineChars"/> ends the session exactly as before.
    /// </summary>
    internal sealed class BoundedLineReader(StreamReader reader)
    {
        private readonly char[] _buffer = new char[4096];
        private int _position;
        private int _length;

        /// <summary>One line, or null at end of stream and for a line that runs past the ceiling.</summary>
        public async Task<string?> ReadLineAsync()
        {
            var builder = new System.Text.StringBuilder();

            while (true)
            {
                if (_position >= _length)
                {
                    _length = await reader.ReadAsync(_buffer, 0, _buffer.Length);
                    _position = 0;
                    if (_length == 0)
                        return builder.Length > 0 ? builder.ToString().TrimEnd('\r') : null;
                }

                var end = Array.IndexOf(_buffer, '\n', _position, _length - _position);
                var take = (end < 0 ? _length : end) - _position;
                if (builder.Length + take > MaxJobLineChars)
                    return null;

                builder.Append(_buffer, _position, take);
                if (end >= 0)
                {
                    _position = end + 1;
                    return builder.ToString().TrimEnd('\r');
                }

                _position = _length;
            }
        }
    }

    private static async Task<int> ServeAsync(StreamReader reader, StreamWriter writer, LoggingService logging)
    {
        var running = new ConcurrentDictionary<Task, byte>();
        var lineReader = new BoundedLineReader(reader);
        var exitCode = 0;

        while (true)
        {
            ElevatedJob? job;
            try
            {
                var line = await lineReader.ReadLineAsync();
                if (line is null)
                    break;      // the app closed the pipe, or sent more than a job can legitimately be

                job = JsonSerializer.Deserialize<ElevatedJob>(line, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A broken pipe is the same situation as end-of-stream, so it leaves through the one path
                // below that stops what is still running instead of returning past it.
                exitCode = 2;
                break;
            }

            // Anything unrecognised ends the session rather than being guessed at: an elevated process
            // should do nothing at all before it does the wrong thing.
            // IsNullOrEmpty: a line with "id": null deserialises to a null Id, and .Length on it threw outside
            // every catch here, ending the helper uncleanly instead of by this rule.
            if (job is null || !IsRecognizedKind(job.Kind) || string.IsNullOrEmpty(job.Id))
            {
                logging.Warn($"Elevated worker received an unusable job ('{job?.Kind}'); ending the session.");
                break;
            }

            // Stopping and holding are answered on the read loop itself, never queued behind the job they
            // are aimed at, waiting for the exclusive slot to stop the thing that holds it is a deadlock.
            if (job.Kind == "control")
            {
                Control(job, logging);
                continue;
            }

            // Logged from the DERIVED answer, not the wire field: the field is never read for
            // the actual decision, so logging it instead would tell an admin something the worker did not
            // actually act on.
            logging.Info($"Elevated job '{job.Kind}' accepted (exclusive: {IsExclusiveJob(job)}).");

            // Registered here rather than inside the task: the pipe is ordered, so a control line that
            // follows this job always finds its switch, however soon it arrives.
            var control = new JobControl();
            Active[job.Id] = control;

            // Each job on its own task, so the read loop is free to accept the next one immediately.
            var task = Task.Run(() => RunJobAsync(job, writer, logging, control));
            running[task] = 0;
            _ = task.ContinueWith(t => running.TryRemove(t, out _), TaskScheduler.Default);
        }

        // The pipe is gone, so nothing is watching this process any more. Waiting alone let an install or a
        // cleanup keep running unobserved for the whole bound below, long after the app had closed, so
        // every job in flight is stopped first, then what is left is waited out.
        CancelAll(logging);

        // Never leave an elevated job half-done because the app went away mid-write: a stopped job still
        // unwinds at its own safe boundary (a DISM/sfc step runs to its end, a write is never cut), so let
        // what is running finish, bounded, then exit.
        try
        {
            await Task.WhenAll(running.Keys).WaitAsync(TimeSpan.FromMinutes(45));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            logging.Warn("Elevated worker stopped waiting for its last job.");
        }

        return exitCode;
    }

    /// <summary>
    /// Trips the stop switch of every job still in flight. This is the SAME token a Stop from the app would
    /// cancel, which is what makes it safe: each handler ends where it is allowed to, rather than being torn
    /// off mid-step. A switch may already have been disposed by its own job's finally, that race only
    /// happens when the job is ending anyway.
    /// </summary>
    internal static void CancelAll(LoggingService logging)
    {
        if (Active.IsEmpty)
            return;

        logging.Info("The app stopped listening; stopping every elevated job still in flight.");

        foreach (var control in Active.Values)
        {
            try
            {
                control.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // its own job finished and took the switch down first
            }
        }
    }

    /// <summary>
    /// Stops or holds a running job. Unknown ids are ignored: the job may already have finished on its own,
    /// and that is a race, not an error.
    /// </summary>
    private static void Control(ElevatedJob job, LoggingService logging)
    {
        string? targetId = null;
        string? action = null;
        string? itemSourceId = null;
        string? itemId = null;
        try
        {
            if (job.Payload.TryGetProperty("jobId", out var idValue))
                targetId = idValue.GetString();

            if (job.Payload.TryGetProperty("action", out var actionValue))
                action = actionValue.GetString();

            if (job.Payload.TryGetProperty("itemSourceId", out var itemSourceValue))
                itemSourceId = itemSourceValue.GetString();

            if (job.Payload.TryGetProperty("itemId", out var itemIdValue))
                itemId = itemIdValue.GetString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return;
        }

        if (targetId is null || !Active.TryGetValue(targetId, out var control))
            return;

        try
        {
            switch (action)
            {
                case "cancel":
                    logging.Info("Elevated job stopped on the app's request.");
                    control.Cancellation.Cancel();
                    break;

                case "pause":
                    control.Pause.Pause();
                    break;

                case "resume":
                    control.Pause.Resume();
                    break;

                case "cancel-item":
                    // Looks up the job as above, then the per-item source INSIDE that job. A key this job
                    // never registered (already finished, or never existed) is ignored, not an error: an
                    // unknown key is trusted as nothing beyond "nothing to do", the same rule the elevated
                    // worker applies everywhere else.
                    if (itemSourceId is not null && itemId is not null
                        && control.ItemCancellations.TryGetValue($"{itemSourceId}:{itemId}", out var itemCts))
                    {
                        logging.Info($"Elevated item '{itemSourceId}/{itemId}' stopped on the app's request.");
                        itemCts.Cancel();
                    }
                    else if (itemSourceId is not null && itemId is not null)
                    {
                        // Not running YET (the job waits behind a cleanup, or the item is queued for the serial
                        // retry) while its row already shows Running with a stop button: the stop is remembered
                        // as an already-cancelled entry, which the item picks up when it starts.
                        // GetOrAdd: the item may have registered its own source between the look-up above and
                        // here; then that one is the one to stop.
                        var stopped = new CancellationTokenSource();
                        stopped.Cancel();
                        var registered = control.ItemCancellations.GetOrAdd($"{itemSourceId}:{itemId}", stopped);
                        if (ReferenceEquals(registered, stopped))
                        {
                            logging.Info($"Elevated item '{itemSourceId}/{itemId}' stopped before it started.");
                        }
                        else
                        {
                            stopped.Dispose();
                            logging.Info($"Elevated item '{itemSourceId}/{itemId}' stopped on the app's request.");
                            registered.Cancel();
                        }
                    }

                    break;
            }
        }
        catch (ObjectDisposedException)
        {
            // Same race CancelAll already guards against: the job finished and disposed the switch
            // between the TryGetValue above and this call. The job is over either way, so there is
            // nothing left to cancel/pause/resume, letting this escape would take the whole elevated
            // worker down instead of just failing this one control message.
        }
    }

    private static async Task RunJobAsync(
        ElevatedJob job, StreamWriter writer, LoggingService logging, JobControl control)
    {
        // Derived, never read off the wire: see IsExclusiveJob's own doc comment.
        var isExclusive = IsExclusiveJob(job);
        var lane = isExclusive ? LaneFor(job.Kind) : null;
        var held = false;
        IAsyncDisposable? slots = null;

        try
        {
            if (isExclusive)
            {
                // The slots first, the per-kind lane second, always in that order: two different job
                // kinds acquiring these in the same sequence can never wait on each other in opposite
                // directions. The waiting line goes to the app BEFORE the wait, not after: a row sitting
                // at 0% with no explanation looks hung.
                slots = await AcquireJobSlotsAsync(job, () => NotifyWaitingForCleanup(job, writer), control.Cancellation.Token);

                // With the token: a job still queued behind another one of its own kind must be stoppable
                // without first waiting for the thing in front of it to finish.
                await lane!.WaitAsync(control.Cancellation.Token);
                held = true;
            }

            if (job.Kind == "install")
                await RunInstallAsync(job, writer, logging, control);
            else
                await RunCleanupAsync(job, writer, logging, control);
        }
        catch (OperationCanceledException)
        {
            logging.Info($"Elevated job '{job.Kind}' was stopped.");
            Send(writer, job.Id, new InstallProgressMessage { Type = "done" });
        }
        catch (Exception ex)
        {
            // The app is waiting for this job's "done" and would otherwise wait for ever.
            logging.Error($"Elevated job '{job.Kind}' failed", ex);
            Send(writer, job.Id, new InstallProgressMessage { Type = "done" });
        }
        finally
        {
            Active.TryRemove(job.Id, out _);

            // Resumed first: a paused job that is being torn down must not leave a worker thread parked on
            // the gate for the rest of the session.
            control.Pause.Resume();
            control.Cancellation.Dispose();

            if (held && lane is not null)
                lane.Release();

            if (slots is not null)
                await slots.DisposeAsync();
        }
    }

    /// <summary>
    /// Tells the app that this install job is about to wait for a contending cleanup lane to
    /// release its slot, one progress line per item so every row in the batch (and the queue behind it)
    /// says so instead of sitting at an unexplained 0%. Best-effort: a payload that will not deserialize
    /// just means no message goes out, the wait still happens either way.
    /// </summary>
    private static void NotifyWaitingForCleanup(ElevatedJob job, StreamWriter writer)
    {
        InstallJobFile? file;
        try
        {
            file = job.Payload.Deserialize<InstallJobFile>(JsonOptions);
        }
        catch (JsonException)
        {
            file = null;
        }

        if (file?.Items is null)
            return;

        var text = LocalizationService.Instance["InstallWaitingForCleanup"];
        foreach (var item in file.Items)
        {
            Send(writer, job.Id, new InstallProgressMessage
            {
                Type = "progress",
                SourceId = item.SourceId,
                ItemId = item.ItemId,
                Percent = 0,
                Status = text,
            });
        }
    }

    private static async Task RunInstallAsync(
        ElevatedJob job, StreamWriter writer, LoggingService logging, JobControl control)
    {
        InstallJobFile? file;
        try
        {
            file = job.Payload.Deserialize<InstallJobFile>(JsonOptions);
        }
        catch (JsonException)
        {
            file = null;
        }

        await InstallWorker.RunJobAsync(file, writer, job.Id, logging, control.Cancellation.Token, itemCancellations: control.ItemCancellations);
    }

    private static async Task RunCleanupAsync(
        ElevatedJob job, StreamWriter writer, LoggingService logging, JobControl control)
    {
        CleanupCommand? command;
        try
        {
            command = job.Payload.Deserialize<CleanupCommand>(JsonOptions);
        }
        catch (JsonException)
        {
            command = null;
        }

        await CleanupWorker.RunCommandAsync(
            command, writer, job.Id, logging, control.Cancellation.Token, control.Pause);
    }
}
