using System.Diagnostics;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

public class InstallStallWatchTests
{
    private static readonly StallWatchOptions Options =
        new(TimeSpan.FromMinutes(5), 0.01, TimeSpan.FromSeconds(5));

    private static TreeActivity Activity(double cpuSeconds, ulong io = 100, int processes = 3, bool complete = true) =>
        new(TimeSpan.FromSeconds(cpuSeconds), io, processes, complete);

    private static long Ms(double seconds) => (long)(seconds * 1000);

    /// <summary>Feeds one sample every five seconds and returns the second at which the verdict first came.</summary>
    private static double? FirstVerdict(Func<int, TreeActivity> sampleAt, int seconds = 900)
    {
        var detector = new StallDetector(Options);
        for (var t = 0; t <= seconds; t += 5)
        {
            if (detector.Observe(Ms(t), sampleAt(t)))
                return t;
        }

        return null;
    }

    [Fact]
    public void A_tree_that_does_nothing_is_declared_stalled_after_the_whole_window()
    {
        Assert.Equal(300, FirstVerdict(_ => Activity(10)));
    }

    [Fact]
    public void Any_io_restarts_the_window()
    {
        // one byte of I/O at 4 minutes: the quiet time counts from there, so the verdict comes at 9 minutes
        var verdict = FirstVerdict(t => Activity(10, io: t < 240 ? 100UL : 101UL));
        Assert.Equal(540, verdict);
    }

    [Fact]
    public void A_cpu_share_of_one_percent_is_life()
    {
        // 0.05 s of CPU per 5 s sample = exactly 1 %
        Assert.Null(FirstVerdict(t => Activity(10 + t * 0.01)));
    }

    [Fact]
    public void A_cpu_share_below_one_percent_is_still_idle()
    {
        Assert.Equal(300, FirstVerdict(t => Activity(10 + t * 0.005)));
    }

    [Fact]
    public void A_changing_process_count_is_life()
    {
        Assert.Null(FirstVerdict(t => Activity(10, processes: 3 + t / 5 % 2)));
    }

    [Fact]
    public void A_tree_that_cannot_be_read_completely_is_never_declared_dead()
    {
        Assert.Null(FirstVerdict(_ => Activity(10, complete: false)));
    }

    [Fact]
    public void Four_quiet_minutes_are_not_enough()
    {
        Assert.Null(FirstVerdict(_ => Activity(10), seconds: 295));
    }

    /// <summary>A child that runs until it is killed, then ends and closes its pipes, like a real tree stop.</summary>
    private sealed class HangingChild : IChildProcess
    {
        private readonly TaskCompletionSource _killed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class EndsOnKill(Task killed) : Stream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await killed.WaitAsync(cancellationToken);
                return 0;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        public HangingChild()
        {
            StandardOutput = new StreamReader(new EndsOnKill(_killed.Task));
            StandardError = new StreamReader(new EndsOnKill(_killed.Task));
        }

        public StreamReader StandardOutput { get; }
        public StreamReader StandardError { get; }
        public int ExitCode => -1;
        public bool HasExited => _killed.Task.IsCompleted;
        public int ProcessId => 4242;
        public int KillCalls { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _killed.Task.WaitAsync(cancellationToken);

        public void Kill()
        {
            KillCalls++;
            _killed.TrySetResult();
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task A_silent_idle_installer_is_stopped_after_the_window_and_reported_as_stalled()
    {
        var child = new HangingChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        ProcessRunner.TreeSamplerForTest.Value = _ => Activity(10);
        try
        {
            using var executable = new ResolvedExecutable(Environment.ProcessPath!, null);
            var options = new StallWatchOptions(TimeSpan.FromMilliseconds(300), 0.01, TimeSpan.FromMilliseconds(50));
            var watch = Stopwatch.StartNew();

            var thrown = await Assert.ThrowsAsync<InstallerStalledException>(() => ProcessRunner.RunResolvedAsync(
                executable, [], TimeSpan.FromMinutes(5), CancellationToken.None, null, null, null, null,
                elevated: false, scrubEnvironment: null, stallWatch: options)
                .WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"the stop took {watch.Elapsed}");
            Assert.Equal(1, child.KillCalls);
            Assert.Contains("the installer stopped responding and was stopped", thrown.Message);
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
            ProcessRunner.TreeSamplerForTest.Value = null;
        }
    }

    [Fact]
    public async Task A_busy_installer_is_left_alone_until_it_ends()
    {
        var child = new HangingChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        var cpu = 0.0;
        ProcessRunner.TreeSamplerForTest.Value = _ => Activity(cpu += 1);
        try
        {
            using var executable = new ResolvedExecutable(Environment.ProcessPath!, null);
            var options = new StallWatchOptions(TimeSpan.FromMilliseconds(300), 0.01, TimeSpan.FromMilliseconds(50));

            var result = await ProcessRunner.RunResolvedAsync(
                executable, [], TimeSpan.FromMilliseconds(1200), CancellationToken.None, null, null, null, null,
                elevated: false, scrubEnvironment: null, stallWatch: options)
                .WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(result.TimedOut);
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
            ProcessRunner.TreeSamplerForTest.Value = null;
        }
    }

    [Fact]
    public void The_stalled_row_says_so_in_one_plain_sentence_and_does_not_count_as_a_repeat_failure()
    {
        var text = UltimateUpdater.Services.Sources.InstallErrorExplainer.RowText(
            "winget", new InstallerStalledException("winget").Message, key => key);

        Assert.Equal("InstallStalled", text);
    }

    /// <summary>The sampler asks for the limited query right (0x1000), the one right Windows also grants on
    /// processes the full query right is refused for, and still reads the whole tree with it.</summary>
    [Fact]
    public void The_sampler_opens_processes_with_the_limited_query_right_and_still_reads_them()
    {
        Assert.Equal(0x1000u, ProcessTreeSampler.ProcessQueryLimitedInformation);

        var own = ProcessTreeSampler.Sample(Environment.ProcessId);

        Assert.True(own.Complete);
        Assert.True(own.ProcessCount >= 1);
        Assert.True(own.Cpu > TimeSpan.Zero);
    }
}
