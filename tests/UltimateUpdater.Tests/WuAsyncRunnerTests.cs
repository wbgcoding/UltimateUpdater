using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="WuAsyncRunner"/>: polls a Windows Update download/install job with the token and, on a Stop,
/// aborts it and returns control in bounded time. The job is a scripted fake, never the real service.
/// </summary>
public class WuAsyncRunnerTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    private sealed class FakeJob : IWuAsyncJob
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        /// <summary>The job finishes by itself after this long; null = never (a transfer that ignores the abort).</summary>
        public TimeSpan? FinishesAfter { get; init; }

        /// <summary>The job finishes as soon as an abort was requested.</summary>
        public bool EndsOnAbort { get; init; }

        public Exception? AbortFailure { get; init; }

        public Exception? EndFailure { get; init; }

        public List<string> Calls { get; } = [];

        private bool _aborted;

        public bool IsCompleted => (_aborted && EndsOnAbort) || (FinishesAfter is { } after && _clock.Elapsed >= after);

        public void RequestAbort()
        {
            Calls.Add("abort");
            _aborted = true;
            if (AbortFailure is not null)
                throw AbortFailure;
        }

        public object? End()
        {
            Calls.Add("end");
            if (EndFailure is not null)
                throw EndFailure;

            return "result";
        }
    }

    [Fact]
    public void A_job_that_finishes_is_ended_and_its_result_returned()
    {
        var job = new FakeJob { FinishesAfter = TimeSpan.FromMilliseconds(50) };

        var result = WuAsyncRunner.Run(job, CancellationToken.None, Poll);

        Assert.Equal("result", result);
        Assert.Equal(["end"], job.Calls);
    }

    [Fact]
    public void A_stop_aborts_the_job_then_ends_it_and_throws_within_a_second()
    {
        var job = new FakeJob { EndsOnAbort = true };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var sw = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => WuAsyncRunner.Run(job, cts.Token, Poll));

        Assert.True(sw.ElapsedMilliseconds < 1500, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(["abort", "end"], job.Calls);
    }

    [Fact]
    public void A_job_that_ignores_the_abort_is_given_up_after_the_grace_without_a_blocking_end()
    {
        var job = new FakeJob();
        var log = new List<string>();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var sw = Stopwatch.StartNew();
        Assert.Throws<WuOperationStillRunningException>(() =>
            WuAsyncRunner.Run(job, cts.Token, Poll, TimeSpan.FromMilliseconds(300), log.Add));

        Assert.InRange(sw.ElapsedMilliseconds, 250, 2500);
        Assert.Equal(["abort"], job.Calls);
        Assert.Contains(log, l => l.Contains("did not end", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refused_abort_and_a_failing_end_still_end_in_the_cancellation()
    {
        var job = new FakeJob
        {
            FinishesAfter = TimeSpan.FromMilliseconds(120),
            AbortFailure = new COMException("busy", unchecked((int)0x80070005)),
            EndFailure = new COMException("aborted", unchecked((int)0x80240044)),
        };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(30));

        Assert.Throws<OperationCanceledException>(() =>
            WuAsyncRunner.Run(job, cts.Token, Poll, TimeSpan.FromSeconds(2)));

        Assert.Equal(["abort", "end"], job.Calls);
    }

    [Fact]
    public void A_token_cancelled_before_the_call_aborts_at_once()
    {
        var job = new FakeJob { EndsOnAbort = true };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => WuAsyncRunner.Run(job, cts.Token, Poll));

        Assert.Equal(["abort", "end"], job.Calls);
    }

    [Fact]
    public void A_job_already_complete_when_the_stop_arrives_returns_its_result()
    {
        var job = new FakeJob { FinishesAfter = TimeSpan.Zero };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal("result", WuAsyncRunner.Run(job, cts.Token, Poll));
        Assert.Equal(["end"], job.Calls);
    }
}
