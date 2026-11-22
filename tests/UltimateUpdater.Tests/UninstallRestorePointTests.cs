using System.IO;
using System.Text;
using System.Text.Json;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Install;
using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>The restore point before an uninstall run: when the setting asks for one, the 24 hour reuse, the answer to a failure and the worker's side.</summary>
[Collection("UiLanguage")]
public class UninstallRestorePointTests
{
    [Theory]
    [InlineData(UninstallBackupMode.Automatic, true, false, true)]
    [InlineData(UninstallBackupMode.Automatic, false, true, true)]
    [InlineData(UninstallBackupMode.Automatic, true, true, true)]
    [InlineData(UninstallBackupMode.Automatic, false, false, false)]
    [InlineData(UninstallBackupMode.Always, false, false, true)]
    [InlineData(UninstallBackupMode.Always, true, true, true)]
    [InlineData(UninstallBackupMode.Off, true, true, false)]
    [InlineData(UninstallBackupMode.Off, false, false, false)]
    public void The_policy_follows_the_mode_the_elevated_batch_and_the_risk(
        UninstallBackupMode mode, bool anyElevated, bool anyRisky, bool expected)
    {
        Assert.Equal(expected, UninstallRestorePointPolicy.Wanted(mode, anyElevated, anyRisky));
    }

    [Fact]
    public void The_setting_defaults_to_automatic_and_an_unknown_value_falls_back_to_it()
    {
        Assert.Equal(UninstallBackupMode.Automatic, new AppSettings().Install.UninstallBackup);

        var path = Path.Combine(Path.GetTempPath(), "uu-uninstall-backup-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"Install":{"UninstallBackup":42}}""");
            var settings = new SettingsService(new LoggingService(), path);
            Assert.Equal(UninstallBackupMode.Automatic, settings.Current.Install.UninstallBackup);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static UninstallRestorePointService Service(
        Func<DateTimeOffset?> created, Func<DateTimeOffset?> readLast, Action<DateTimeOffset> writeLast,
        DateTimeOffset now, RestorePointReservation? reservation = null, Action? onCreate = null) =>
        new(
            new LoggingService(),
            _ =>
            {
                onCreate?.Invoke();
                return Task.FromResult(created());
            },
            readLast, writeLast, reservation ?? new RestorePointReservation(), () => now);

    [Fact]
    public async Task A_new_restore_point_is_made_and_its_time_written()
    {
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? written = null;
        var service = Service(() => now, () => null, at => written = at, now);

        var result = await service.EnsureAsync(CancellationToken.None);

        Assert.Equal(UninstallRestorePointOutcome.Created, result.Outcome);
        Assert.Equal(now, written);
    }

    [Fact]
    public async Task A_restore_point_from_the_last_24_hours_is_reused_and_nothing_is_made()
    {
        var now = DateTimeOffset.UtcNow;
        var earlier = now - TimeSpan.FromHours(23);
        var made = 0;
        DateTimeOffset? written = null;
        var service = Service(() => now, () => earlier, at => written = at, now, onCreate: () => made++);

        var result = await service.EnsureAsync(CancellationToken.None);

        Assert.Equal(UninstallRestorePointOutcome.Reused, result.Outcome);
        Assert.Equal(earlier, result.At);
        Assert.Equal(0, made);
        Assert.Null(written);
    }

    [Fact]
    public async Task A_restore_point_older_than_24_hours_is_replaced()
    {
        var now = DateTimeOffset.UtcNow;
        var made = 0;
        var service = Service(() => now, () => now - TimeSpan.FromHours(25), _ => { }, now, onCreate: () => made++);

        var result = await service.EnsureAsync(CancellationToken.None);

        Assert.Equal(UninstallRestorePointOutcome.Created, result.Outcome);
        Assert.Equal(1, made);
    }

    [Fact]
    public async Task A_failed_restore_point_writes_nothing_and_is_reported()
    {
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? written = null;
        var service = Service(() => null, () => null, at => written = at, now);

        var result = await service.EnsureAsync(CancellationToken.None);

        Assert.Equal(UninstallRestorePointOutcome.Failed, result.Outcome);
        Assert.Null(written);
    }

    [Fact]
    public async Task A_restore_point_still_being_made_by_another_batch_counts_as_reused()
    {
        var now = DateTimeOffset.UtcNow;
        var reservation = new RestorePointReservation();
        using var other = reservation.TryReserve(() => null, now);
        var made = 0;
        var service = Service(() => now, () => null, _ => { }, now, reservation, () => made++);

        var result = await service.EnsureAsync(CancellationToken.None);

        Assert.Equal(UninstallRestorePointOutcome.Reused, result.Outcome);
        Assert.Equal(0, made);
    }

    private static UninstallListing NonElevatedListing(string name, CleanupRiskLevel level = CleanupRiskLevel.Safe) =>
        new(
            new InstalledProgramEntry { Key = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + name, Name = name },
            new UninstallPlan { Method = UninstallMethod.QuietString, Executable = @"C:\Users\x\u.exe", Elevated = false },
            new UninstallRisk(level, UninstallRiskReason.Ordinary, true));

    [Fact]
    public async Task Automatic_without_an_elevated_row_makes_no_restore_point_and_with_one_makes_it_first()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(NonElevatedListing("Mine"), UninstallerViewModelTests.Listing("System wide"));
        backend.BackupMode = UninstallBackupMode.Automatic;
        vm.Confirm = (_, _, _) => true;
        vm.AllRows.Single(row => row.Name == "Mine").IsSelected = true;
        await vm.UninstallSelectedCommand.ExecuteAsync(null);
        Assert.Equal(0, backend.RestorePointCalls);

        var states = new List<UninstallRowState>();
        backend.DuringRestorePoint = () => states.AddRange(vm.AllRows.Select(row => row.State));
        vm.AllRows.Single(row => row.Name == "System wide").IsSelected = true;
        await vm.UninstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, backend.RestorePointCalls);
        Assert.DoesNotContain(UninstallRowState.Removed, states.Where((_, index) => vm.AllRows[index % vm.AllRows.Count].Name == "System wide"));
    }

    [Fact]
    public async Task Automatic_with_a_risky_row_but_no_elevated_batch_makes_a_restore_point()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(NonElevatedListing("Risky one", CleanupRiskLevel.Risky));
        backend.BackupMode = UninstallBackupMode.Automatic;
        vm.Confirm = (_, _, _) => true;
        vm.AllRows.Single().IsSelected = true;

        await vm.UninstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, backend.RestorePointCalls);
    }

    [Fact]
    public async Task Always_makes_a_restore_point_even_without_an_elevated_row_and_off_never()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(NonElevatedListing("Mine"));
        vm.Confirm = (_, _, _) => true;
        backend.BackupMode = UninstallBackupMode.Always;
        vm.AllRows.Single().IsSelected = true;
        await vm.UninstallSelectedCommand.ExecuteAsync(null);
        Assert.Equal(1, backend.RestorePointCalls);

        backend.BackupMode = UninstallBackupMode.Off;
        vm.AllRows.Single().IsSelected = true;
        await vm.UninstallSelectedCommand.ExecuteAsync(null);
        Assert.Equal(1, backend.RestorePointCalls);
    }

    [Fact]
    public async Task A_reused_restore_point_is_said_in_the_window()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("Prog"));
        backend.BackupMode = UninstallBackupMode.Always;
        var at = new DateTimeOffset(2026, 3, 2, 14, 30, 0, TimeSpan.Zero);
        backend.RestorePoint = () => new UninstallRestorePointResult(UninstallRestorePointOutcome.Reused, at);
        vm.Confirm = (_, _, _) => true;
        vm.SelectAllCommand.Execute(null);

        await vm.UninstallSelectedCommand.ExecuteAsync(null);

        Assert.True(vm.HasRestorePointText);
        Assert.Contains(at.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentUICulture), vm.RestorePointText);
        Assert.StartsWith("Using the restore point from", vm.RestorePointText);
    }

    [Fact]
    public async Task A_failed_restore_point_that_is_declined_touches_no_item_and_leaves_no_summary()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("Prog"));
        backend.BackupMode = UninstallBackupMode.Always;
        backend.RestorePoint = () => new UninstallRestorePointResult(UninstallRestorePointOutcome.Failed);
        vm.Confirm = (_, _, _) => true;
        vm.ConfirmWithoutRestorePoint = () => false;
        vm.SelectAllCommand.Execute(null);

        await vm.UninstallSelectedCommand.ExecuteAsync(null);

        Assert.Empty(backend.Started);
        Assert.Empty(backend.Recorded);
        Assert.All(vm.AllRows, row => Assert.Equal(UninstallRowState.Idle, row.State));
        Assert.False(vm.IsRunning);
        Assert.False(vm.HasRestorePointText);
    }

    [Fact]
    public async Task A_failed_restore_point_that_is_accepted_goes_on_and_records_the_removal()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(UninstallerViewModelTests.Listing("Prog"));
        backend.BackupMode = UninstallBackupMode.Always;
        backend.RestorePoint = () => new UninstallRestorePointResult(UninstallRestorePointOutcome.Failed);
        vm.Confirm = (_, _, _) => true;
        vm.ConfirmWithoutRestorePoint = () => true;
        vm.SelectAllCommand.Execute(null);

        await vm.UninstallSelectedCommand.ExecuteAsync(null);

        Assert.Single(backend.Started);
        Assert.Equal(UninstallRowState.Removed, vm.AllRows.Single().State);
        Assert.Equal("Prog", backend.Recorded.Single().Name);
    }

    [Fact]
    public async Task Only_removed_entries_are_recorded()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(
            UninstallerViewModelTests.Listing("Good"), UninstallerViewModelTests.Listing("Bad"));
        backend.Handler = (entry, _) => Task.FromResult(new UninstallExecutionResult(entry.Name == "Bad" ? 1603 : 0));
        backend.Gone = entry => entry.Name != "Bad";
        vm.Confirm = (_, _, _) => true;
        vm.SelectAllCommand.Execute(null);

        await vm.UninstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(["Good"], backend.Recorded.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void The_history_line_of_a_removed_program_is_an_uninstall_entry()
    {
        var entry = new InstalledProgramEntry
        {
            Key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foo", Name = "Foo", Version = "2.1", SizeBytes = 1234,
        };

        var line = UninstallerBackend.HistoryForRemoved(entry);

        Assert.Equal("uninstall", line.Action);
        Assert.Equal("uninstaller", line.SourceId);
        Assert.Equal("Foo", line.Name);
        Assert.Equal("2.1", line.FromVersion);
        Assert.Equal(entry.Key, line.ItemId);
        Assert.True(line.Success);
        Assert.Equal(1234, line.SizeBytes);
        Assert.Null(UninstallerBackend.HistoryForRemoved(new InstalledProgramEntry { Key = "k", Name = "n" }).FromVersion);
    }

    private static async Task<List<string>> RunWorkerJobAsync(UninstallProgramJob job, Func<string, LoggingService, bool> create)
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true })
        {
            var payload = JsonSerializer.SerializeToElement(job, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await ProgramUninstallWorker.RunJobAsync(payload, writer, "job1", new LoggingService(), CancellationToken.None, create);
        }

        var text = Encoding.UTF8.GetString(stream.ToArray());
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("payload").GetProperty("type").GetString() ?? string.Empty)
            .ToList();
    }

    [Fact]
    public async Task The_worker_job_without_an_entry_makes_only_the_restore_point()
    {
        var made = 0;
        var types = await RunWorkerJobAsync(
            new UninstallProgramJob { CreateRestorePoint = true, StopIfRestorePointFails = true },
            (_, _) =>
            {
                made++;
                return true;
            });

        Assert.Equal(1, made);
        Assert.Equal(["restorePointCreated", "done"], types);
    }

    [Fact]
    public async Task The_worker_stops_before_touching_the_entry_when_the_restore_point_fails()
    {
        var types = await RunWorkerJobAsync(
            new UninstallProgramJob { CreateRestorePoint = true, StopIfRestorePointFails = true, Key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foo", Method = "QuietString" },
            (_, _) => false);

        Assert.Equal(["restorePointFailed", "done"], types);
    }

    [Fact]
    public async Task The_worker_treats_a_throwing_restore_point_as_a_failure()
    {
        var types = await RunWorkerJobAsync(
            new UninstallProgramJob { CreateRestorePoint = true, StopIfRestorePointFails = true },
            (_, _) => throw new InvalidOperationException("WMI damaged"));

        Assert.Equal(["restorePointFailed", "done"], types);
    }
}
