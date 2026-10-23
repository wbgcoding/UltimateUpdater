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
