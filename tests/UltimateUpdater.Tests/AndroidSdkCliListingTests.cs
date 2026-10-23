using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// The newer Android command-line tools print one "Installed packages:" section in which a package that
/// has an update carries "installed -> available" inline, and no separate updates table. The rows below are
/// taken verbatim from a real listing (column spacing included): the emulator and one system image have an
/// update, another image would be a downgrade (9.0.0 -> 3.0.0) and must not be offered.
/// </summary>
public class AndroidSdkCliListingTests
{
    private const string Listing = """
        Installed packages:
          add-ons/addon-google_apis-google-15                                                     3.0.0                                   Google APIs
          cmdline-tools/23.0                                                                      23.0.0                                  Android SDK Command-line Tools
          cmdline-tools/latest                                                                    23.0.0                                  Android SDK Command-line Tools (latest)
          emulator                                                                                37.1.11              ->        37.2.12  Android Emulator
          extras/google/auto                                                                      2.0.0                                   Android Auto Desktop Head Unit Emulator
          system-images/android-37.1/google_apis_ps16k/arm64-v8a                                  9.0.0                                   16 KB Page Size Google APIs ARM 64 v8a System Image
          system-images/android-37.2-beta3/google_apis_ps16k/arm64-v8a                            9.0.0                ->        3.0.0    16 KB Page Size Google APIs ARM 64 v8a System Image
          system-images/android-37.2/google_apis_playstore_ps16k/arm64-v8a                        5.0.0                ->        6.0.0    16 KB Page Size Google Play ARM 64 v8a System Image
        """;

    [Fact]
    public void The_inline_arrow_layout_yields_only_real_updates()
    {
        var packages = PackageManagerParsers.ParseSdkManagerList(Listing, out var recognized);

        Assert.True(recognized);
        Assert.Collection(packages.OrderBy(p => p.Id, StringComparer.Ordinal),
            p =>
            {
                Assert.Equal("emulator", p.Id);
                Assert.Equal("37.1.11", p.Installed);
                Assert.Equal("37.2.12", p.Available);
            },
            p =>
            {
                Assert.Equal("system-images/android-37.2/google_apis_playstore_ps16k/arm64-v8a", p.Id);
                Assert.Equal("5.0.0", p.Installed);
                Assert.Equal("6.0.0", p.Available);
            });
    }

    /// <summary>A listing with nothing to update is still a recognised, successful scan: the tool exits with
    /// 127 after printing it, which must not turn into a failed source.</summary>
    [Fact]
    public void An_inline_layout_listing_without_updates_is_recognised_and_empty()
    {
        const string upToDate = "Installed packages:\n  emulator                          37.2.12       Android Emulator\n";

        var packages = PackageManagerParsers.ParseSdkManagerList(upToDate, out var recognized);

        Assert.True(recognized);
        Assert.Empty(packages);
    }

    [Fact]
    public void The_scan_keeps_the_parsed_rows_although_the_tool_exits_with_127()
    {
        var source = new AndroidSdkSource(new LoggingService(), new System.Net.Http.HttpClient());

        var items = source.BuildScanResult(new ProcessResult(127, Listing, string.Empty));

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void An_update_is_confirmed_when_the_package_row_is_gone_from_the_listing()
    {
        var afterUpdate = Listing.Replace("37.1.11              ->        37.2.12", "37.2.12                                ");

        Assert.False(AndroidSdkSource.OffersNoUpdateFor(new ProcessResult(127, Listing, string.Empty), "emulator"));
        Assert.True(AndroidSdkSource.OffersNoUpdateFor(new ProcessResult(127, afterUpdate, string.Empty), "emulator"));
    }

    [Fact]
    public void A_removal_is_confirmed_when_the_listing_no_longer_holds_the_package()
    {
        var withoutAuto = string.Join('\n', Listing.Split('\n').Where(l => !l.Contains("extras/google/auto")));

        Assert.False(AndroidSdkSource.IsGoneFromListing(new ProcessResult(127, Listing, string.Empty), "extras/google/auto"));
        Assert.True(AndroidSdkSource.IsGoneFromListing(new ProcessResult(127, withoutAuto, string.Empty), "extras/google/auto"));
        Assert.False(AndroidSdkSource.IsGoneFromListing(new ProcessResult(127, "garbage", string.Empty), "extras/google/auto"));
    }

    [Fact]
    public void An_update_that_left_the_package_missing_is_not_confirmed()
    {
        var withoutEmulator = string.Join('\n', Listing.Split('\n').Where(l => !l.TrimStart().StartsWith("emulator ")));

        Assert.False(AndroidSdkSource.OffersNoUpdateFor(new ProcessResult(127, withoutEmulator, string.Empty), "emulator"));
    }

    /// <summary>An unreadable listing proves nothing, so it never counts as a confirmed update.</summary>
    [Fact]
    public void An_unreadable_listing_does_not_confirm_an_update() =>
        Assert.False(AndroidSdkSource.OffersNoUpdateFor(new ProcessResult(127, "garbage", string.Empty), "emulator"));
}
