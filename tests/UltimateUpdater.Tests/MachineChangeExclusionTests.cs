using System.Text.Json;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// A later fix narrowed this: an elevated install and an elevated cleanup must never run at the same
/// time ONLY for the two cleanup kinds that can actually delete a temp file an install is unpacking
/// ("cleanup", the app's own file deletion, and "cleanup-diskcleanup", Windows Disk Cleanup), a DISM
/// repair or a drive defrag touch neither an install's staging area nor each other's, so they take no
/// cross-category slot at all and run fully alongside an install. The worker must decide this for itself
/// rather than trusting the job envelope's own "exclusive" field. Both halves (the derivation, and the lock
/// it feeds) are pulled out of <see cref="ElevatedWorker"/> as their own internal, directly testable
/// pieces, so this is provable with no pipe, no real install and no real cleanup target.
/// </summary>
public class MachineChangeExclusionTests
{
    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static ElevatedJob JobWith(string kind, bool wireExclusive, object? payload = null) => new()
    {
        Id = "test",
        Kind = kind,
        Exclusive = wireExclusive,
        Payload = JsonSerializer.SerializeToElement(payload ?? new { }, Json),
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsExclusiveJob_always_exclusive_for_install_regardless_of_the_wire_field(bool wireExclusive)
    {
        var job = JobWith("install", wireExclusive);

        Assert.True(ElevatedWorker.IsExclusiveJob(job));
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("cleanup-repair")]
    [InlineData("cleanup-diskcleanup")]
    [InlineData("cleanup-defrag")]
    [InlineData("cleanup-registry")]
    public void IsExclusiveJob_is_exclusive_for_a_clean_command_even_when_the_wire_field_says_otherwise(string kind)
    {
        var job = JobWith(kind, wireExclusive: false, payload: new { command = "clean" });

        Assert.True(ElevatedWorker.IsExclusiveJob(job));
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("cleanup-repair")]
    [InlineData("cleanup-diskcleanup")]
    [InlineData("cleanup-defrag")]
    [InlineData("cleanup-registry")]
    public void IsExclusiveJob_is_not_exclusive_for_a_scan_command_even_when_the_wire_field_says_otherwise(string kind)
    {
        var job = JobWith(kind, wireExclusive: true, payload: new { command = "scan" });

        Assert.False(ElevatedWorker.IsExclusiveJob(job));
    }

    /// <summary>DISM's own AnalyzeComponentStore never deletes anything, same as a scan, so a
    /// press must never queue silently behind an unrelated, already-running exclusive cleanup job.</summary>
    [Theory]
    [InlineData("cleanup")]
    [InlineData("cleanup-repair")]
    [InlineData("cleanup-diskcleanup")]
    [InlineData("cleanup-defrag")]
    [InlineData("cleanup-registry")]
    public void IsExclusiveJob_is_not_exclusive_for_an_analyze_command_even_when_the_wire_field_says_otherwise(string kind)
    {
        var job = JobWith(kind, wireExclusive: true, payload: new { command = "analyze" });

        Assert.False(ElevatedWorker.IsExclusiveJob(job));
    }

    /// <summary>
    /// The property the finding is about: the worker's own decision, not the message's claim. A message
    /// that lied about the field either way must make no difference to either kind.
    /// </summary>
    [Fact]
    public void IsExclusiveJob_never_reads_the_wire_field_at_all()
    {
        var install = JobWith("install", wireExclusive: false);
        var cleanupClean = JobWith("cleanup", wireExclusive: false, payload: new { command = "clean" });
        var cleanupScan = JobWith("cleanup", wireExclusive: true, payload: new { command = "scan" });

        Assert.True(ElevatedWorker.IsExclusiveJob(install));
        Assert.True(ElevatedWorker.IsExclusiveJob(cleanupClean));
        Assert.False(ElevatedWorker.IsExclusiveJob(cleanupScan));
    }

    /// <summary>
    /// The bug itself, reproduced directly on the lock: an install holding its slot must block a cleanup
    /// from acquiring its own, and releasing the install slot must free the cleanup up at once.
    /// </summary>
    [Fact]
    public async Task AcquireMachineChangeSlotAsync_blocks_a_cleanup_while_an_install_holds_the_slot()
    {
        var installSlot = await ElevatedWorker.AcquireMachineChangeSlotAsync("install", CancellationToken.None);

        var cleanupAcquire = ElevatedWorker.AcquireMachineChangeSlotAsync("cleanup", CancellationToken.None);

        // Still blocked: the cleanup attempt must not be the one that finishes first.
        var raced = await Task.WhenAny(cleanupAcquire, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(cleanupAcquire, raced);

        await installSlot.DisposeAsync();

        var cleanupSlot = await cleanupAcquire.WaitAsync(TimeSpan.FromSeconds(5));
        await cleanupSlot.DisposeAsync();
    }

    /// <summary>The other direction: a cleanup holding its reader slot blocks an install from starting.</summary>
    [Fact]
    public async Task AcquireMachineChangeSlotAsync_blocks_an_install_while_a_cleanup_holds_the_slot()
    {
        var cleanupSlot = await ElevatedWorker.AcquireMachineChangeSlotAsync("cleanup", CancellationToken.None);

        var installAcquire = ElevatedWorker.AcquireMachineChangeSlotAsync("install", CancellationToken.None);

        var raced = await Task.WhenAny(installAcquire, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(installAcquire, raced);

        await cleanupSlot.DisposeAsync();

        var installSlot = await installAcquire.WaitAsync(TimeSpan.FromSeconds(5));
        await installSlot.DisposeAsync();
    }

    /// <summary>
    /// The property this fix already relied on and must survive this change: the two CONTENDING cleanup
    /// kinds still run alongside each other, they only ever exclude install, never each other.
    /// </summary>
    [Fact]
    public async Task AcquireMachineChangeSlotAsync_lets_several_contending_cleanup_kinds_run_together()
    {
        var fileSlot = await ElevatedWorker.AcquireMachineChangeSlotAsync("cleanup", CancellationToken.None);
        var diskCleanupSlot = await ElevatedWorker.AcquireMachineChangeSlotAsync("cleanup-diskcleanup", CancellationToken.None)
            .WaitAsync(TimeSpan.FromMilliseconds(200));

        // An install must still be kept out while either of the two readers above is active.
        var installAcquire = ElevatedWorker.AcquireMachineChangeSlotAsync("install", CancellationToken.None);
        var raced = await Task.WhenAny(installAcquire, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(installAcquire, raced);

        await fileSlot.DisposeAsync();

        // Still blocked: one reader remains.
        var stillRaced = await Task.WhenAny(installAcquire, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(installAcquire, stillRaced);

        await diskCleanupSlot.DisposeAsync();

        var installSlot = await installAcquire.WaitAsync(TimeSpan.FromSeconds(5));
        await installSlot.DisposeAsync();
    }

    /// <summary>
    /// This fix's core fix: "cleanup-repair" and "cleanup-defrag" take no cross-category slot at all, so
    /// neither one blocks an install, nor is either one blocked by an install holding the writer lock.
    /// "cleanup-registry" joins them here: it used to fall through to the plain "cleanup" kind, which IS a
    /// contending kind, silently blocking installs behind a registry cleanup even though the product
    /// decision is that registry cleanup runs beside installs.
    /// </summary>
    [Theory]
    [InlineData("cleanup-repair")]
    [InlineData("cleanup-defrag")]
    [InlineData("cleanup-registry")]
    public async Task AcquireMachineChangeSlotAsync_a_non_contending_cleanup_kind_never_blocks_or_is_blocked_by_install(string kind)
    {
        var installSlot = await ElevatedWorker.AcquireMachineChangeSlotAsync("install", CancellationToken.None);

        // The non-contending kind must acquire at once even while install holds the writer lock outright.
        var nonContendingSlot = await ElevatedWorker.AcquireMachineChangeSlotAsync(kind, CancellationToken.None)
            .WaitAsync(TimeSpan.FromMilliseconds(200));
        await nonContendingSlot.DisposeAsync();
        await installSlot.DisposeAsync();

        // And the other direction: holding the non-contending kind's slot must never keep an install out.
        var heldSlot = await ElevatedWorker.AcquireMachineChangeSlotAsync(kind, CancellationToken.None);
        var freshInstall = await ElevatedWorker.AcquireMachineChangeSlotAsync("install", CancellationToken.None)
            .WaitAsync(TimeSpan.FromMilliseconds(200));
        await freshInstall.DisposeAsync();
        await heldSlot.DisposeAsync();
    }

    /// <summary>A cancelled wait must not leave a phantom reader or writer behind for the next job.</summary>
    [Fact]
    public async Task AcquireMachineChangeSlotAsync_cancelling_a_blocked_wait_leaves_no_phantom_holder()
    {
        var installSlot = await ElevatedWorker.AcquireMachineChangeSlotAsync("install", CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var cleanupAcquire = ElevatedWorker.AcquireMachineChangeSlotAsync("cleanup", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cleanupAcquire);

        await installSlot.DisposeAsync();

        // A fresh install must be free to acquire the slot at once: the cancelled cleanup attempt above
        // must not have left the reader counter, or the writer lock, in a stuck state.
        var nextInstall = await ElevatedWorker.AcquireMachineChangeSlotAsync("install", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await nextInstall.DisposeAsync();
    }
}

/// <summary>
/// The ONE extra exclusion on top of this fix's own pairing above - a "cleanup-repair" job
/// (DISM/sfc) and an elevated install job that itself contains a Windows Update item wait for each other,
/// because both drive the servicing stack and can fail busy running together; every other pairing
/// (repair beside a non-Windows-Update install, repair beside any cleanup kind) takes no slot here at all
/// and runs fully alongside the other, exactly as this fix left it. Pulled out of
/// <see cref="ElevatedWorker"/> the same way this fix's own lock is, so it is provable with no pipe and no
/// real install or repair.
/// </summary>
public class RepairVsWindowsUpdateExclusionTests
{
    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static ElevatedJob InstallJobWith(params string[] sourceIds) => new()
    {
        Id = "test",
        Kind = "install",
        Payload = JsonSerializer.SerializeToElement(new
        {
            items = sourceIds.Select(id => new { sourceId = id, itemId = "x", name = "x" }).ToArray(),
        }, Json),
    };

    private static ElevatedJob RepairJob() => new()
    {
        Id = "test",
        Kind = "cleanup-repair",
        Payload = JsonSerializer.SerializeToElement(new { command = "clean" }, Json),
    };

    [Fact]
    public async Task A_repair_job_blocks_an_install_that_contains_a_windows_update_item()
    {
        var repairSlot = await ElevatedWorker.AcquireRepairVsWindowsUpdateSlotAsync(RepairJob(), CancellationToken.None);

        var installAcquire = ElevatedWorker.AcquireRepairVsWindowsUpdateSlotAsync(
            InstallJobWith("windowsupdate"), CancellationToken.None);

        await Task.Delay(50);
        Assert.False(installAcquire.IsCompleted);

        await repairSlot.DisposeAsync();
        var installSlot = await installAcquire.WaitAsync(TimeSpan.FromSeconds(5));
        await installSlot.DisposeAsync();
    }

    [Fact]
    public async Task An_install_containing_a_windows_update_item_blocks_a_repair_job_the_other_way_too()
    {
        var installSlot = await ElevatedWorker.AcquireRepairVsWindowsUpdateSlotAsync(
            InstallJobWith("windowsupdate"), CancellationToken.None);

        var repairAcquire = ElevatedWorker.AcquireRepairVsWindowsUpdateSlotAsync(RepairJob(), CancellationToken.None);

        await Task.Delay(50);
        Assert.False(repairAcquire.IsCompleted);

        await installSlot.DisposeAsync();
        var repairSlot = await repairAcquire.WaitAsync(TimeSpan.FromSeconds(5));
        await repairSlot.DisposeAsync();
    }

    /// <summary>The narrow pairing, not a general install/repair block: an install with no Windows Update
    /// item must never wait on a repair job, matching this fix's own 18.09. measurement (a winget install
    /// once waited 15 minutes behind a repair that could never have collided with it).</summary>
    [Fact]
    public async Task A_repair_job_never_blocks_an_install_with_no_windows_update_item()
    {
        var repairSlot = await ElevatedWorker.AcquireRepairVsWindowsUpdateSlotAsync(RepairJob(), CancellationToken.None);

        var installSlot = await ElevatedWorker.AcquireRepairVsWindowsUpdateSlotAsync(
            InstallJobWith("winget"), CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(500));

        await installSlot.DisposeAsync();
        await repairSlot.DisposeAsync();
    }

    /// <summary>A Windows Update item mixed in with others in the same batch still counts as "contains
    /// one" - the exclusion is about the whole job, not a single-item batch.</summary>
    [Fact]
    public async Task A_repair_job_blocks_a_mixed_batch_that_includes_a_windows_update_item()
    {
        var repairSlot = await ElevatedWorker.AcquireRepairVsWindowsUpdateSlotAsync(RepairJob(), CancellationToken.None);

        var installAcquire = ElevatedWorker.AcquireRepairVsWindowsUpdateSlotAsync(
            InstallJobWith("winget", "windowsupdate", "chocolatey"), CancellationToken.None);

        await Task.Delay(50);
        Assert.False(installAcquire.IsCompleted);

        await repairSlot.DisposeAsync();
        var installSlot = await installAcquire.WaitAsync(TimeSpan.FromSeconds(5));
        await installSlot.DisposeAsync();
    }

    /// <summary>Every other cleanup kind (plain file cleanup, Disk Cleanup, defrag) takes no slot here at
    /// all - this exclusion is repair-specific, not "any cleanup vs Windows Update".</summary>
    [Theory]
    [InlineData("cleanup")]
    [InlineData("cleanup-diskcleanup")]
    [InlineData("cleanup-defrag")]
    [InlineData("cleanup-registry")]
    public async Task A_non_repair_cleanup_kind_never_blocks_an_install_with_a_windows_update_item(string kind)
    {
        var otherCleanupSlot = await ElevatedWorker.AcquireRepairVsWindowsUpdateSlotAsync(
            new ElevatedJob { Id = "test", Kind = kind, Payload = JsonSerializer.SerializeToElement(new { command = "clean" }, Json) },
            CancellationToken.None);

        var installSlot = await ElevatedWorker.AcquireRepairVsWindowsUpdateSlotAsync(
            InstallJobWith("windowsupdate"), CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(500));

        await installSlot.DisposeAsync();
        await otherCleanupSlot.DisposeAsync();
    }

    /// <summary>
    /// A Windows Update install queued behind a running repair must not hold the install/cleanup writer
    /// slot while it waits: a file cleanup started meanwhile would otherwise sit behind the whole repair
    /// plus the install. The servicing lock is taken first, so the writer slot stays free.
    /// </summary>
    [Fact]
    public async Task A_windows_update_install_waiting_for_a_repair_does_not_block_a_file_cleanup()
    {
        var repairSlot = await ElevatedWorker.AcquireRepairVsWindowsUpdateSlotAsync(RepairJob(), CancellationToken.None);
        Task<IAsyncDisposable>? installSlots = null;
        try
        {
            installSlots = ElevatedWorker.AcquireJobSlotsAsync(InstallJobWith("windowsupdate"), () => { }, CancellationToken.None);
            await Task.Delay(100);
            Assert.False(installSlots.IsCompleted);

            var cleanupSlot = await ElevatedWorker.AcquireMachineChangeSlotAsync("cleanup", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3));
            await cleanupSlot.DisposeAsync();
        }
        finally
        {
            await repairSlot.DisposeAsync();
            if (installSlots is not null)
                await (await installSlots.WaitAsync(TimeSpan.FromSeconds(5))).DisposeAsync();
        }
    }

    /// <summary>The queued install tells the app why its rows sit at 0 %.</summary>
    [Fact]
    public async Task A_windows_update_install_waiting_for_a_repair_says_so()
    {
        var repairSlot = await ElevatedWorker.AcquireRepairVsWindowsUpdateSlotAsync(RepairJob(), CancellationToken.None);
        var notices = 0;
        var installSlots = ElevatedWorker.AcquireJobSlotsAsync(
            InstallJobWith("windowsupdate"), () => Interlocked.Increment(ref notices), CancellationToken.None);
        try
        {
            await Task.Delay(100);
            Assert.Equal(1, Volatile.Read(ref notices));
        }
        finally
        {
            await repairSlot.DisposeAsync();
            await (await installSlots.WaitAsync(TimeSpan.FromSeconds(5))).DisposeAsync();
        }

        Assert.Equal(1, notices);
    }
}

/// <summary>The worker's log-flush timer must be fully stopped before a job's final drain.</summary>
public class LogFlushTimerTests
{
    /// <summary>Contrast test for a structural fix: a callback that is still running when the timer is
    /// stopped has finished by the time the stop returns, so it cannot write after the final drain.</summary>
    [Fact]
    public void Stopping_the_flush_timer_waits_for_a_running_callback()
    {
        var started = new ManualResetEventSlim(false);
        var finished = 0;
        var timer = new System.Threading.Timer(_ =>
        {
            started.Set();
            Thread.Sleep(400);
            Volatile.Write(ref finished, 1);
        }, null, 0, Timeout.Infinite);

        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        ElevatedWorker.StopLogFlushTimer(timer);

        Assert.Equal(1, Volatile.Read(ref finished));
    }

    /// <summary>A callback that outlives the bounded wait signals the stop handle when it finally ends.
    /// That handle must still be alive then: signalling a disposed one throws on a thread-pool thread and
    /// ends the whole elevated worker mid-job.</summary>
    [Fact]
    public void A_callback_outliving_the_wait_does_not_crash_the_process_when_it_ends()
    {
        var started = new ManualResetEventSlim(false);
        var finished = new ManualResetEventSlim(false);
        var timer = new System.Threading.Timer(_ =>
        {
            started.Set();
            Thread.Sleep(ElevatedWorker.LogFlushStopWait + TimeSpan.FromMilliseconds(600));
            finished.Set();
        }, null, 0, Timeout.Infinite);

        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        ElevatedWorker.StopLogFlushTimer(timer);

        Assert.True(finished.Wait(TimeSpan.FromSeconds(10)));
        // The timer signals the stop handle right after the callback returns; give it that moment.
        Thread.Sleep(300);
    }
}

/// <summary>The elevated worker's bounded line reader reads in blocks but must hand out lines exactly as
/// the one character reader did.</summary>
public class BoundedLineReaderTests
{
    private static ElevatedWorker.BoundedLineReader ReaderOver(string text) =>
        new(new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text))));

    [Fact]
    public async Task Several_lines_in_one_block_come_out_one_by_one_with_the_line_ending_removed()
    {
        var reader = ReaderOver("first\r\nsecond\nthird");

        Assert.Equal("first", await reader.ReadLineAsync());
        Assert.Equal("second", await reader.ReadLineAsync());
        Assert.Equal("third", await reader.ReadLineAsync());
        Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task A_line_longer_than_one_block_is_returned_whole()
    {
        var longLine = new string('x', 10_000);
        var reader = ReaderOver(longLine + "\nnext\n");

        Assert.Equal(longLine, await reader.ReadLineAsync());
        Assert.Equal("next", await reader.ReadLineAsync());
    }

    [Fact]
    public async Task A_line_past_the_ceiling_ends_the_reading()
    {
        var reader = ReaderOver(new string('x', 2 * 1024 * 1024 + 1) + "\n");

        Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task A_line_exactly_at_the_ceiling_is_still_accepted()
    {
        var line = new string('x', 2 * 1024 * 1024);
        var reader = ReaderOver(line + "\n");

        Assert.Equal(line, await reader.ReadLineAsync());
    }
}
