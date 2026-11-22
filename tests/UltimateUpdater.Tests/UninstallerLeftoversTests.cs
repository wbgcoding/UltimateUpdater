using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>The uninstaller's end of a run: every row state, the summary line, the leftover scan and removal, and the busy rules.</summary>
[Collection("UiLanguage")]
public class UninstallerLeftoversTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static LeftoverItem Item(string path, bool preselected = true, LeftoverKind kind = LeftoverKind.Folder, LeftoverLane lane = LeftoverLane.User) =>
        new(kind, lane, path, Path.GetFileName(path), preselected, 1024);

    private static Func<IReadOnlyList<InstalledProgramEntry>, IReadOnlyList<UninstallLeftoverGroup>> TwoItemsFor(params string[] names) =>
        removed => [.. removed.Where(entry => names.Contains(entry.Name)).Select(entry => new UninstallLeftoverGroup(
            entry, [Item($@"C:\Users\x\AppData\Roaming\{entry.Name}"), Item($@"HKCU\Software\{entry.Name}", preselected: false, LeftoverKind.RegistryKey)]))];

    private static async Task RunAsync(UninstallerViewModel vm)
    {
        vm.SelectAllCommand.Execute(null);
        vm.Confirm = (_, _, _) => true;
        await vm.UninstallSelectedCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Every_row_state_has_its_own_text_in_both_languages()
    {
        var (vm, _) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("A"));
        var row = vm.AllRows.Single();

        string Text(UninstallRowState state)
        {
            row.State = state;
            return row.StateText;
        }

        var states = Enum.GetValues<UninstallRowState>().Where(state => state != UninstallRowState.Idle).ToList();
        var english = states.Select(Text).ToList();
        LocalizationService.Instance.Apply("de");
        row.RefreshText();
        var german = states.Select(Text).ToList();
        LocalizationService.Instance.Apply("en");

        Assert.All(english, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(english.Count, english.Distinct().Count());
        Assert.Equal(german.Count, german.Distinct().Count());
        Assert.NotEqual(english, german);
    }

    [Fact]
    public async Task A_failed_row_shows_its_reason_and_the_summary_counts_the_outcomes()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(
            UninstallerViewModelTests.Listing("Good"), UninstallerViewModelTests.Listing("Bad"), UninstallerViewModelTests.Listing("Reboot"));
        backend.Handler = (entry, _) => Task.FromResult(entry.Name switch
        {
            "Bad" => new UninstallExecutionResult(1603),
            "Reboot" => new UninstallExecutionResult(3010),
            _ => new UninstallExecutionResult(0),
        });
        backend.Gone = entry => entry.Name != "Bad";

        await RunAsync(vm);

        Assert.False(string.IsNullOrEmpty(vm.AllRows.Single(row => row.Name == "Bad").Reason));
        Assert.True(vm.HasSummary);
        Assert.Equal("2 removed, 1 failed, 0 leftovers removed", vm.SummaryText);
    }

    [Fact]
    public async Task The_summary_is_hidden_before_a_run_and_german_in_german()
    {
        var (vm, _) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("A"));
        Assert.False(vm.HasSummary);

        await RunAsync(vm);
        LocalizationService.Instance.Apply("de");
        vm.RefreshText();

        Assert.Equal("1 entfernt, 0 fehlgeschlagen, 0 Reste entfernt", vm.SummaryText);
        LocalizationService.Instance.Apply("en");
    }

    [Fact]
    public async Task Only_removed_rows_are_scanned_for_leftovers()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(
            UninstallerViewModelTests.Listing("Good"), UninstallerViewModelTests.Listing("Bad"));
        backend.Handler = (entry, _) => Task.FromResult(new UninstallExecutionResult(entry.Name == "Bad" ? 1603 : 0));
        backend.Gone = entry => entry.Name != "Bad";
        IReadOnlyList<InstalledProgramEntry>? scanned = null;
        backend.Leftovers = removed =>
        {
            scanned ??= removed;
            return TwoItemsFor("Good", "Bad")(removed);
        };

        await RunAsync(vm);

        Assert.Equal(["Good"], scanned!.Select(entry => entry.Name));
        Assert.Equal(["Good"], vm.LeftoverGroups.Select(group => group.Name));
    }

    [Fact]
    public async Task A_run_without_a_removed_row_does_not_scan()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("Bad"));
        backend.Handler = (_, _) => Task.FromResult(new UninstallExecutionResult(1603));
        backend.Gone = _ => false;

        await RunAsync(vm);

        Assert.Equal(0, backend.ScanCalls);
        Assert.False(vm.HasLeftovers);
    }

    [Fact]
    public async Task A_stopped_run_scans_only_what_it_removed()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(
            UninstallerViewModelTests.Listing("First"), UninstallerViewModelTests.Listing("Second"));
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        backend.Handler = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new UninstallExecutionResult(0);
        };
        backend.Leftovers = TwoItemsFor("First", "Second");
        vm.SelectAllCommand.Execute(null);
        vm.Confirm = (_, _, _) => true;

        var run = vm.UninstallSelectedCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Patience);
        vm.StopCommand.Execute(null);
        release.SetResult();
        await run.WaitAsync(Patience);

        Assert.Single(vm.LeftoverGroups);
        Assert.Equal(1, vm.AllRows.Count(row => row.State == UninstallRowState.NotStarted));
    }

    [Fact]
    public async Task Only_a_certain_leftover_starts_ticked_and_the_count_follows_the_ticks()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("A"));
        backend.Leftovers = TwoItemsFor("A");

        await RunAsync(vm);

        var items = vm.LeftoverGroups.Single().Items;
        Assert.True(items[0].IsSelected);
        Assert.False(items[1].IsSelected);
        Assert.Equal(1, vm.SelectedLeftoverCount);
        Assert.True(vm.RemoveLeftoversCommand.CanExecute(null));

        items[0].IsSelected = false;

        Assert.Equal(0, vm.SelectedLeftoverCount);
        Assert.False(vm.RemoveLeftoversCommand.CanExecute(null));
    }

    [Fact]
    public async Task Remove_leftovers_removes_only_the_ticked_items_and_counts_them_in_the_summary()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("A"));
        var present = TwoItemsFor("A");
        var gone = new HashSet<string>();
        backend.Leftovers = removed => [.. present(removed)
            .Select(group => group with { Items = [.. group.Items.Where(item => !gone.Contains(item.Path))] })
            .Where(group => group.Items.Count > 0)];
        backend.RemoveLeftovers = groups =>
        {
            foreach (var item in groups.SelectMany(group => group.Items))
                gone.Add(item.Path);

            return Task.FromResult(new UninstallLeftoverResult(groups.Sum(group => group.Items.Count), 0, 2048));
        };
        await RunAsync(vm);

        await vm.RemoveLeftoversCommand.ExecuteAsync(null);

        var removedItems = Assert.Single(backend.RemovedGroups).SelectMany(group => group.Items).ToList();
        Assert.Equal([LeftoverKind.Folder], removedItems.Select(item => item.Kind));
        Assert.Equal(1, vm.LeftoversRemoved);
        Assert.Equal("1 removed, 0 failed, 1 leftovers removed", vm.SummaryText);
        var rest = Assert.Single(vm.LeftoverGroups).Items;
        Assert.Equal(LeftoverKind.RegistryKey, Assert.Single(rest).Item.Kind);
        Assert.False(rest[0].IsSelected);
        Assert.False(vm.CanRemoveLeftovers);
    }

    [Fact]
    public async Task A_rescan_keeps_an_item_the_user_unticked_unticked()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("A"));
        backend.Leftovers = removed =>
            [new UninstallLeftoverGroup(removed[0], [Item(@"C:\x\one"), Item(@"C:\x\two"), Item(@"C:\x\three")])];
        backend.RemoveLeftovers = groups => Task.FromResult(new UninstallLeftoverResult(0, groups.Sum(group => group.Items.Count), 0));
        await RunAsync(vm);
        vm.LeftoverGroups.Single().Items[1].IsSelected = false;

        await vm.RemoveLeftoversCommand.ExecuteAsync(null);

        var items = vm.LeftoverGroups.Single().Items;
        Assert.Equal([true, false, true], items.Select(item => item.IsSelected));
        Assert.Equal(2, Assert.Single(backend.RemovedGroups).Single().Items.Count);
    }

    [Fact]
    public async Task A_failed_scan_shows_an_empty_list_and_keeps_the_run_result()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("A"));
        backend.Leftovers = _ => throw new IOException("denied");

        await RunAsync(vm);

        Assert.False(vm.HasLeftovers);
        Assert.False(vm.IsScanningLeftovers);
        Assert.Equal(UninstallRowState.Removed, vm.AllRows.Single().State);
        Assert.Equal("1 removed, 0 failed, 0 leftovers removed", vm.SummaryText);
    }

    [Fact]
    public async Task Nothing_starts_while_leftovers_are_being_removed()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("A"), UninstallerViewModelTests.Listing("B"));
        var inRemoval = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        backend.Leftovers = TwoItemsFor("A");
        backend.RemoveLeftovers = async _ =>
        {
            inRemoval.TrySetResult();
            await release.Task;
            return new UninstallLeftoverResult(1, 0, 0);
        };
        vm.AllRows.Single(row => row.Name == "A").IsSelected = true;
        vm.Confirm = (_, _, _) => true;
        await vm.UninstallSelectedCommand.ExecuteAsync(null);
        vm.AllRows.Single(row => row.Name == "B").IsSelected = true;

        var removal = vm.RemoveLeftoversCommand.ExecuteAsync(null);
        await inRemoval.Task.WaitAsync(Patience);

        Assert.True(vm.IsBusy);
        Assert.False(vm.CanRemoveLeftovers);
        Assert.False(vm.CanUninstall);
        Assert.False(vm.UninstallSelectedCommand.CanExecute(null));
        release.SetResult();
        await removal.WaitAsync(Patience);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanUninstall);
    }

    [Fact]
    public async Task Closing_the_window_in_the_middle_of_a_run_neither_stops_it_nor_clears_the_busy_state()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("A"));
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        backend.Handler = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new UninstallExecutionResult(0);
        };
        vm.AllRows.Single().IsSelected = true;
        vm.Confirm = (_, _, _) => true;

        var run = vm.UninstallSelectedCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Patience);
        vm.Discard();

        Assert.True(vm.IsBusy);
        Assert.True(vm.IsRunning);
        release.SetResult();
        await run.WaitAsync(Patience);
        Assert.Equal(UninstallRowState.Removed, vm.AllRows.Single().State);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_new_run_clears_the_previous_leftovers_and_the_summary()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("A"), UninstallerViewModelTests.Listing("B"));
        backend.Leftovers = TwoItemsFor("A");
        vm.AllRows.Single(row => row.Name == "A").IsSelected = true;
        vm.Confirm = (_, _, _) => true;
        await vm.UninstallSelectedCommand.ExecuteAsync(null);
        Assert.True(vm.HasLeftovers);

        backend.Leftovers = _ => [];
        vm.AllRows.Single(row => row.Name == "B").IsSelected = true;
        await vm.UninstallSelectedCommand.ExecuteAsync(null);

        Assert.False(vm.HasLeftovers);
        Assert.Equal("1 removed, 0 failed, 0 leftovers removed", vm.SummaryText);
    }

    [Fact]
    public void The_machine_command_names_the_program_caps_the_picks_and_carries_the_claims()
    {
        var subject = new LeftoverSubject(@"HKLM\X", "Acme Tool", "Acme", @"C:\Program Files\Acme");
        var picks = Enumerable.Range(0, CleanupWorker.MaxSelectedLeftovers + 40)
            .Select(i => new LeftoverSelectionDto { Kind = LeftoverKind.Folder, Path = $@"C:\ProgramData\Acme\{i}" }).ToList();
        var claims = LeftoverClaims.FromRegistry(new FakeUninstallRegistry())
            .WithText(["othertool"], ["otherpublisher"], [@"C:\Program Files\Other"]);

        var command = UninstallLeftoverService.BuildMachineCommand(subject, picks, claims, new RegistryCleanupSettings());

        Assert.Equal("leftovers", command.Command);
        Assert.Equal(subject, command.LeftoverSubject);
        Assert.Equal(CleanupWorker.MaxSelectedLeftovers, command.SelectedLeftovers.Count);
        Assert.Contains("othertool", command.LeftoverClaimedNames);
        Assert.Contains("otherpublisher", command.LeftoverClaimedPublishers);
        Assert.Contains(@"C:\Program Files\Other", command.LeftoverClaimedPaths);
    }

    [Fact]
    public async Task A_missing_helper_leaves_the_machine_items_where_they_are()
    {
        var service = new UninstallLeftoverService(new LoggingService(), elevated: null);
        var subject = new LeftoverSubject(@"HKLM\X", "Acme Tool", "Acme", string.Empty);
        var picks = new List<LeftoverSelectionDto> { new() { Kind = LeftoverKind.Folder, Path = @"C:\ProgramData\Acme" } };

        var result = await service.RemoveMachineAsync(
            subject, picks, LeftoverClaims.FromRegistry(new FakeUninstallRegistry()), new RegistryCleanupSettings(), CancellationToken.None);

        Assert.Equal(new UninstallLeftoverResult(0, 1, 0), result);
    }
}
