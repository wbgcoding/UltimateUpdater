namespace UltimateUpdater.Services;

/// <summary>
/// Marks work that belongs to an update scan, so the tool processes it starts yield to the user's own
/// programs (<see cref="ProcessRunner"/> launches them below normal priority). An installation never enters
/// it: the user is waiting on that and it keeps the normal priority. The mark is an async-local, so it
/// follows the scan across awaits and thread hops, and a scope entered inside an async method does not
/// reach the method's caller.
/// </summary>
internal static class ScanScope
{
    private static readonly AsyncLocal<bool> Active = new();

    public static bool IsActive => Active.Value;

    public static IDisposable Enter()
    {
        var previous = Active.Value;
        Active.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }
}
