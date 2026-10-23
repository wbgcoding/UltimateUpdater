namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Whether a drive optimisation is running anywhere in the app right now. Quitting the app kills
/// it, unlike a cleanup dialog close (that keeps the run going in the background), so <c>App.ExitApplication</c>
/// asks first instead of silently ending it (step 6b's dialog); the tray exit path shares that same one
/// method, so a single flag here covers both. A simple counter rather than a bool: two drives optimising at
/// once must not have the first one finishing clear the flag out from under the second.
/// </summary>
public static class DefragRunState
{
    private static int _runningCount;

    public static bool IsRunning => Volatile.Read(ref _runningCount) > 0;

    /// <summary>Raised, on the thread that ended it, each time a run finishes. Lets something that waits for
    /// all drive optimisations to be over (a pending app update) react without polling.</summary>
    public static event Action? RunEnded;

    /// <summary>Marks one run as started; disposing marks it finished. <c>using var ... = Enter();</c>
    /// around the run is what guarantees the decrement happens on every exit path, cancellation and
    /// exceptions included.</summary>
    public static IDisposable Enter()
    {
        Interlocked.Increment(ref _runningCount);
        return new Handle();
    }

    private sealed class Handle : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // Interlocked.Exchange rather than a bool: Dispose must be safe to call twice without
            // decrementing the shared counter a second time for the same run.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref _runningCount);
                RunEnded?.Invoke();
            }
        }
    }
}
