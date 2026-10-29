using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace UltimateUpdater.Services;

/// <summary>
/// A grip on one job that is already with the elevated helper, so the caller can hold or stop it while it
/// runs. Handed to <see cref="ElevatedHost.RunAsync"/>, which fills it in before the job is sent; every
/// method is safe to call before that and after the job has finished, it then simply does nothing.
/// </summary>
public sealed class ElevatedJobHandle
{
    private ElevatedHost? _host;
    private string? _id;
    private bool _held;

    /// <summary>
    /// Called by the host once the job has an id. A hold asked for BEFORE that, the user paused while the
    /// previous target was still finishing, is applied now, because a pause naming an id the worker has
    /// never seen is simply dropped.
    /// </summary>
    internal void Attach(ElevatedHost host, string id)
    {
        _host = host;
        _id = id;

        if (_held)
            _ = host.SendControlAsync(id, "pause");
    }

    public Task PauseAsync()
    {
        _held = true;
        return ControlAsync("pause");
    }

    public Task ResumeAsync()
    {
        _held = false;
        return ControlAsync("resume");
    }

    public Task CancelAsync() => ControlAsync("cancel");

    /// <summary>Stops just ONE item inside this job; the worker keeps working through the
    /// rest, and an item that already finished (or was never part of this job) is simply ignored, the
    /// same "unknown key, not an error" rule <see cref="ElevatedWorker.Control"/> already applies to a
    /// whole-job id it has never seen.</summary>
    public Task CancelItemAsync(string sourceId, string itemId) =>
        _host is { } host && _id is { } id ? host.SendControlAsync(id, "cancel-item", sourceId, itemId) : Task.CompletedTask;

    private Task ControlAsync(string action) =>
        _host is { } host && _id is { } id ? host.SendControlAsync(id, action) : Task.CompletedTask;
}

/// <summary>
/// The app's one and only administrator prompt. The first job that needs admin rights starts an elevated
/// copy of this exe; it then stays connected for the rest of the session and every later admin job, an
/// install batch, a cleanup measurement, a cleanup run, is handed to that same process.
/// The trade is deliberate: an administrator process lives as long as the app instead of a few seconds.
/// What protects it is unchanged and matters more for it, the pipe is authenticated in BOTH directions
/// (the app checks the client's process id, the worker checks the server's), the pipe carries the job so
/// nothing lands in a file another process could swap, and the worker only ever does work this app knows
/// how to describe.
/// A declined prompt is remembered for the session: the user said no once, and being asked again on the
/// next click would be worse than the feature simply not being available.
/// </summary>
public sealed class ElevatedHost(LoggingService logging) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly SemaphoreSlim _startGate = new(1, 1);

    /// <summary>Serialises the WRITE of a job line; the replies come back on their own, tagged.</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>
    /// The conversations currently in flight, by job id. One reader loop owns the pipe and hands each
    /// reply to the job it belongs to, which is what lets a cleanup measurement run alongside an install,
    /// and what stops an abandoned conversation's leftovers being read as the next job's answers.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Conversation> _inFlight = new();

    private sealed class Conversation(Func<string, bool> onLine)
    {
        public Func<string, bool> OnLine { get; } = onLine;

        public TaskCompletionSource<bool> Finished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private Task? _pump;

    /// <summary>
    /// Bumped for every helper session. A pump from a session that has already ended must not clear the
    /// state of the one that replaced it, nor fail its conversations, which is exactly what happens when
    /// the old pipe faults a moment after a restart.
    /// </summary>
    private int _generation;

    /// <summary>
    /// Completed exactly once, the moment THIS session truly ends, by <see cref="PumpAsync"/> noticing
    /// the pipe died, or by <see cref="ReleaseAsync"/> tearing it down explicitly. <see cref="RunAsync"/>
    /// waits on it as well as on its own <c>Conversation.Finished</c>: that one is only completed by
    /// <see cref="PumpAsync"/>'s <c>finally</c> iterating <see cref="_inFlight"/>, so a session that died
    /// between <see cref="EnsureStartedAsync"/> returning and the conversation being added to
    /// <see cref="_inFlight"/> would leave the conversation waiting forever. Captured into a
    /// local by both <see cref="RunAsync"/> and <see cref="PumpAsync"/> at the moment their own session
    /// starts, never read from this field again afterward, a later restart replaces this field with a
    /// fresh instance for the NEW session, and a stale reader must keep waiting on the OLD one.
    /// </summary>
    private TaskCompletionSource<bool> _sessionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private NamedPipeServerStream? _server;
    private System.Diagnostics.Process? _worker;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    /// <summary>True while the elevated worker is connected and answering.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Raised whenever <see cref="IsRunning"/> flips, on whatever thread noticed it (the pump's reader
    /// thread for a drop, the caller's own thread for a successful start). A listener that touches a bound
    /// property must marshal to its own dispatcher itself, the same rule every other elevated callback in
    /// this class already follows.
    /// </summary>
    public event Action? StateChanged;

    private void SetRunning(bool value)
    {
        if (IsRunning == value)
            return;

        IsRunning = value;

        // A throwing listener must never escape: this runs inside the pump's cleanup, and an exception
        // there would skip completing the open conversations and the session, leaving their callers waiting.
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            logging.Error("An elevated state listener failed", ex);
        }
    }

    /// <summary>Test-only seam: flips <see cref="IsRunning"/> and raises <see cref="StateChanged"/> without
    /// starting a real elevated process. Tests must never raise a real UAC prompt, so this is how a
    /// "helper already running" scenario is simulated.</summary>
    internal void SetRunningForTest(bool value) => SetRunning(value);

    /// <summary>Test-only seam: adopts a ready-made pair of streams as the live session, exactly as the end
    /// of a real start does, so the pump and its cleanup can be driven without a real elevated process.</summary>
    internal void AttachSessionForTest(StreamReader reader, StreamWriter writer)
    {
        _reader = reader;
        _writer = writer;
        SetRunning(true);
        var generation = ++_generation;
        var sessionEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionEnded = sessionEnded;
        _pump = Task.Run(() => PumpAsync(generation, sessionEnded));
    }

    /// <summary>Test-only seam: replaces the whole start sequence so a launch failure can be simulated
    /// without a real elevated process or prompt.</summary>
    internal Func<CancellationToken, Task<bool>>? EnsureStartedOverrideForTest { get; set; }

    /// <summary>True once the user has turned down the prompt in this session.</summary>
    public bool WasDeclined { get; private set; }

    /// <summary>
    /// Makes sure the elevated worker is running, raising the prompt only the first time. Returns false
    /// when the user declined or the worker could not be started, both are normal outcomes the caller
    /// continues without.
    /// </summary>
    /// <param name="force">
    /// Ask again even though the user turned the prompt down earlier. Only for a deliberate, user-initiated
    /// action that needs admin, without it a single accidental "no" would silently degrade the rest of the
    /// session with no way back short of restarting the app.
    /// </param>
    public async Task<bool> EnsureStartedAsync(CancellationToken cancellationToken, bool force = false)
    {
        if (EnsureStartedOverrideForTest is { } testOverride)
            return await testOverride(cancellationToken);

        if (IsRunning)
            return true;

        if (WasDeclined && !force)
            return false;

        await _startGate.WaitAsync(cancellationToken);
        try
        {
            // Two actions can ask at the same moment; the second one must not raise a second prompt.
            if (IsRunning)
                return true;

            if (WasDeclined && !force)
                return false;

            WasDeclined = false;
            return await StartAsync(cancellationToken);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        // A previous helper may have died (the machine slept, the process was killed). Its pipe, streams
        // and Process object are still here, and overwriting the fields without letting go of them leaks a
        // handle set per restart.
        await ReleaseAsync();

        var pipeName = $"UltimateUpdater-elevated-{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        System.Diagnostics.Process? worker = null;
        Task? connected = null;
        Task? exited = null;

        try
        {
            // Off the UI thread: Process.Start with Verb=runas blocks until the prompt is answered, and
            // the window would be frozen white for exactly that long.
            try
            {
                worker = await Task.Run(
                    () => ElevationService.StartWorker(ElevationService.WorkerArgument, pipeName, logging),
                    cancellationToken);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Anything but a declined prompt (elevation blocked by policy, the image missing): already
                // logged by StartWorker. The contract is "false when the worker could not be started"; a
                // throw here ended a whole install batch through its generic catch.
                return false;
            }

            if (worker is null)
            {
                WasDeclined = true;
                return false;
            }

            connected = server.WaitForConnectionAsync(cancellationToken);
            exited = worker.WaitForExitAsync(CancellationToken.None);

            // A worker that dies during startup never calls back, and WaitForConnectionAsync has no
            // timeout of its own.
            if (await Task.WhenAny(connected, exited) != connected)
            {
                logging.Error("The elevated worker exited before it connected back.");
                return false;
            }

            await connected;

            // Pipe names are enumerable by any process running as this user, so the name is not a secret.
            // This is what stops a stranger connecting first and being handed an elevated job list.
            if (!IsWorkerConnection(server, worker))
            {
                logging.Error("Refusing the pipe: the process that connected is not the elevated worker.");
                return false;
            }

            _server = server;
            _worker = worker;
            _reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            _writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
            SetRunning(true);
            var generation = ++_generation;
            var sessionEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _sessionEnded = sessionEnded;
            _pump = Task.Run(() => PumpAsync(generation, sessionEnded));
            logging.Info("Elevated helper started; further admin work needs no second prompt.");
            return true;
        }
        finally
        {
            // Both tasks outlive every failure path here, and an unobserved fault is a process-wide event.
            if (!IsRunning)
            {
                Observe(connected);
                Observe(exited);
                worker?.Dispose();
                await server.DisposeAsync();
            }
            else
            {
                Observe(exited);
            }
        }
    }

    /// <summary>
    /// The single reader. Every reply carries the id of the job it belongs to, so this hands each line to
    /// the right conversation, and a line for a job nobody is waiting for any more is simply dropped
    /// rather than being read as the next job's answer, which is what left the queue stuck after a stop.
    /// </summary>
    private async Task PumpAsync(int generation, TaskCompletionSource<bool> sessionEnded)
    {
        var reader = _reader;

        try
        {
            while (reader is not null)
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                ElevatedReply? reply;
                try
                {
                    reply = JsonSerializer.Deserialize<ElevatedReply>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    continue;   // hostile-input rule: a malformed line is ignored, never fatal
                }

                if (reply?.Id is null || !_inFlight.TryGetValue(reply.Id, out var conversation))
                    continue;

                bool finished;
                try
                {
                    finished = !conversation.OnLine(reply.Payload.GetRawText());
                }
                catch (Exception ex)
                {
                    // A handler raises events into the UI. If one of them throws, the reader must not die
                    // with it, that would strand every other conversation on this pipe.
                    logging.Error("An elevated reply could not be handled", ex);
                    finished = true;
                }

                if (finished)
                    conversation.Finished.TrySetResult(true);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // the pipe went away; every waiter is released below
        }
        finally
        {
            try
            {
                // A pump whose session has already been replaced must keep its hands off the current one.
                if (generation == _generation)
                    SetRunning(false);
            }
            finally
            {
                // Nothing else will ever answer these, so they complete even if the state change above failed.
                if (generation == _generation)
                {
                    foreach (var conversation in _inFlight.Values)
                        conversation.Finished.TrySetResult(false);
                }

                // Unconditional, unlike the block above: THIS session ended regardless of whether a newer one
                // has already replaced it in `_generation`, a caller in RunAsync captured this exact instance
                // and is waiting on it, not on the field, so a stale pump must still release it.
                sessionEnded.TrySetResult(false);
            }
        }
    }

    /// <summary>
    /// Sends one job and pumps every reply line to <paramref name="onLine"/> until the worker reports it is
    /// done. Returns false when the session is broken, so the caller can fall back to what it can do
    /// unelevated instead of hanging.
    /// </summary>
    /// <param name="exclusive">
    /// False for read-only work (measuring), which then runs alongside whatever else is in flight. True for
    /// anything that changes the machine: the worker runs those strictly one at a time.
    /// </param>
    /// <param name="allowStart">
    /// False for a job that must never be the reason for an elevation prompt (the startup prescan): when
    /// the helper is not running by now, the job is refused rather than the helper launched.
    /// </param>
    public async Task<bool> RunAsync(
        string kind, object payload, Func<string, bool> onLine, CancellationToken cancellationToken,
        bool exclusive = true, ElevatedJobHandle? handle = null, bool allowStart = true)
    {
        // A job that may not start the helper never enters the start sequence: if the helper ended right
        // after the check, that sequence would launch it and raise the prompt this flag exists to prevent.
        // A drop after the check is caught by the session capture below.
        if (!allowStart)
        {
            if (!IsRunning)
                return false;
        }
        else if (!await EnsureStartedAsync(cancellationToken))
        {
            return false;
        }

        // Captured NOW, not read again later: a restart mid-wait replaces the field with a fresh instance
        // for the NEW session, and this conversation belongs to the one that was actually just ensured.
        var sessionEnded = _sessionEnded;
        var generation = _generation;

        var id = Guid.NewGuid().ToString("N");
        var conversation = new Conversation(onLine);
        _inFlight[id] = conversation;

        // Fast path: the session could have died in the instant between EnsureStartedAsync returning and
        // the line above, the wait below covers this regardless, but there is no reason to wait for it
        // when it already happened.
        if (!IsRunning)
            conversation.Finished.TrySetResult(false);

        try
        {
            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                // The generation check is what stops this conversation being sent into a DIFFERENT
                // session than the one `sessionEnded` above is tracking: without it, a restart landing
                // between capturing `sessionEnded` and this write would pass `IsRunning`/`_writer` (both
                // now the NEW session's) and hand the job to the new worker anyway, while this call still
                // waits on the OLD, already-completed `sessionEnded`, returns a false "failed" the instant
                // `Task.WhenAny` sees it, and removes the conversation before the new worker's real reply
                // ever arrives (silently dropped by PumpAsync, since nothing is waiting on `id` any more).
                if (!IsRunning || _writer is null || _generation != generation)
                    return false;

                var envelope = JsonSerializer.Serialize(new { id, kind, exclusive, payload }, JsonOptions);

                // Checked BEFORE the write and never passed into it: a cancel that lands mid-write would
                // leave half a line on the pipe, and the worker ends the whole session on a line it cannot
                // parse. Once the line is out, the cancel registered below is what tells the worker; a
                // token that fired during the write runs that callback the moment it is registered.
                cancellationToken.ThrowIfCancellationRequested();
                await _writer.WriteLineAsync(envelope.AsMemory(), CancellationToken.None);
            }
            finally
            {
                _sendGate.Release();
            }

            // Both of these are wired up AFTER the job went out, and deliberately so: a control message that
            // overtook its own job would name an id the worker has never heard of, be discarded, and the
            // batch would then run to the end anyway, a stop that does not stop, a hold that does not hold.
            // Attaching the handle can send a "pause" straight away (the user held the run while the
            // previous target was still finishing), so it carries exactly the same risk as the stop below.
            handle?.Attach(this, id);
            using var stopOnCancel = cancellationToken.Register(() => _ = Background.Run(SendControlAsync(id, "cancel"), logging, "cancel control send"));

            // The wait itself stays un-cancellable. The worker owns what it was handed and always answers,
            // walking away would leave its replies arriving with nobody to read them, which is what left the
            // queue stuck. Stopping means TELLING it to stop, above, and then waiting for it to say it did.
            // `sessionEnded` is the backstop: if the session dies before this conversation's own Finished
            // is ever completed by PumpAsync (the race described on `_sessionEnded`'s own doc comment),
            // nothing else would ever unblock this wait.
            await Task.WhenAny(conversation.Finished.Task, sessionEnded.Task);
            return conversation.Finished.Task.IsCompleted && await conversation.Finished.Task;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            logging.Warn($"The elevated session ended early: {ex.Message}");
            SetRunning(false);
            return false;
        }
        finally
        {
            _inFlight.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Tells the helper to stop or hold a job it is already running, or to stop just ONE item
    /// inside it (<paramref name="itemSourceId"/>/<paramref name="itemId"/> given for "cancel-item" only).
    /// Sent on the same pipe as the job, but as a non-exclusive line the worker answers on its read loop,
    /// queueing it behind the exclusive slot would mean waiting for the very job it is meant to stop.
    /// Never throws: this runs from a cancellation callback, where an exception has nowhere to go.
    /// </summary>
    internal async Task SendControlAsync(string jobId, string action, string? itemSourceId = null, string? itemId = null)
    {
        try
        {
            await _sendGate.WaitAsync();
            try
            {
                if (!IsRunning || _writer is null)
                    return;

                var envelope = JsonSerializer.Serialize(
                    new
                    {
                        id = Guid.NewGuid().ToString("N"),
                        kind = "control",
                        exclusive = false,
                        payload = new { jobId, action, itemSourceId, itemId },
                    },
                    JsonOptions);

                await _writer.WriteLineAsync(envelope);
            }
            finally
            {
                _sendGate.Release();
            }
        }
        catch (Exception ex)
        {
            logging.Warn($"An elevated job could not be told to '{action}': {ex.Message}");
        }
    }

    private static bool IsWorkerConnection(NamedPipeServerStream server, System.Diagnostics.Process worker)
    {
        try
        {
            return GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var clientProcessId)
                   && clientProcessId == (uint)worker.Id;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    private static void Observe(Task? task) =>
        task?.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>
    /// Closing the pipe is the whole shutdown protocol: the worker's next read returns end-of-stream and it
    /// exits. That also covers the case a farewell message never could, the app being killed. The
    /// semaphores, in turn, are deliberately NOT disposed here: shutdown can land while a job is still
    /// waiting on one, and disposing it out from under that wait throws where the wait happens. Neither
    /// holds an unmanaged handle (no AvailableWaitHandle is ever touched), so there is nothing to release.
    /// </summary>
    public ValueTask DisposeAsync() => new(ReleaseAsync());

    /// <summary>
    /// Lets go of one helper session. Never throws, it runs on shutdown and on restart alike.
    /// Disposing a stream against a pipe a dead worker already broke can throw, so every disposal
    /// is guarded on its own and the fields are cleared in <c>finally</c>: an unguarded failure here
    /// would leave stale, half-torn-down state that fails the NEXT restart attempt too.
    /// </summary>
    private async Task ReleaseAsync()
    {
        SetRunning(false);

        // An explicit release (shutdown, restart) can land before PumpAsync's own read even notices the
        // pipe is gone, a caller waiting in RunAsync must not depend on that race resolving in time.
        _sessionEnded.TrySetResult(false);

        try
        {
            TryDispose(_writer);
            TryDispose(_reader);

            if (_server is not null)
            {
                try
                {
                    await _server.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    logging.Info($"Elevated pipe server disposal during release: {ex.Message}");
                }
            }

            if (_pump is not null)
            {
                try
                {
                    await _pump.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
                {
                    logging.Info($"Elevated pump wait during release did not finish cleanly: {ex.Message}");
                }
            }

            TryDispose(_worker);
        }
        finally
        {
            _writer = null;
            _reader = null;
            _server = null;
            _worker = null;
            _pump = null;
        }
    }

    private void TryDispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            logging.Info($"Elevated host resource disposal failed, already torn down: {ex.Message}");
        }
    }
}
