using UltimateUpdater.Services.Uninstall;
using static UltimateUpdater.Tests.UninstallerViewModelTests;

namespace UltimateUpdater.Tests;

/// <summary>The once-per-session background read of the uninstaller listing: it fills the saved listing, an opening window
/// takes its task instead of reading again, and every failure falls back to the window's own read.</summary>
public class UninstallListingPrefetchTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static UninstallListingPrefetch Prefetch(FakeBackend backend, Func<DateTime>? clock = null) =>
        new(backend, new LoggingService(), clock);

    [Fact]
    public async Task The_prefetch_reads_the_listing_and_saves_it_as_the_cache()
    {
        var backend = new FakeBackend(Listing("A"), Listing("B"));
        var prefetch = Prefetch(backend);

        Assert.True(prefetch.Start());
        var rows = await prefetch.Current!.WaitAsync(Patience);

        Assert.Equal(["A", "B"], rows.Select(row => row.Entry.Name));
        Assert.Equal(1, backend.ListCalls);
        Assert.Equal(["A", "B"], Assert.Single(backend.Saved).Select(entry => entry.Name));
    }

    [Fact]
    public async Task Starting_twice_reads_once()
    {
        var backend = new FakeBackend(Listing("A"));
        var prefetch = Prefetch(backend);

        Assert.True(prefetch.Start());
        Assert.False(prefetch.Start());
        await prefetch.Current!.WaitAsync(Patience);

        Assert.Equal(1, backend.ListCalls);
    }

    [Fact]
    public async Task A_window_opened_during_the_prefetch_waits_for_it_and_reads_nothing_itself()
    {
        var gate = new TaskCompletionSource();
        var reader = new FakeBackend(Listing("A")) { ListGate = gate.Task };
        var prefetch = Prefetch(reader);
        prefetch.Start();
        var window = new UninstallerBackend(new LoggingService(), elevated: null, prefetch: prefetch);

        var open = window.ListForOpenAsync(CancellationToken.None);
        Assert.False(open.IsCompleted);

        gate.SetResult();
        var rows = await open.WaitAsync(Patience);

        Assert.Equal(["A"], rows.Select(row => row.Entry.Name));
        Assert.Equal(1, reader.ListCalls);
    }

    [Fact]
    public async Task A_finished_prefetch_is_reused_while_it_is_fresh_and_not_after()
    {
        var now = new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc);
        var backend = new FakeBackend(Listing("A"));
        var prefetch = Prefetch(backend, () => now);
        prefetch.Start();
        await prefetch.Current!.WaitAsync(Patience);

        now += UninstallListingPrefetch.ReuseWindow - TimeSpan.FromSeconds(1);
        Assert.NotNull(await prefetch.TryTakeAsync(CancellationToken.None));

        now += TimeSpan.FromSeconds(2);
        Assert.Null(await prefetch.TryTakeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_started_prefetch_there_is_nothing_to_take()
    {
        Assert.Null(await Prefetch(new FakeBackend()).TryTakeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_prefetch_leaves_the_window_to_its_own_read()
    {
        var backend = new FakeBackend(Listing("A")) { ListThrows = new IOException("boom") };
        var prefetch = Prefetch(backend);
        prefetch.Start();

        Assert.Null(await prefetch.TryTakeAsync(CancellationToken.None));
        Assert.Empty(backend.Saved);
    }

    [Fact]
    public async Task Cancelling_stops_a_read_in_flight_and_the_window_reads_for_itself()
    {
        var gate = new TaskCompletionSource();
        var backend = new FakeBackend(Listing("A")) { ListGate = gate.Task };
        var prefetch = Prefetch(backend);
        prefetch.Start();

        prefetch.Cancel();

        Assert.Null(await prefetch.TryTakeAsync(CancellationToken.None).WaitAsync(Patience));
        Assert.Empty(backend.Saved);
    }

    [Fact]
    public async Task A_window_that_is_closed_while_waiting_stops_waiting_without_stopping_the_prefetch()
    {
        var gate = new TaskCompletionSource();
        var backend = new FakeBackend(Listing("A")) { ListGate = gate.Task };
        var prefetch = Prefetch(backend);
        prefetch.Start();
        using var closed = new CancellationTokenSource();

        var take = prefetch.TryTakeAsync(closed.Token);
        await closed.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => take.WaitAsync(Patience));

        gate.SetResult();
        Assert.Single(await prefetch.Current!.WaitAsync(Patience));
    }

    [Fact]
    public async Task The_read_runs_off_the_calling_thread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var readOn = new TaskCompletionSource<int>();
        var backend = new FakeBackend(Listing("A")) { OnList = () => readOn.TrySetResult(Environment.CurrentManagedThreadId) };
        var prefetch = Prefetch(backend);

        prefetch.Start();

        Assert.NotEqual(caller, await readOn.Task.WaitAsync(Patience));
        await prefetch.Current!.WaitAsync(Patience);
    }
}
