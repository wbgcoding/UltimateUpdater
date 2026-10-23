using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// The interval timer skips an automatic scan while the connection is metered. The probe is
/// injected, never <see cref="MeteredConnection.IsMetered"/> itself, so these never depend on whatever
/// connection genuinely happens to be active on the machine that runs them. Only the scheduled TICK is
/// gated here; a scan the user starts by hand goes through a completely separate command that never
/// touches <see cref="ScanScheduler"/>, let alone its metered check, so it is not something this class
/// could accidentally skip.
/// </summary>
public class ScanSchedulerTests
{
    private static SettingsService NewSettings()
    {
        var logging = new LoggingService();
        return new SettingsService(logging, Path.Combine(
            Directory.CreateTempSubdirectory("wu-scanscheduler-test-").FullName, "settings.json"));
    }

    [Fact]
    public async Task A_due_scan_is_skipped_while_the_connection_is_metered()
    {
        var settings = NewSettings();
        var ran = false;
        var scheduler = new ScanScheduler(settings, new LoggingService(), () => { ran = true; return Task.CompletedTask; },
            isMetered: () => true);

        await scheduler.TickAsync();

        Assert.False(ran);
    }

    [Fact]
    public async Task A_due_scan_runs_when_the_connection_is_not_metered()
    {
        var settings = NewSettings();
        var ran = false;
        var scheduler = new ScanScheduler(settings, new LoggingService(), () => { ran = true; return Task.CompletedTask; },
            isMetered: () => false);

        await scheduler.TickAsync();

        Assert.True(ran);
    }

    /// <summary>The toggle this test guards (<see cref="Models.Settings.ScanSettings.RespectMetered"/>)
    /// defaults on; turning it off lets the automatic scan run on a metered connection anyway.</summary>
    [Fact]
    public async Task Turning_off_RespectMetered_runs_the_scan_even_while_metered()
    {
        var settings = NewSettings();
        settings.Current.Scan.RespectMetered = false;
        var ran = false;
        var scheduler = new ScanScheduler(settings, new LoggingService(), () => { ran = true; return Task.CompletedTask; },
            isMetered: () => true);

        await scheduler.TickAsync();

        Assert.True(ran);
    }

    /// <summary>The tick comes every minute and the scan stays due the whole time the connection is metered, so
    /// the skip must be said once, not once a minute.</summary>
    [Fact]
    public async Task Skipping_on_a_metered_connection_is_logged_once_not_on_every_tick()
    {
        var lines = new List<string>();
        var logging = new LoggingService(line => { lock (lines) lines.Add(line); });
        var scheduler = new ScanScheduler(NewSettings(), logging, () => Task.CompletedTask, isMetered: () => true);

        await scheduler.TickAsync();
        await scheduler.TickAsync();
        await scheduler.TickAsync();

        // The log folds identical neighbours into a counter line once anything else is written; that
        // counter is the proof the skip was reported more than once.
        logging.Info("something else happened");
        logging.Flush();

        lock (lines)
        {
            Assert.Single(lines, line => line.Contains("metered", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(lines, line => line.Contains("same as above", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Once the connection is unmetered the scan runs at once (the due time was never moved), and a
    /// later metered stretch is logged again.</summary>
    [Fact]
    public async Task The_skip_is_logged_again_after_a_scan_ran_in_between()
    {
        var lines = new List<string>();
        var logging = new LoggingService(line => { lock (lines) lines.Add(line); });
        var metered = true;
        var ran = 0;
        var scheduler = new ScanScheduler(NewSettings(), logging, () => { ran++; return Task.CompletedTask; },
            isMetered: () => metered);

        await scheduler.TickAsync();
        metered = false;
        await scheduler.TickAsync();
        Assert.Equal(1, ran);

        // _lastRun moved on with the scan, so make the next one due again before the metered stretch.
        scheduler.MakeDueForTest();
        metered = true;
        await scheduler.TickAsync();
        logging.Flush();

        lock (lines)
            Assert.Equal(2, lines.Count(line => line.Contains("metered", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A due scan that stepped aside (an install was still running) did not scan, so the
    /// interval is not restarted: the next tick tries again instead of a whole interval later.</summary>
    [Fact]
    public async Task A_scan_that_stepped_aside_stays_due_for_the_next_tick()
    {
        var calls = 0;
        var scanRuns = false;
        var scheduler = new ScanScheduler(NewSettings(), new LoggingService(), () =>
        {
            calls++;
            return Task.FromResult(scanRuns);
        }, isMetered: () => false);

        await scheduler.TickAsync();
        Assert.Equal(1, calls);

        scanRuns = true;
        await scheduler.TickAsync();
        Assert.Equal(2, calls);

        // This one did run, so the interval restarts and the next tick has nothing due.
        await scheduler.TickAsync();
        Assert.Equal(2, calls);
    }
}
