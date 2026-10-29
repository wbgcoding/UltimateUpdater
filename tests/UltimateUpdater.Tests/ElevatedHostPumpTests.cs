using System.IO;
using System.IO.Pipes;
using System.Text;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>The reader loop's cleanup must always release every waiting caller: no conversation
/// waits longer than its session.</summary>
public class ElevatedHostPumpTests
{
    [Fact]
    public async Task A_throwing_state_listener_does_not_stop_pending_conversations_from_completing()
    {
        var host = new ElevatedHost(new LoggingService());
        using var workerToApp = new AnonymousPipeServerStream(PipeDirection.In);
        using var appToWorker = new MemoryStream();
        using var workerEnd = new AnonymousPipeClientStream(PipeDirection.Out, workerToApp.ClientSafePipeHandle);

        host.AttachSessionForTest(
            new StreamReader(workerToApp, Encoding.UTF8),
            new StreamWriter(appToWorker, new UTF8Encoding(false)) { AutoFlush = true });

        // Armed only after the attach, so the session is up and the next flip is the pump's own cleanup.
        host.StateChanged += () => throw new InvalidOperationException("a listener bug");

        var run = host.RunAsync("install", new { }, _ => true, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(run.IsCompleted, "the job is still waiting for its reply");

        // The worker goes away: the pump reads end-of-stream and runs its cleanup.
        workerEnd.Dispose();

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(run, finished);
        Assert.False(await run);
        Assert.False(host.IsRunning);
    }

    /// <summary>A job that must never raise a prompt checks the helper is up and then goes straight on;
    /// asking the start sequence in between would launch the helper if it ended in that instant.</summary>
    [Fact]
    public async Task A_job_that_may_not_start_the_helper_never_enters_the_start_sequence()
    {
        var host = new ElevatedHost(new LoggingService());
        var starts = 0;
        host.EnsureStartedOverrideForTest = _ =>
        {
            Interlocked.Increment(ref starts);
            return Task.FromResult(true);
        };
        host.SetRunningForTest(true);

        var reached = await host.RunAsync("scan", new { }, _ => true, CancellationToken.None, allowStart: false);

        Assert.False(reached, "there is no session behind the flag");
        Assert.Equal(0, starts);
    }

    [Fact]
    public void A_throwing_state_listener_is_logged_and_never_escapes_the_state_change()
    {
        var host = new ElevatedHost(new LoggingService());
        host.StateChanged += () => throw new InvalidOperationException("a listener bug");

        host.SetRunningForTest(true);

        Assert.True(host.IsRunning);
    }
}
