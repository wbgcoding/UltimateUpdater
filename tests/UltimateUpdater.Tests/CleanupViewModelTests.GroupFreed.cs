using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class CleanupViewModelTests
{
    /// <summary>After a run each section reports what it freed, but only a section that freed something: an
    /// unticked section would otherwise read "+0 B", a result where nothing was attempted.</summary>
    [Fact]
    public void A_finished_run_shows_the_freed_figure_only_for_sections_that_freed_bytes()
    {
        var viewModel = Create(CreateSettings());
        var freed = viewModel.Groups.First(g => g.Items.Count > 0 && !g.IsRegistryGroup);
        var untouched = viewModel.Groups.First(g => !ReferenceEquals(g, freed));

        freed.Items[0].IsSelected = true;
        freed.Items[0].FreedBytes = 5L * 1024 * 1024;
        untouched.Items.ToList().ForEach(i => i.IsSelected = false);

        Assert.False(freed.ShowsFreed);
        Assert.False(untouched.ShowsFreed);

        viewModel.Phase = CleanupPhase.Finished;

        Assert.True(freed.ShowsFreed);
        Assert.False(untouched.ShowsFreed);
        Assert.Equal(0, untouched.FreedBytes);
    }

    [Fact]
    public void A_section_whose_freed_bytes_arrive_after_the_run_ended_starts_showing_them()
    {
        var viewModel = Create(CreateSettings());
        var group = viewModel.Groups.First(g => g.Items.Count > 0);
        viewModel.Phase = CleanupPhase.Finished;
        Assert.False(group.ShowsFreed);

        var changed = new List<string?>();
        group.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        group.Items[0].FreedBytes = 1024;

        Assert.True(group.ShowsFreed);
        Assert.Contains(nameof(CleanupGroupViewModel.ShowsFreed), changed);

        viewModel.Phase = CleanupPhase.Choosing;
        Assert.False(group.ShowsFreed);
    }
}
