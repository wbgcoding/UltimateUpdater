using UltimateUpdater.Models;

namespace UltimateUpdater.Tests;

/// <summary>
/// One install search per batch: a real search took 20 to 90 s, and the install ran one per update.
/// </summary>
public class WindowsUpdateSearchCacheTests
{
    private const string Offer = "IsInstalled=0 and IsHidden=0";
    private const string Installed = "IsInstalled=1";
    private const string A = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string B = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string C = "cccccccc-cccc-cccc-cccc-cccccccccccc";

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static UpdateItem Row(string guid) => new()
    {
        Id = guid + ":1",
        SourceId = "windowsupdate",
        Category = UpdateCategory.SystemAndDrivers,
        Name = "Update " + guid[..2],
    };

    private static void Run(WindowsUpdateSource source, string guid) =>
        source.Install(Row(guid), new RecordingProgress(), CancellationToken.None);

    [Fact]
    public void Three_updates_of_one_batch_run_one_search()
    {
        var api = new FakeWindowsUpdateApi();
        api.Offer(Offer, (A, 1), (B, 1), (C, 1));
        var source = new WindowsUpdateSource(new LoggingService(), api, new Clock());

        Run(source, A);
        Run(source, B);
        Run(source, C);

        Assert.Equal([Offer], api.SearchCriteria);
        Assert.Equal(["install:" + A, "install:" + B, "install:" + C],
            api.Calls.Where(c => c.StartsWith("install:", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_miss_in_the_cached_search_triggers_exactly_one_new_search()
    {
        var api = new FakeWindowsUpdateApi();
        api.Offer(Offer, (A, 1));
        var source = new WindowsUpdateSource(new LoggingService(), api, new Clock());
        Run(source, A);

        // B appeared in the offer after the first search ran.
        api.Offer(Offer, (A, 1), (B, 1));
        Run(source, B);

        Assert.Equal([Offer, Offer], api.SearchCriteria);
        Assert.Contains("install:" + B, api.Calls);
    }

    [Fact]
    public void A_miss_that_stays_a_miss_after_the_one_new_search_ends_in_the_installed_lookup()
    {
        var api = new FakeWindowsUpdateApi();
        api.Offer(Offer, (A, 1));
        api.Offer(Installed, (B, 1));
        var source = new WindowsUpdateSource(new LoggingService(), api, new Clock());
        Run(source, A);

        Run(source, B);

        Assert.Equal([Offer, Offer, Installed], api.SearchCriteria);
        Assert.DoesNotContain("install:" + B, api.Calls);
    }

    [Fact]
    public void A_miss_in_a_search_that_was_just_made_does_not_search_again()
    {
        var api = new FakeWindowsUpdateApi();
        api.Offer(Offer, (A, 1));
        var source = new WindowsUpdateSource(new LoggingService(), api, new Clock());

        Assert.Throws<InvalidOperationException>(() => Run(source, B));

        Assert.Equal([Offer, Installed], api.SearchCriteria);
    }

    [Fact]
    public void The_search_is_made_again_once_the_cache_lifetime_has_passed()
    {
        var clock = new Clock();
        var api = new FakeWindowsUpdateApi();
        api.Offer(Offer, (A, 1), (B, 1));
        var source = new WindowsUpdateSource(new LoggingService(), api, clock);
        Run(source, A);

        clock.Now += WindowsUpdateSource.SearchCacheLifetime - TimeSpan.FromSeconds(1);
        Run(source, B);
        Assert.Single(api.SearchCriteria);

        clock.Now += TimeSpan.FromSeconds(2);
        Run(source, A);
        Assert.Equal([Offer, Offer], api.SearchCriteria);
    }

    [Fact]
    public void A_replaced_search_is_released_and_a_failing_new_search_keeps_the_old_cache()
    {
        var clock = new Clock();
        var api = new FakeWindowsUpdateApi();
        api.Offer(Offer, (A, 1));
        var source = new WindowsUpdateSource(new LoggingService(), api, clock);
        Run(source, A);
        Assert.Equal(0, api.DisposedResults);

        clock.Now += WindowsUpdateSource.SearchCacheLifetime + TimeSpan.FromSeconds(1);
        api.FailSearch(Offer, new InvalidOperationException("Windows Update search failed."));
        Assert.Throws<InvalidOperationException>(() => Run(source, A));
        Assert.Equal(0, api.DisposedResults);

        api.FailSearch(Offer, null);
        Run(source, A);
        Assert.Equal(1, api.DisposedResults);
    }
}
