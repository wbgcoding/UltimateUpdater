using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void A_row_that_leaves_the_table_is_no_longer_watched_for_persistent_failures()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var kept = new UpdateRowViewModel(Item("kept", "Kept", "npm"));
            var replaced = new UpdateRowViewModel(Item("old", "Old", "npm"));
            viewModel.Updates.AddRange([kept, replaced]);
            Assert.True(viewModel.IsWatchingPersistentFailuresForTest(kept));
            Assert.True(viewModel.IsWatchingPersistentFailuresForTest(replaced));

            // what a rescan that swaps in a new row for the same package does to the old one
            viewModel.Updates.RemoveRange([replaced]);

            Assert.False(viewModel.IsWatchingPersistentFailuresForTest(replaced));
            Assert.True(viewModel.IsWatchingPersistentFailuresForTest(kept));
        });
    }
}
