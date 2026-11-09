using System.IO;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// What a cleanup reports must match what it did: a recycle-bin item that could not be removed leaves a log
/// line, the driver cleanup counts the space of the packages it really deleted, backups are pruned by the
/// moment in their name, and other users' Temp folders come from the machine's profile list.
/// </summary>
[Collection("DriverStaging")]
public class CleanupReportingFixesTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-1001";

    private readonly string _root = Directory.CreateTempSubdirectory("wu-reporting-").FullName;

    public CleanupReportingFixesTests()
    {
        DriverBackupStaging.ProgramDataRootOverride = Path.Combine(_root, "programdata");
        Directory.CreateDirectory(DriverBackupStaging.ProgramDataRootOverride);
    }

    public void Dispose()
    {
        DriverBackupStaging.ProgramDataRootOverride = null;
        DriverBackupStaging.StagingNameOverride = null;
        DriverCleanupExecutor.PnputilForTest.Value = null;
        CleanupCatalog.ProfileListForTest.Value = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // a: a recycle-bin item that cannot be removed is logged

    [Fact]
    public void A_recycled_item_held_open_is_not_removed_and_the_failure_is_logged()
    {
        var lines = new List<string>();
        var service = new CleanupService(new LoggingService(lines.Add));
        var bin = Path.Combine(_root, "bin", Sid);
        Directory.CreateDirectory(bin);
        var content = Path.Combine(bin, "$R1.txt");
        File.WriteAllText(content, "x");
        File.WriteAllText(Path.Combine(bin, "$I1.txt"), "x");
        var entry = new CleanupEntry(
            content, "item", 1, 1, DateTimeOffset.Now, DateTimeOffset.Now, CleanupEntryType.Other, @"C:\original\held.txt");

        using (new FileStream(content, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(service.DeleteRecycledItem(entry, Sid, (_, _) => true));
        }

        Assert.True(File.Exists(content));
        Assert.Contains(lines, l => l.Contains(@"C:\original\held.txt") && l.Contains("could not be removed"));
    }

    // b: freed bytes of the driver cleanup

    private sealed class FakeInventory(IReadOnlyList<DriverDeviceInfo> devices, IReadOnlyList<DriverPackageInfo> packages)
        : IDriverInventoryProvider
    {
        public IReadOnlyList<DriverDeviceInfo> GatherDevices(CancellationToken cancellationToken) => devices;

        public Func<string, bool>? LastMeasure { get; private set; }

        public IReadOnlyList<DriverPackageInfo> GatherPackages(CancellationToken cancellationToken, Func<string, bool>? measure = null)
        {
            LastMeasure = measure;
            return packages;
        }
    }

    private static DriverDeviceInfo OldDevice(string id, string inf) => new(
        id, "Old gadget " + id, "Net", Present: false, @"C:\Windows\INF\" + inf, "1.0", null,
        LastArrivalDate: null, LastRemovalDate: DateTimeOffset.UtcNow.AddDays(-400));

    private static DriverPackageInfo Package(string name, long size) =>
        new(name, "Acme", "Net", "1.0", null, "gadget.inf", size);

    private static async Task<CleanupOutcome> RunDrivers(
        IDriverInventoryProvider inventory, string[] devices, string[] packages, Func<IReadOnlyList<string>, int> pnputil)
    {
        DriverCleanupExecutor.PnputilForTest.Value = args => Task.FromResult(pnputil(args));
        return await DriverCleanupExecutor.CleanAsync(inventory, devices, packages, new LoggingService(), CancellationToken.None);
    }

    [Fact]
    public async Task A_deleted_driver_package_adds_its_size_to_the_freed_bytes()
    {
        var inventory = new FakeInventory([], [Package("oem20.inf", 5000)]);

        var outcome = await RunDrivers(inventory, [], ["oem20.inf"], _ => 0);

        Assert.Equal(1, outcome.DeletedFiles);
        Assert.Equal(5000, outcome.FreedBytes);
    }

    [Fact]
    public async Task A_driver_package_that_could_not_be_deleted_frees_nothing()
    {
        var inventory = new FakeInventory([], [Package("oem20.inf", 5000)]);

        var outcome = await RunDrivers(inventory, [], ["oem20.inf"], args => args[0] == "/delete-driver" ? 5 : 0);

        Assert.Equal(0, outcome.DeletedFiles);
        Assert.Equal(1, outcome.SkippedFiles);
        Assert.Equal(0, outcome.FreedBytes);
    }

    [Fact]
    public async Task A_package_shared_by_two_removed_devices_is_counted_once()
    {
        var inventory = new FakeInventory(
            [OldDevice("ROOT\\GADGET\\0001", "oem20.inf"), OldDevice("ROOT\\GADGET\\0002", "oem20.inf")],
            [Package("oem20.inf", 7000)]);

        var outcome = await RunDrivers(
            inventory, ["ROOT\\GADGET\\0001", "ROOT\\GADGET\\0002"], [], _ => 0);

        Assert.Equal(2, outcome.DeletedFiles);
        Assert.Equal(7000, outcome.FreedBytes);
    }

    [Fact]
    public async Task A_removed_device_whose_package_stays_in_use_frees_nothing()
    {
        var inventory = new FakeInventory(
            [OldDevice("ROOT\\GADGET\\0001", "oem20.inf")],
            [Package("oem20.inf", 7000)]);

        var outcome = await RunDrivers(
            inventory, ["ROOT\\GADGET\\0001"], [], args => args[0] == "/delete-driver" ? 5 : 0);

        Assert.Equal(1, outcome.DeletedFiles);
        Assert.Equal(0, outcome.FreedBytes);
    }

    [Fact]
    public async Task The_clean_pass_measures_only_the_selected_packages_and_those_of_selected_devices()
    {
        var inventory = new FakeInventory(
            [OldDevice("ROOT\\GADGET\\0001", "oem20.inf"), OldDevice("ROOT\\GADGET\\0002", "oem21.inf")],
            [Package("oem20.inf", 1), Package("oem21.inf", 1), Package("oem22.inf", 1), Package("oem23.inf", 1)]);

        await RunDrivers(inventory, ["ROOT\\GADGET\\0001"], ["oem22.inf"], _ => 0);

        var measure = inventory.LastMeasure;
        Assert.NotNull(measure);
        Assert.True(measure!("oem20.inf"));
        Assert.True(measure("OEM22.INF"));
        Assert.False(measure("oem21.inf"));
        Assert.False(measure("oem23.inf"));
    }

    // c: backups are pruned by the moment in their name

    [Theory]
    [InlineData("Ultimate Updater Registry Backup 05.03.2026 14-30.reg", 2026, 3, 5, 14, 30, 0)]
    [InlineData("Ultimate Updater Registry Backup 05.03.2026 14-30 (2).reg", 2026, 3, 5, 14, 30, 2)]
    [InlineData("Ultimate Updater Registry Backup 05.03.2026.reg", 2026, 3, 5, 0, 0, 0)]
    public void The_moment_of_a_backup_is_read_from_its_name(string name, int y, int mo, int d, int h, int mi, int suffix)
    {
        var moment = RegistryBackupService.BackupMoment(name);

        Assert.Equal((new DateTime(y, mo, d, h, mi, 0), suffix), moment);
    }

    [Theory]
    [InlineData("backup.reg")]
    [InlineData("Ultimate Updater Registry Backup 31.02.2026 10-00.reg")]
    [InlineData("Ultimate Updater Registry Backup 05.03.2026 14-30.txt")]
    public void A_name_that_is_not_a_backup_has_no_moment(string name)
    {
        Assert.Null(RegistryBackupService.BackupMoment(name));
    }

    [Fact]
    public void Pruning_keeps_the_newest_backups_by_name_whatever_the_file_times_say()
    {
        var folder = Path.Combine(_root, "backups");
        Directory.CreateDirectory(folder);
        var names = new[]
        {
            "Ultimate Updater Registry Backup 01.01.2026 10-00.reg",
            "Ultimate Updater Registry Backup 02.01.2026 10-00.reg",
            "Ultimate Updater Registry Backup 03.01.2026 10-00.reg",
            "Ultimate Updater Registry Backup 03.01.2026 10-00 (2).reg",
        };

        // the oldest name gets the newest file time: only the name decides
        for (var i = 0; i < names.Length; i++)
        {
            var path = Path.Combine(folder, names[i]);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-i));
        }

        RegistryBackupService.PruneOldBackups(folder, 2, new LoggingService());

        var left = Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { names[2], names[3] }.Order(StringComparer.Ordinal), left);
    }

    // e: other users' Temp folders come from the profile list

    [Fact]
    public void Only_real_account_profiles_are_taken_from_the_profile_list()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var drive = Path.GetPathRoot(windows)!.TrimEnd('\\');

        var folders = CleanupCatalog.ProfileFolders(
        [
            new("S-1-5-21-1-2-3-1001", @"D:\Profiles\anna"),
            new("S-1-5-21-1-2-3-1002", @"%SystemDrive%\Users\bob"),
            new("S-1-5-18", @"%systemroot%\system32\config\systemprofile"),
            new("S-1-5-80-1", @"C:\Windows\ServiceProfiles\X"),
            new("S-1-5-21-1-2-3-1003", @"%USERPROFILE%\carl"),
            new("S-1-5-21-1-2-3-1004", @"\\server\share\dora"),
            new("S-1-5-21-1-2-3-1005", @"relative\erin"),
            new("S-1-5-21-1-2-3-1006", null),
        ]);

        Assert.Equal([@"D:\Profiles\anna", drive + @"\Users\bob"], folders);
    }

    [Fact]
    public void The_other_users_Temp_list_follows_a_profile_outside_the_Users_folder()
    {
        var anna = Path.Combine(_root, "elsewhere", "anna");
        var annaTemp = Path.Combine(anna, "AppData", "Local", "Temp");
        Directory.CreateDirectory(annaTemp);
        var system = Path.Combine(_root, "system");
        Directory.CreateDirectory(Path.Combine(system, "AppData", "Local", "Temp"));
        CleanupCatalog.ProfileListForTest.Value = () =>
        [
            new("S-1-5-21-1-2-3-1001", anna),
            new("S-1-5-18", system),
        ];

        var catalog = CleanupCatalog.BuildFor(null, [], usersRoot: null);

        var directories = catalog.SelectMany(t => t.Directories).ToList();
        Assert.Contains(annaTemp, directories, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(directories, d => d.StartsWith(system, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_profile_reached_through_a_link_is_left_out_of_the_other_users_Temp_list()
    {
        var real = Path.Combine(_root, "real", "anna");
        Directory.CreateDirectory(Path.Combine(real, "AppData", "Local", "Temp"));
        var link = Path.Combine(_root, "linked");
        CreateJunction(link, Path.Combine(_root, "real"));
        var viaLink = Path.Combine(link, "anna");
        CleanupCatalog.ProfileListForTest.Value = () => [new("S-1-5-21-1-2-3-1001", viaLink)];
        var warnings = new List<string>();

        var catalog = CleanupCatalog.BuildFor(warnings.Add, [], usersRoot: null);

        var directories = catalog.SelectMany(t => t.Directories).ToList();
        Assert.DoesNotContain(directories, d => d.StartsWith(_root, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, w => w.Contains(viaLink, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_profile_in_a_system_folder_or_at_a_drive_root_is_not_a_usable_profile()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Assert.False(CleanupCatalog.IsUsableProfileFolder(Path.Combine(windows, "anna")));
        Assert.False(CleanupCatalog.IsUsableProfileFolder(Path.Combine(programData, "anna")));
        Assert.False(CleanupCatalog.IsUsableProfileFolder(Path.GetPathRoot(windows)!));
        Assert.False(CleanupCatalog.IsUsableProfileFolder(@"\\server\share\anna"));
        Assert.True(CleanupCatalog.IsUsableProfileFolder(Path.Combine(_root, "anna")));
    }

    private static void CreateJunction(string link, string target)
    {
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        Assert.True(mklink.WaitForExit(60_000), "mklink did not finish");
        Assert.Equal(0, mklink.ExitCode);
    }
}
