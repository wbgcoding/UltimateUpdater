using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>The uninstaller's Windows updates section: listed with the rest, greyed rows for what Windows keeps, and the
/// runner's failure texts for a removal.</summary>
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
    public async Task The_updates_are_listed_with_the_rest_and_the_section_counts_them()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(
            [UninstallerViewModelTests.Listing("Alpha"), .. UpdateListings()]);
        var section = UpdatesOf(vm);

        Assert.Equal(1, backend.ListCalls);
        Assert.True(section.IsVisible);
        Assert.Equal(2, section.Rows.Count);
        Assert.EndsWith("(2)", section.Header);
    }

    [Fact]
    public async Task Without_an_update_the_section_is_hidden()
    {
        var (vm, _) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("Alpha"));

        Assert.False(UpdatesOf(vm).IsVisible);
        Assert.Equal(UpdatesOf(vm).Title + " (0)", UpdatesOf(vm).Header);
    }

    [Fact]
    public async Task A_removable_update_can_be_ticked_and_one_windows_keeps_is_greyed_with_the_reason()
    {
        var (vm, _) = await UninstallerViewModelTests.LoadedAsync([.. UpdateListings()]);

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
        var (vm, _) = await UninstallerViewModelTests.LoadedAsync([.. UpdateListings()]);
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
    public async Task A_removed_update_row_leaves_no_program_leftover_scan()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync([.. UpdateListings()]);

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
}
