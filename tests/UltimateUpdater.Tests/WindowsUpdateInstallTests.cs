using UltimateUpdater.Models;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="WindowsUpdateSource.Install"/> against a scripted Windows Update API: what an update that is
/// no longer in the offer means, and which update row the scan may show at all.
/// </summary>
public class WindowsUpdateInstallTests
{
    private const string Offer = "IsInstalled=0 and IsHidden=0";
    private const string Installed = "IsInstalled=1";
    private const string Guid1 = "f810c1cb-e548-4194-990e-7334879ceda0";

    private static UpdateItem Row(string guid, int revision = 1) => new()
    {
        Id = $"{guid}:{revision}",
        SourceId = "windowsupdate",
        Category = UpdateCategory.SystemAndDrivers,
        Name = "Some update " + guid[..4],
    };

    private static WindowsUpdateSource SourceOver(FakeWindowsUpdateApi api) => new(new LoggingService(), api);

    [Fact]
    public void An_update_that_left_the_offer_but_is_installed_counts_as_success()
    {
        var api = new FakeWindowsUpdateApi();
        api.Offer(Offer);
        api.Offer(Installed, (Guid1, 1));
        var progress = new RecordingProgress();

        SourceOver(api).Install(Row(Guid1), progress, CancellationToken.None);

        Assert.Equal([Offer, Installed], api.SearchCriteria);
        Assert.Empty(api.Calls);
        var last = progress.Reports[^1];
        Assert.Equal(100, last.PercentComplete);
        Assert.False(last.RebootRequired);
    }

    [Fact]
    public void An_update_absent_from_the_offer_and_the_installed_list_keeps_the_no_longer_offered_error()
    {
        var api = new FakeWindowsUpdateApi();
        api.Offer(Offer);
        api.Offer(Installed, ("11111111-1111-1111-1111-111111111111", 1));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SourceOver(api).Install(Row(Guid1), new RecordingProgress(), CancellationToken.None));

        Assert.Contains("is no longer offered", ex.Message);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void A_failing_installed_lookup_keeps_the_no_longer_offered_error()
    {
        var api = new FakeWindowsUpdateApi();
        api.Offer(Offer);
        api.FailSearch(Installed, new InvalidOperationException("Windows Update search failed."));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SourceOver(api).Install(Row(Guid1), new RecordingProgress(), CancellationToken.None));

        Assert.Contains("is no longer offered", ex.Message);
    }

    [Fact]
    public void An_offered_update_is_downloaded_and_installed_without_asking_the_installed_list()
    {
        var api = new FakeWindowsUpdateApi { OnInstall = (_, _) => new WuInstallOutcome(true) };
        api.Offer(Offer, (Guid1, 3));
        var progress = new RecordingProgress();

        SourceOver(api).Install(Row(Guid1), progress, CancellationToken.None);

        Assert.Equal([Offer], api.SearchCriteria);
        Assert.Equal(["eula:" + Guid1, "download:" + Guid1, "install:" + Guid1], api.Calls);
        Assert.True(progress.Reports[^1].RebootRequired);
        Assert.Equal(100, progress.Reports[^1].PercentComplete);
    }

    [Fact]
    public void A_cancellation_before_the_download_stops_the_install()
    {
        var api = new FakeWindowsUpdateApi();
        api.Offer(Offer, (Guid1, 1));
        using var cts = new CancellationTokenSource();
        api.OnDownload = (_, _) => cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SourceOver(api).Install(Row(Guid1), new RecordingProgress(), cts.Token));

        Assert.DoesNotContain("install:" + Guid1, api.Calls);
    }

    [Fact]
    public void Offered_rows_whose_guid_is_installed_are_dropped_from_the_scan()
    {
        var offered = new List<UpdateItem> { Row(Guid1), Row("22222222-2222-2222-2222-222222222222", 7) };
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Guid1.ToUpperInvariant() };

        var kept = WindowsUpdateSource.DropInstalled(offered, installed, out var dropped);

        Assert.Single(dropped);
        Assert.Equal($"{Guid1}:1", dropped[0].Id);
        Assert.Single(kept);
        Assert.Equal("22222222-2222-2222-2222-222222222222:7", kept[0].Id);
    }

    [Fact]
    public void With_nothing_installed_every_offered_row_is_kept()
    {
        var offered = new List<UpdateItem> { Row(Guid1) };

        var kept = WindowsUpdateSource.DropInstalled(offered, [], out var dropped);

        Assert.Empty(dropped);
        Assert.Same(offered, kept);
    }
}
