using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="CleanupViewModel.ApplySingleTargetMessage"/> is the rule that keeps a late
/// "progress" message from moving a finished target's displayed figure back to an earlier value.
/// CleanupWorker's own progress callbacks run on thread-pool threads while its terminal "result" is sent
/// synchronously right after the clean finishes, so a progress line queued just before the end can still
/// reach the app after the result. Proven here with a fabricated message sequence, no real elevated worker.
/// </summary>
public class SingleTargetCleanupProgressTests
{
    private static CleanupWorkerMessage Progress(double percent, long freed, string current = "file.tmp") => new()
    {
        Type = "progress",
        Percent = percent,
        Freed = freed,
        Current = current,
    };

    private static CleanupWorkerMessage Result(long freed, int files = 1, int skipped = 0, long skippedBytes = 0) => new()
    {
        Type = "result",
        Freed = freed,
        Files = files,
        Skipped = skipped,
        SkippedBytes = skippedBytes,
    };

    [Fact]
    public void A_progress_message_before_the_result_is_applied()
    {
        var state = CleanupViewModel.SingleTargetCleanupState.Initial;

        var (next, applied) = CleanupViewModel.ApplySingleTargetMessage(state, Progress(40, 1_000));

        Assert.NotNull(applied);
        Assert.Equal(1_000, applied!.Value.FreedBytes);
        Assert.False(next.Finished);
        Assert.Null(next.Outcome);
    }

    [Fact]
    public void The_result_message_finishes_the_target_and_carries_the_outcome()
    {
        var state = CleanupViewModel.SingleTargetCleanupState.Initial;

        var (next, applied) = CleanupViewModel.ApplySingleTargetMessage(state, Result(5_000));

        Assert.Null(applied);
        Assert.True(next.Finished);
        Assert.Equal(5_000, next.Outcome!.Value.FreedBytes);
    }

    /// <summary>The bug itself: a progress message that arrives AFTER the result must not be applied.</summary>
    [Fact]
    public void A_progress_message_after_the_result_is_dropped()
    {
        var state = CleanupViewModel.SingleTargetCleanupState.Initial;
        (state, _) = CleanupViewModel.ApplySingleTargetMessage(state, Result(5_000));

        var (next, applied) = CleanupViewModel.ApplySingleTargetMessage(state, Progress(60, 3_000));

        Assert.Null(applied);
        Assert.True(next.Finished);
        Assert.Equal(5_000, next.Outcome!.Value.FreedBytes);
    }

    /// <summary>
    /// End to end over the whole rule: progress, then result, then a late progress with a SMALLER freed
    /// figure (the exact shape of the bug, an earlier-queued report landing after the end) - the final,
    /// displayed figure must stay the result's own value, never fall back to the late report's.
    /// </summary>
    [Fact]
    public void The_displayed_figure_stays_the_final_result_even_after_a_late_progress_message()
    {
        var state = CleanupViewModel.SingleTargetCleanupState.Initial;
        long? lastAppliedFreed = null;

        foreach (var message in new[] { Progress(40, 1_000), Result(5_000), Progress(60, 3_000) })
        {
            CleanupProgress? applied;
            (state, applied) = CleanupViewModel.ApplySingleTargetMessage(state, message);
            if (applied is { } p)
                lastAppliedFreed = p.FreedBytes;
        }

        Assert.Equal(1_000, lastAppliedFreed);   // only the pre-result progress was ever applied to the ring
        Assert.True(state.Finished);
        Assert.Equal(5_000, state.Outcome!.Value.FreedBytes);   // the figure the row actually shows
    }

    /// <summary>A stopped Python cleanup still reports what it removed before the stop; the outcome must say
    /// stopped (so the row reads stopped, not done) and keep those counts.</summary>
    [Fact]
    public void A_stopped_result_keeps_its_counts_and_is_flagged_stopped()
    {
        var message = Result(2_048, files: 1);
        message.Stopped = true;

        var (next, _) = CleanupViewModel.ApplySingleTargetMessage(CleanupViewModel.SingleTargetCleanupState.Initial, message);

        Assert.True(next.Finished);
        Assert.True(next.Outcome!.Value.WasStopped);
        Assert.Equal(1, next.Outcome.Value.DeletedFiles);
        Assert.Equal(2_048, next.Outcome.Value.FreedBytes);
    }

    [Fact]
    public void A_result_without_the_stopped_flag_is_not_flagged_stopped()
    {
        var (next, _) = CleanupViewModel.ApplySingleTargetMessage(
            CleanupViewModel.SingleTargetCleanupState.Initial, Result(2_048));

        Assert.False(next.Outcome!.Value.WasStopped);
    }

    [Fact]
    public void An_unrelated_message_type_changes_nothing()
    {
        var state = CleanupViewModel.SingleTargetCleanupState.Initial;

        var (next, applied) = CleanupViewModel.ApplySingleTargetMessage(state, new CleanupWorkerMessage { Type = "log", Text = "x" });

        Assert.Null(applied);
        Assert.Equal(state, next);
    }

    [Fact]
    public void The_refused_share_of_an_elevated_result_reaches_the_outcome()
    {
        var message = Result(0, skipped: 5, skippedBytes: 500);
        message.ProtectedFiles = 3;
        message.ProtectedBytes = 300;

        var (next, _) = CleanupViewModel.ApplySingleTargetMessage(CleanupViewModel.SingleTargetCleanupState.Initial, message);

        Assert.Equal(3, next.Outcome!.Value.ProtectedFiles);
        Assert.Equal(300, next.Outcome!.Value.ProtectedBytes);
    }
}
