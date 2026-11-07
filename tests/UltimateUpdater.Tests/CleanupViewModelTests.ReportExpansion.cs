using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class CleanupViewModelTests
{
    /// <summary>The report button of a finished run opens the history with the newest entry unfolded and the
    /// older ones folded, even when the user unfolded an old one earlier (the rows are shared).</summary>
    [Fact]
    public void Opening_the_report_unfolds_only_the_newest_history_row()
    {
        var history = CreateHistory();
        for (var day = 3; day >= 1; day--)
        {
            history.Append(new CleanupHistoryEntry
            {
                Timestamp = DateTimeOffset.UtcNow.AddDays(-day),
                FreedBytes = day * 1024L * 1024,
                Areas = [],
            });
        }

        var viewModel = Create(CreateSettings(), history);
        Assert.True(viewModel.History.Count >= 3);
        Assert.All(viewModel.History, row => Assert.False(row.IsExpanded));

        viewModel.History[2].IsExpanded = true;
        viewModel.ExpandNewestHistoryRow();

        Assert.True(viewModel.History[0].IsExpanded);
        Assert.All(viewModel.History.Skip(1), row => Assert.False(row.IsExpanded));
    }

    [Fact]
    public void Opening_the_report_with_no_history_does_nothing()
    {
        var viewModel = Create(CreateSettings(), CreateHistory());

        viewModel.ExpandNewestHistoryRow();

        Assert.Empty(viewModel.History);
    }
}
