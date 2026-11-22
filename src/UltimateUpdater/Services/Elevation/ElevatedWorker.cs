using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Services.Elevation;

/// <summary>One instruction from the app, naming which handler should read the payload.</summary>
public sealed class ElevatedJob
{
    public string Id { get; set; } = string.Empty;

    /// <summary>"install", "cleanup" (and its per-lane variants "cleanup-repair", "cleanup-diskcleanup",
    /// "cleanup-defrag", "cleanup-registry"), or "control" to stop or hold one of those.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Sent for the sender's own bookkeeping only. The worker never reads it to decide exclusivity (a message
    /// naming its own serialisation could run an elevated install and cleanup together); see
    /// <see cref="ElevatedWorker.IsExclusiveJob"/>.
    /// </summary>
    public bool Exclusive { get; set; } = true;

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
/// app and serves jobs on that one pipe until the app closes it: install and uninstall batches, cleanup
/// measurements, cleanup runs.
/// One long-lived worker means a single UAC prompt per session. The cost is an administrator process living
/// as long as the app, so the pipe is authenticated in both directions, every job is validated against what
/// this app knows how to do, and the worker writes no files of its own (its log lines go back over the
/// pipe). Jobs are answered concurrently and every reply carries its job's id; only jobs that change the
/// machine take the exclusive slot.
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
    /// One lane per kind of machine-changing work. Installing serialises against installing and cleaning
    /// against cleaning, but an install and a cleanup run side by side. Measurements take no lane.
    /// The accepted trade: an installer and a cleanup can touch the same temp folder. The cleanup skips (and
    /// counts) every file another process holds open, and CleanupSafety protects the extraction roots of
    /// running single-file apps. A file the installer has not created yet is the residual risk.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Lanes = new();

    private static SemaphoreSlim LaneFor(string kind) =>
        Lanes.GetOrAdd(kind, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// The cross-category exclusion on top of the per-kind lane: an elevated install only waits on the two
    /// cleanup kinds that touch the temp files an install unpacks (<see cref="ContendingCleanupKinds"/>).
    /// System repair and drive optimisation never touch that area and keep only their own lane.
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
    /// Acquires the cross-category slot for one exclusive job: the writer lock for "install", a shared reader
    /// slot for a contending cleanup kind, or a no-op disposable for every other exclusive kind. Independent
    /// of <see cref="LaneFor"/> and of any real work, so it is testable by racing fabricated jobs.
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
            // Only the FIRST reader waits on the writer lock; later readers join the group already holding it.
            // Doing this under CleanupReaderGate stops a second reader from seeing "a reader is active" before the
            // first has finished acquiring the writer lock.
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

    private sealed class NoOpSlot : IAsyncDisposable
    {
        public static readonly NoOpSlot Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// The one extra exclusion beyond the install/cleanup pairing: "cleanup-repair" (DISM /RestoreHealth,
    /// component cleanup, sfc) runs freely beside an install, except an elevated install that contains a
    /// Windows Update item. Both use the servicing stack and can fail busy when run together.
    /// A plain mutual-exclusion lock, because exactly two kinds take it and neither is a "many readers"
    /// group: whichever gets there first holds it.
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

        // The callback is still running and signals this handle when it ends. Disposing it now would make that
        // signal throw on a thread-pool thread and take the worker down, so the handle is left for the finalizer.
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
    /// Whether an "install" job's own payload names the Windows Update source. Read from the job about to
    /// run, never from a separate field. A payload that fails to deserialize, or any other kind, answers false.
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
    /// Whether <paramref name="job"/> changes the machine and so needs the exclusion above. Derived purely
    /// from the job's kind and, for a cleanup job, whether its payload's "command" is "scan" or "analyze"
    /// (read-only); the wire's <see cref="ElevatedJob.Exclusive"/> is deliberately never read. Internal and
    /// pure so a fabricated job whose wire field lies is testable.
    /// </summary>
    internal static bool IsExclusiveJob(ElevatedJob job) => job.Kind switch
    {
        "install" => true,
        "cleanup" or "cleanup-repair" or "cleanup-diskcleanup" or "cleanup-defrag" or "cleanup-registry" =>
            !IsReadOnlyCleanupCommand(job.Payload),
        _ => true,
    };

    /// <summary>"scan" (measuring what could be freed) and "analyze" (DISM's read-only AnalyzeComponentStore)
    /// never delete anything, so they run alongside other work instead of taking the exclusive lane. Every
    /// other command arrives on the "cleanup" kind only as "clean" (see CleanupViewModel.RunElevatedAsync),
    /// which changes the machine and stays exclusive.</summary>
    private static bool IsReadOnlyCleanupCommand(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("command", out var commandValue)
        && commandValue.ValueKind == JsonValueKind.String
        && commandValue.GetString() is "scan" or "analyze";

    /// <summary>
    /// Every job kind this worker accepts; anything else ends the session rather than being guessed at.
    /// The "cleanup-*" variants exist only so each gets its own lane above, keeping cleanup's five lanes from
    /// serialising against each other; all are handled by <see cref="RunCleanupAsync"/> like plain "cleanup".
    /// Extracted so an unrecognised kind can be tested without a live pipe.
    /// </summary>
    internal static bool IsRecognizedKind(string kind) =>
        kind is "install" or "cleanup" or "cleanup-repair" or "cleanup-diskcleanup" or "cleanup-defrag"
            or "cleanup-registry" or "control";

    /// <summary>
    /// The stop switch and the hold of a running job, so a stop does not have to wait for every remaining
    /// package of an elevated batch.
    /// </summary>
    private sealed class JobControl
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public PauseGate Pause { get; } = new();

        /// <summary>
        /// Per-item cancellation sources for this job, keyed <c>"sourceId:itemId"</c>: stops one item without
        /// touching <see cref="Cancellation"/>, the whole-job switch. Populated by <see cref="InstallWorker"/>
        /// for the span of one item's install/uninstall call. A key this dictionary does not have is ignored by
        /// <see cref="Control"/>: the item may already have finished, which is a race, not an error.
        /// </summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> ItemCancellations { get; } = new();
    }

    private static readonly ConcurrentDictionary<string, JobControl> Active = new();

    /// <summary>
    /// Log lines waiting to go back to the app. This process must not write files (every directory it could
    /// write to belongs to the unelevated user), so lines are held here and drained by the job that logged
    /// them. Lines logged outside any job (the read loop) wait for whichever job drains next.
    /// </summary>
    private static readonly ConcurrentQueue<string> PendingLog = new();

    /// <summary>The running job's own queue; flows to every task and timer the job starts, so two jobs
    /// running at once never send each other's lines under their own id.</summary>
    private static readonly AsyncLocal<ConcurrentQueue<string>?> JobLog = new();

    public static void CollectLog(string line) => (JobLog.Value ?? PendingLog).Enqueue(line);

    /// <summary>Runs <paramref name="body"/> with a log queue of its own, so lines it (or anything it starts)
    /// logs are drained only by the same job.</summary>
    internal static async Task RunWithJobLogAsync(Func<Task> body)
    {
        // An async method: the value set here is visible to what body starts and never leaks to the caller.
        JobLog.Value = new ConcurrentQueue<string>();
        await body();
    }

    internal static bool TryDequeueLog(out string line)
    {
        var own = JobLog.Value;
        if (own is not null && own.TryDequeue(out line!))
            return true;

        return PendingLog.TryDequeue(out line!);
    }

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

    public static async Task<int> RunAsync(string pipeName, int expectedServerProcessId, LoggingService logging, int connectTimeoutMs = 30_000)
    {
        // Anonymous impersonation: with the default the pipe SERVER may impersonate its client, and this
        // client holds an administrator token.
        // Asynchronous is required: the read loop waits for the next job while a running job writes its replies,
        // and on a pipe handle opened without overlapped I/O the write blocks behind the pending read.
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Anonymous);

        try
        {
            await pipe.ConnectAsync(connectTimeoutMs);
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
            // A pending repeat count would otherwise never reach PendingLog: this is the last line of the worker's
            // life and nothing after it logs again. Sink mode has no disk write, so this never blocks.
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
    /// (see <see cref="InstallWorker"/>), but those limits apply after deserialisation, and an unbounded line
    /// would let a compromised parent make this administrator process allocate without limit first.
    /// </summary>
    private const int MaxJobLineChars = 2 * 1024 * 1024;

    /// <summary>
    /// Reads job lines in blocks of 4 KB instead of one character per call. Characters read past the end
    /// of a line stay in this reader for the next call. A line that runs past <see cref="MaxJobLineChars"/>
    /// ends the session.
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
            // IsNullOrEmpty: a line with "id": null deserialises to a null Id.
            if (job is null || !IsRecognizedKind(job.Kind) || string.IsNullOrEmpty(job.Id))
            {
                logging.Warn($"Elevated worker received an unusable job ('{job?.Kind}'); ending the session.");
                break;
            }

            // Stopping and holding are answered on the read loop itself, never queued behind the job they are
            // aimed at: waiting for the exclusive slot to stop the thing that holds it is a deadlock.
            if (job.Kind == "control")
            {
                Control(job, logging);
                continue;
            }

            // Logged from the derived answer, not the wire field, which is never read for the decision.
            logging.Info($"Elevated job '{job.Kind}' accepted (exclusive: {IsExclusiveJob(job)}).");

            // Registered here rather than inside the task: the pipe is ordered, so a control line that
            // follows this job always finds its switch, however soon it arrives.
            // A second job under an id still in flight would leave the first one without its stop switch; the
            // app never sends one, so it is treated like any other unusable line.
            var control = new JobControl();
            if (!Active.TryAdd(job.Id, control))
            {
                logging.Warn($"Elevated worker received a second job under a running id ('{job.Kind}'); ending the session.");
                control.Cancellation.Dispose();
                break;
            }

            // Each job on its own task, so the read loop is free to accept the next one immediately.
            var task = Task.Run(() => RunJobAsync(job, writer, logging, control));
            running[task] = 0;
            _ = task.ContinueWith(t => running.TryRemove(t, out _), TaskScheduler.Default);
        }

        // The pipe is gone, so nothing is watching this process. Every job in flight is stopped first, then
        // what is left is waited out.
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
                    // A key this job never registered (already finished, or never existed) is ignored, not an error.
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
            // Same race CancelAll guards against: the job finished and disposed the switch between the
            // TryGetValue above and this call. Letting this escape would take the whole worker down.
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
        JobLog.Value = new ConcurrentQueue<string>();

        try
        {
            if (isExclusive)
            {
                // Slots first, per-kind lane second, always in that order, so two job kinds can never wait on each
                // other in opposite directions. The waiting line goes to the app BEFORE the wait: a row sitting at 0%
                // with no explanation looks hung.
                slots = await AcquireJobSlotsAsync(job, () => NotifyWaitingForCleanup(job, writer), control.Cancellation.Token);

                // With the token: a job still queued behind another one of its own kind must be stoppable
                // without first waiting for the thing in front of it to finish.
                await lane!.WaitAsync(control.Cancellation.Token);
                held = true;
            }

            // A stop that landed in the instant the slot was granted must still end the job here: from this
            // point a repair runs to its end and is never interrupted, so it must not be begun after a stop.
            // Installs are left alone: they report a stopped item by item themselves.
            if (job.Kind != "install")
                control.Cancellation.Token.ThrowIfCancellationRequested();

            if (job.Kind == "install")
                await RunInstallAsync(job, writer, logging, control);
            else
                await RunCleanupAsync(job, writer, logging, control);
        }
        catch (OperationCanceledException)
        {
            logging.Info($"Elevated job '{job.Kind}' was stopped.");
            SendJobLog(job, writer);
            Send(writer, job.Id, new InstallProgressMessage { Type = "done" });
        }
        catch (Exception ex)
        {
            // The app is waiting for this job's "done" and would otherwise wait for ever.
            logging.Error($"Elevated job '{job.Kind}' failed", ex);
            SendJobLog(job, writer);
            Send(writer, job.Id, new InstallProgressMessage { Type = "done" });
        }
        finally
        {
            // Only this job's own control: a later job with the same id keeps its stop switch.
            Active.TryRemove(new KeyValuePair<string, JobControl>(job.Id, control));

            // Resumed first: a paused job that is being torn down must not leave a worker thread parked on
            // the gate for the rest of the session.
            control.Pause.Resume();
            control.Cancellation.Dispose();

            // What a stop registered for an item that never started (and nothing else removed) is released
            // with the job.
            foreach (var itemCancellation in control.ItemCancellations.Values)
                itemCancellation.Dispose();
            control.ItemCancellations.Clear();

            if (held && lane is not null)
                lane.Release();

            if (slots is not null)
                await slots.DisposeAsync();
        }
    }

    /// <summary>Sends the job's own log lines that no worker handler drained (the job ended on a stop or an
    /// error), in the shape the job's kind uses, before its closing line.</summary>
    private static void SendJobLog(ElevatedJob job, StreamWriter writer)
    {
        while (TryDequeueLog(out var line))
        {
            object message = job.Kind == "install"
                ? new InstallProgressMessage { Type = "log", Status = line }
                : new CleanupWorkerMessage { Type = "log", Text = line };
            if (!Send(writer, job.Id, message))
                return;
        }
    }

    /// <summary>
    /// Tells the app that this install job is about to wait for a contending cleanup lane, one progress
    /// line per item so every row in the batch says why it sits at 0%. Best-effort: a payload that will
    /// not deserialize means no message goes out.
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
