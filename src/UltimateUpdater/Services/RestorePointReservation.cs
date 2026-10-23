namespace UltimateUpdater.Services;

/// <summary>
/// Makes "should this job create a restore point" a single step across every job that asks at the same
/// moment. The decision reads the timestamp file, and that file is only written once the elevated worker
/// reports a created point, which is up to a minute after the job went out: two batches deciding inside that
/// window both read the old timestamp and both asked for a restore point. The first caller reserves the slot
/// in memory under one gate, so a second caller inside the window is told no; the reservation is handed
/// back when the job is over, by which time the real timestamp (or its absence) is on disk and decides alone.
/// </summary>
internal sealed class RestorePointReservation
{
    /// <summary>The one reservation every install batch of this process shares.</summary>
    internal static RestorePointReservation Shared { get; } = new();

    private readonly object _gate = new();
    private DateTimeOffset? _pending;

    /// <summary>A lease for the slot: disposing it gives the slot back. Null when the answer was no.</summary>
    internal IDisposable? TryReserve(Func<DateTimeOffset?> readLastCreated, DateTimeOffset now)
    {
        lock (_gate)
        {
            // The in-memory reservation is checked first: it is newer than anything the file can say yet.
            if (!RestorePointThrottle.ShouldCreate(_pending, now) || !RestorePointThrottle.ShouldCreate(readLastCreated(), now))
                return null;

            _pending = now;
            return new Lease(this, now);
        }
    }

    private void Release(DateTimeOffset reservedAt)
    {
        lock (_gate)
        {
            // Only the reservation that is still current: a newer one belongs to a later job.
            if (_pending == reservedAt)
                _pending = null;
        }
    }

    private sealed class Lease(RestorePointReservation owner, DateTimeOffset reservedAt) : IDisposable
    {
        public void Dispose() => owner.Release(reservedAt);
    }
}
