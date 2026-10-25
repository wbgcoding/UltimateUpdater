using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// The one real, live-run test for the leftover scanner's deleter (Phase 9.3, 2026-08-01), same
/// "impure edge, exercised through a live run rather than a unit test" role as <c>CleanupService</c>'s
/// own recycle-bin methods, except this one CAN run safely: it only ever touches a scratch folder it
/// creates itself under Temp, moved to the recycle bin (never `Directory.Delete`) so nothing is lost even
/// if the assertions below are wrong.
/// </summary>
public class RecycleBinDeleterTests
{
    [Fact]
    public void A_real_folder_is_actually_moved_to_the_recycle_bin_not_permanently_deleted()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"wu-recyclebin-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "marker.txt"), "leftover scanner test");

        try
        {
            var logging = new LoggingService();

            var deleted = RecycleBinDeleter.DeleteToRecycleBin(folder, logging);

            Assert.True(deleted);
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            // Best-effort: if the assertion above already failed and the folder is still there, clean it up.
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_path_that_does_not_exist_is_never_reported_as_deleted()
    {
        var logging = new LoggingService();
        var missing = Path.Combine(Path.GetTempPath(), $"wu-recyclebin-missing-{Guid.NewGuid():N}");

        Assert.False(RecycleBinDeleter.DeleteToRecycleBin(missing, logging));
    }
}

/// <summary>The result mapping of the recycle-bin delete. The "too large for the bin" case itself cannot be
/// forced in a test, so the decision that keeps it from reading as success is tested on its own.</summary>
public class RecycleBinDeleterResultTests
{
    [Fact]
    public void A_clean_success_is_the_only_success() =>
        Assert.True(RecycleBinDeleter.Succeeded(0, anyOperationsAborted: false, itemStillExists: false));

    [Theory]
    [InlineData(unchecked((int)0x80004005), false, false)]
    [InlineData(0, true, false)]
    [InlineData(0, false, true)]
    [InlineData(unchecked((int)0x80270000), true, true)]
    public void Any_failure_is_a_refusal_never_a_success(int hresult, bool aborted, bool stillThere) =>
        Assert.False(RecycleBinDeleter.Succeeded(hresult, aborted, stillThere));
}

/// <summary>The progress sink is what makes "recycle" a guarantee: the shell asks it before every delete and
/// it refuses unless the shell says this delete goes to the bin.</summary>
public class RecycleBinDeleterSinkTests
{
    [Theory]
    [InlineData(0x80u, true)]
    [InlineData(0x81u, true)]
    [InlineData(0x00u, false)]
    [InlineData(0x01u, false)]
    [InlineData(0x7Fu, false)]
    public void A_delete_is_allowed_only_when_the_shell_will_recycle_it(uint flags, bool expected) =>
        Assert.Equal(expected, RecycleBinDeleter.AllowDelete(flags));

    [Fact]
    public void A_real_delete_is_offered_to_the_sink_as_a_recycle()
    {
        var file = Path.Combine(Path.GetTempPath(), $"wu-recyclebin-sink-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "sink test");

        try
        {
            RecycleBinDeleter.LastPreDeleteFlagsForTest = null;

            Assert.True(RecycleBinDeleter.DeleteToRecycleBin(file, new LoggingService()));

            Assert.NotNull(RecycleBinDeleter.LastPreDeleteFlagsForTest);
            Assert.True(RecycleBinDeleter.AllowDelete(RecycleBinDeleter.LastPreDeleteFlagsForTest!.Value));
            Assert.False(File.Exists(file));
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    /// <summary>A shell call that never returns must not hold the caller for good: the move is reported as
    /// failed within the limit and the item stays where it was.</summary>
    [Fact]
    public void A_shell_call_that_never_returns_is_abandoned_after_the_time_limit()
    {
        var file = Path.Combine(Path.GetTempPath(), $"wu-recyclebin-hang-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "hang test");
        using var release = new ManualResetEventSlim();

        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var deleted = RecycleBinDeleter.DeleteToRecycleBin(
                file, new LoggingService(), (_, _, _) => { release.Wait(); return (0, false); },
                TimeSpan.FromMilliseconds(300), CancellationToken.None);
            watch.Stop();

            Assert.False(deleted);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"waited {watch.Elapsed}");
            Assert.True(File.Exists(file));
        }
        finally
        {
            release.Set();
            File.Delete(file);
        }
    }

    [Fact]
    public void Cancelling_ends_the_wait_for_a_shell_call_that_never_returns()
    {
        var file = Path.Combine(Path.GetTempPath(), $"wu-recyclebin-cancel-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "cancel test");
        using var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var deleted = RecycleBinDeleter.DeleteToRecycleBin(
                file, new LoggingService(), (_, _, _) => { release.Wait(); return (0, false); },
                TimeSpan.FromMinutes(5), cts.Token);
            watch.Stop();

            Assert.False(deleted);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"waited {watch.Elapsed}");
        }
        finally
        {
            release.Set();
            File.Delete(file);
        }
    }

    /// <summary>An abandoned shell call that finally wakes up must not recycle the item after the caller
    /// already counted it as not moved: the call is told to stand down, and a late finisher that honours it
    /// leaves the file in place.</summary>
    [Fact]
    public void A_timed_out_shell_call_is_told_to_stand_down_and_leaves_the_item_alone()
    {
        var file = Path.Combine(Path.GetTempPath(), $"wu-recyclebin-late-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "late test");
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var movedAfterAbandon = false;

        try
        {
            var deleted = RecycleBinDeleter.DeleteToRecycleBin(
                file, new LoggingService(),
                (path, _, abandoned) =>
                {
                    release.Wait();
                    if (!abandoned.IsCancellationRequested)
                    {
                        movedAfterAbandon = true;
                        File.Delete(path);
                    }

                    finished.Set();
                    return (0, false);
                },
                TimeSpan.FromMilliseconds(300), CancellationToken.None);

            Assert.False(deleted);

            release.Set();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(movedAfterAbandon);
            Assert.True(File.Exists(file));
        }
        finally
        {
            release.Set();
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    [Fact]
    public void The_progress_sink_refuses_every_delete_once_the_call_is_abandoned()
    {
        Assert.True(RecycleBinDeleter.AllowDelete(0x80, abandoned: false));
        Assert.False(RecycleBinDeleter.AllowDelete(0x80, abandoned: true));
        Assert.False(RecycleBinDeleter.AllowDelete(0, abandoned: false));
    }
}
