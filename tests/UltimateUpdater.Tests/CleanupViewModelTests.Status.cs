using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class CleanupViewModelTests
{
    [Fact]
    public void The_status_block_stays_on_the_idle_facts_until_a_run_exists()
    {
        var viewModel = Create(CreateSettings());

        Assert.False(viewModel.ShowsRunStatus);
        Assert.Equal(string.Empty, viewModel.StatusCaptionText);
        Assert.Equal("0:00", viewModel.ElapsedText);

        viewModel.Phase = CleanupPhase.Cleaning;
        Assert.True(viewModel.ShowsRunStatus);
        Assert.Equal(CleanupStatusKind.Cleaning, viewModel.StatusKind);
        Assert.Equal(LocalizationService.Instance["CleanupCurrentLabel"], viewModel.StatusCaptionText);
    }

    [Theory]
    [InlineData(false, CleanupStatusKind.Done, "CleanupSummaryDone")]
    [InlineData(true, CleanupStatusKind.Stopped, "CleanupSummaryStopped")]
    public void A_finished_run_is_summed_up_as_done_or_stopped(bool stopped, CleanupStatusKind kind, string captionKey)
    {
        var viewModel = Create(CreateSettings());
        viewModel.StartRunClock();
        viewModel.StopRunClock();
        viewModel.WasStopped = stopped;

        viewModel.Phase = CleanupPhase.Finished;

        Assert.Equal(kind, viewModel.StatusKind);
        Assert.Equal(LocalizationService.Instance[captionKey], viewModel.StatusCaptionText);
    }

    [Fact]
    public void The_elapsed_time_counts_with_the_clock_and_freezes_when_the_run_ends()
    {
        var viewModel = Create(CreateSettings());
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        viewModel.Clock = () => now;
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        viewModel.StartRunClock();
        Assert.Equal("0:00", viewModel.ElapsedText);

        now += TimeSpan.FromSeconds(83);
        viewModel.RefreshElapsed();
        Assert.Equal("1:23", viewModel.ElapsedText);
        Assert.Contains(nameof(CleanupViewModel.ElapsedText), changed);

        viewModel.StopRunClock();
        now += TimeSpan.FromMinutes(10);
        viewModel.RefreshElapsed();
        Assert.Equal("1:23", viewModel.ElapsedText);
    }

    [Fact]
    public void A_second_that_did_not_change_raises_nothing()
    {
        var viewModel = Create(CreateSettings());
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        viewModel.Clock = () => now;
        viewModel.StartRunClock();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        now += TimeSpan.FromMilliseconds(300);
        viewModel.RefreshElapsed();

        Assert.DoesNotContain(nameof(CleanupViewModel.ElapsedText), changed);
    }

    [Fact]
    public void Every_run_state_of_an_area_has_its_own_step_glyph_kind()
    {
        var viewModel = Create(CreateSettings());
        var item = viewModel.Items[0];
        item.IsScanning = false;
        item.Bytes = 4096;
        item.Files = 3;

        item.RunState = CleanupTargetState.Waiting;
        Assert.Equal(CleanupStepKind.Waiting, item.StepKind);

        item.RunState = CleanupTargetState.Running;
        Assert.Equal(CleanupStepKind.Running, item.StepKind);

        item.RunState = CleanupTargetState.Done;
        Assert.Equal(CleanupStepKind.Done, item.StepKind);

        item.Bytes = 0;
        item.Files = 0;
        Assert.Equal(CleanupStepKind.NothingFound, item.StepKind);

        item.RunState = CleanupTargetState.Stopped;
        Assert.Equal(CleanupStepKind.Stopped, item.StepKind);

        item.RunState = CleanupTargetState.Skipped;
        Assert.Equal(CleanupStepKind.Stopped, item.StepKind);
    }

    [Fact]
    public void A_step_names_its_area_and_its_state_for_the_hover_and_screen_readers()
    {
        var viewModel = Create(CreateSettings());
        var item = viewModel.Items[0];
        item.RunState = CleanupTargetState.Running;

        Assert.Equal(
            string.Format(
                LocalizationService.Instance["CleanupStepTooltip"],
                item.Title,
                LocalizationService.Instance["CleanupStateCleaning"]),
            item.StepTooltip);
    }

    [Fact]
    public void The_step_bar_keeps_the_areas_of_the_run_after_it_ends_and_clears_on_a_new_choice()
    {
        var viewModel = Create(CreateSettings());
        var areas = viewModel.Items.Take(3).ToList();

        viewModel.StepItems = areas;
        viewModel.Phase = CleanupPhase.Finished;
        Assert.Equal(3, viewModel.StepItems.Count);

        viewModel.Phase = CleanupPhase.Choosing;
        Assert.Empty(viewModel.StepItems);
        Assert.Equal(string.Empty, viewModel.DoneCountText);
    }

    [Fact]
    public void The_done_count_follows_the_areas_of_the_step_bar()
    {
        var viewModel = Create(CreateSettings());
        var areas = viewModel.Items.Take(4).ToList();
        viewModel.StepItems = areas;
        areas[0].IsDone = true;
        areas[1].IsDone = true;

        Assert.Equal(
            string.Format(LocalizationService.Instance["CleanupDoneCount"], 2, 4),
            viewModel.DoneCountText);
    }
}
