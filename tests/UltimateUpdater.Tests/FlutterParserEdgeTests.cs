using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>Edges of the Flutter feed readers that the happy-path tests do not reach.</summary>
public class FlutterParserEdgeTests
{
    private static string Feed(string releases, string current = "{\"stable\":\"h1\"}") =>
        $"{{\"current_release\":{current},\"releases\":{releases}}}";

    [Theory]
    [InlineData("{\"channel\":\"stable\"}")]                                  // no version
    [InlineData("{\"frameworkVersion\":\"3.1.0\"}")]                          // no channel
    [InlineData("{\"frameworkVersion\":\"\",\"channel\":\"stable\"}")]        // empty version
    [InlineData("{\"frameworkVersion\":\"3.1.0\",\"channel\":\"\"}")]         // empty channel
    [InlineData("{\"frameworkVersion\":3,\"channel\":\"stable\"}")]           // wrong type
    public void An_installed_report_missing_either_half_is_not_a_version(string json) =>
        Assert.Null(FlutterParsers.ParseInstalled(json));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_channel_finds_no_release(string channel) =>
        Assert.Null(FlutterParsers.ParseLatestRelease(Feed("[]"), channel));

    [Theory]
    [InlineData("[]")]                                                // root is an array
    [InlineData("{\"releases\":[]}")]                                 // no current_release
    [InlineData("{\"current_release\":[],\"releases\":[]}")]          // current_release not an object
    [InlineData("{\"current_release\":{\"stable\":\"h1\"}}")]         // no releases list
    [InlineData("{\"current_release\":{\"stable\":\"h1\"},\"releases\":{}}")] // releases not an array
    [InlineData("{\"current_release\":{\"stable\":\"\"},\"releases\":[]}")]   // empty hash for the channel
    public void A_feed_with_a_missing_or_misshapen_part_yields_nothing(string json) =>
        Assert.Null(FlutterParsers.ParseLatestRelease(json, "stable"));

    [Fact]
    public void A_hash_that_belongs_to_another_channel_is_not_taken()
    {
        // a hash is only unique per channel: the same hash on beta must not answer a stable request
        var json = Feed("[{\"hash\":\"h1\",\"channel\":\"beta\",\"version\":\"9.9.9\"}]");

        Assert.Null(FlutterParsers.ParseLatestRelease(json, "stable"));
    }

    [Fact]
    public void Non_object_entries_and_entries_without_a_version_are_skipped_to_the_next_match()
    {
        var json = Feed(
            "[42,\"x\",null,{\"hash\":\"h1\",\"channel\":\"stable\",\"version\":\"\"},"
            + "{\"hash\":\"H1\",\"channel\":\"STABLE\",\"version\":\"3.2.0\"}]");

        var release = FlutterParsers.ParseLatestRelease(json, "stable");

        Assert.NotNull(release);
        Assert.Equal("3.2.0", release!.Value.Version);
    }

    [Fact]
    public void An_unreadable_date_and_an_empty_archive_become_null_not_a_failure()
    {
        var json = Feed("[{\"hash\":\"h1\",\"channel\":\"stable\",\"version\":\"3.2.0\",\"release_date\":\"soon\",\"archive\":\"\"}]");

        var release = FlutterParsers.ParseLatestRelease(json, "stable");

        Assert.NotNull(release);
        Assert.Null(release!.Value.ReleaseDate);
        Assert.Null(release.Value.ArchivePath);
    }

    [Fact]
    public void A_full_entry_carries_its_date_in_utc_and_its_archive_path()
    {
        var json = Feed("[{\"hash\":\"h1\",\"channel\":\"stable\",\"version\":\"3.2.0\","
            + "\"release_date\":\"2026-07-30T20:57:55.349209Z\",\"archive\":\"stable/windows/flutter_windows_3.2.0-stable.zip\"}]");

        var release = FlutterParsers.ParseLatestRelease(json, "stable")!.Value;

        Assert.Equal(new DateTimeOffset(2026, 7, 30, 20, 57, 55, TimeSpan.Zero).AddTicks(3492090), release.ReleaseDate);
        Assert.Equal("stable/windows/flutter_windows_3.2.0-stable.zip", release.ArchivePath);
    }

    [Fact]
    public void A_date_without_an_offset_is_read_as_utc()
    {
        var json = Feed("[{\"hash\":\"h1\",\"channel\":\"stable\",\"version\":\"3.2.0\",\"release_date\":\"2026-07-30T20:00:00\"}]");

        var release = FlutterParsers.ParseLatestRelease(json, "stable")!.Value;

        Assert.Equal(TimeSpan.Zero, release.ReleaseDate!.Value.Offset);
        Assert.Equal(20, release.ReleaseDate.Value.Hour);
    }
}
