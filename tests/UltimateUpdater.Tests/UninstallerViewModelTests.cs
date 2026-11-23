using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>The uninstaller window's logic: selection rules around Risky rows, the confirmation, search, sort and the run's row states, all against a fake backend.</summary>
[Collection("UiLanguage")]
public class UninstallerViewModelTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    internal static UninstallListing Listing(
        string name,
        CleanupRiskLevel level = CleanupRiskLevel.Safe,
        UninstallRiskReason reason = UninstallRiskReason.Ordinary,
        UninstallEntryKind kind = UninstallEntryKind.Program,
        long? size = null,
        DateOnly? installed = null,
        string version = "1.0",
        string publisher = "Acme",
        bool runnable = true,
        bool selectable = true) =>
        new(
            new InstalledProgramEntry
            {
                Key = $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
                Kind = kind,
                Name = name,
                Publisher = publisher,
                Version = version,
                SizeBytes = size,
                InstallDate = installed,
            },
            runnable
                ? new UninstallPlan { Method = UninstallMethod.QuietString, Executable = @"C:\Program Files\X\u.exe", Elevated = true }
                : new UninstallPlan { Method = UninstallMethod.Refused, Refusal = UninstallRefusal.NoCommand },
            new UninstallRisk(level, reason, selectable));

    internal sealed class FakeBackend(params UninstallListing[] listings) : IUninstallerBackend
    {
        public Func<InstalledProgramEntry, CancellationToken, Task<UninstallExecutionResult>> Handler { get; set; } =
            (_, _) => Task.FromResult(new UninstallExecutionResult(0));

        public Func<InstalledProgramEntry, bool> Gone { get; set; } = _ => true;

        public List<string> Started { get; } = [];

        /// <summary>What the leftover scan finds for the removed entries; none by default.</summary>
        public Func<IReadOnlyList<InstalledProgramEntry>, IReadOnlyList<UninstallLeftoverGroup>> Leftovers { get; set; } = _ => [];

        public Func<IReadOnlyList<UninstallLeftoverGroup>, Task<UninstallLeftoverResult>>? RemoveLeftovers { get; set; }

        public List<IReadOnlyList<UninstallLeftoverGroup>> RemovedGroups { get; } = [];

        public int ScanCalls { get; private set; }

        /// <summary>Off by default so the older tests see no restore point step.</summary>
        public UninstallBackupMode BackupMode { get; set; } = UninstallBackupMode.Off;

        public Func<UninstallRestorePointResult> RestorePoint { get; set; } =
            () => new UninstallRestorePointResult(UninstallRestorePointOutcome.Created);

        public int RestorePointCalls { get; private set; }

        /// <summary>Runs while the restore point is being made, for tests that look at the run's first step.</summary>
        public Action? DuringRestorePoint { get; set; }

        public List<InstalledProgramEntry> Recorded { get; } = [];

        public Task<UninstallRestorePointResult> CreateRestorePointAsync(CancellationToken cancellationToken)
        {
            RestorePointCalls++;
            DuringRestorePoint?.Invoke();
            return Task.FromResult(RestorePoint());
        }

        public void RecordRemoved(InstalledProgramEntry entry) => Recorded.Add(entry);

        public Task<IReadOnlyList<UninstallLeftoverGroup>> FindLeftoversAsync(
            IReadOnlyList<InstalledProgramEntry> removed, CancellationToken cancellationToken)
        {
            ScanCalls++;
            return Task.FromResult(Leftovers(removed));
        }

        public Task<UninstallLeftoverResult> RemoveLeftoversAsync(
            IReadOnlyList<UninstallLeftoverGroup> groups, CancellationToken cancellationToken)
        {
            RemovedGroups.Add(groups);
            return RemoveLeftovers?.Invoke(groups)
                ?? Task.FromResult(new UninstallLeftoverResult(groups.Sum(group => group.Items.Count), 0, 0));
        }

        /// <summary>How often the installed programs were listed.</summary>
        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<UninstallListing>> ListAsync(CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<UninstallListing>>(listings);
        }

        /// <summary>What the installed Windows updates read gives; null is a read that failed. Empty by default.</summary>
        public Func<IReadOnlyList<UninstallListing>?> Updates { get; set; } = () => [];

        /// <summary>How often the installed updates were read.</summary>
        public int UpdateCalls { get; private set; }

        public Task<IReadOnlyList<UninstallListing>?> ListUpdatesAsync(CancellationToken cancellationToken)
        {
            UpdateCalls++;
            return Task.FromResult(Updates());
        }

        public UninstallRunner CreateRunner(Func<InstalledProgramEntry, UninstallPlan> plan)
        {
            var executor = new Executor(this);
            return new UninstallRunner(plan, executor, executor, (entry, _) => Task.FromResult(Gone(entry)), key => key);
        }

        private sealed class Executor(FakeBackend owner) : IUninstallExecutor
        {
            public Task<UninstallExecutionResult> RunAsync(InstalledProgramEntry entry, UninstallPlan plan, CancellationToken ct)
            {
                lock (owner.Started)
                    owner.Started.Add(entry.Name);

                return owner.Handler(entry, ct);
            }
        }
    }

    internal static async Task<(UninstallerViewModel ViewModel, FakeBackend Backend)> LoadedAsync(params UninstallListing[] listings)
    {
        LocalizationService.Instance.Apply("en");
        var backend = new FakeBackend(listings);
        var viewModel = new UninstallerViewModel(backend, new LoggingService());
        await viewModel.LoadAsync();
        return (viewModel, backend);
    }

    private static UninstallRowViewModel Row(UninstallerViewModel viewModel, string name) =>
        viewModel.AllRows.Single(row => row.Name == name);

    [Fact]
    public async Task Rows_are_sorted_into_their_sections_and_only_programs_start_expanded()
    {
        var (vm, _) = await LoadedAsync(
            Listing("A"), Listing("Pkg", kind: UninstallEntryKind.Package), Listing("Drv", kind: UninstallEntryKind.Driver),
            Listing("Upd", kind: UninstallEntryKind.WindowsUpdate), Listing("Store", kind: UninstallEntryKind.StoreApp));

        Assert.Equal(4, vm.Sections.Count);
        Assert.Equal(["A", "Store"], vm.Sections[0].Rows.Select(row => row.Name).Order());
        Assert.Equal(["Pkg"], vm.Sections[1].Rows.Select(row => row.Name));
        Assert.Equal(["Drv"], vm.Sections[2].Rows.Select(row => row.Name));
        Assert.Equal(["Upd"], vm.Sections[3].Rows.Select(row => row.Name));
        Assert.True(vm.Sections[0].IsExpanded);
        Assert.All(vm.Sections.Skip(1), section => Assert.False(section.IsExpanded));
        Assert.Equal("Programs and apps (2)", vm.Sections[0].Header);
    }

    [Fact]
    public async Task The_refresh_command_reads_the_installed_programs_again()
    {
        var (vm, backend) = await LoadedAsync(Listing("A"));
        Assert.Equal(1, backend.ListCalls);

        await vm.ReloadCommand.ExecuteAsync(null);

        Assert.Equal(2, backend.ListCalls);
        Assert.Single(vm.AllRows);
    }

    [Fact]
    public void The_uninstaller_window_binds_the_refresh_command_to_a_button_and_to_F5()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "UltimateUpdater", "Views", "Uninstall", "UninstallerWindow.xaml")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var xaml = File.ReadAllText(Path.Combine(directory.FullName, "src", "UltimateUpdater", "Views", "Uninstall", "UninstallerWindow.xaml"));

        Assert.Contains("Command=\"{Binding ReloadCommand}\"", xaml);
        Assert.Contains("<KeyBinding Key=\"F5\" Command=\"{Binding ReloadCommand}\"", xaml);
    }

    [Fact]
    public async Task Select_all_ticks_only_the_ordinary_rows_and_a_second_press_unticks_them()
    {
        var (vm, _) = await LoadedAsync(
            Listing("Plain"), Listing("Considered", CleanupRiskLevel.Considered, UninstallRiskReason.Unknown),
            Listing("Risk", CleanupRiskLevel.Risky, UninstallRiskReason.Driver),
            Listing("Own", CleanupRiskLevel.Considered, UninstallRiskReason.Own, selectable: false),
            Listing("NoWay", runnable: false));

        vm.SelectAllCommand.Execute(null);

        Assert.True(Row(vm, "Plain").IsSelected);
        Assert.True(Row(vm, "Considered").IsSelected);
        Assert.False(Row(vm, "Risk").IsSelected);
        Assert.False(Row(vm, "Own").IsSelected);
        Assert.False(Row(vm, "NoWay").IsSelected);
        Assert.Equal(2, vm.SelectedCount);

        vm.SelectAllCommand.Execute(null);

        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task Select_all_under_a_search_filter_ticks_only_the_shown_rows_and_never_a_risky_one()
    {
        var (vm, _) = await LoadedAsync(
            Listing("Alpha Tool"), Listing("Alpha Driver", CleanupRiskLevel.Risky, UninstallRiskReason.Driver),
            Listing("Beta Tool"));

        vm.SearchText = "alpha";
        vm.SelectAllCommand.Execute(null);

        Assert.True(Row(vm, "Alpha Tool").IsSelected);
        Assert.False(Row(vm, "Alpha Driver").IsSelected);
        Assert.False(Row(vm, "Beta Tool").IsSelected);
    }

    [Fact]
    public async Task A_risky_row_is_ticked_by_its_own_checkbox_only()
    {
        var (vm, _) = await LoadedAsync(Listing("Risk", CleanupRiskLevel.Risky, UninstallRiskReason.Security), Listing("Plain"));
        var risky = Row(vm, "Risk");

        risky.IsSelected = true;

        Assert.True(risky.IsSelected);
        Assert.Equal(1, vm.SelectedCount);
        Assert.Single(vm.SelectedRiskyRows);
    }

    [Fact]
    public async Task A_row_that_cannot_be_removed_cannot_be_ticked()
    {
        var (vm, _) = await LoadedAsync(Listing("NoWay", runnable: false), Listing("Own", selectable: false));

        Row(vm, "NoWay").IsSelected = true;
        Row(vm, "Own").IsSelected = true;

        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.CanUninstall);
    }

    [Fact]
    public async Task Shift_range_skips_risky_rows()
    {
        var (vm, _) = await LoadedAsync(
            Listing("A"), Listing("B", CleanupRiskLevel.Risky, UninstallRiskReason.Hardware), Listing("C"), Listing("D"));
        var shown = vm.Sections[0].Rows.ToList();

        vm.SelectRange(shown, shown[0], shown[2]);

        Assert.Equal(["A", "C"], vm.SelectedRows.Select(row => row.Name).Order());
        Assert.False(Row(vm, "B").IsSelected);
        Assert.False(Row(vm, "D").IsSelected);
    }

    [Fact]
    public async Task Space_on_several_rows_leaves_risky_ones_alone_and_toggles_the_rest()
    {
        var (vm, _) = await LoadedAsync(
            Listing("A"), Listing("B", CleanupRiskLevel.Risky, UninstallRiskReason.Hardware), Listing("C"));
        var rows = vm.Sections[0].Rows.ToList();

        vm.ToggleRows(rows);
        Assert.Equal(["A", "C"], vm.SelectedRows.Select(row => row.Name).Order());

        vm.ToggleRows(rows);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task Space_on_one_risky_row_ticks_it_like_its_own_checkbox()
    {
        var (vm, _) = await LoadedAsync(Listing("B", CleanupRiskLevel.Risky, UninstallRiskReason.Hardware));

        vm.ToggleRows([Row(vm, "B")]);

        Assert.True(Row(vm, "B").IsSelected);
    }

    [Fact]
    public async Task Select_none_unticks_risky_rows_too()
    {
        var (vm, _) = await LoadedAsync(Listing("A"), Listing("B", CleanupRiskLevel.Risky, UninstallRiskReason.Hardware));
        Row(vm, "A").IsSelected = true;
        Row(vm, "B").IsSelected = true;

        vm.ClearSelectionCommand.Execute(null);

        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task The_confirmation_of_ordinary_rows_has_no_acknowledge_text()
    {
        var (vm, _) = await LoadedAsync(Listing("A"), Listing("B"));
        vm.SelectAllCommand.Execute(null);

        var (headline, body, acknowledge) = vm.ConfirmationTexts(vm.SelectedRows);

        Assert.Equal("Uninstall 2 items? Programs without a silent mode show their own window.", headline);
        Assert.Equal(string.Empty, body);
        Assert.Null(acknowledge);
    }

    [Fact]
    public async Task The_confirmation_with_a_risky_row_lists_it_with_its_reason_and_asks_for_the_acknowledge_text()
    {
        var (vm, _) = await LoadedAsync(Listing("Chip Driver", CleanupRiskLevel.Risky, UninstallRiskReason.Driver), Listing("A"));
        Row(vm, "Chip Driver").IsSelected = true;
        Row(vm, "A").IsSelected = true;

        var (_, body, acknowledge) = vm.ConfirmationTexts(vm.SelectedRows);

        Assert.Contains("Chip Driver: " + LocalizationService.Instance["UninstallRiskDriver"], body);
        Assert.DoesNotContain("A:", body.Replace("Chip Driver:", string.Empty));
        Assert.Equal("I understand these can affect Windows or other programs.", acknowledge);
    }

    [Fact]
    public async Task The_acknowledge_text_is_the_german_wording_in_german()
    {
        var (vm, _) = await LoadedAsync(Listing("X", CleanupRiskLevel.Risky, UninstallRiskReason.Driver));
        LocalizationService.Instance.Apply("de");
        try
        {
            Row(vm, "X").IsSelected = true;

            var (_, _, acknowledge) = vm.ConfirmationTexts(vm.SelectedRows);

            Assert.Equal("Mir ist klar, dass diese Einträge Windows oder andere Programme beeinträchtigen können.", acknowledge);
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public async Task Search_matches_name_and_publisher_and_a_search_without_hits_shows_the_no_match_text()
    {
        var (vm, _) = await LoadedAsync(Listing("Notepad Plus", publisher: "Don"), Listing("Browser", publisher: "Acme"));

        vm.SearchText = "acme";
        Assert.Equal(["Browser"], vm.Sections[0].Rows.Select(row => row.Name));

        vm.SearchText = "zzz";
        Assert.True(vm.ShowsEmptyText);
        Assert.Equal("No program matches the search.", vm.EmptyText);

        vm.SearchText = string.Empty;
        Assert.Equal(2, vm.VisibleCount);
    }

    [Fact]
    public async Task An_empty_inventory_shows_the_empty_text()
    {
        var (vm, _) = await LoadedAsync();

        Assert.True(vm.ShowsEmptyText);
        Assert.Equal("Nothing installed was found.", vm.EmptyText);
    }

    [Fact]
    public async Task Sorting_by_size_puts_rows_without_a_size_last_in_both_directions()
    {
        var (vm, _) = await LoadedAsync(Listing("None"), Listing("Small", size: 10), Listing("Big", size: 1000));

        vm.SortByCommand.Execute("size");
        Assert.Equal(["Big", "Small", "None"], vm.Sections[0].Rows.Select(row => row.Name));

        vm.SortByCommand.Execute("size");
        Assert.Equal(["Small", "Big", "None"], vm.Sections[0].Rows.Select(row => row.Name));
    }

    [Fact]
    public async Task Sorting_by_installed_date_puts_rows_without_a_date_last()
    {
        var (vm, _) = await LoadedAsync(
            Listing("None"), Listing("Old", installed: new DateOnly(2020, 1, 1)), Listing("New", installed: new DateOnly(2025, 5, 5)));

        vm.SortByCommand.Execute("installed");

        Assert.Equal(["New", "Old", "None"], vm.Sections[0].Rows.Select(row => row.Name));
    }

    [Fact]
    public async Task Sorting_by_version_compares_numbers_not_text()
    {
        var (vm, _) = await LoadedAsync(Listing("Ten", version: "10.0"), Listing("Two", version: "2.0"));

        vm.SortByCommand.Execute("version");
        Assert.Equal(["Two", "Ten"], vm.Sections[0].Rows.Select(row => row.Name));

        vm.SortByCommand.Execute("version");
        Assert.Equal(["Ten", "Two"], vm.Sections[0].Rows.Select(row => row.Name));
    }

    [Fact]
    public async Task A_run_asks_first_and_a_declined_confirmation_removes_nothing()
    {
        var (vm, backend) = await LoadedAsync(Listing("A"));
        Row(vm, "A").IsSelected = true;
        vm.Confirm = (_, _, _) => false;

        await vm.UninstallSelectedCommand.ExecuteAsync(null);

        Assert.Empty(backend.Started);
        Assert.Equal(UninstallRowState.Idle, Row(vm, "A").State);
        Assert.True(Row(vm, "A").IsSelected);
    }

    [Fact]
    public async Task A_run_passes_the_acknowledge_text_to_the_confirmation_for_a_risky_row()
    {
        var (vm, _) = await LoadedAsync(Listing("R", CleanupRiskLevel.Risky, UninstallRiskReason.Driver));
        Row(vm, "R").IsSelected = true;
        string? seen = "unset";
        vm.Confirm = (_, _, acknowledge) =>
        {
            seen = acknowledge;
            return false;
        };

        await vm.UninstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal("I understand these can affect Windows or other programs.", seen);
    }

    [Fact]
    public async Task A_run_marks_each_row_with_its_outcome_and_unticks_afterwards()
    {
        var (vm, backend) = await LoadedAsync(Listing("Good"), Listing("Bad"), Listing("Reboot"));
        backend.Handler = (entry, _) => Task.FromResult(entry.Name switch
        {
            "Bad" => new UninstallExecutionResult(1603),
            "Reboot" => new UninstallExecutionResult(3010),
            _ => new UninstallExecutionResult(0),
        });
        backend.Gone = entry => entry.Name != "Bad";
        vm.SelectAllCommand.Execute(null);
        vm.Confirm = (_, _, _) => true;

        await vm.UninstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(UninstallRowState.Removed, Row(vm, "Good").State);
        Assert.Equal(UninstallRowState.Failed, Row(vm, "Bad").State);
        Assert.False(string.IsNullOrEmpty(Row(vm, "Bad").Reason));
        Assert.Equal(UninstallRowState.NeedsRestart, Row(vm, "Reboot").State);
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.IsRunning);
        Assert.Equal("Removed", Row(vm, "Good").StateText);
        Assert.Equal("Removed, restart needed", Row(vm, "Reboot").StateText);
    }

    [Fact]
    public async Task Stop_lets_the_running_removal_finish_and_leaves_the_rest_not_started()
    {
        var (vm, backend) = await LoadedAsync(Listing("First"), Listing("Second"), Listing("Third"));
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        backend.Handler = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new UninstallExecutionResult(0);
        };
        vm.SelectAllCommand.Execute(null);
        vm.Confirm = (_, _, _) => true;

        var run = vm.UninstallSelectedCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Patience);
        vm.StopCommand.Execute(null);
        Assert.True(vm.IsStopping);
        release.SetResult();
        await run.WaitAsync(Patience);

        Assert.Equal(1, vm.AllRows.Count(row => row.State == UninstallRowState.Removed));
        Assert.Equal(2, vm.AllRows.Count(row => row.State == UninstallRowState.NotStarted));
        Assert.False(vm.IsRunning);
        Assert.False(vm.IsStopping);
    }

    [Fact]
    public async Task Nothing_can_be_ticked_or_started_while_a_run_is_going()
    {
        var (vm, backend) = await LoadedAsync(Listing("A"), Listing("B"));
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        backend.Handler = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new UninstallExecutionResult(0);
        };
        Row(vm, "A").IsSelected = true;
        vm.Confirm = (_, _, _) => true;

        var run = vm.UninstallSelectedCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Patience);
        vm.SelectAllCommand.Execute(null);

        Assert.False(vm.UninstallSelectedCommand.CanExecute(null));
        Assert.False(Row(vm, "B").IsSelected);
        release.SetResult();
        await run.WaitAsync(Patience);
    }

    [Fact]
    public void The_listing_rating_plans_and_rates_every_entry()
    {
        var entries = new[]
        {
            new InstalledProgramEntry { Key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Own", Name = "Own" },
            new InstalledProgramEntry { Key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Plain", Name = "Plain", Publisher = "Acme" },
        };

        var listings = UninstallerBackend.Rate(entries, new NoFilesProbe(), @"C:\Windows");

        Assert.Equal(2, listings.Count);
        Assert.Equal(entries[0], listings[0].Entry);
        Assert.All(listings, listing => Assert.NotNull(listing.Risk));
    }

    private sealed class NoFilesProbe : IUninstallFileProbe
    {
        public bool FileExists(string path) => false;

        public byte[]? ReadHead(string path, int count) => null;

        public bool IsTrustedForElevation(string path) => false;
    }
}
