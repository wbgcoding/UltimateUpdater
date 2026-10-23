using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>Two batches deciding at the same moment must not both ask for a restore point.</summary>
public class RestorePointReservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Two_concurrent_decisions_yield_exactly_one_reservation()
    {
        var reservation = new RestorePointReservation();
        using var bothReady = new Barrier(2);

        // Both read the same stale timestamp (none yet) at the same moment, the shape of the original race.
        IDisposable? Decide()
        {
            return reservation.TryReserve(() =>
            {
                bothReady.SignalAndWait(TimeSpan.FromMilliseconds(300));
                return null;
            }, Now);
        }

        var results = Enumerable.Range(0, 2).Select(_ => Task.Run(Decide)).ToArray();
        await Task.WhenAll(results);

        var leases = results.Select(task => task.Result).ToList();
        Assert.Equal(1, leases.Count(lease => lease is not null));
        foreach (var lease in leases)
            lease?.Dispose();
    }

    [Fact]
    public void Many_concurrent_decisions_still_yield_exactly_one_reservation()
    {
        var reservation = new RestorePointReservation();

        var leases = Enumerable.Range(0, 32)
            .AsParallel().WithDegreeOfParallelism(16)
            .Select(_ => reservation.TryReserve(() => null, Now))
            .ToList();

        Assert.Equal(1, leases.Count(lease => lease is not null));
    }

    [Fact]
    public void A_released_reservation_lets_the_next_job_decide_again()
    {
        var reservation = new RestorePointReservation();

        var first = reservation.TryReserve(() => null, Now);
        Assert.NotNull(first);
        Assert.Null(reservation.TryReserve(() => null, Now.AddSeconds(5)));

        // the job never went out (declined prompt, failed send): the slot is handed back
        first.Dispose();

        Assert.NotNull(reservation.TryReserve(() => null, Now.AddSeconds(10)));
    }

    [Fact]
    public void A_recent_timestamp_on_disk_still_refuses()
    {
        var reservation = new RestorePointReservation();

        Assert.Null(reservation.TryReserve(() => Now.AddHours(-2), Now));
    }

    [Fact]
    public void A_reservation_older_than_a_day_no_longer_blocks()
    {
        var reservation = new RestorePointReservation();
        _ = reservation.TryReserve(() => null, Now);   // never released, as if the process had leaked it

        Assert.NotNull(reservation.TryReserve(() => null, Now.AddHours(25)));
    }
}
