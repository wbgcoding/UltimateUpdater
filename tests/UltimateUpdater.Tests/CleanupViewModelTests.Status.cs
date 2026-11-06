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

        viewModel.StepItems = viewModel.Items.Take(4).ToList();
        viewModel.Phase = CleanupPhase.Cleaning;
        Assert.True(viewModel.ShowsRunStatus);
        Assert.Equal(CleanupStatusKind.Cleaning, viewModel.StatusKind);
        Assert.Equal(string.Format(LocalizationService.Instance["CleanupAreaOfTotal"], 1, 4), viewModel.StatusCaptionText);
    }

    [Fact]
    public void The_caption_counts_the_area_at_work_and_never_runs_past_the_last_one()
    {
        var viewModel = Create(CreateSettings());
        var areas = viewModel.Items.Take(3).ToList();
        viewModel.StepItems = areas;
        viewModel.Phase = CleanupPhase.Cleaning;

        areas[0].IsDone = true;
        viewModel.RefreshAreaProgress();
        Assert.Equal(string.Format(LocalizationService.Instance["CleanupAreaOfTotal"], 2, 3), viewModel.AreaOfTotalText);
        Assert.Equal(1, viewModel.AreaDoneCount);
        Assert.Equal(3, viewModel.AreaTotalCount);

        areas[1].IsDone = true;
        areas[2].IsDone = true;
        viewModel.RefreshAreaProgress();
        Assert.Equal(string.Format(LocalizationService.Instance["CleanupAreaOfTotal"], 3, 3), viewModel.AreaOfTotalText);
    }

    [Fact]
    public void The_last_done_line_names_the_area_that_finished_most_recently_with_what_it_freed()
    {
        var viewModel = Create(CreateSettings());
        var areas = viewModel.Items.Take(3).ToList();
        viewModel.StepItems = areas;
        Assert.Equal(string.Empty, viewModel.LastDoneText);

        areas[1].FreedBytes = 5 * 1024 * 1024;
        areas[1].IsDone = true;
        viewModel.RefreshAreaProgress();
        areas[0].FreedBytes = 2 * 1024 * 1024;
        areas[0].IsDone = true;
        viewModel.RefreshAreaProgress();

        Assert.Equal(
            string.Format(LocalizationService.Instance["CleanupLastDone"], areas[0].Title, ByteSize.Format(2 * 1024 * 1024)),
            viewModel.LastDoneText);

        viewModel.Phase = CleanupPhase.Finished;
        Assert.NotEqual(string.Empty, viewModel.LastDoneText);
        viewModel.Phase = CleanupPhase.Choosing;
        Assert.Equal(string.Empty, viewModel.LastDoneText);
    }

    [Fact]
    public void A_long_path_is_cut_in_the_middle_keeping_its_start_and_its_file_name()
    {
        const string path = @"C:\Users\Someone\AppData\Local\Packages\Store\LocalCache\very-long-file-name-1234567890.tmp";

        var shortened = CleanupViewModel.TrimMiddle(path, 34);

        Assert.Equal(34, shortened.Length);
        Assert.StartsWith(@"C:\Users\", shortened);
        Assert.EndsWith("1234567890.tmp", shortened);
        Assert.Contains("…", shortened);
        Assert.Equal(@"C:\Temp\a.tmp", CleanupViewModel.TrimMiddle(@"C:\Temp\a.tmp", 34));

        var viewModel = Create(CreateSettings());
        viewModel.CurrentDetailText = path;
        Assert.Equal(shortened, viewModel.CurrentPathText);
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
    public void The_run_keeps_its_areas_of_the_run_after_it_ends_and_clears_on_a_new_choice()
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
    public void The_done_count_follows_the_areas_of_the_run()
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
