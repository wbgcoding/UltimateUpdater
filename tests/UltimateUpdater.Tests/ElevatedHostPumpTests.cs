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

    [Fact]
    public void A_throwing_state_listener_is_logged_and_never_escapes_the_state_change()
    {
        var host = new ElevatedHost(new LoggingService());
        host.StateChanged += () => throw new InvalidOperationException("a listener bug");

        host.SetRunningForTest(true);

        Assert.True(host.IsRunning);
    }
}
