using System.Text;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// Audit 1b-16/17: both output streams now go through the same bounded, awaitable
/// <see cref="ProcessRunner.PumpAsync"/> the streaming stdout path already used, DataReceived's
/// underlying line reader had no cap on a single delimiter-free line before the callback ever fired, and
/// cancelling gave no guarantee an in-flight callback had finished before the same StringBuilder was read.
/// Exercised directly against a <see cref="StringReader"/> rather than a real process: deterministic and
/// does not need to actually generate 10 MB of process output to prove the cap.
/// </summary>
public class ProcessRunnerStreamingTests
{
    [Fact]
    public async Task A_single_segment_with_no_delimiter_is_capped_before_it_grows_past_the_limit()
    {
        // No '\r', '\n' or '\b' anywhere, under the OLD DataReceived-based reader this would have grown
        // the framework's internal line buffer unbounded before a single callback ever fired.
        var huge = new string('x', 12 * 1024 * 1024);
        using var reader = new StringReader(huge);
        var sink = new StringBuilder();
        var segments = new List<string>();

        await ProcessRunner.PumpAsync(reader, sink, segments.Add, CancellationToken.None);

        Assert.True(segments.Count > 1, "a delimiter-free stream must be force-flushed in bounded chunks");
        Assert.All(segments, s => Assert.True(s.Length <= 10 * 1024 * 1024));
    }

    [Fact]
    public async Task Segments_split_on_carriage_return_newline_and_backspace()
    {
        using var reader = new StringReader("one\rtwo\nthree\bfour");
        var segments = new List<string>();

        await ProcessRunner.PumpAsync(reader, new StringBuilder(), segments.Add, CancellationToken.None);

        Assert.Equal(["one", "two", "three", "four"], segments);
    }

    [Fact]
    public async Task A_throwing_callback_does_not_stop_the_rest_of_the_stream_from_being_read()
    {
        using var reader = new StringReader("first\nsecond\nthird");
        var seen = new List<string>();

        await ProcessRunner.PumpAsync(reader, new StringBuilder(), s =>
        {
            seen.Add(s);
            if (s == "second")
                throw new InvalidOperationException("parser fault");
        }, CancellationToken.None);

        Assert.Equal(["first", "second", "third"], seen);
    }

    [Fact]
    public async Task RunAsync_captures_both_streams_from_a_real_process()
    {
        var result = await ProcessRunner.RunAsync(
            "cmd.exe", ["/c", "echo out-line & echo err-line 1>&2"], TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("out-line", result.StandardOutput);
        Assert.Contains("err-line", result.StandardError);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task A_timed_out_process_is_killed_and_returns_cleanly_without_torn_output()
    {
        var result = await ProcessRunner.RunAsync(
            "cmd.exe", ["/c", "ping -n 30 127.0.0.1 >nul"], TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    /// <summary>
    /// Seven output bursts, each separated by roughly a one-second gap of real silence (ping's own
    /// spacing, the same trick <see cref="A_timed_out_process_is_killed_and_returns_cleanly_without_torn_output"/>
    /// uses); total wall time comfortably PAST the idle span given below, which an old flat timeout of
    /// that size would have killed outright. A source is judged by silence, not total runtime. The idle
    /// span leaves two seconds of headroom per gap, so a loaded machine does not stretch a gap past it.
    /// </summary>
    [Fact]
    public async Task Steady_output_across_several_gaps_survives_well_past_the_idle_span()
    {
        var result = await ProcessRunner.RunAsync(
            "cmd.exe",
            ["/c", "echo a& ping -n 2 127.0.0.1 >nul& echo b& ping -n 2 127.0.0.1 >nul& " +
                   "echo c& ping -n 2 127.0.0.1 >nul& echo d& ping -n 2 127.0.0.1 >nul& " +
                   "echo e& ping -n 2 127.0.0.1 >nul& echo f& ping -n 2 127.0.0.1 >nul& echo g"],
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None,
            idleTimeout: TimeSpan.FromSeconds(3));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("g", result.StandardOutput);
    }

    /// <summary>Real silence (no output at all, not merely a slow finish) for longer than the idle span
    /// fails fast, well before the generous hard timeout would ever have caught it.</summary>
    [Fact]
    public async Task Silence_past_the_idle_span_fails_before_the_hard_timeout()
    {
        await Assert.ThrowsAsync<ProcessIdleTimeoutException>(() => ProcessRunner.RunAsync(
            "cmd.exe",
            ["/c", "echo start& ping -n 6 127.0.0.1 >nul& echo done"],
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None,
            idleTimeout: TimeSpan.FromSeconds(1.5)));
    }

    /// <summary>The idle watchdog runs alongside RunStreamingAsync's own segment callback without
    /// interfering with it: ordinary output still arrives exactly as before.</summary>
    [Fact]
    public async Task RunStreamingAsync_still_delivers_segments_when_an_idle_timeout_is_set()
    {
        var segments = new List<string>();

        var result = await ProcessRunner.RunStreamingAsync(
            "cmd.exe", ["/c", "echo one& echo two"], TimeSpan.FromSeconds(10), segments.Add,
            CancellationToken.None, idleTimeout: TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(segments, s => s.Contains("one"));
        Assert.Contains(segments, s => s.Contains("two"));
    }

    /// <summary>No idle timeout given (the default) keeps the original behaviour: only the hard
    /// deadline decides, exactly like every existing caller of RunAsync still expects.</summary>
    [Fact]
    public async Task With_no_idle_timeout_given_only_the_hard_deadline_applies()
    {
        var result = await ProcessRunner.RunAsync(
            "cmd.exe", ["/c", "echo start& ping -n 4 127.0.0.1 >nul& echo done"],
            TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("done", result.StandardOutput);
    }

    /// <summary>
    /// Once the process itself has exited, <see cref="ProcessRunner.DrainPumpsAsync"/> waits for the
    /// stdout/stderr pumps to catch up to EOF - but a grandchild that inherited the pipe (an MSI custom
    /// action, a vendor updater) can keep it open forever, and cancelling the token does not reliably
    /// unblock an in-flight pipe read underneath it. Before the fix, only the first bound existed and this
    /// pump - standing in for that stuck read - hung the whole call; this proves the second bound actually
    /// ends it. A never-completing <see cref="TaskCompletionSource"/> is the pump, cheaper and more
    /// deterministic than an actual stuck pipe, and both bounds are given small values so a regression back
    /// to unbounded fails fast rather than timing out the test run.
    /// </summary>
    [Fact]
    public async Task DrainPumpsAsync_gives_up_on_a_pump_that_never_completes()
    {
        var neverCompletes = new TaskCompletionSource();
        using var timeoutCts = new CancellationTokenSource();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await ProcessRunner.DrainPumpsAsync(
            neverCompletes.Task, timeoutCts, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"DrainPumpsAsync must never hang on a pump that never completes (took {stopwatch.Elapsed}).");
        Assert.True(timeoutCts.IsCancellationRequested, "the first bound expiring must still cancel the token.");
    }
}
