using System.Diagnostics;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// A stop or timeout must return even when a descendant that escaped the kill still holds the inherited
/// write end of the output pipe, so the pumps never see EOF.
/// </summary>
public class ProcessRunnerStopTests
{
    /// <summary>A stream that yields one chunk, then blocks every read, ignoring cancellation like a pipe
    /// held open by a grandchild, until it is disposed.</summary>
    private sealed class StuckStream(byte[] first) : Stream
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _gaveFirst;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_gaveFirst)
            {
                _gaveFirst = true;
                first.CopyTo(buffer);
                return first.Length;
            }

            await _disposed.Task;
            throw new ObjectDisposedException(nameof(StuckStream));
        }

        protected override void Dispose(bool disposing)
        {
            _disposed.TrySetResult();
            base.Dispose(disposing);
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

    /// <summary>Never exits, and its Kill does nothing: the escaped-descendant case.</summary>
    private sealed class UnkillableChild : IChildProcess
    {
        public StreamReader StandardOutput { get; } = new(new StuckStream("partial"u8.ToArray()));
        public StreamReader StandardError { get; } = new(new StuckStream([]));
        public int ExitCode => -1;
        public bool HasExited => false;
        public int ProcessId => 0;
        public int KillCalls { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);

        public void Kill() => KillCalls++;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task A_timeout_returns_although_the_output_pipe_is_never_closed()
    {
        var child = new UnkillableChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        var warnings = new List<string>();
        ProcessRunner.Diagnostics = warnings.Add;
        try
        {
            using var executable = new ResolvedExecutable(Environment.ProcessPath!, null);
            var watch = Stopwatch.StartNew();

            var result = await ProcessRunner.RunResolvedAsync(
                executable, [], TimeSpan.FromMilliseconds(300), CancellationToken.None, null, null, null, null, elevated: false)
                .WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(result.TimedOut);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"the timeout took {watch.Elapsed}");
            Assert.Equal(1, child.KillCalls);
            Assert.Equal("partial", result.StandardOutput.Trim());
            Assert.Single(warnings);
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
            ProcessRunner.Diagnostics = null;
        }
    }

    [Fact]
    public async Task A_timeout_without_kill_leaves_the_child_running_and_releases_it_once_it_exits()
    {
        var child = new LingeringChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        try
        {
            using var executable = new ResolvedExecutable(Environment.ProcessPath!, null);

            var result = await ProcessRunner.RunResolvedAsync(
                executable, [], TimeSpan.FromMilliseconds(300), CancellationToken.None, null, null, null, null,
                elevated: false, killOnTimeout: false)
                .WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(result.TimedOut);
            Assert.Equal(0, child.KillCalls);
            Assert.False(child.IsDisposed);

            child.Exit();
            await child.Disposed.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, child.KillCalls);
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
        }
    }

    [Fact]
    public async Task A_child_that_never_exits_is_let_go_after_the_detached_lifetime_without_a_kill()
    {
        var child = new LingeringChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        ProcessRunner.DetachedLifetimeForTest.Value = TimeSpan.FromMilliseconds(300);
        try
        {
            using var executable = new ResolvedExecutable(Environment.ProcessPath!, null);

            var result = await ProcessRunner.RunResolvedAsync(
                executable, [], TimeSpan.FromMilliseconds(300), CancellationToken.None, null, null, null, null,
                elevated: false, killOnTimeout: false)
                .WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(result.TimedOut);
            await child.Disposed.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, child.KillCalls);
            Assert.False(child.HasExited);
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
            ProcessRunner.DetachedLifetimeForTest.Value = null;
        }
    }

    [Fact]
    public async Task A_stop_request_still_ends_a_child_started_without_kill_on_timeout()
    {
        var child = new LingeringChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        try
        {
            using var executable = new ResolvedExecutable(Environment.ProcessPath!, null);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunResolvedAsync(
                executable, [], TimeSpan.FromSeconds(60), stop.Token, null, null, null, null,
                elevated: false, killOnTimeout: false)
                .WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.Equal(1, child.KillCalls);
            Assert.True(child.IsDisposed);
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
        }
    }

    [Fact]
    public async Task A_stop_request_returns_although_the_output_pipe_is_never_closed()
    {
        var child = new UnkillableChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        try
        {
            using var executable = new ResolvedExecutable(Environment.ProcessPath!, null);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunResolvedAsync(
                executable, [], TimeSpan.FromSeconds(60), stop.Token, null, null, null, null, elevated: false)
                .WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
        }
    }
}
