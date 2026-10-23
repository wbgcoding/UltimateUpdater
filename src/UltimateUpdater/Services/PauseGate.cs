namespace UltimateUpdater.Services;

/// <summary>
/// A hold that long work checks between steps. Open means "keep going", closed means "wait here", so
/// pausing costs nothing while running and needs no polling loop of its own.
/// Cancellation is deliberately NOT folded in: a paused pass that is then stopped must leave the wait with
/// an <see cref="OperationCanceledException"/>, which is what <see cref="Wait"/> does when the token fires.
/// </summary>
public sealed class PauseGate
{
    private readonly ManualResetEventSlim _open = new(initialState: true);

    public bool IsPaused => !_open.IsSet;

    public void Pause() => _open.Reset();

    public void Resume() => _open.Set();

    /// <summary>Blocks while paused. Returns at once when running, throws when the token is cancelled.</summary>
    public void Wait(CancellationToken cancellationToken)
    {
        if (_open.IsSet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        _open.Wait(cancellationToken);
    }
}
