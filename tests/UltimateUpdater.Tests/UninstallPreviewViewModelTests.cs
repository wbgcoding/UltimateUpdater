using System.Collections.Specialized;
using System.Globalization;
using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Uninstall;
using static UltimateUpdater.Tests.UninstallerViewModelTests;

namespace UltimateUpdater.Tests;

/// <summary>
/// Opening the uninstaller: the saved rows first (greyed, with a note), the cheap recheck, then the full read merged into
/// the same row objects, so a tick and the scroll position survive. Plus the cache being written after a read and a run.
/// </summary>
[Collection("UiLanguage")]
public class UninstallPreviewViewModelTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset SavedAt = new(2026, 3, 4, 15, 30, 0, TimeSpan.Zero);

    private static (UninstallerViewModel ViewModel, FakeBackend Backend) Open(
        UninstallListing[] cached, params UninstallListing[] fresh)
    {
        LocalizationService.Instance.Apply("en");
        var backend = new FakeBackend(fresh) { Cached = cached.Length == 0 ? null : new UninstallCachedListing(cached, SavedAt) };
        return (new UninstallerViewModel(backend, new LoggingService()), backend);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition never became true");
            await Task.Delay(10);
        }
    }

    private static UninstallRowViewModel Row(UninstallerViewModel viewModel, string name) =>
        viewModel.AllRows.Single(row => row.Name == name);

    [Fact]
    public async Task Without_a_saved_listing_opening_reads_everything_and_shows_no_note()
    {
        var (vm, backend) = Open([], Listing("A"), Listing("B"));

        await vm.OpenAsync().WaitAsync(Patience);

        Assert.Equal(2, vm.AllRows.Count);
        Assert.False(vm.HasPreviewNote);
        Assert.Equal(1, backend.ListCalls);
        Assert.Equal(["A", "B"], Assert.Single(backend.Saved).Select(entry => entry.Name));
    }

    [Fact]
    public async Task Saved_rows_show_at_once_greyed_with_the_note_until_the_recheck_confirms_them()
    {
        var recheck = new TaskCompletionSource();
        var read = new TaskCompletionSource();
        var (vm, backend) = Open([Listing("A"), Listing("B"), Listing("Gone")], Listing("A"), Listing("B"));
        backend.Recheck = cached => [.. cached.Where(listing => listing.Entry.Name != "Gone")];
        backend.RecheckGate = recheck.Task;
        backend.ListGate = read.Task;

        var open = vm.OpenAsync();

        Assert.Equal(["A", "B", "Gone"], vm.AllRows.Select(row => row.Name));
        Assert.True(vm.HasPreviewNote);
        Assert.False(vm.ShowsLoadingText);
        Assert.Equal("As of " + SavedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) + ", updating", vm.PreviewNote);
        Assert.All(vm.AllRows, row => Assert.False(row.Selectable));

        Row(vm, "A").IsSelected = true;
        Assert.False(Row(vm, "A").IsSelected);

        // The recheck only proves a row is not gone: the rows stay greyed (and refuse a tick) until the full read merged.
        recheck.SetResult();
        await WaitUntilAsync(() => vm.AllRows.Count == 2);
        Assert.All(vm.AllRows, row => Assert.False(row.Selectable));
        Row(vm, "A").IsSelected = true;
        Assert.False(Row(vm, "A").IsSelected);
        Assert.True(vm.HasPreviewNote);

        read.SetResult();
        await open.WaitAsync(Patience);
        Assert.All(vm.AllRows, row => Assert.True(row.Selectable));
        Assert.False(vm.HasPreviewNote);
        Assert.False(vm.IsLoading);
        Assert.Single(backend.Saved);
    }

    [Fact]
    public async Task The_note_is_the_german_text_in_german()
    {
        var read = new TaskCompletionSource();
        var (vm, backend) = Open([Listing("A")], Listing("A"));
        backend.ListGate = read.Task;
        var open = vm.OpenAsync();
        LocalizationService.Instance.Apply("de");
        try
        {
            vm.RefreshText();

            Assert.Equal(
                "Stand vom " + SavedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) + ", wird aktualisiert", vm.PreviewNote);
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
            read.SetResult();
            await open.WaitAsync(Patience);
        }
    }

    [Fact]
    public async Task The_recheck_drops_a_row_that_is_gone_before_the_full_read_ends()
    {
        var read = new TaskCompletionSource();
        var (vm, backend) = Open([Listing("A"), Listing("Gone")], Listing("A"));
        backend.Recheck = cached => [.. cached.Where(listing => listing.Entry.Name != "Gone")];
        backend.ListGate = read.Task;

        var open = vm.OpenAsync();

        Assert.Equal(["A"], vm.AllRows.Select(row => row.Name));
        Assert.False(Row(vm, "A").Selectable);

        read.SetResult();
        await open.WaitAsync(Patience);
        Assert.True(Row(vm, "A").Selectable);
    }

    [Fact]
    public async Task The_full_read_keeps_the_row_objects_and_a_tick_and_updates_what_changed()
    {
        var read = new TaskCompletionSource();
        var (vm, backend) = Open(
            [Listing("A", version: "1.0"), Listing("B", version: "1.0")],
            Listing("A", version: "2.0"), Listing("B", version: "1.0"));
        backend.ListGate = read.Task;
        var open = vm.OpenAsync();
        var a = Row(vm, "A");
        var b = Row(vm, "B");

        read.SetResult();
        await open.WaitAsync(Patience);

        Assert.Same(a, Row(vm, "A"));
        Assert.Same(b, Row(vm, "B"));
        Assert.Equal("2.0", a.Version);

        // A tick set after the merge survives the next merge of the same rows.
        b.IsSelected = true;
        vm.MergeListings([Listing("A", version: "3.0"), Listing("B", version: "1.0")]);

        Assert.Same(b, Row(vm, "B"));
        Assert.Equal("3.0", a.Version);
        Assert.True(b.IsSelected);
        Assert.Equal(1, vm.SelectedCount);
    }

    [Fact]
    public async Task The_full_read_removes_a_vanished_key_and_adds_a_new_one()
    {
        var read = new TaskCompletionSource();
        var (vm, backend) = Open([Listing("A"), Listing("Vanished")], Listing("A"), Listing("New"));
        backend.ListGate = read.Task;
        var open = vm.OpenAsync();
        var a = Row(vm, "A");

        read.SetResult();
        await open.WaitAsync(Patience);

        Assert.Equal(["A", "New"], vm.AllRows.Select(row => row.Name));
        Assert.Same(a, Row(vm, "A"));
        Assert.True(Row(vm, "New").Selectable);
        Assert.Equal(["A", "New"], vm.Sections[0].Rows.Select(row => row.Name));
    }

    [Fact]
    public async Task A_row_that_stops_being_removable_loses_its_tick()
    {
        var (vm, _) = await LoadedAsync(Listing("A"));
        Row(vm, "A").IsSelected = true;
        Assert.True(Row(vm, "A").IsSelected);

        vm.MergeListings([Listing("A", runnable: false)]);

        Assert.False(Row(vm, "A").IsSelected);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task Merging_an_unchanged_listing_does_not_touch_the_section_so_the_scroll_stays()
    {
        var (vm, _) = await LoadedAsync(Listing("A"), Listing("B"), Listing("C"));
        var changes = new List<NotifyCollectionChangedEventArgs>();
        vm.Sections[0].Rows.CollectionChanged += (_, e) => changes.Add(e);

        vm.MergeListings([Listing("A"), Listing("B"), Listing("C")]);

        Assert.Empty(changes);
        Assert.Equal(["A", "B", "C"], vm.Sections[0].Rows.Select(row => row.Name));
    }

    [Fact]
    public async Task A_merge_that_adds_one_row_inserts_only_that_row()
    {
        var (vm, _) = await LoadedAsync(Listing("A"), Listing("C"));
        var changes = new List<NotifyCollectionChangedEventArgs>();
        vm.Sections[0].Rows.CollectionChanged += (_, e) => changes.Add(e);

        vm.MergeListings([Listing("A"), Listing("B"), Listing("C")]);

        var change = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Add, change.Action);
        Assert.Equal(["A", "B", "C"], vm.Sections[0].Rows.Select(row => row.Name));
    }

    [Fact]
    public async Task A_run_in_progress_blocks_the_merge()
    {
        var (vm, _) = await LoadedAsync(Listing("A"), Listing("B"));
        vm.IsRunning = true;

        vm.MergeListings([Listing("A")]);

        Assert.Equal(["A", "B"], vm.AllRows.Select(row => row.Name));
    }

    [Fact]
    public async Task A_failed_full_read_does_not_leave_the_unconfirmed_saved_rows_on_show()
    {
        var (vm, backend) = Open([Listing("A")], Listing("A"));
        backend.ListThrows = new IOException("boom");

        await vm.OpenAsync().WaitAsync(Patience);

        Assert.Empty(vm.AllRows);
        Assert.False(vm.HasPreviewNote);
        Assert.False(vm.IsLoading);
        Assert.True(vm.LoadFailed);
        Assert.Empty(backend.Saved);
    }

    [Fact]
    public async Task A_failed_full_read_without_saved_rows_says_so()
    {
        var (vm, backend) = Open([], Listing("A"));
        backend.ListThrows = new IOException("boom");

        await vm.OpenAsync().WaitAsync(Patience);

        Assert.Empty(vm.AllRows);
        Assert.True(vm.LoadFailed);
    }

    [Fact]
    public async Task Reload_reads_fresh_and_saves_again()
    {
        var (vm, backend) = await LoadedAsync(Listing("A"));
        backend.Current = [Listing("A"), Listing("B")];

        await vm.ReloadCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.AllRows.Count);
        Assert.Equal(2, backend.Saved.Count);
        Assert.Equal(2, backend.Saved[^1].Count);
    }

    [Fact]
    public async Task After_a_run_the_saved_listing_no_longer_holds_the_removed_row()
    {
        var (vm, backend) = await LoadedAsync(Listing("A"), Listing("B"));
        Row(vm, "A").IsSelected = true;
        vm.Confirm = (_, _, _) => true;

        await vm.UninstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(UninstallRowState.Removed, Row(vm, "A").State);
        Assert.Equal(["B"], backend.Saved[^1].Select(entry => entry.Name));
    }

    [Fact]
    public async Task A_saved_listing_that_is_empty_shows_no_note()
    {
        LocalizationService.Instance.Apply("en");
        var backend = new FakeBackend(Listing("A")) { Cached = new UninstallCachedListing([], SavedAt) };
        var vm = new UninstallerViewModel(backend, new LoggingService());

        await vm.OpenAsync().WaitAsync(Patience);

        Assert.False(vm.HasPreviewNote);
        Assert.Equal(["A"], vm.AllRows.Select(row => row.Name));
    }
}
