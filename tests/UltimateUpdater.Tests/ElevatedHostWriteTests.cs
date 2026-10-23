using System.IO;
using System.IO.Pipes;
using System.Text;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>A stop clicked while a job line is being written must not tear the line (the worker ends the
/// whole session on a line it cannot parse); the stop is sent afterwards instead.</summary>
public class ElevatedHostWriteTests
{
    /// <summary>A pipe stand-in whose write waits for a gate and, like a real async pipe, gives up with a
    /// cancellation when the token it was handed fires first.</summary>
    private sealed class SlowStream : Stream
    {
        private readonly MemoryStream _sink = new();
        private readonly object _lock = new();

        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Written
        {
            get
            {
                lock (_lock)
                    return Encoding.UTF8.GetString(_sink.ToArray());
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await Gate.Task.WaitAsync(cancellationToken);

            lock (_lock)
                _sink.Write(buffer.Span);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_stop_during_the_job_write_still_delivers_the_whole_line_and_then_the_cancel()
    {
        var host = new ElevatedHost(new LoggingService())
        {
            EnsureStartedOverrideForTest = _ => Task.FromResult(true),
        };
        using var workerToApp = new AnonymousPipeServerStream(PipeDirection.In);
        using var workerEnd = new AnonymousPipeClientStream(PipeDirection.Out, workerToApp.ClientSafePipeHandle);
        var slow = new SlowStream();

        host.AttachSessionForTest(
            new StreamReader(workerToApp, Encoding.UTF8),
            new StreamWriter(slow, new UTF8Encoding(false)) { AutoFlush = true });

        using var cts = new CancellationTokenSource();
        var run = host.RunAsync("install", new { name = "x" }, _ => true, cts.Token);

        await slow.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Task.Delay(100);
        Assert.False(run.IsCompleted, "the call must wait for the write instead of abandoning it half way");

        slow.Gate.SetResult();

        // The cancel control line follows the job line on the same stream.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && slow.Written.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            await Task.Delay(20);

        var lines = slow.Written.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2, $"expected the job line and a cancel, got: {slow.Written}");
        Assert.Contains("\"kind\":\"install\"", lines[0]);
        Assert.EndsWith("}", lines[0].TrimEnd('\r'));
        Assert.Contains("\"cancel\"", lines[1]);

        workerEnd.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_sends_nothing()
    {
        var host = new ElevatedHost(new LoggingService())
        {
            EnsureStartedOverrideForTest = _ => Task.FromResult(true),
        };
        using var workerToApp = new AnonymousPipeServerStream(PipeDirection.In);
        using var workerEnd = new AnonymousPipeClientStream(PipeDirection.Out, workerToApp.ClientSafePipeHandle);
        var slow = new SlowStream();
        slow.Gate.SetResult();

        host.AttachSessionForTest(
            new StreamReader(workerToApp, Encoding.UTF8),
            new StreamWriter(slow, new UTF8Encoding(false)) { AutoFlush = true });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.RunAsync("install", new { }, _ => true, cts.Token));

        Assert.Equal(string.Empty, slow.Written);
    }
}
