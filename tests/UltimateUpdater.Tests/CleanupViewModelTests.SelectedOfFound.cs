using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class CleanupViewModelTests
{
    private static void SetFoundSizes(CleanupViewModel viewModel, long bytesEach)
    {
        foreach (var item in viewModel.Items)
        {
            item.IsScanning = false;
            item.Bytes = bytesEach;
            item.Files = 1;
        }
    }

    [Fact]
    public void The_header_reads_selected_against_found_size()
    {
        var viewModel = Create(CreateSettings());
        SetFoundSizes(viewModel, 1024 * 1024);
        foreach (var item in viewModel.Items)
            item.IsSelected = false;
        var first = viewModel.Items[0];
        first.IsSelected = true;
        viewModel.RestoreDefaultSelectionCommand.CanExecute(null);

        var expected = string.Format(
            LocalizationService.Instance["CleanupSelectedOfFound"],
            ByteSize.Format(first.SelectedEntryBytes),
            ByteSize.Format(viewModel.Items.Sum(i => i.Bytes)));

        Assert.Equal(expected, viewModel.SelectedOfFoundText);
        Assert.StartsWith(ByteSize.Format(1024 * 1024) + " / ", viewModel.SelectedOfFoundText);
    }

    [Fact]
    public void The_selected_against_found_text_follows_the_ticks()
    {
        var viewModel = Create(CreateSettings());
        SetFoundSizes(viewModel, 1024 * 1024);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        viewModel.RestoreDefaultSelectionCommand.Execute(null);

        Assert.Contains(nameof(CleanupViewModel.SelectedOfFoundText), changed);
    }

    [Fact]
    public void A_group_header_shows_ticked_against_found_only_while_part_of_it_is_ticked()
    {
        var viewModel = Create(CreateSettings());
        var group = viewModel.Groups.First(g => g.Items.Count > 1 && !g.IsRegistryGroup);
        SetFoundSizes(viewModel, 2 * 1024 * 1024);
        foreach (var item in group.Items)
            item.IsSelected = true;

        Assert.Equal(group.SizeText, group.ChosenSizeText);

        group.Items[0].IsSelected = false;

        Assert.Equal(
            $"{ByteSize.Format(group.SelectedBytes)} / {ByteSize.Format(group.TotalBytes)}", group.ChosenSizeText);
        Assert.True(group.SelectedBytes < group.TotalBytes);
    }
}
