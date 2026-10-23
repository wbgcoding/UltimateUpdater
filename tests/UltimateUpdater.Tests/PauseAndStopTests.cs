using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// Stopping and holding long work.
/// The gate itself is pure and tested here directly; the pipe side is tested for the one thing that would
/// break the session, a control line the worker cannot match must be ignored, never fatal.
/// </summary>
public class PauseAndStopTests
{
    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void An_open_gate_never_blocks()
    {
        var gate = new PauseGate();

        Assert.False(gate.IsPaused);
        gate.Wait(CancellationToken.None);   // returns at once or the test times out
    }

    [Fact]
    public async Task A_closed_gate_holds_until_it_is_opened()
    {
        var gate = new PauseGate();
        gate.Pause();
        Assert.True(gate.IsPaused);

        var waiting = Task.Run(() => gate.Wait(CancellationToken.None));

        // Still held: the wait must not be the one that finished first.
        Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(150)));

        gate.Resume();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.IsPaused);
    }

    /// <summary>
    /// Stopping a run that is currently held must leave the wait, not sit on it, that is the difference
    /// between "stop" and "stop once someone presses resume".
    /// </summary>
    [Fact]
    public async Task Cancelling_releases_a_held_gate()
    {
        var gate = new PauseGate();
        gate.Pause();

        using var cts = new CancellationTokenSource();
        var waiting = Task.Run(() =>
        {
            try
            {
                gate.Wait(cts.Token);
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        });

        await cts.CancelAsync();
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// A control line names a job by id. One that names a job the worker has already finished (or never
    /// had) is a race, not an error: it is dropped and the session carries on serving jobs. Anything else
    /// would let a late stop take the whole elevated helper down with it.
    /// </summary>
    [Fact]
    public async Task An_unmatched_control_line_does_not_end_the_session()
    {
        var pipeName = $"UltimateUpdater-test-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var worker = ElevatedWorker.RunAsync(pipeName, Environment.ProcessId, new LoggingService());
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(20));

        var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        // A stop for a job that does not exist, then a real job. The second one must still be answered.
        await writer.WriteLineAsync(JsonSerializer.Serialize(
            new { id = "c1", kind = "control", exclusive = false, payload = new { jobId = "gone", action = "cancel" } },
            Json));

        await writer.WriteLineAsync(JsonSerializer.Serialize(
            new
            {
                id = "j1",
                kind = "install",
                exclusive = true,
                payload = new InstallJobFile
                {
                    CreateRestorePoint = false,
                    Language = "en",
                    Items = [new InstallJobItem { SourceId = "nosuchsource", ItemId = "thing", Name = "thing" }],
                },
            },
            Json));

        var done = false;
        while (!done && await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is { } line)
        {
            using var envelope = JsonDocument.Parse(line);
            if (!envelope.RootElement.TryGetProperty("id", out var id) || id.GetString() != "j1")
                continue;

            var payload = envelope.RootElement.GetProperty("payload");
            done = payload.TryGetProperty("type", out var type) && type.GetString() == "done";
        }

        Assert.True(done);

        await writer.DisposeAsync();
        reader.Dispose();
        server.Disconnect();

        Assert.Equal(0, await worker.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    /// <summary>
    /// The worker discards a control line naming a job id it has never seen (the test above proves exactly
    /// that, and it is the right behaviour). So every control message the host can emit for a job must be
    /// wired up only once that job's envelope is on the pipe. Attaching the handle is one of them: it
    /// re-sends a hold the user asked for while the previous target was still running, and doing that
    /// before the write let a paused cleanup run its next target to completion.
    /// Asserted on the source text: <c>ElevatedHost.RunAsync</c> talks to a real elevated worker over a
    /// real pipe, and there is no seam in this codebase to observe that ordering at runtime without
    /// refactoring a security-critical class purely for a test.
    /// </summary>
    [Fact]
    public void The_job_handle_is_attached_only_after_the_job_envelope_has_been_written()
    {
        var source = File.ReadAllText(FindSourceFile("Services", "ElevatedHost.cs"));
        var runAsync = source[source.IndexOf("public async Task<bool> RunAsync(", StringComparison.Ordinal)..];

        var write = runAsync.IndexOf("_writer.WriteLineAsync(envelope", StringComparison.Ordinal);
        var attach = runAsync.IndexOf("handle?.Attach(", StringComparison.Ordinal);

        Assert.True(write >= 0 && attach >= 0, "RunAsync no longer writes an envelope or attaches the handle.");
        Assert.True(write < attach, "handle?.Attach runs before the job envelope is written.");
    }

    private static string FindSourceFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, "src", "UltimateUpdater", .. relativeParts]);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate '{string.Join('/', relativeParts)}' from the test output path.");
    }
}
