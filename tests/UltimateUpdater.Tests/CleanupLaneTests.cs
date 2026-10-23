using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The cleanup dialog runs its file, repair and Disk Cleanup targets in three lanes that overlap
/// in time instead of one strict catalogue-order queue, and the elevated worker had to stop collapsing them
/// back onto a single "cleanup" lane underneath.
/// </summary>
public class CleanupLaneTests
{
    /// <summary>
    /// Proves the lanes genuinely overlap rather than running one after another: lane B only signals once
    /// it has observed lane A already under way, and only THEN lets lane A finish. If the lanes ran one at
    /// a time instead, lane A would sit on its own gate forever waiting for a lane B that never even
    /// starts, so a stuck implementation shows up as a timeout, not a silently wrong pass.
    /// </summary>
    [Fact]
    public async Task RunLanesConcurrentlyAsync_runs_every_lane_at_the_same_time()
    {
        var laneAEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laneBEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLaneA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task LaneA()
        {
            laneAEntered.SetResult();
            await releaseLaneA.Task;
        }

        async Task LaneB()
        {
            // Proves A is already running, not merely about to start once B is out of the way.
            await laneAEntered.Task;
            laneBEntered.SetResult();
            releaseLaneA.SetResult();
        }

        var run = CleanupViewModel.RunLanesConcurrentlyAsync([LaneA, LaneB]);

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(run, finished);
        await run;
        Assert.True(laneAEntered.Task.IsCompletedSuccessfully);
        Assert.True(laneBEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RunLanesConcurrentlyAsync_waits_for_every_lane_even_when_one_finishes_first()
    {
        var fast = false;
        var slow = false;

        Task Fast()
        {
            fast = true;
            return Task.CompletedTask;
        }

        async Task Slow()
        {
            await Task.Delay(20);
            slow = true;
        }

        await CleanupViewModel.RunLanesConcurrentlyAsync([Fast, Slow]);

        Assert.True(fast);
        Assert.True(slow);
    }

    /// <summary>
    /// Before this fix, `RegistryCleanup`, `AppCacheSweep` and `LeftoverInstalls` were
    /// selectable in the dialog but matched none of the three lane filters `RunCleanupAsync` built at the
    /// time - they landed in no lane at all and always ended "Skipped", even though a registry-only
    /// selection still went through the motion of creating a restore point for nothing. This theory pins
    /// every real `CleanupKind` to a real lane, none of them "nowhere", and `CleanupViewModel.LaneFor`'s own
    /// switch has no default arm, so a future kind missing from this list fails the BUILD rather than
    /// silently repeating the bug.
    /// </summary>
    [Theory]
    [InlineData(CleanupKind.Directories, CleanupViewModel.CleanupLane.File)]
    [InlineData(CleanupKind.RecycleBin, CleanupViewModel.CleanupLane.File)]
    [InlineData(CleanupKind.AppCacheSweep, CleanupViewModel.CleanupLane.File)]
    [InlineData(CleanupKind.LeftoverInstalls, CleanupViewModel.CleanupLane.File)]
    [InlineData(CleanupKind.RegistryCleanup, CleanupViewModel.CleanupLane.Registry)]
    [InlineData(CleanupKind.SystemRepair, CleanupViewModel.CleanupLane.Repair)]
    [InlineData(CleanupKind.DiskCleanup, CleanupViewModel.CleanupLane.DiskCleanup)]
    [InlineData(CleanupKind.Defragmentation, CleanupViewModel.CleanupLane.Defrag)]
    [InlineData(CleanupKind.DriverCleanup, CleanupViewModel.CleanupLane.Registry)]
    [InlineData(CleanupKind.PipInstallLeftovers, CleanupViewModel.CleanupLane.File)]
    [InlineData(CleanupKind.OldPythonVersions, CleanupViewModel.CleanupLane.Registry)]
    public void LaneFor_assigns_every_real_kind_to_a_real_lane_none_of_them_nowhere(CleanupKind kind, CleanupViewModel.CleanupLane expectedLane)
    {
        Assert.Equal(expectedLane, CleanupViewModel.LaneFor(kind));
    }

    [Theory]
    [InlineData(CleanupKind.SystemRepair, "cleanup-repair")]
    [InlineData(CleanupKind.DiskCleanup, "cleanup-diskcleanup")]
    [InlineData(CleanupKind.Defragmentation, "cleanup-defrag")]
    [InlineData(CleanupKind.RegistryCleanup, "cleanup-registry")]
    [InlineData(CleanupKind.Directories, "cleanup")]
    [InlineData(CleanupKind.RecycleBin, "cleanup")]
    [InlineData(CleanupKind.AppCacheSweep, "cleanup")]
    [InlineData(CleanupKind.LeftoverInstalls, "cleanup")]
    [InlineData(CleanupKind.DriverCleanup, "cleanup-registry")]
    [InlineData(CleanupKind.PipInstallLeftovers, "cleanup")]
    [InlineData(CleanupKind.OldPythonVersions, "cleanup-registry")]
    public void ElevatedJobKindFor_gives_repair_disk_cleanup_defrag_and_registry_their_own_lane(CleanupKind kind, string expectedJobKind)
    {
        Assert.Equal(expectedJobKind, CleanupViewModel.ElevatedJobKindFor(kind));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("cleanup")]
    [InlineData("cleanup-repair")]
    [InlineData("cleanup-diskcleanup")]
    [InlineData("cleanup-defrag")]
    [InlineData("cleanup-registry")]
    [InlineData("control")]
    public void ElevatedWorker_recognizes_every_real_job_kind(string kind)
    {
        Assert.True(ElevatedWorker.IsRecognizedKind(kind));
    }

    [Theory]
    [InlineData("")]
    [InlineData("cleanup-repair-x")]
    [InlineData("Cleanup")]
    [InlineData("uninstall")]
    public void ElevatedWorker_refuses_an_unknown_job_kind(string kind)
    {
        Assert.False(ElevatedWorker.IsRecognizedKind(kind));
    }
}

/// <summary>
/// The restore point for a cleanup run is now created once, in parallel with the repair,
/// Disk Cleanup and defrag lanes, instead of ahead of them; only the file lane, the one lane that
/// removes anything, waits on it. <see cref="CleanupViewModel.WaitForRestorePointAsync"/> carries that
/// wait alone, with no real elevated worker, so it is provable on its own.
/// </summary>
public class WaitForRestorePointAsyncTests
{
    [Fact]
    public async Task Does_nothing_and_shows_no_text_when_there_is_no_task_at_all()
    {
        var shown = false;

        await CleanupViewModel.WaitForRestorePointAsync(null, () => shown = true, CancellationToken.None);

        Assert.False(shown);
    }

    [Fact]
    public async Task Does_nothing_and_shows_no_text_when_the_task_is_already_finished()
    {
        var shown = false;
        var alreadyDone = Task.FromResult(true);

        await CleanupViewModel.WaitForRestorePointAsync(alreadyDone, () => shown = true, CancellationToken.None);

        Assert.False(shown);
    }

    /// <summary>
    /// The one case a run actually reaches while a restore point is still being created: the callback
    /// fires before the wait completes, so a caller can put the waiting text on screen while there is
    /// still something to wait for, and the returned task only completes once the restore point does.
    /// </summary>
    [Fact]
    public async Task Shows_the_waiting_text_then_waits_for_a_still_pending_task()
    {
        var shown = false;
        var restorePoint = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var wait = CleanupViewModel.WaitForRestorePointAsync(
            restorePoint.Task, () => shown = true, CancellationToken.None);

        // The callback runs synchronously before the first await inside WaitForRestorePointAsync, so it
        // is already true here even though the underlying task has not resolved yet.
        Assert.True(shown);
        Assert.False(wait.IsCompleted);

        restorePoint.SetResult(true);

        var finished = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(wait, finished);
        await wait;
    }

    [Fact]
    public async Task A_cancellation_token_stops_the_wait_without_needing_the_task_to_ever_finish()
    {
        var restorePoint = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var wait = CleanupViewModel.WaitForRestorePointAsync(restorePoint.Task, () => { }, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}

/// <summary>
/// The cleanup half of the restore point throttle: one shared timestamp file throttles a NEW restore
/// point to at most once every 24 hours, regardless of whether an install or a cleanup run asked for it.
/// </summary>
[Collection("UiLanguage")]
public class RestorePointThrottleTests
{
    [Theory]
    [InlineData(0.5, false)]
    [InlineData(23.9, false)]
    [InlineData(24.0, true)]
    [InlineData(48, true)]
    public void ShouldCreate_skips_within_24_hours_and_creates_past_it(double hoursAgo, bool expectedCreate)
    {
        var now = DateTimeOffset.UtcNow;
        var lastCreated = now - TimeSpan.FromHours(hoursAgo);

        Assert.Equal(expectedCreate, RestorePointThrottle.ShouldCreate(lastCreated, now));
    }

    [Fact]
    public void ShouldCreate_creates_when_nothing_was_ever_recorded()
    {
        Assert.True(RestorePointThrottle.ShouldCreate(null, DateTimeOffset.UtcNow));
    }

    /// <summary>A stored timestamp in the FUTURE (a corrupted file, a clock that jumped
    /// backwards, or a same-user edit of the app's own state file) must read the same as no timestamp at
    /// all. Before the fix, <c>now - lastCreated</c> went negative and never crossed the 24-hour threshold,
    /// so this throttled every restore point until that future date actually arrived.</summary>
    [Fact]
    public void ShouldCreate_treats_a_future_timestamp_as_no_timestamp()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(RestorePointThrottle.ShouldCreate(now + TimeSpan.FromMinutes(1), now));
        Assert.True(RestorePointThrottle.ShouldCreate(now + TimeSpan.FromDays(365), now));
    }

    /// <summary>Same known relative path the class doc names ("restore-point.json" under
    /// AppPaths.DataDirectory); deleted first so an earlier test's write cannot leak into this one.</summary>
    private static string TimestampPath => Path.Combine(AppPaths.DataDirectory, "restore-point.json");

    private static void DeleteTimestampFile()
    {
        if (File.Exists(TimestampPath))
            File.Delete(TimestampPath);
    }

    /// <summary>This fix's read-back: TryWriteCreated then TryReadLastCreated round-trips the exact moment,
    /// the same pair InstallQueue and CleanupWorker already write through, now read from the unelevated
    /// app side for the pre-flight dialog and the cleanup window header.</summary>
    [Fact]
    public void TryWriteCreated_then_TryReadLastCreated_round_trips_the_timestamp()
    {
        var logging = new LoggingService();
        DeleteTimestampFile();

        var timestamp = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        RestorePointThrottle.TryWriteCreated(logging, timestamp);

        Assert.Equal(timestamp, RestorePointThrottle.TryReadLastCreated(logging));
    }

    /// <summary>No file at all reads as "never created by this program", never as an error and never as
    /// a guess at what Windows itself might separately have.</summary>
    [Fact]
    public void TryReadLastCreated_is_null_when_nothing_was_ever_written()
    {
        var logging = new LoggingService();
        DeleteTimestampFile();

        Assert.Null(RestorePointThrottle.TryReadLastCreated(logging));
    }

    /// <summary>This fix's formatting: the "none yet" line with nothing on record, then the real line with
    /// the written timestamp folded in once something is.</summary>
    [Fact]
    public void DescribeLastCreated_falls_back_when_empty_and_formats_the_timestamp_once_written()
    {
        LocalizationService.Instance.Apply("en");
        var logging = new LoggingService();
        DeleteTimestampFile();

        Assert.Equal(LocalizationService.Instance["RestorePointNone"], RestorePointThrottle.DescribeLastCreated(logging));

        var timestamp = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        RestorePointThrottle.TryWriteCreated(logging, timestamp);

        var described = RestorePointThrottle.DescribeLastCreated(logging);
        Assert.Contains(timestamp.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentUICulture), described);
        Assert.StartsWith("Last restore point:", described);
    }
}

/// <summary>
/// The finding this theory pins down: a run whose selection is repair, Disk Cleanup or defrag only never
/// passes the restore-point task into any of those three lanes (none of them delete anything), so before
/// this fix the task ran completely unobserved once dispatched, and a repair-only run could report Finished
/// while a restore point was still being created. <see cref="CleanupViewModel.BuildRunLanes"/> always
/// appends the task as its own lane, with no real elevated worker or cleanup target needed to prove it.
/// </summary>
public class BuildRunLanesTests
{
    private static Task NoOp() => Task.CompletedTask;

    [Fact]
    public async Task Waits_for_the_restore_point_task_even_when_every_other_lane_is_a_no_op()
    {
        var restorePoint = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var lanes = CleanupViewModel.BuildRunLanes(NoOp, NoOp, NoOp, NoOp, NoOp, restorePoint.Task);
        var run = CleanupViewModel.RunLanesConcurrentlyAsync(lanes);

        // The old lane array never included the restore-point task, so a run with no file/registry
        // target would already have finished here even with the task still pending.
        await Task.Delay(50);
        Assert.False(run.IsCompleted);

        restorePoint.SetResult(true);

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(run, finished);
        await run;
    }

    /// <summary>The other half of "observe its failure": a restore-point task that faults must surface
    /// through the run, not be silently dropped because nothing was awaiting it.</summary>
    [Fact]
    public async Task Propagates_a_restore_point_failure_even_when_every_other_lane_is_a_no_op()
    {
        var restorePoint = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var lanes = CleanupViewModel.BuildRunLanes(NoOp, NoOp, NoOp, NoOp, NoOp, restorePoint.Task);
        var run = CleanupViewModel.RunLanesConcurrentlyAsync(lanes);

        restorePoint.SetException(new InvalidOperationException("simulated restore-point failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => run);
    }
}

/// <summary>
/// The finding this proves: <c>_scanCts.Cancel()</c> alone is only a request, the previous scan's own
/// loop can still be mid-await, one iteration from writing a fresh Bytes/Files figure into an item right
/// as a run resets the same row for cleaning. <see cref="CleanupViewModel.AwaitScanUnwindAsync"/> is the
/// piece that closes the gap: it is directly testable with a bare fake task, no real scan or
/// <c>CleanupService</c> required.
/// </summary>
public class AwaitScanUnwindAsyncTests
{
    [Fact]
    public async Task Returns_at_once_when_there_is_no_scan_task()
    {
        await CleanupViewModel.AwaitScanUnwindAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Returns_at_once_when_the_scan_already_finished()
    {
        await CleanupViewModel.AwaitScanUnwindAsync(Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>The race itself: a run must not proceed while the previous scan is still mid-flight and
    /// could still write into the very rows the run is about to reset.</summary>
    [Fact]
    public async Task Waits_for_a_still_running_scan_to_actually_finish_before_returning()
    {
        var scanWroteIntoItem = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Scan()
        {
            await release.Task;
            scanWroteIntoItem = true;
        }

        var wait = CleanupViewModel.AwaitScanUnwindAsync(Scan());

        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        Assert.False(scanWroteIntoItem);

        release.SetResult();

        var finished = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(wait, finished);
        await wait;
        Assert.True(scanWroteIntoItem);
    }

    /// <summary>A scan that actually observed its own cancellation must not fault the run that cancelled
    /// it; that is exactly the scan RunCleanupAsync just cancelled above this call.</summary>
    [Fact]
    public async Task Swallows_the_scans_own_cancellation_rather_than_faulting_the_run()
    {
        using var cts = new CancellationTokenSource();
        var cancelledScan = Task.Delay(Timeout.Infinite, cts.Token);
        cts.Cancel();

        await CleanupViewModel.AwaitScanUnwindAsync(cancelledScan).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>A scan that fails for a real reason (not cancellation) must still surface, never be
    /// swallowed alongside the expected cancellation case.</summary>
    [Fact]
    public async Task Propagates_a_genuine_scan_failure()
    {
        var failed = Task.FromException(new IOException("simulated scan failure"));

        await Assert.ThrowsAsync<IOException>(
            () => CleanupViewModel.AwaitScanUnwindAsync(failed));
    }
}
