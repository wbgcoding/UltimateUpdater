using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>The uninstaller's Windows updates section: read only when opened, greyed rows for what Windows keeps,
/// a failed read that can be retried, the runner's failure texts, and the worker rows' JSON.</summary>
[Collection("UiLanguage")]
public class UninstallUpdatesViewModelTests
{
    private const string Removable = "Package_for_KB5031234~31bf3856ad364e35~amd64~~10.0.1.0";
    private const string Rollup = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4061.1.9";

    private sealed class NoFiles : IUninstallFileProbe
    {
        public bool FileExists(string path) => false;

        public byte[]? ReadHead(string path, int count) => null;

        public bool IsTrustedForElevation(string path) => true;
    }

    private static IReadOnlyList<UninstallListing> UpdateListings()
    {
        var rows = new[]
        {
            new WindowsUpdateRow { Identity = Removable, ReleaseType = "Security Update", InstalledOn = new DateOnly(2025, 10, 14), Removable = true },
            new WindowsUpdateRow { Identity = Rollup, ReleaseType = "Update", InstalledOn = new DateOnly(2025, 10, 14), Removable = false },
        };
        var entries = WindowsUpdateDiscovery.ToEntries(
            rows, [new QuickFix("KB5031234", "Security Update", new DateOnly(2025, 10, 14))], key => LocalizationService.Instance[key]);
        return UninstallerBackend.Rate(entries, new NoFiles(), @"C:\Windows");
    }

    private static UninstallSectionViewModel UpdatesOf(UninstallerViewModel vm) =>
        vm.Sections.Single(section => section.Kind == UninstallSection.Updates);

    [Fact]
    public async Task The_updates_are_not_read_until_the_section_is_opened()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("Alpha"));
        backend.Updates = UpdateListings;
        var section = UpdatesOf(vm);

        Assert.Equal(0, backend.UpdateCalls);
        Assert.True(section.IsVisible);
        Assert.False(section.HasRows);
        Assert.Equal(section.Title, section.Header);

        await vm.LoadUpdatesAsync();

        Assert.Equal(1, backend.UpdateCalls);
        Assert.Equal(UninstallSectionLoad.Loaded, section.Load);
        Assert.Equal(2, section.Rows.Count);
        Assert.EndsWith("(2)", section.Header);
    }

    [Fact]
    public async Task Opening_the_section_starts_the_read()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("Alpha"));
        backend.Updates = UpdateListings;
        var section = UpdatesOf(vm);

        section.IsExpanded = true;
        await WaitAsync(() => section.Load == UninstallSectionLoad.Loaded);

        Assert.Equal(1, backend.UpdateCalls);
    }

    [Fact]
    public async Task A_removable_update_can_be_ticked_and_one_windows_keeps_is_greyed_with_the_reason()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync();
        backend.Updates = UpdateListings;
        await vm.LoadUpdatesAsync();

        var removable = vm.AllRows.Single(row => row.Entry.Key.Contains("KB5031234", StringComparison.Ordinal));
        var kept = vm.AllRows.Single(row => row.Entry.Key.Contains("RollupFix", StringComparison.Ordinal));

        Assert.True(removable.Selectable);
        Assert.True(removable.IsRisky);
        Assert.False(removable.HasNote);

        Assert.False(kept.Selectable);
        Assert.True(kept.HasNote);
        Assert.Equal("Windows does not allow removing this update", kept.NoteText);
    }

    [Fact]
    public async Task The_german_note_for_a_kept_update_is_the_specified_text()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync();
        backend.Updates = UpdateListings;
        await vm.LoadUpdatesAsync();
        LocalizationService.Instance.Apply("de");
        try
        {
            var kept = vm.AllRows.Single(row => row.Entry.Key.Contains("RollupFix", StringComparison.Ordinal));
            Assert.Equal("Windows erlaubt das Entfernen dieses Updates nicht", UninstallRowText.Note(kept.Listing.Plan, kept.Entry));
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public async Task A_failed_read_says_so_and_the_next_open_tries_again()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync();
        backend.Updates = () => null;
        var section = UpdatesOf(vm);

        await vm.LoadUpdatesAsync();

        Assert.Equal(UninstallSectionLoad.Failed, section.Load);
        Assert.True(section.IsVisible);
        Assert.True(section.HasStatus);
        Assert.Equal(LocalizationService.Instance["UninstallerUpdatesFailed"], section.StatusText);
        Assert.DoesNotContain("(", section.Header);

        backend.Updates = UpdateListings;
        await vm.LoadUpdatesAsync();

        Assert.Equal(2, backend.UpdateCalls);
        Assert.Equal(UninstallSectionLoad.Loaded, section.Load);
        Assert.False(section.HasStatus);
    }

    [Fact]
    public async Task A_read_that_throws_is_a_failed_read()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync();
        backend.Updates = () => throw new InvalidOperationException("boom");
        await vm.LoadUpdatesAsync();

        Assert.Equal(UninstallSectionLoad.Failed, UpdatesOf(vm).Load);
    }

    [Fact]
    public async Task Nothing_to_list_says_so_and_is_not_read_again()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync();
        var section = UpdatesOf(vm);

        await vm.LoadUpdatesAsync();
        await vm.LoadUpdatesAsync();

        Assert.Equal(UninstallSectionLoad.Empty, section.Load);
        Assert.Equal(1, backend.UpdateCalls);
        Assert.Equal(LocalizationService.Instance["UninstallerUpdatesNone"], section.StatusText);
    }

    [Fact]
    public async Task A_reload_forgets_the_updates_and_reads_them_again_while_the_section_is_open()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync();
        backend.Updates = UpdateListings;
        var section = UpdatesOf(vm);
        section.IsExpanded = true;
        await WaitAsync(() => section.Load == UninstallSectionLoad.Loaded);

        await vm.LoadAsync();
        await WaitAsync(() => backend.UpdateCalls == 2 && section.Load == UninstallSectionLoad.Loaded);

        Assert.Equal(2, section.Rows.Count);
    }

    [Fact]
    public async Task A_reload_with_the_section_closed_leaves_the_updates_unread()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync();
        backend.Updates = UpdateListings;
        await vm.LoadUpdatesAsync();

        await vm.LoadAsync();

        Assert.Equal(UninstallSectionLoad.NotLoaded, UpdatesOf(vm).Load);
        Assert.Empty(vm.AllRows);
        Assert.Equal(1, backend.UpdateCalls);
    }

    [Fact]
    public async Task Other_sections_never_show_a_status_line()
    {
        var (vm, _) = await UninstallerViewModelTests.LoadedAsync();

        Assert.All(vm.Sections.Where(section => section.Kind != UninstallSection.Updates), section =>
        {
            Assert.False(section.HasStatus);
            Assert.False(section.IsVisible);
        });
    }

    [Fact]
    public async Task A_removed_update_row_leaves_no_program_leftover_scan()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync();
        backend.Updates = UpdateListings;
        await vm.LoadUpdatesAsync();

        vm.AllRows.Single(row => row.Entry.Key.Contains("KB5031234", StringComparison.Ordinal)).IsSelected = true;
        vm.Confirm = (_, _, _) => true;
        await vm.UninstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(0, backend.ScanCalls);
    }

    private static UninstallPlan UpdatePlan() => UninstallPlanner.Plan(
        new InstalledProgramEntry
        {
            Key = "update:" + Removable, Kind = UninstallEntryKind.WindowsUpdate, Name = "KB5031234", WindowsAllowsRemoval = true,
        },
        new NoFiles());

    [Theory]
    [InlineData(unchecked((int)0x800F0825), "UninstallerUpdateKept")]
    [InlineData(1, "UninstallDismFailed")]
    [InlineData(unchecked((int)0x800F0922), "UninstallDismFailed")]
    public void A_failed_dism_removal_names_its_cause(int exitCode, string expectedKey)
    {
        var runner = new UninstallRunner(
            _ => UpdatePlan(), new NeverExecutor(), new NeverExecutor(), (_, _) => Task.FromResult(false), key => key + "|{0}");

        var reason = runner.FailureReason(UpdatePlan(), exitCode, "output");

        Assert.StartsWith(expectedKey, reason, StringComparison.Ordinal);
        if (expectedKey == "UninstallDismFailed")
            Assert.Contains("0x" + exitCode.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dism_failure_text_exists_in_english_and_carries_the_code()
    {
        LocalizationService.Instance.Apply("en");
        var text = string.Format(LocalizationService.Instance["UninstallDismFailed"], "0x800F0922");

        Assert.Equal("Windows could not remove the update (code 0x800F0922).", text);
    }

    private sealed class NeverExecutor : IUninstallExecutor
    {
        public Task<UninstallExecutionResult> RunAsync(InstalledProgramEntry entry, UninstallPlan plan, CancellationToken ct, CancellationToken stopWhenIdle) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void The_workers_rows_survive_the_wire_and_garbage_is_no_list()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new[] { new WindowsUpdateRow { Identity = Removable, ReleaseType = "Update", InstalledOn = new DateOnly(2025, 1, 2), Removable = true } },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        var rows = WindowsUpdateLister.ParseRows(json);

        Assert.NotNull(rows);
        Assert.Equal(Removable, rows[0].Identity);
        Assert.True(rows[0].Removable);
        Assert.Equal(new DateOnly(2025, 1, 2), rows[0].InstalledOn);
        Assert.Null(WindowsUpdateLister.ParseRows("not json"));
        Assert.Null(WindowsUpdateLister.ParseRows(null));
        Assert.Null(WindowsUpdateLister.ParseRows(" "));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "the condition was not reached in time");
            await Task.Delay(10);
        }
    }
}
