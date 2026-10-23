using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void The_headline_counts_a_folded_duplicate_once()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.Updates.AddRange(
            [
                new UpdateRowViewModel(Item("graphify", "graphify", "pip")),
                new UpdateRowViewModel(Item("graphify", "graphify", "uv-tool")),
            ]);

            Assert.Single(viewModel.UpdatesView.Cast<UpdateRowViewModel>());
            Assert.Equal(string.Format(viewModel.Loc["HeadlineUpdatesOne"], 1), viewModel.HeadlineText);
        });
    }

    [Fact]
    public void The_headline_leaves_out_rows_of_a_source_that_is_switched_off()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.Updates.AddRange(
            [
                new UpdateRowViewModel(Item("a", "A", "npm")),
                new UpdateRowViewModel(Item("b", "B", "winget")),
            ]);
            Assert.Equal(string.Format(viewModel.Loc["HeadlineUpdates"], 2), viewModel.HeadlineText);

            viewModel.ToggleSourceCommand.Execute("npm");

            Assert.Equal(string.Format(viewModel.Loc["HeadlineUpdatesOne"], 1), viewModel.HeadlineText);
        });
    }
}
