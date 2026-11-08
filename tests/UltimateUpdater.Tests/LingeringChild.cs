using System.IO;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>A child that keeps running until the test lets it exit, with empty output, and records whether it
/// was killed or disposed.</summary>
internal sealed class LingeringChild : IChildProcess
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StreamReader StandardOutput { get; } = new(new MemoryStream());

    public StreamReader StandardError { get; } = new(new MemoryStream());

    public int ExitCode => 0;

    public bool HasExited => _exit.Task.IsCompleted;

    public int ProcessId => 0;

    public int KillCalls { get; private set; }

    public bool IsDisposed => _disposed.Task.IsCompleted;

    public Task Disposed => _disposed.Task;

    public void Exit() => _exit.TrySetResult();

    public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

    public void Kill() => KillCalls++;

    public void Dispose() => _disposed.TrySetResult();
}
