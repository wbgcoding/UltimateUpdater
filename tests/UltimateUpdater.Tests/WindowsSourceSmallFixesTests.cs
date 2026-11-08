using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>Small edge cases of the Windows Update, driver-vendor and mock sources.</summary>
public class WindowsSourceSmallFixesTests
{
    [Theory]
    [InlineData("Intel Corporation - Display - 31.0.101.5333 Version 31.0.101.5333", "31.0.101.5333")]
    [InlineData("NVIDIA - Display - Driver 32.0.15.6094", "32.0.15.6094")]
    [InlineData("Intel - Net - version 22.250.0.1", "22.250.0.1")]
    public void The_version_is_the_number_right_after_Version_or_Driver(string title, string expected) =>
        Assert.Equal(expected, WindowsUpdateSource.VersionFromTitle(title));

    [Theory]
    [InlineData("2024-05 Cumulative Update for Windows 11 Version 23H2 for x64-based Systems (KB5039212)")]
    [InlineData("Windows Malicious Software Removal Tool x64 - v5.126 (KB890830)")]
    [InlineData("")]
    public void A_title_without_a_version_after_the_keyword_gives_none_so_the_kb_number_is_used(string title) =>
        Assert.Null(WindowsUpdateSource.VersionFromTitle(title));

    [Theory]
    [InlineData(" - ")]
    [InlineData("(,)")]
    [InlineData("®-")]
    public void A_title_made_only_of_separators_matches_no_vendor(string title)
    {
        var tools = new DriverVendorTools(_ => false, text => text, [new("nvidia", ["nvidia"], [], null)]);

        Assert.Null(tools.VendorFor("windowsupdate", title));
    }

    [Fact]
    public void The_mock_install_size_comes_from_a_fixed_hash_not_a_per_process_one()
    {
        // 17, then h * 31 + char for "abc": 602801, above the 15 MB floor.
        Assert.Equal(15_000_000L + 602_801L, MockUpdateSource.FakeSizeFor("abc"));
        Assert.Equal(15_000_000L + 17L, MockUpdateSource.FakeSizeFor(""));

        foreach (var name in new[] { "", "a", "Mozilla Firefox", new string('￿', 500) })
        {
            var size = MockUpdateSource.FakeSizeFor(name);
            Assert.InRange(size, 15_000_000L, 75_000_000L - 1);
        }
    }

    [Fact]
    public async Task The_reported_mock_install_size_is_equal_across_two_runs()
    {
        async Task<long?> TotalAsync()
        {
            var source = MockUpdateSource.CreateAll().First(s => s.Id == "winget");
            UpdateItem? item = null;
            await foreach (var scanned in source.ScanAsync(CancellationToken.None))
            {
                item = scanned;
                break;
            }

            long? total = null;
            await source.InstallAsync(item!, new Immediate(value => total = value.BytesTotal), CancellationToken.None);
            Assert.Equal(MockUpdateSource.FakeSizeFor(item!.Name), total);
            return total;
        }

        Assert.Equal(await TotalAsync(), await TotalAsync());
    }

    private sealed class Immediate(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }
}
