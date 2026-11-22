using UltimateUpdater.Models;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>"Uninstall" on a row whose source has no removal of its own: offered only when exactly one installed program fits, never guessed, and the removal runs through the uninstaller.</summary>
public partial class MainViewModelTests
{
    private static UpdateItem GitHubItem(string name) => Item($"github:acme/{name.ToLowerInvariant()}", name, "github-release");

    private static (MainViewModel ViewModel, UninstallerViewModelTests.FakeBackend Backend) CreateMapped(
        IReadOnlyList<IUpdateSource>? sources, params UninstallListing[] listings)
    {
        var backend = new UninstallerViewModelTests.FakeBackend(listings);
        var viewModel = Create(sources ?? [new OneShotSource("github-release")]);
        viewModel.UninstallerBackendOverride = () => backend;
        viewModel.UninstallConfirmOverride = () => true;
        return (viewModel, backend);
    }

    private static UpdateRowViewModel AddRow(MainViewModel viewModel, UpdateItem item)
    {
        var row = new UpdateRowViewModel(item, actions: viewModel);
        viewModel.Updates.Add(row);
        return row;
    }

    [Fact]
    public void A_github_release_row_offers_uninstall_once_it_maps_to_exactly_one_installed_program()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, _) = CreateMapped(null, UninstallerViewModelTests.Listing("Fancy Tool"));
            var row = AddRow(viewModel, GitHubItem("Fancy Tool"));

            // Nothing is read while the table is built: the answer is "no" until the menu asked.
            Assert.False(row.CanUninstall);

            RunPumped(viewModel.LoadUninstallMappingAsync(row));

            Assert.True(row.CanUninstall);
            Assert.True(row.UninstallCommand.CanExecute(null));
        });
    }

    [Fact]
    public void An_ambiguous_mapping_with_two_installed_entries_offers_no_uninstall()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, _) = CreateMapped(
                null,
                UninstallerViewModelTests.Listing("Fancy Tool", version: "1.0"),
                UninstallerViewModelTests.Listing("fancy  tool", version: "2.0"));
            var row = AddRow(viewModel, GitHubItem("Fancy Tool"));

            RunPumped(viewModel.LoadUninstallMappingAsync(row));

            Assert.False(row.CanUninstall);
        });
    }

    [Fact]
    public void Windows_update_and_nvidia_driver_rows_never_get_uninstall_even_with_an_installed_entry_of_the_same_name()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, backend) = CreateMapped(
                [new OneShotSource("windowsupdate"), new OneShotSource("nvidia-driver")],
                UninstallerViewModelTests.Listing("Same Name"),
                UninstallerViewModelTests.Listing("Same Name", kind: UninstallEntryKind.Driver));
            var windowsUpdate = AddRow(viewModel, Item("kb1", "Same Name", "windowsupdate"));
            var nvidia = AddRow(viewModel, Item("nvidia", "Same Name", "nvidia-driver"));

            RunPumped(viewModel.LoadUninstallMappingAsync(windowsUpdate));
            RunPumped(viewModel.LoadUninstallMappingAsync(nvidia));

            Assert.False(windowsUpdate.CanUninstall);
            Assert.False(nvidia.CanUninstall);
            Assert.Equal(0, backend.ListCalls);
        });
    }

    [Fact]
    public void A_game_row_below_a_launcher_gets_no_uninstall_but_the_launcher_client_row_may()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, _) = CreateMapped([new OneShotSource("epic")], UninstallerViewModelTests.Listing("Epic Games Launcher"));
            var game = AddRow(viewModel, Item("some-game", "Epic Games Launcher", "epic"));
            var client = AddRow(viewModel, Item("epic", "Epic Games Launcher", "epic"));

            RunPumped(viewModel.LoadUninstallMappingAsync(client));

            Assert.False(game.CanUninstall);
            Assert.True(client.CanUninstall);
        });
    }

    [Fact]
    public void A_row_of_a_source_with_its_own_uninstall_keeps_it_and_never_reads_the_installed_programs()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, source) = CreateGated();
            var backend = new UninstallerViewModelTests.FakeBackend();
            viewModel.UninstallerBackendOverride = () => backend;
            var row = AddRow(viewModel, Item("own.app", "own.app", HoldSource.SourceId));

            ((IRowActions)viewModel).LoadUninstallMapping(row);
            Pump(Task.Delay(100));

            Assert.True(row.CanUninstall);
            Assert.Equal(0, backend.ListCalls);
            Assert.Equal(0, source.Calls("uninstall", "own.app"));
        });
    }

    [Fact]
    public void The_installed_programs_are_read_once_per_scan()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, backend) = CreateMapped(null, UninstallerViewModelTests.Listing("First"), UninstallerViewModelTests.Listing("Second"));
            var first = AddRow(viewModel, GitHubItem("First"));
            var second = AddRow(viewModel, GitHubItem("Second"));

            RunPumped(viewModel.LoadUninstallMappingAsync(first));
            RunPumped(viewModel.LoadUninstallMappingAsync(second));
            RunPumped(viewModel.LoadUninstallMappingAsync(first));

            Assert.Equal(1, backend.ListCalls);
            Assert.True(first.CanUninstall);
            Assert.True(second.CanUninstall);

            // A scan that completes drops what was read: the next menu reads again.
            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            RunPumped(viewModel.LoadUninstallMappingAsync(second));

            Assert.Equal(2, backend.ListCalls);
        });
    }

    [Fact]
    public void A_mapped_removal_runs_the_uninstaller_marks_the_row_removed_and_records_it()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, backend) = CreateMapped(null, UninstallerViewModelTests.Listing("Fancy Tool"));
            var row = AddRow(viewModel, GitHubItem("Fancy Tool"));
            RunPumped(viewModel.LoadUninstallMappingAsync(row));

            RunPumped(viewModel.UninstallRowAsync(row));

            Assert.Equal(["Fancy Tool"], backend.Started);
            Assert.Equal(RowInstallState.Succeeded, row.InstallState);
            Assert.True(row.WasUninstalled);
            Assert.Single(backend.Recorded);
            Assert.False(viewModel.WorkIsRunning);
        });
    }

    [Fact]
    public void A_declined_mapped_confirmation_removes_nothing()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, backend) = CreateMapped(null, UninstallerViewModelTests.Listing("Fancy Tool"));
            viewModel.UninstallConfirmOverride = () => false;
            var row = AddRow(viewModel, GitHubItem("Fancy Tool"));
            RunPumped(viewModel.LoadUninstallMappingAsync(row));

            RunPumped(viewModel.UninstallRowAsync(row));

            Assert.Empty(backend.Started);
            Assert.Equal(RowInstallState.Idle, row.InstallState);
        });
    }

    [Fact]
    public void A_failed_mapped_removal_leaves_a_failed_row_that_is_not_recorded()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, backend) = CreateMapped(null, UninstallerViewModelTests.Listing("Fancy Tool"));
            backend.Handler = (_, _) => Task.FromResult(new UninstallExecutionResult(1603));
            backend.Gone = _ => false;
            var row = AddRow(viewModel, GitHubItem("Fancy Tool"));
            RunPumped(viewModel.LoadUninstallMappingAsync(row));

            RunPumped(viewModel.UninstallRowAsync(row));

            Assert.Equal(RowInstallState.Failed, row.InstallState);
            Assert.Empty(backend.Recorded);
        });
    }

    [Fact]
    public void A_mapped_removal_makes_the_restore_point_the_backup_setting_asks_for()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, backend) = CreateMapped(null, UninstallerViewModelTests.Listing("Fancy Tool"));
            backend.BackupMode = UninstallBackupMode.Always;
            var row = AddRow(viewModel, GitHubItem("Fancy Tool"));
            RunPumped(viewModel.LoadUninstallMappingAsync(row));

            RunPumped(viewModel.UninstallRowAsync(row));

            Assert.Equal(1, backend.RestorePointCalls);
            Assert.Equal(RowInstallState.Succeeded, row.InstallState);
        });
    }

    [Fact]
    public void A_mapped_removal_stops_when_no_restore_point_can_be_made_and_the_user_declines()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, backend) = CreateMapped(null, UninstallerViewModelTests.Listing("Fancy Tool"));
            backend.BackupMode = UninstallBackupMode.Always;
            backend.RestorePoint = () => new UninstallRestorePointResult(UninstallRestorePointOutcome.Failed);
            viewModel.UninstallRestorePointFailedOverride = () => false;
            var row = AddRow(viewModel, GitHubItem("Fancy Tool"));
            RunPumped(viewModel.LoadUninstallMappingAsync(row));

            RunPumped(viewModel.UninstallRowAsync(row));

            Assert.Empty(backend.Started);
            Assert.NotEqual(RowInstallState.Succeeded, row.InstallState);
        });
    }
}
