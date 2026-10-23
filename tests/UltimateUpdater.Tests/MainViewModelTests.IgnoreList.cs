using UltimateUpdater.Models;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void The_ignore_list_names_come_from_rows_and_their_children()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.Updates.AddRange(
            [
                new UpdateRowViewModel(Item("eslint", "ESLint", "npm")),
                new UpdateRowViewModel(new UpdateItem
                {
                    Id = "steam", SourceId = "steam", Category = UpdateCategory.Games, Name = "Steam",
                    Children =
                    [
                        new UpdateItem { Id = "game-a", SourceId = "steam", Category = UpdateCategory.Games, Name = "Game A" },
                    ],
                }),
            ]);

            var names = viewModel.IgnoredPackageNames();

            Assert.Equal("ESLint", names["npm:eslint"]);
            Assert.Equal("Steam", names["steam:steam"]);
            Assert.Equal("Game A", names["steam:game-a"]);
        });
    }

    [Fact]
    public void Removing_an_entry_in_the_ignore_list_brings_the_row_back_at_once()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var row = new UpdateRowViewModel(Item("eslint", "ESLint", "npm"));
            viewModel.Updates.Add(row);
            ((IRowActions)viewModel).IgnoreAllVersions(row);
            Assert.DoesNotContain(row, viewModel.UpdatesView.Cast<UpdateRowViewModel>());

            var ignoreList = viewModel.CreateSettingsViewModel().CreateIgnoreListViewModel();
            var entry = Assert.Single(ignoreList.Entries);
            Assert.Equal("ESLint", entry.Name);

            entry.RemoveCommand.Execute(null);

            Assert.False(row.IsIgnored);
            Assert.Contains(row, viewModel.UpdatesView.Cast<UpdateRowViewModel>());
        });
    }
}
