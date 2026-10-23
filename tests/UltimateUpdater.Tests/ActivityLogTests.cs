using UltimateUpdater.Models;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// The log has two jobs: say what went wrong, and say when something was installed, months later, from a
/// file somebody else may be reading. These fix the shape of the lines that answer those two questions,
/// including the part nobody thinks about until the file is 200 MB: how little each line costs.
/// </summary>
public class ActivityLogTests
{
    private static UpdateItem Item(string installed = "128.0.3", string available = "129.0.1") => new()
    {
        Id = "Mozilla.Firefox",
        SourceId = "winget",
        Category = UpdateCategory.Software,
        Name = "Mozilla Firefox",
        InstalledVersion = installed,
        AvailableVersion = available,
    };

    [Fact]
    public void An_update_says_which_package_moved_from_where_to_where()
    {
        Assert.Equal(
            "start update winget:Mozilla.Firefox 128.0.3>129.0.1 admin",
            ActivityLog.InstallStarted(Item(), InstallAction.Install, elevated: true));

        Assert.Equal(
            "ok update winget:Mozilla.Firefox 12.4s",
            ActivityLog.InstallSucceeded(Item(), InstallAction.Install, TimeSpan.FromSeconds(12.42)));
    }

    [Fact]
    public void A_removal_reads_as_a_removal_not_as_an_update()
    {
        Assert.Equal(
            "start remove winget:Mozilla.Firefox 128.0.3",
            ActivityLog.InstallStarted(Item(), InstallAction.Uninstall, elevated: false));
    }

    /// <summary>An elevated result carries no measured duration, and an invented "0.0s" would be a lie.</summary>
    [Fact]
    public void A_result_without_a_measured_duration_leaves_it_out()
    {
        Assert.Equal(
            "ok update winget:Mozilla.Firefox",
            ActivityLog.InstallSucceeded(Item(), InstallAction.Install, duration: null));
    }

    [Fact]
    public void A_failure_carries_the_reason_but_never_a_wall_of_it()
    {
        var line = ActivityLog.InstallFailed(
            Item(), InstallAction.Install, TimeSpan.FromSeconds(3), new string('x', 400));

        Assert.StartsWith("fail update winget:Mozilla.Firefox 3.0s ", line, StringComparison.Ordinal);
        Assert.True(line.Length < 200, $"a log line of {line.Length} characters is not a log line");
        Assert.EndsWith("…", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_multi_line_tool_error_still_becomes_one_line()
    {
        var line = ActivityLog.InstallFailed(
            Item(), InstallAction.Install, TimeSpan.Zero, "first line\r\nsecond line");

        Assert.DoesNotContain('\n', line);
        Assert.Contains("first line second line", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_download_reports_its_size_and_then_its_speed()
    {
        Assert.Equal(
            "start winget:Mozilla.Firefox 60.0 MB",
            ActivityLog.DownloadStarted("winget", "Mozilla.Firefox", 60 * 1024 * 1024));

        Assert.Equal(
            "done winget:Mozilla.Firefox 60.0 MB 10.0s 6.0 MB/s",
            ActivityLog.DownloadFinished("winget", "Mozilla.Firefox", 60 * 1024 * 1024, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void A_scan_and_a_cleanup_each_summarise_in_one_line()
    {
        Assert.Equal(
            "done 18 sources 42 updates 8.1s",
            ActivityLog.ScanFinished(18, 42, 0, TimeSpan.FromSeconds(8.14)));

        Assert.Equal(
            "done 18 sources 42 updates 2 failed 8.1s",
            ActivityLog.ScanFinished(18, 42, 2, TimeSpan.FromSeconds(8.14)));

        Assert.Equal(
            "done freed 1.0 GB skipped 143 1m05s",
            ActivityLog.CleanupFinished(1024L * 1024 * 1024, 143, stopped: false, TimeSpan.FromSeconds(65)));

        Assert.StartsWith(
            "stopped freed",
            ActivityLog.CleanupFinished(1024, 0, stopped: true, TimeSpan.FromSeconds(2)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Per-item lines never answered "how long did the whole run take", these two
    /// bracket a batch so that question is provable from the log instead of remembered.
    /// </summary>
    [Fact]
    public void A_batch_says_how_many_items_and_how_long_it_took_as_a_whole()
    {
        Assert.Equal("batch 3 items admin", ActivityLog.BatchStarted(3, elevated: true));
        Assert.Equal("batch 1 item", ActivityLog.BatchStarted(1, elevated: false));

        Assert.Equal("batch done 3 items 12.4s", ActivityLog.BatchFinished(3, TimeSpan.FromSeconds(12.42)));
        Assert.Equal("batch done 1 item 1m05s", ActivityLog.BatchFinished(1, TimeSpan.FromSeconds(65)));
    }

    /// <summary>The three startup/runtime timing lines, each a pure function of two fixed
    /// timestamps (or a byte count) so the wiring around the real process start needs no test of its own.</summary>
    [Fact]
    public void Item_156_startup_and_runtime_measurements_format_as_plain_millisecond_and_byte_figures()
    {
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Local);

        Assert.Equal(
            "first paint 842ms",
            ActivityLog.FirstPaint(start, start.AddMilliseconds(842)));

        Assert.Equal(
            "first cached row 315ms",
            ActivityLog.FirstCachedRow(start, start.AddMilliseconds(315)));

        Assert.Equal(
            "working set after scan 1 180.0 MB",
            ActivityLog.WorkingSetAfterScan(1, 180L * 1024 * 1024));

        Assert.Equal(
            "working set after scan 5 210.0 MB",
            ActivityLog.WorkingSetAfterScan(5, 210L * 1024 * 1024));
    }

    [Fact]
    public void A_self_update_names_the_version_at_each_checkpoint()
    {
        Assert.Equal("found 0.2.0", ActivityLog.SelfUpdateFound("0.2.0"));
        Assert.Equal(
            "downloaded Ultimate Updater.exe 60.0 MB",
            ActivityLog.SelfUpdateDownloaded("Ultimate Updater.exe", 60 * 1024 * 1024));
        Assert.Equal("verified 0.2.0", ActivityLog.SelfUpdateVerified("0.2.0"));
        Assert.Equal("applied 0.2.0 portable", ActivityLog.SelfUpdateApplied("0.2.0", portable: true));
        Assert.Equal("applied 0.2.0 installer", ActivityLog.SelfUpdateApplied("0.2.0", portable: false));
    }
}
