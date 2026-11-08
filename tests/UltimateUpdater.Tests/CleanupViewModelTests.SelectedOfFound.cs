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
    public void A_group_header_always_shows_ticked_against_found()
    {
        var viewModel = Create(CreateSettings());
        var group = viewModel.Groups.First(g => g.Items.Count > 1 && !g.IsRegistryGroup
            && g.Items.All(i => !CleanupService.SkipsWithoutAdmin(i.Target, processElevated: false)));
        SetFoundSizes(viewModel, 2 * 1024 * 1024);
        var found = ByteSize.Format(group.TotalBytes);

        foreach (var item in group.Items)
            item.IsSelected = true;
        Assert.Equal($"{ByteSize.Format(group.TotalBytes)} / {found}", group.ChosenSizeText);

        group.Items[0].IsSelected = false;
        Assert.Equal($"{ByteSize.Format(group.SelectedBytes)} / {found}", group.ChosenSizeText);
        Assert.True(group.SelectedBytes < group.TotalBytes);

        foreach (var item in group.Items)
            item.IsSelected = false;
        Assert.Equal($"{ByteSize.Format(0)} / {found}", group.ChosenSizeText);
    }

    [Fact]
    public void A_row_shows_its_own_size_when_ticked_and_nothing_against_found_when_not()
    {
        var viewModel = Create(CreateSettings());
        SetFoundSizes(viewModel, 3 * 1024 * 1024);
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.IsSelected = true;
        Assert.Equal($"{ByteSize.Format(item.Bytes)} / {ByteSize.Format(item.Bytes)}", item.ChosenSizeText);

        item.IsSelected = false;
        Assert.Equal($"{ByteSize.Format(0)} / {ByteSize.Format(item.Bytes)}", item.ChosenSizeText);
        Assert.Contains(nameof(CleanupItemViewModel.ChosenSizeText), changed);
    }

    [Fact]
    public void A_row_with_an_unticked_detail_entry_shows_the_remaining_size_against_found()
    {
        var viewModel = Create(CreateSettings());
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
        item.Bytes = 100;
        item.Files = 10;
        item.SetEntries(
        [
            new CleanupEntry(@"C:\Temp\A", "A", 60, 6, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
            new CleanupEntry(@"C:\Temp\B", "B", 40, 4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
        ]);
        item.IsSelected = true;
        var entry = item.Entries.First(e => e.DisplayName == "A");

        Assert.Equal($"{ByteSize.Format(60)} / {ByteSize.Format(60)}", entry.ChosenSizeText);

        entry.IsSelected = false;

        Assert.Equal($"{ByteSize.Format(0)} / {ByteSize.Format(60)}", entry.ChosenSizeText);
        Assert.Equal($"{ByteSize.Format(40)} / {ByteSize.Format(100)}", item.ChosenSizeText);
    }
}
