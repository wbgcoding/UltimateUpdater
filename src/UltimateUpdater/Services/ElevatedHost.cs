using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace UltimateUpdater.Services;

/// <summary>
/// A grip on one job already with the elevated helper, so the caller can hold or stop it while it runs.
/// Handed to <see cref="ElevatedHost.RunAsync"/>, which fills it in once the job is sent. Every method is
/// safe to call before that (a hold or a stop is remembered and applied then) and after the job has
/// finished (it does nothing).
/// </summary>
public sealed class ElevatedJobHandle
{
    private readonly object _lock = new();

    /// <summary>Held across "decide the hold state, then send it" for pause, resume and the replay in
    /// <see cref="Attach(ElevatedHost, string)"/>, so the worker receives them in the order the state was
    /// decided. Otherwise a resume could overtake the replayed pause and leave the worker paused while the
    /// UI shows the run as going.</summary>
    private readonly SemaphoreSlim _controlOrder = new(1, 1);

    private Func<string, string, string?, string?, Task>? _send;
    private string? _id;
    private bool _held;
    private bool _cancelRequested;
    private bool _cancelAsked;
    private List<(string SourceId, string ItemId)>? _pendingItemCancels;

    /// <summary>
    /// Called by the host once the job has an id. Whatever was asked before that (a pause while the previous
    /// target was still finishing, a stop while the elevation prompt was open) is applied now, because a
    /// control naming an id the worker has never seen is dropped.
    /// </summary>
    internal void Attach(ElevatedHost host, string id) =>
        Attach((jobId, action, sourceId, itemId) => host.SendControlAsync(jobId, action, sourceId, itemId), id);

    /// <summary>The same, with the send given as a function so the ordering can be tested without a pipe.</summary>
    internal void Attach(Func<string, string, string?, string?, Task> send, string id)
    {
        bool cancel;
        (string SourceId, string ItemId)[] itemCancels;

        // Taken before the id is published and released after the pause went out, so a resume asked in between
        // is sent after the pause. When another control holds the gate, the replay queues behind it and
        // re-reads the state when its turn comes.
        var ordered = _controlOrder.Wait(0);
        lock (_lock)
        {
            _send = send;
            _id = id;
            cancel = _cancelRequested;
            itemCancels = _pendingItemCancels?.ToArray() ?? [];
            _pendingItemCancels = null;
        }

        _ = ReplayAsync(send, id, ordered, cancel, itemCancels);
    }

    /// <summary>The pause first (a stop must not arrive before the hold it belongs after), then the item
    /// stops, then the whole-job stop, all inside the gate.</summary>
    private async Task ReplayAsync(
        Func<string, string, string?, string?, Task> send, string id, bool alreadyOrdered, bool cancel,
        (string SourceId, string ItemId)[] itemCancels)
    {
        if (!alreadyOrdered)
            await _controlOrder.WaitAsync();

        try
        {
            // Read again inside the gate: the last state asked for wins.
            bool pause;
            lock (_lock)
                pause = _held && !_cancelAsked;

            if (pause)
                await send(id, "pause", null, null);

            foreach (var (sourceId, itemId) in itemCancels)
                await send(id, "cancel-item", sourceId, itemId);

            if (cancel)
                await send(id, "cancel", null, null);
        }
        finally
        {
            _controlOrder.Release();
        }
    }

    public async Task PauseAsync()
    {
        await _controlOrder.WaitAsync();
        try
        {
            lock (_lock)
                _held = true;

            await ControlAsync("pause");
        }
        finally
        {
            _controlOrder.Release();
        }
    }

    public async Task ResumeAsync()
    {
        await _controlOrder.WaitAsync();
        try
        {
            lock (_lock)
                _held = false;

            await ControlAsync("resume");
        }
        finally
        {
            _controlOrder.Release();
        }
    }

    public async Task CancelAsync()
    {
        lock (_lock)
        {
            if (_send is null)
            {
                _cancelRequested = true;
                return;
            }

            _cancelAsked = true;
        }

        // Behind the gate, so it can never overtake a pause that is still being replayed.
        await _controlOrder.WaitAsync();
        try
        {
            await ControlAsync("cancel");
        }
        finally
        {
            _controlOrder.Release();
        }
    }

    /// <summary>Stops just one item inside this job; the worker keeps working through the rest. An item that
    /// already finished or was never part of this job is ignored, the same "unknown key, not an error" rule
    /// as <see cref="ElevatedWorker.Control"/>. Asked before the job is out, it is remembered and sent right
    /// after.</summary>
    public Task CancelItemAsync(string sourceId, string itemId)
    {
        Func<string, string, string?, string?, Task> send;
        string id;
        lock (_lock)
        {
            if (_send is null || _id is null)
            {
                (_pendingItemCancels ??= []).Add((sourceId, itemId));
                return Task.CompletedTask;
            }

            send = _send;
            id = _id;
        }

        return send(id, "cancel-item", sourceId, itemId);
    }

    private Task ControlAsync(string action)
    {
        Func<string, string, string?, string?, Task> send;
        string id;
        lock (_lock)
        {
            if (_send is null || _id is null)
                return Task.CompletedTask;

            send = _send;
            id = _id;
        }

        return send(id, action, null, null);
    }
}

/// <summary>
/// The app's one administrator prompt. The first job that needs admin rights starts an elevated copy of
/// this exe; it stays connected for the rest of the session and every later admin job (an install batch,
/// a cleanup measurement, a cleanup run) is handed to that same process.
/// The trade is deliberate: an administrator process lives as long as the app. What protects it: the pipe
/// is authenticated in both directions (the app checks the client's process id, the worker the server's),
/// the pipe carries the job so nothing lands in a file another process could swap, and the worker only
/// does work this app knows how to describe.
/// A declined prompt is remembered for the session, since asking again on the next click would be worse
/// than the feature being unavailable.
/// </summary>
public sealed class ElevatedHost(LoggingService logging) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly SemaphoreSlim _startGate = new(1, 1);

    /// <summary>Serialises the write of a job line; the replies come back on their own, tagged.</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>
    /// The conversations in flight, by job id. One reader loop owns the pipe and hands each reply to its job,
    /// which lets a cleanup measurement run alongside an install and keeps an abandoned conversation's
    /// leftovers from being read as the next job's answers.
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
    /// Bumped for every helper session. A pump from an ended session must not clear the state of the one that
    /// replaced it, nor fail its conversations, which happens when the old pipe faults just after a restart.
    /// </summary>
    private int _generation;

    /// <summary>
    /// Completed exactly once, when this session truly ends: <see cref="PumpAsync"/> noticing the pipe died,
    /// or <see cref="ReleaseAsync"/> tearing it down. <see cref="RunAsync"/> waits on it as well as on its
    /// <c>Conversation.Finished</c>, which only <see cref="PumpAsync"/>'s <c>finally</c> completes by
    /// iterating <see cref="_inFlight"/>: a session that died after <see cref="EnsureStartedAsync"/> returned
    /// but before the conversation was added would leave it waiting forever. Both methods capture it into a
    /// local when their session starts and never read the field again, because a restart replaces it with a
    /// fresh instance and a stale reader must keep waiting on the old one.
    /// </summary>
    private TaskCompletionSource<bool> _sessionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private NamedPipeServerStream? _server;
    private System.Diagnostics.Process? _worker;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    /// <summary>True while the elevated worker is connected and answering.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Raised whenever <see cref="IsRunning"/> flips, on whatever thread noticed it (the pump's reader thread
    /// for a drop, the caller's thread for a start). A listener that touches a bound property must marshal to
    /// its own dispatcher.
    /// </summary>
    public event Action? StateChanged;

    private void SetRunning(bool value)
    {
        if (IsRunning == value)
            return;

        IsRunning = value;

        // A throwing listener must not escape: this runs inside the pump's cleanup, and an exception would skip
        // completing the open conversations and the session.
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
    /// starting a real elevated process (tests must never raise a UAC prompt).</summary>
    internal void SetRunningForTest(bool value) => SetRunning(value);

    /// <summary>Test-only seam: adopts a ready-made pair of streams as the live session, as the end of a real
    /// start does, so the pump and its cleanup can be driven without an elevated process.</summary>
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

    /// <summary>Test-only seam: replaces the whole start sequence so a launch failure can be simulated.</summary>
    internal Func<CancellationToken, Task<bool>>? EnsureStartedOverrideForTest { get; set; }

    /// <summary>True once the user has turned down the prompt in this session.</summary>
    public bool WasDeclined { get; private set; }

    /// <summary>
    /// Makes sure the elevated worker is running, raising the prompt only the first time. Returns false when
    /// the user declined or the worker could not be started; both are normal outcomes the caller continues
    /// without.
    /// </summary>
    /// <param name="force">
    /// Ask again even though the user declined earlier. Only for a deliberate, user-initiated action that
    /// needs admin; otherwise one accidental "no" would degrade the rest of the session with no way back.
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
            // Two actions can ask at the same moment; the second must not raise a second prompt.
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
        // A previous helper may have died (the machine slept, the process was killed). Overwriting its pipe,
        // streams and Process object without releasing them leaks a handle set per restart.
        await ReleaseAsync();

        var pipeName = $"UltimateUpdater-elevated-{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        System.Diagnostics.Process? worker = null;
        Task? connected = null;
        Task? exited = null;

        try
        {
            // Off the UI thread: Process.Start with Verb=runas blocks until the prompt is answered.
            try
            {
                worker = await Task.Run(
                    () => ElevationService.StartWorker(ElevationService.WorkerArgument, pipeName, logging),
                    cancellationToken);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Anything but a declined prompt (elevation blocked by policy, the image missing) is already logged by
                // StartWorker. The contract is "false when the worker could not be started"; a throw would end a whole
                // install batch through its generic catch.
                return false;
            }

            if (worker is null)
            {
                WasDeclined = true;
                return false;
            }

            connected = server.WaitForConnectionAsync(cancellationToken);
            exited = worker.WaitForExitAsync(CancellationToken.None);

            // A worker that dies during startup never calls back, and WaitForConnectionAsync has no timeout.
            if (await Task.WhenAny(connected, exited) != connected)
            {
                logging.Error("The elevated worker exited before it connected back.");
                return false;
            }

            await connected;

            // Pipe names are enumerable by any process running as this user, so the name is not a secret. This check
            // stops a stranger connecting first and being handed an elevated job list.
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
    /// The single reader. Every reply carries the id of its job, so each line goes to the right
    /// conversation, and a line for a job nobody waits for any more is dropped instead of being read as the
    /// next job's answer.
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
                    // A handler raises events into the UI. If one throws, the reader must not die with it, which would
                    // strand every other conversation on this pipe.
                    logging.Error("An elevated reply could not be handled", ex);
                    finished = true;
                }

                if (finished)
                    conversation.Finished.TrySetResult(true);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe went away; every waiter is released below.
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

                // Unconditional, unlike the block above: this session ended whether or not a newer one replaced it in
                // _generation, and a caller in RunAsync waits on this exact instance, not on the field.
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
    /// False for read-only work (measuring), which runs alongside whatever else is in flight. True for
    /// anything that changes the machine: the worker runs those strictly one at a time.
    /// </param>
    /// <param name="allowStart">
    /// False for a job that must never be the reason for an elevation prompt (the startup prescan): when the
    /// helper is not running, the job is refused rather than the helper launched.
    /// </param>
    public async Task<bool> RunAsync(
        string kind, object payload, Func<string, bool> onLine, CancellationToken cancellationToken,
        bool exclusive = true, ElevatedJobHandle? handle = null, bool allowStart = true)
    {
        // A job that may not start the helper never enters the start sequence: if the helper ended right after
        // the check, that sequence would launch it and raise the prompt this flag prevents. A drop after the
        // check is caught by the session capture below.
        if (!allowStart)
        {
            if (!IsRunning)
                return false;
        }
        else if (!await EnsureStartedAsync(cancellationToken))
        {
            return false;
        }

        // Captured now, not read again: a restart mid-wait replaces the field for the new session, and this
        // conversation belongs to the session just ensured.
        var sessionEnded = _sessionEnded;
        var generation = _generation;

        var id = Guid.NewGuid().ToString("N");
        var conversation = new Conversation(onLine);
        _inFlight[id] = conversation;

        // The session could have died between EnsureStartedAsync returning and this line. The wait below covers
        // it, but there is no reason to wait when it already happened.
        if (!IsRunning)
            conversation.Finished.TrySetResult(false);

        try
        {
            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                // The generation check stops this conversation being sent into a different session than the one
                // sessionEnded tracks. Without it, a restart between capturing sessionEnded and this write would pass
                // IsRunning and _writer (now the new session's), hand the job to the new worker, and still wait on the
                // old, already-completed sessionEnded: the call would return a false "failed" and remove the
                // conversation before the new worker's reply arrives.
                if (!IsRunning || _writer is null || _generation != generation)
                    return false;

                var envelope = JsonSerializer.Serialize(new { id, kind, exclusive, payload }, JsonOptions);

                // Checked before the write and never passed into it: a cancel landing mid-write would leave half a line
                // on the pipe, and the worker ends the session on a line it cannot parse. Once the line is out, the
                // cancel registered below tells the worker; a token that fired during the write runs that callback as
                // soon as it is registered.
                cancellationToken.ThrowIfCancellationRequested();
                await _writer.WriteLineAsync(envelope.AsMemory(), CancellationToken.None);
            }
            finally
            {
                _sendGate.Release();
            }

            // Wired up after the job went out: a control message that overtook its job would name an id the worker
            // has not heard of and be discarded, so a stop would not stop and a hold would not hold. Attaching the
            // handle can send a "pause" straight away, so it carries the same risk as the stop below.
            handle?.Attach(this, id);
            using var stopOnCancel = cancellationToken.Register(() => _ = Background.Run(SendControlAsync(id, "cancel"), logging, "cancel control send"));

            // The wait itself stays un-cancellable. The worker owns what it was handed and always answers; walking
            // away would leave its replies with nobody to read them. Stopping means telling it to stop, above, and
            // waiting for it to say so. sessionEnded is the backstop if the session dies before the conversation's
            // own Finished is completed (see _sessionEnded).
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
    /// Tells the helper to stop or hold a job it is already running, or to stop just one item inside it
    /// (<paramref name="itemSourceId"/>/<paramref name="itemId"/> given for "cancel-item" only). Sent on the
    /// same pipe as the job, but as a non-exclusive line the worker answers on its read loop: queueing it
    /// behind the exclusive slot would mean waiting for the very job it is meant to stop. Never throws: this
    /// runs from a cancellation callback, where an exception has nowhere to go.
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
    /// exits, which also covers the app being killed. The semaphores are deliberately not disposed: shutdown
    /// can land while a job waits on one, and disposing it would throw there. Neither holds an unmanaged
    /// handle (no AvailableWaitHandle is touched), so there is nothing to release.
    /// </summary>
    public ValueTask DisposeAsync() => new(ReleaseAsync());

    /// <summary>
    /// Lets go of one helper session. Never throws, since it runs on shutdown and restart alike. Disposing a
    /// stream against a pipe a dead worker already broke can throw, so every disposal is guarded on its own
    /// and the fields are cleared in <c>finally</c>; otherwise stale state would fail the next restart too.
    /// </summary>
    private async Task ReleaseAsync()
    {
        SetRunning(false);

        // An explicit release can land before PumpAsync's read notices the pipe is gone; a caller waiting in
        // RunAsync must not depend on that race.
        _sessionEnded.TrySetResult(false);

        // Taken before the writer goes away: a control line being written right now must finish (or the pipe
        // must break) rather than meet a disposed writer. Bounded, because a write stuck on a pipe nobody reads
        // is what disposing the pipe below ends.
        var gateHeld = await _sendGate.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
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

            if (gateHeld)
                _sendGate.Release();
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
