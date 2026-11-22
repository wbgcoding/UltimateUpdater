using System.IO;
using System.Text.Json;
using UltimateUpdater.Services.App;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Elevation;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

public sealed class LeftoverTargetSafetyTests
{
    [Fact]
    public void A_plain_local_folder_below_a_root_is_safe()
    {
        using var tree = new LeftoverTree();
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");

        Assert.True(LeftoverTargetSafety.IsSafeFolder(folder, tree.Roots, out _));
    }

    [Fact]
    public void Drive_roots_the_windows_folder_and_the_roots_themselves_are_never_safe()
    {
        using var tree = new LeftoverTree();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        foreach (var path in new[] { Path.GetPathRoot(tree.Root)!, windows, Path.Combine(windows, "System32"), tree.ProgramFiles,
                     tree.AppData, tree.LocalAppData, tree.ProgramData, tree.Documents, tree.Root })
        {
            Assert.False(LeftoverTargetSafety.IsSafeFolder(path, tree.Roots, out var reason), path);
            Assert.NotEmpty(reason);
        }
    }

    [Fact]
    public void The_apps_own_folders_and_anything_holding_them_are_never_safe()
    {
        using var tree = new LeftoverTree();
        var own = tree.Folder("AppData", "Roaming", "SomeProgram", "own");
        var data = tree.Folder("Elsewhere", "UltimateUpdater");
        CleanupSafety.OwnPathsOverrideForTest.Value = [own];
        try
        {
            Assert.False(LeftoverTargetSafety.IsSafeFolder(own, tree.Roots, out _));
            Assert.False(LeftoverTargetSafety.IsSafeFolder(Path.GetDirectoryName(own), tree.Roots, out _));
        }
        finally
        {
            CleanupSafety.OwnPathsOverrideForTest.Value = null;
        }

        // A moved data folder lands in a folder with the app's name, wherever the user put it.
        Assert.False(LeftoverTargetSafety.IsSafeFolder(data, tree.Roots, out _));
    }

    [Fact]
    public void A_missing_empty_or_relative_folder_is_not_safe()
    {
        using var tree = new LeftoverTree();

        Assert.False(LeftoverTargetSafety.IsSafeFolder(Path.Combine(tree.AppData, "Nope Nothing"), tree.Roots, out _));
        Assert.False(LeftoverTargetSafety.IsSafeFolder(string.Empty, tree.Roots, out _));
        Assert.False(LeftoverTargetSafety.IsSafeFolder(null, tree.Roots, out _));
        Assert.False(LeftoverTargetSafety.IsSafeFolder(@"AppData\Contoso Notes", tree.Roots, out _));
    }

    [Fact]
    public void A_folder_behind_a_junction_is_not_safe()
    {
        using var tree = new LeftoverTree();
        var target = tree.Folder("Elsewhere", "Contoso Notes");
        var link = Path.Combine(tree.AppData, "Linked");
        LeftoverTestLinks.CreateJunction(link, target);
        try
        {
            Assert.False(LeftoverTargetSafety.IsSafeFolder(link, tree.Roots, out _));
            Assert.False(LeftoverTargetSafety.IsSafeFolder(Path.Combine(link, "sub"), tree.Roots, out _));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Theory]
    [InlineData(@"HKCU\Software\Contoso\Contoso Notes", true)]
    [InlineData(@"HKLM\Software\Contoso\Contoso Notes", true)]
    [InlineData(@"HKLM\Software\WOW6432Node\Contoso\Contoso Notes", true)]
    [InlineData(@"HKCU\Software\Contoso", false)]
    [InlineData(@"HKCU\Software", false)]
    [InlineData(@"HKCU\Software\Contoso\Contoso Notes\Sub", false)]
    [InlineData(@"HKCU\Software\Microsoft\Contoso Notes", false)]
    [InlineData(@"HKCU\Software\Contoso\Windows", false)]
    [InlineData(@"HKLM\Software\WOW6432Node\Contoso", false)]
    [InlineData(@"HKLM\System\Contoso\Contoso Notes", false)]
    [InlineData(@"HKCR\Software\Contoso\Contoso Notes", false)]
    [InlineData(@"HKCU\Software\..\Contoso\Contoso Notes", false)]
    [InlineData(@"HKCU\Software\Contoso\..", false)]
    public void Only_a_program_key_below_a_publisher_key_below_software_is_safe(string keyPath, bool expected)
    {
        Assert.Equal(expected, LeftoverTargetSafety.IsSafeRegistryKeyPath(keyPath, out _));
    }

    [Fact]
    public void A_shortcut_must_be_an_existing_lnk_file_below_a_shortcut_folder()
    {
        using var tree = new LeftoverTree();
        var good = tree.Shortcut(tree.UserStartMenu, "Contoso Notes", null);
        var wrongType = Path.Combine(tree.UserStartMenu, "notes.txt");
        File.WriteAllText(wrongType, "x");
        var outside = tree.Shortcut(tree.Documents, "Contoso Notes", null);

        Assert.True(LeftoverTargetSafety.IsSafeShortcut(good, tree.Roots, out _));
        Assert.False(LeftoverTargetSafety.IsSafeShortcut(wrongType, tree.Roots, out _));
        Assert.False(LeftoverTargetSafety.IsSafeShortcut(outside, tree.Roots, out _));
        Assert.False(LeftoverTargetSafety.IsSafeShortcut(Path.Combine(tree.UserStartMenu, "gone.lnk"), tree.Roots, out _));
        Assert.False(LeftoverTargetSafety.IsSafeShortcut(tree.UserStartMenu, tree.Roots, out _));
    }
}

public sealed class LeftoverRemoverTests
{
    private const string BackupFolder = @"C:\unused-backups";

    private static readonly LeftoverSubject Notes = new(LeftoverTree.RemovedKey, "Contoso Notes", "Contoso Ltd.", string.Empty);

    private sealed class Recorder
    {
        internal List<string> Recycled { get; } = [];

        internal List<string> KeysDeleted { get; } = [];

        internal List<IReadOnlyList<string>> Backups { get; } = [];

        internal bool BackupSucceeds { get; set; } = true;

        internal LeftoverRemover Remover(LeftoverTree tree) => new(
            tree.Scanner(), tree.Roots, new LoggingService(),
            recycle: path =>
            {
                Recycled.Add(path);
                return true;
            },
            backup: (keys, _, _, _) =>
            {
                Backups.Add(keys);
                return Task.FromResult(BackupSucceeds ? new RegistryBackupService.RegistryBackupResult("backup.reg", keys) : null);
            },
            deleteKey: key =>
            {
                KeysDeleted.Add(key);
                return RegistryBackupService.KeyTreeDeleteStatus.Deleted;
            });
    }

    private static LeftoverSelectionDto Pick(LeftoverKind kind, string path) => new() { Kind = kind, Path = path };

    [Fact]
    public async Task A_ticked_folder_and_shortcut_go_to_the_recycle_bin_and_nothing_else_does()
    {
        using var tree = new LeftoverTree();
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");
        var other = tree.Folder("AppData", "Local", "Contoso Notes");
        var shortcut = tree.Shortcut(tree.UserStartMenu, "Contoso Notes", Path.Combine(tree.ProgramFiles, "x", "notes.exe"));
        var recorder = new Recorder();

        var result = await recorder.Remover(tree).RemoveAsync(
            Notes, tree.Claims(Notes.Key), LeftoverLane.User,
            [Pick(LeftoverKind.Folder, folder), Pick(LeftoverKind.Shortcut, shortcut)], BackupFolder, 3, CancellationToken.None);

        Assert.Equal(2, result.Removed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal([folder, shortcut], recorder.Recycled.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(other, recorder.Recycled);
        Assert.Empty(recorder.Backups);
    }

    [Fact]
    public async Task An_item_the_fresh_scan_does_not_find_is_skipped_never_removed()
    {
        using var tree = new LeftoverTree();
        var look = tree.Folder("AppData", "Roaming", "Contoso Notes Pro");
        var protectedFolder = tree.Documents;
        var invented = Path.Combine(tree.AppData, "Never Existed");
        var recorder = new Recorder();

        var result = await recorder.Remover(tree).RemoveAsync(
            Notes, tree.Claims(Notes.Key), LeftoverLane.User,
            [Pick(LeftoverKind.Folder, look), Pick(LeftoverKind.Folder, protectedFolder), Pick(LeftoverKind.Folder, invented),
             Pick(LeftoverKind.RegistryKey, @"HKCU\Software\Contoso")], BackupFolder, 3, CancellationToken.None);

        Assert.Equal(0, result.Removed);
        Assert.Equal(4, result.Skipped);
        Assert.Empty(recorder.Recycled);
        Assert.Empty(recorder.KeysDeleted);
    }

    [Fact]
    public async Task A_folder_a_still_installed_program_claims_after_the_scan_is_not_removed()
    {
        using var tree = new LeftoverTree();
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");
        var recorder = new Recorder();
        var remover = recorder.Remover(tree);
        tree.StillInstalled("other", "Other", "Contoso Ltd.", folder);

        var result = await remover.RemoveAsync(
            Notes, tree.Claims(Notes.Key), LeftoverLane.User, [Pick(LeftoverKind.Folder, folder)], BackupFolder, 3, CancellationToken.None);

        Assert.Equal(0, result.Removed);
        Assert.Empty(recorder.Recycled);
    }

    [Fact]
    public async Task Registry_keys_are_backed_up_first_and_only_then_removed()
    {
        using var tree = new LeftoverTree();
        tree.Registry.Set(@"HKCU\Software\Contoso\Contoso Notes", "Setting", "1");
        var recorder = new Recorder();

        var result = await recorder.Remover(tree).RemoveAsync(
            Notes, tree.Claims(Notes.Key), LeftoverLane.User,
            [Pick(LeftoverKind.RegistryKey, @"HKCU\Software\Contoso\Contoso Notes")], BackupFolder, 3, CancellationToken.None);

        Assert.Equal(1, result.Removed);
        Assert.Equal([@"HKCU\Software\Contoso\Contoso Notes"], recorder.Backups.Single());
        Assert.Equal([@"HKCU\Software\Contoso\Contoso Notes"], recorder.KeysDeleted);
    }

    [Fact]
    public async Task No_registry_backup_means_no_key_is_deleted()
    {
        using var tree = new LeftoverTree();
        tree.Registry.Set(@"HKCU\Software\Contoso\Contoso Notes", "Setting", "1");
        var recorder = new Recorder { BackupSucceeds = false };

        var result = await recorder.Remover(tree).RemoveAsync(
            Notes, tree.Claims(Notes.Key), LeftoverLane.User,
            [Pick(LeftoverKind.RegistryKey, @"HKCU\Software\Contoso\Contoso Notes")], BackupFolder, 3, CancellationToken.None);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(recorder.KeysDeleted);
    }

    [Fact]
    public async Task A_lane_removes_only_its_own_items()
    {
        using var tree = new LeftoverTree();
        var user = tree.Folder("AppData", "Roaming", "Contoso Notes");
        var machine = tree.Folder("Program Files", "Contoso", "Contoso Notes");
        var recorder = new Recorder();

        var result = await recorder.Remover(tree).RemoveAsync(
            Notes, tree.Claims(Notes.Key), LeftoverLane.Machine,
            [Pick(LeftoverKind.Folder, user), Pick(LeftoverKind.Folder, machine)], BackupFolder, 3, CancellationToken.None);

        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Skipped);
        Assert.Equal([machine], recorder.Recycled);
    }

    [Fact]
    public async Task A_failing_recycle_counts_as_skipped()
    {
        using var tree = new LeftoverTree();
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");
        var remover = new LeftoverRemover(tree.Scanner(), tree.Roots, new LoggingService(), recycle: _ => false);

        var result = await remover.RemoveAsync(
            Notes, tree.Claims(Notes.Key), LeftoverLane.User, [Pick(LeftoverKind.Folder, folder)], BackupFolder, 3, CancellationToken.None);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task An_empty_selection_touches_nothing()
    {
        using var tree = new LeftoverTree();
        tree.Folder("AppData", "Roaming", "Contoso Notes");
        var recorder = new Recorder();

        var result = await recorder.Remover(tree).RemoveAsync(
            Notes, tree.Claims(Notes.Key), LeftoverLane.User, [], BackupFolder, 3, CancellationToken.None);

        Assert.Equal(new LeftoverRemoval(0, 0, 0), result);
        Assert.Empty(recorder.Recycled);
    }

    [Fact]
    public void A_real_current_user_key_is_removed_with_its_subkeys_and_the_publisher_key_stays()
    {
        var publisher = "UUTestPub" + Guid.NewGuid().ToString("N")[..10];
        var program = "UUTestProgram" + Guid.NewGuid().ToString("N")[..10];
        var publisherPath = $@"Software\{publisher}";
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{publisherPath}\{program}\Sub"))
                key.SetValue("Setting", "1");
            using (var sibling = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{publisherPath}\Sibling"))
                sibling.SetValue("Setting", "1");

            var status = LeftoverRemover.DeleteKeyTree($@"HKCU\{publisherPath}\{program}", new LoggingService());

            Assert.Equal(RegistryBackupService.KeyTreeDeleteStatus.Deleted, status);
            using var left = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(publisherPath);
            Assert.NotNull(left);
            Assert.Equal(["Sibling"], left.GetSubKeyNames());
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(publisherPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void A_key_path_that_fails_the_safety_check_is_not_deleted_at_all()
    {
        var publisher = "UUTestPub" + Guid.NewGuid().ToString("N")[..10];
        var publisherPath = $@"Software\{publisher}";
        try
        {
            using (Microsoft.Win32.Registry.CurrentUser.CreateSubKey(publisherPath))
            {
            }

            var status = LeftoverRemover.DeleteKeyTree($@"HKCU\{publisherPath}", new LoggingService());

            Assert.NotEqual(RegistryBackupService.KeyTreeDeleteStatus.Deleted, status);
            using var stays = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(publisherPath);
            Assert.NotNull(stays);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(publisherPath, throwOnMissingSubKey: false);
        }
    }
}

public sealed class LeftoverWorkerCommandTests
{
    private static readonly LeftoverSubject Notes = new(LeftoverTree.RemovedKey, "Contoso Notes", "Contoso Ltd.", string.Empty);

    [Fact]
    public async Task The_worker_removes_only_machine_lane_items_that_its_own_scan_finds()
    {
        using var tree = new LeftoverTree();
        var machine = tree.Folder("Program Files", "Contoso", "Contoso Notes");
        var user = tree.Folder("AppData", "Roaming", "Contoso Notes");
        var invented = Path.Combine(tree.ProgramFiles, "Contoso", "Not Found");
        var recycled = new List<string>();
        var remover = new LeftoverRemover(tree.Scanner(), tree.Roots, new LoggingService(), recycle: p =>
        {
            recycled.Add(p);
            return true;
        });
        var command = new CleanupCommand
        {
            Command = "leftovers",
            LeftoverSubject = Notes,
            SelectedLeftovers =
            [
                new LeftoverSelectionDto { Kind = LeftoverKind.Folder, Path = machine },
                new LeftoverSelectionDto { Kind = LeftoverKind.Folder, Path = user },
                new LeftoverSelectionDto { Kind = LeftoverKind.Folder, Path = invented },
            ],
        };

        var message = await CleanupWorker.LeftoversResultAsync(command, remover, tree.Registry, "unused", CancellationToken.None);

        Assert.Equal("result", message.Type);
        Assert.Equal(CleanupWorker.LeftoversId, message.Id);
        Assert.Equal(1, message.Files);
        Assert.Equal(2, message.Skipped);
        Assert.Equal([machine], recycled);
    }

    [Fact]
    public async Task Names_the_app_could_read_but_the_worker_could_not_still_hold_an_item_back()
    {
        using var tree = new LeftoverTree();
        var machine = tree.Folder("Program Files", "Contoso", "Contoso Notes");
        var recycled = new List<string>();
        var remover = new LeftoverRemover(tree.Scanner(), tree.Roots, new LoggingService(), recycle: p =>
        {
            recycled.Add(p);
            return true;
        });
        var command = new CleanupCommand
        {
            Command = "leftovers",
            LeftoverSubject = Notes,
            SelectedLeftovers = [new LeftoverSelectionDto { Kind = LeftoverKind.Folder, Path = machine }],
            LeftoverClaimedPaths = [machine],
        };

        var message = await CleanupWorker.LeftoversResultAsync(command, remover, tree.Registry, "unused", CancellationToken.None);

        Assert.Equal(0, message.Files);
        Assert.Empty(recycled);
    }

    [Fact]
    public async Task A_command_without_a_subject_or_selection_does_nothing()
    {
        using var tree = new LeftoverTree();
        tree.Folder("Program Files", "Contoso", "Contoso Notes");
        var remover = new LeftoverRemover(tree.Scanner(), tree.Roots, new LoggingService(), recycle: _ => throw new InvalidOperationException());

        var none = await CleanupWorker.LeftoversResultAsync(
            new CleanupCommand { Command = "leftovers" }, remover, tree.Registry, "unused", CancellationToken.None);
        var noSelection = await CleanupWorker.LeftoversResultAsync(
            new CleanupCommand { Command = "leftovers", LeftoverSubject = Notes }, remover, tree.Registry, "unused", CancellationToken.None);

        Assert.Equal(0, none.Files);
        Assert.Equal(0, noSelection.Files);
    }

    [Fact]
    public void A_leftovers_command_is_never_a_read_only_job()
    {
        var job = new ElevatedJob
        {
            Id = "test",
            Kind = "cleanup",
            Exclusive = false,
            Payload = JsonSerializer.SerializeToElement(new { command = "leftovers" }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
        };

        Assert.True(ElevatedWorker.IsExclusiveJob(job));
    }
}
