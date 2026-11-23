using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services.App;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Elevation;
using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.History;

namespace UltimateUpdater.Tests;

/// <summary>The user side of the leftover quarantine: moving in, the manifest, restoring, the 14-day prune and the
/// folder resolution that keeps every later step inside the quarantine root.</summary>
public sealed class LeftoverQuarantineTests
{
    private static readonly LeftoverSubject Notes = new(LeftoverTree.RemovedKey, "Contoso Notes", "Contoso Ltd.", string.Empty);

    private static LeftoverItem FolderItem(string path, LeftoverLane lane = LeftoverLane.User) =>
        new(LeftoverKind.Folder, lane, path, "Contoso Notes", true, 1234);

    [Fact]
    public async Task A_user_folder_is_moved_into_the_quarantine_with_a_manifest_and_restored_to_the_same_place()
    {
        using var tree = new LeftoverTree();
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");
        var userRoot = QuarantineStore.UserRoot(tree.LocalAppData);

        using var session = QuarantineSession.OpenUser("Contoso Notes", new LoggingService(), userRoot);
        Assert.NotNull(session);
        Assert.True(session!.TryMove(FolderItem(folder)));

        Assert.False(Directory.Exists(folder));
        Assert.True(session.HasItems);
        var manifest = QuarantineStore.ReadManifest(session.Root);
        Assert.NotNull(manifest);
        Assert.Equal(LeftoverLane.User, manifest!.Lane);
        var entry = Assert.Single(manifest.Items);
        Assert.Equal(folder, entry.OriginalPath);
        Assert.Equal(LeftoverKind.Folder, entry.Kind);
        Assert.Equal(1234, entry.SizeBytes);
        Assert.True(File.Exists(Path.Combine(session.Root, entry.StoredAs, "data.txt")));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}_\d{6}(_\d+)?$", session.FolderName);

        var restored = await new QuarantineRestorer(new LoggingService(), tree.Roots)
            .RestoreAsync(session.Root, LeftoverLane.User, CancellationToken.None);

        Assert.Equal(new QuarantineRestoreResult(1, 0, 0), restored);
        Assert.True(File.Exists(Path.Combine(folder, "data.txt")));
        Assert.False(Directory.Exists(Path.Combine(session.Root, entry.StoredAs)));

        var again = await new QuarantineRestorer(new LoggingService(), tree.Roots)
            .RestoreAsync(session.Root, LeftoverLane.User, CancellationToken.None);
        Assert.Equal(QuarantineRestoreResult.None, again);
    }

    [Fact]
    public async Task A_shortcut_goes_into_the_quarantine_and_comes_back()
    {
        using var tree = new LeftoverTree();
        var shortcut = tree.Shortcut(tree.UserStartMenu, "Contoso Notes", null);
        using var session = QuarantineSession.OpenUser("Contoso Notes", new LoggingService(), QuarantineStore.UserRoot(tree.LocalAppData));

        Assert.True(session!.TryMove(new LeftoverItem(LeftoverKind.Shortcut, LeftoverLane.User, shortcut, "Contoso Notes", true, 3)));
        Assert.False(File.Exists(shortcut));

        var restored = await new QuarantineRestorer(new LoggingService(), tree.Roots)
            .RestoreAsync(session.Root, LeftoverLane.User, CancellationToken.None);

        Assert.Equal(1, restored.Restored);
        Assert.True(File.Exists(shortcut));
    }

    [Fact]
    public async Task A_restore_never_overwrites_a_place_that_exists_again()
    {
        using var tree = new LeftoverTree();
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");
        using var session = QuarantineSession.OpenUser("Contoso Notes", new LoggingService(), QuarantineStore.UserRoot(tree.LocalAppData));
        Assert.True(session!.TryMove(FolderItem(folder)));

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "new.txt"), "newer");

        var restored = await new QuarantineRestorer(new LoggingService(), tree.Roots)
            .RestoreAsync(session.Root, LeftoverLane.User, CancellationToken.None);

        Assert.Equal(new QuarantineRestoreResult(0, 1, 0), restored);
        Assert.Equal("newer", File.ReadAllText(Path.Combine(folder, "new.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "data.txt")));
        Assert.True(File.Exists(Path.Combine(session.Root, "items", "1", "data.txt")));
    }

    [Fact]
    public async Task A_restore_target_outside_the_lanes_places_or_in_the_other_lane_is_refused()
    {
        using var tree = new LeftoverTree();
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");
        using var session = QuarantineSession.OpenUser("Contoso Notes", new LoggingService(), QuarantineStore.UserRoot(tree.LocalAppData));
        Assert.True(session!.TryMove(FolderItem(folder)));

        // The manifest is tampered with: the original path now points into Documents (not a leftover place).
        var manifest = QuarantineStore.ReadManifest(session.Root)!;
        manifest.Items[0].OriginalPath = Path.Combine(tree.Documents, "Contoso Notes");
        File.WriteAllText(Path.Combine(session.Root, QuarantineStore.ManifestName), JsonSerializer.Serialize(manifest, QuarantineStore.Json));

        var outside = await new QuarantineRestorer(new LoggingService(), tree.Roots)
            .RestoreAsync(session.Root, LeftoverLane.User, CancellationToken.None);
        var otherLane = await new QuarantineRestorer(new LoggingService(), tree.Roots)
            .RestoreAsync(session.Root, LeftoverLane.Machine, CancellationToken.None);

        Assert.Equal(new QuarantineRestoreResult(0, 0, 1), outside);
        Assert.Equal(new QuarantineRestoreResult(0, 0, 1), otherLane);
        Assert.False(Directory.Exists(Path.Combine(tree.Documents, "Contoso Notes")));
    }

    [Fact]
    public async Task A_stored_path_that_climbs_out_of_the_items_folder_is_refused()
    {
        using var tree = new LeftoverTree();
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");
        using var session = QuarantineSession.OpenUser("Contoso Notes", new LoggingService(), QuarantineStore.UserRoot(tree.LocalAppData));
        Assert.True(session!.TryMove(FolderItem(folder)));

        var manifest = QuarantineStore.ReadManifest(session.Root)!;
        manifest.Items[0].StoredAs = Path.Combine("items", "..", "..");
        File.WriteAllText(Path.Combine(session.Root, QuarantineStore.ManifestName), JsonSerializer.Serialize(manifest, QuarantineStore.Json));

        var restored = await new QuarantineRestorer(new LoggingService(), tree.Roots)
            .RestoreAsync(session.Root, LeftoverLane.User, CancellationToken.None);

        Assert.Equal(new QuarantineRestoreResult(0, 0, 1), restored);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void A_path_that_leads_through_a_junction_is_not_moved()
    {
        using var tree = new LeftoverTree();
        var real = tree.Folder("Elsewhere", "Contoso Notes");
        var link = Path.Combine(tree.AppData, "Linked");
        LeftoverTestLinks.CreateJunction(link, Path.GetDirectoryName(real)!);
        try
        {
            using var session = QuarantineSession.OpenUser("Contoso Notes", new LoggingService(), QuarantineStore.UserRoot(tree.LocalAppData));

            Assert.False(session!.TryMove(FolderItem(Path.Combine(link, "Contoso Notes"))));
            Assert.False(session.TryMove(FolderItem(link)));
            Assert.False(session.HasItems);
            Assert.True(File.Exists(Path.Combine(real, "data.txt")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void No_user_quarantine_is_opened_through_a_junction()
    {
        using var tree = new LeftoverTree();
        var elsewhere = Path.Combine(tree.Root, "Elsewhere");
        Directory.CreateDirectory(elsewhere);
        var link = Path.Combine(tree.LocalAppData, "LinkedQuarantine");
        LeftoverTestLinks.CreateJunction(link, elsewhere);
        try
        {
            Assert.Null(QuarantineSession.OpenUser("Contoso Notes", new LoggingService(), link));
            Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void An_unused_session_leaves_no_folder_behind()
    {
        using var tree = new LeftoverTree();
        var root = QuarantineStore.UserRoot(tree.LocalAppData);
        using (var session = QuarantineSession.OpenUser("Contoso Notes", new LoggingService(), root))
            Assert.True(Directory.Exists(session!.Root));

        Assert.Empty(Directory.EnumerateDirectories(root));
    }

    [Fact]
    public void Two_sessions_in_the_same_second_get_different_folders()
    {
        using var tree = new LeftoverTree();
        var root = QuarantineStore.UserRoot(tree.LocalAppData);
        var now = new DateTime(2026, 11, 22, 10, 0, 0);

        using var first = QuarantineSession.OpenUser("A", new LoggingService(), root, now);
        using var second = QuarantineSession.OpenUser("B", new LoggingService(), root, now);

        Assert.NotEqual(first!.FolderName, second!.FolderName);
        Assert.True(QuarantineStore.IsFolderName(first.FolderName));
        Assert.True(QuarantineStore.IsFolderName(second.FolderName));
    }

    [Fact]
    public async Task A_registry_key_is_exported_into_the_quarantine_and_listed_before_the_remover_deletes_it()
    {
        using var tree = new LeftoverTree();
        tree.Registry.Set(@"HKCU\Software\Contoso\Contoso Notes", "Setting", "1");
        var deleted = new List<string>();
        var recycled = new List<string>();
        var userRoot = QuarantineStore.UserRoot(tree.LocalAppData);
        using var session = QuarantineSession.OpenUser(
            "Contoso Notes", new LoggingService(), userRoot,
            backup: (key, folder, _) =>
            {
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "export.reg");
                File.WriteAllText(file, "x");
                return Task.FromResult<RegistryBackupService.RegistryBackupResult?>(new RegistryBackupService.RegistryBackupResult(file, [key]));
            });
        var remover = new LeftoverRemover(
            tree.Scanner(), tree.Roots, new LoggingService(),
            recycle: path =>
            {
                recycled.Add(path);
                return true;
            },
            backup: (_, _, _, _) => throw new InvalidOperationException("the regular backup must not run"),
            deleteKey: key =>
            {
                // The export is already listed in the manifest at the moment the key is deleted.
                Assert.Contains(QuarantineStore.ReadManifest(session!.Root)!.Items, i => i.OriginalPath == key);
                deleted.Add(key);
                return RegistryBackupService.KeyTreeDeleteStatus.Deleted;
            });
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");

        var result = await remover.RemoveAsync(
            Notes, tree.Claims(Notes.Key), LeftoverLane.User,
            [new() { Kind = LeftoverKind.Folder, Path = folder }, new() { Kind = LeftoverKind.RegistryKey, Path = @"HKCU\Software\Contoso\Contoso Notes" }],
            @"C:\unused-backups", 3, CancellationToken.None, session);

        Assert.Equal(2, result.Removed);
        Assert.Empty(recycled);
        Assert.Equal([@"HKCU\Software\Contoso\Contoso Notes"], deleted);
        Assert.Equal(2, session!.Manifest.Items.Count);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task An_item_the_quarantine_cannot_take_goes_the_old_way_and_a_failed_key_export_uses_the_regular_backup()
    {
        using var tree = new LeftoverTree();
        tree.Registry.Set(@"HKCU\Software\Contoso\Contoso Notes", "Setting", "1");
        var recycled = new List<string>();
        var regular = new List<IReadOnlyList<string>>();
        using var session = QuarantineSession.OpenUser(
            "Contoso Notes", new LoggingService(), QuarantineStore.UserRoot(tree.LocalAppData),
            backup: (_, _, _) => Task.FromResult<RegistryBackupService.RegistryBackupResult?>(null));

        // A file named "items" blocks the folder the move needs.
        File.WriteAllText(Path.Combine(session!.Root, "items"), "block");
        var remover = new LeftoverRemover(
            tree.Scanner(), tree.Roots, new LoggingService(),
            recycle: path =>
            {
                recycled.Add(path);
                return true;
            },
            backup: (keys, _, _, _) =>
            {
                regular.Add(keys);
                return Task.FromResult<RegistryBackupService.RegistryBackupResult?>(new RegistryBackupService.RegistryBackupResult("b.reg", keys));
            },
            deleteKey: _ => RegistryBackupService.KeyTreeDeleteStatus.Deleted);
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");

        var result = await remover.RemoveAsync(
            Notes, tree.Claims(Notes.Key), LeftoverLane.User,
            [new() { Kind = LeftoverKind.Folder, Path = folder }, new() { Kind = LeftoverKind.RegistryKey, Path = @"HKCU\Software\Contoso\Contoso Notes" }],
            @"C:\unused-backups", 3, CancellationToken.None, session);

        Assert.Equal(2, result.Removed);
        Assert.Equal([folder], recycled);
        Assert.Equal([@"HKCU\Software\Contoso\Contoso Notes"], regular.Single());
        Assert.False(session.HasItems);
    }

    [Fact]
    public void Prune_removes_only_quarantine_folders_older_than_fourteen_days()
    {
        using var tree = new LeftoverTree();
        var root = QuarantineStore.UserRoot(tree.LocalAppData);
        var now = new DateTime(2026, 11, 22, 12, 0, 0);
        string[] expired = ["2026-11-07_115959", "2026-10-01_080000", "2026-11-07_115959_2"];
        string[] kept = ["2026-11-08_120000", "2026-11-20_101010", "2026-11-22_115959"];
        foreach (var name in expired.Concat(kept).Append("keep-me"))
        {
            var folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "manifest.json"), "{}");
        }

        File.WriteAllText(Path.Combine(root, "2026-01-01_000000"), "a file with a folder-like name");

        var removed = QuarantineStore.Prune(root, now, new LoggingService());

        Assert.Equal(expired.Length, removed);
        foreach (var name in expired)
            Assert.False(Directory.Exists(Path.Combine(root, name)), name);

        foreach (var name in kept.Append("keep-me"))
            Assert.True(Directory.Exists(Path.Combine(root, name)), name);

        Assert.True(File.Exists(Path.Combine(root, "2026-01-01_000000")));
    }

    [Fact]
    public void Prune_does_not_follow_a_junction_inside_the_root_or_enter_a_linked_root()
    {
        using var tree = new LeftoverTree();
        var root = QuarantineStore.UserRoot(tree.LocalAppData);
        Directory.CreateDirectory(root);
        var outside = tree.Folder("Elsewhere", "precious");
        var now = new DateTime(2026, 11, 22, 12, 0, 0);
        var inner = Path.Combine(root, "2026-01-01_000000");
        LeftoverTestLinks.CreateJunction(inner, outside);
        var linkedRoot = Path.Combine(tree.Root, "LinkedRoot");
        LeftoverTestLinks.CreateJunction(linkedRoot, root);
        try
        {
            QuarantineStore.Prune(root, now, new LoggingService());
            QuarantineStore.Prune(linkedRoot, now, new LoggingService());

            Assert.True(File.Exists(Path.Combine(outside, "data.txt")));
        }
        finally
        {
            Directory.Delete(linkedRoot);
            Directory.Delete(inner);
        }
    }

    [Fact]
    public void An_expired_folder_goes_with_its_nested_content_and_a_link_inside_it_loses_only_itself()
    {
        using var tree = new LeftoverTree();
        var outside = tree.Folder("Elsewhere", "precious");
        var expired = Path.Combine(tree.Root, "Expired");
        Directory.CreateDirectory(Path.Combine(expired, "sub", "deeper"));
        File.WriteAllText(Path.Combine(expired, "sub", "deeper", "a.txt"), "a");
        File.WriteAllText(Path.Combine(expired, "b.txt"), "b");
        File.SetAttributes(Path.Combine(expired, "b.txt"), FileAttributes.ReadOnly);
        LeftoverTestLinks.CreateJunction(Path.Combine(expired, "link"), outside);

        Assert.True(QuarantineTreeDelete.Delete(expired, new LoggingService()));

        Assert.False(Directory.Exists(expired));
        Assert.True(File.Exists(Path.Combine(outside, "data.txt")));
    }

    [Fact]
    public void A_folder_swapped_for_a_junction_after_the_listing_is_not_entered_and_the_folder_stays()
    {
        using var tree = new LeftoverTree();
        var outside = tree.Folder("Elsewhere", "precious");
        var expired = Path.Combine(tree.Root, "Expired");
        var sub = Path.Combine(expired, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "a.txt"), "a");

        QuarantineTreeDelete.AfterListingForTest.Value = directory =>
        {
            if (!string.Equals(directory, expired, StringComparison.OrdinalIgnoreCase))
                return;

            Directory.Delete(sub, recursive: true);
            LeftoverTestLinks.CreateJunction(sub, outside);
        };
        try
        {
            Assert.False(QuarantineTreeDelete.Delete(expired, new LoggingService()));

            Assert.True(File.Exists(Path.Combine(outside, "data.txt")));
            Assert.True(Directory.Exists(expired));
        }
        finally
        {
            QuarantineTreeDelete.AfterListingForTest.Value = null;
            Directory.Delete(sub);
        }
    }

    [Fact]
    public void A_file_swapped_for_a_junction_after_the_listing_is_left_alone()
    {
        using var tree = new LeftoverTree();
        var outside = tree.Folder("Elsewhere", "precious");
        var expired = Path.Combine(tree.Root, "Expired");
        var file = Path.Combine(expired, "a.txt");
        Directory.CreateDirectory(expired);
        File.WriteAllText(file, "a");

        QuarantineTreeDelete.AfterListingForTest.Value = directory =>
        {
            if (!string.Equals(directory, expired, StringComparison.OrdinalIgnoreCase))
                return;

            File.Delete(file);
            LeftoverTestLinks.CreateJunction(file, outside);
        };
        try
        {
            Assert.False(QuarantineTreeDelete.Delete(expired, new LoggingService()));

            Assert.True(File.Exists(Path.Combine(outside, "data.txt")));
            Assert.True(Directory.Exists(expired));
        }
        finally
        {
            QuarantineTreeDelete.AfterListingForTest.Value = null;
            Directory.Delete(file);
        }
    }

    [Fact]
    public void An_expired_folder_that_became_a_junction_is_not_deleted_through()
    {
        using var tree = new LeftoverTree();
        var outside = tree.Folder("Elsewhere", "precious");
        var link = Path.Combine(tree.Root, "2026-01-01_000000");
        LeftoverTestLinks.CreateJunction(link, outside);
        try
        {
            Assert.False(QuarantineTreeDelete.Delete(link, new LoggingService()));

            Assert.True(File.Exists(Path.Combine(outside, "data.txt")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void A_folder_is_restorable_for_fourteen_days_by_the_moment_in_its_name()
    {
        var now = new DateTime(2026, 11, 22, 12, 0, 0);

        Assert.True(QuarantineStore.IsRestorable("2026-11-22_115959", now));
        Assert.True(QuarantineStore.IsRestorable("2026-11-08_120000", now));
        Assert.False(QuarantineStore.IsRestorable("2026-11-08_115959", now));
        Assert.False(QuarantineStore.IsRestorable(null, now));
        Assert.False(QuarantineStore.IsRestorable("not-a-folder", now));
        Assert.Equal(14, QuarantineStore.RetentionDays);
    }

    [Fact]
    public void A_folder_is_resolved_only_as_a_direct_child_of_the_root_with_a_quarantine_name()
    {
        using var tree = new LeftoverTree();
        var root = Path.Combine(tree.Root, "Quarantine");
        var good = Path.Combine(root, "2026-11-21_100000");
        Directory.CreateDirectory(good);
        var outside = Path.Combine(tree.Root, "Other", "2026-11-21_100000");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(root, "not-a-name"));
        Directory.CreateDirectory(Path.Combine(root, "2026-11-21_100000", "2026-11-21_100001"));

        Assert.Equal(good, QuarantineStore.ResolveFolder(root, "2026-11-21_100000"));
        Assert.Equal(good, QuarantineStore.ResolveFolder(root, good));
        Assert.Null(QuarantineStore.ResolveFolder(root, outside));
        Assert.Null(QuarantineStore.ResolveFolder(root, Path.Combine(root, "..", "Other", "2026-11-21_100000")));
        Assert.Null(QuarantineStore.ResolveFolder(root, Path.Combine(root, "2026-11-21_100000", "..", "..", "Other", "2026-11-21_100000")));
        Assert.Null(QuarantineStore.ResolveFolder(root, Path.Combine(good, "2026-11-21_100001")));
        Assert.Null(QuarantineStore.ResolveFolder(root, "not-a-name"));
        Assert.Null(QuarantineStore.ResolveFolder(root, "2026-11-21_100002"));
        Assert.Null(QuarantineStore.ResolveFolder(root, @"..\Other\2026-11-21_100000"));
        Assert.Null(QuarantineStore.ResolveFolder(root, null));
        Assert.Null(QuarantineStore.ResolveFolder(root, string.Empty));
    }

    [Fact]
    public void A_folder_that_is_a_junction_or_sits_behind_one_is_not_resolved()
    {
        using var tree = new LeftoverTree();
        var root = Path.Combine(tree.Root, "Quarantine");
        Directory.CreateDirectory(root);
        var target = Path.Combine(tree.Root, "Target", "2026-11-21_100000");
        Directory.CreateDirectory(target);
        var linkedFolder = Path.Combine(root, "2026-11-20_100000");
        LeftoverTestLinks.CreateJunction(linkedFolder, target);
        var linkedRoot = Path.Combine(tree.Root, "LinkedRoot");
        LeftoverTestLinks.CreateJunction(linkedRoot, Path.GetDirectoryName(target)!);
        try
        {
            Assert.Null(QuarantineStore.ResolveFolder(root, "2026-11-20_100000"));
            Assert.Null(QuarantineStore.ResolveFolder(root, linkedFolder));
            Assert.Null(QuarantineStore.ResolveFolder(linkedRoot, "2026-11-21_100000"));
            Assert.Null(QuarantineStore.ResolveFolder(root, Path.Combine(linkedRoot, "2026-11-21_100000")));
        }
        finally
        {
            Directory.Delete(linkedFolder);
            Directory.Delete(linkedRoot);
        }
    }

    [Fact]
    public void A_registry_export_is_imported_only_when_every_header_is_the_items_own_key()
    {
        using var tree = new LeftoverTree();
        const string key = @"HKCU\Software\Contoso\Contoso Notes";
        string Write(string name, params string[] lines)
        {
            var file = Path.Combine(tree.Root, name);
            File.WriteAllLines(file, ["Windows Registry Editor Version 5.00", string.Empty, .. lines], Encoding.Unicode);
            return file;
        }

        var own = Write("own.reg", @"[HKEY_CURRENT_USER\Software\Contoso\Contoso Notes]", "\"A\"=\"1\"", string.Empty,
            @"[HKEY_CURRENT_USER\Software\Contoso\Contoso Notes\Sub]");
        var other = Write("other.reg", @"[HKEY_CURRENT_USER\Software\Contoso\Contoso Notes]", @"[HKEY_CURRENT_USER\Software\Microsoft\Windows]");
        var sibling = Write("sibling.reg", @"[HKEY_CURRENT_USER\Software\Contoso\Contoso Notes Pro]");
        var deletion = Write("delete.reg", @"[-HKEY_CURRENT_USER\Software\Contoso\Contoso Notes]");
        var noHeader = Write("none.reg", "\"A\"=\"1\"");
        var wrongFirstLine = Path.Combine(tree.Root, "wrong.reg");
        File.WriteAllLines(wrongFirstLine, ["not a registry file", @"[HKEY_CURRENT_USER\Software\Contoso\Contoso Notes]"], Encoding.Unicode);

        Assert.True(QuarantineRestorer.RegFileHoldsOnly(own, key));
        Assert.False(QuarantineRestorer.RegFileHoldsOnly(other, key));
        Assert.False(QuarantineRestorer.RegFileHoldsOnly(sibling, key));
        Assert.False(QuarantineRestorer.RegFileHoldsOnly(deletion, key));
        Assert.False(QuarantineRestorer.RegFileHoldsOnly(noHeader, key));
        Assert.False(QuarantineRestorer.RegFileHoldsOnly(wrongFirstLine, key));
        Assert.False(QuarantineRestorer.RegFileHoldsOnly(Path.Combine(tree.Root, "missing.reg"), key));
    }

    [Fact]
    public void A_registry_export_with_any_line_reg_export_never_writes_is_not_imported()
    {
        using var tree = new LeftoverTree();
        const string key = @"HKCU\Software\Contoso\Contoso Notes";
        const string header = @"[HKEY_CURRENT_USER\Software\Contoso\Contoso Notes]";
        string Write(string name, params string[] lines)
        {
            var file = Path.Combine(tree.Root, name);
            File.WriteAllLines(file, ["Windows Registry Editor Version 5.00", string.Empty, .. lines], Encoding.Unicode);
            return file;
        }

        var real = Write(
            "real.reg", header, "@=\"default\"", "\"Path\"=\"C:\\\\Program Files\\\\Contoso\"", "\"Count\"=dword:0000002a",
            "\"Blob\"=hex:01,02,03,04,\\", "  05,06,07", "\"Multi\"=hex(7):41,00,00,00,00,00", "\"Empty\"=hex:", string.Empty,
            header.Replace("Notes]", @"Notes\Sub]"), "\"Name with \\\"quote\\\"\"=\"x\"");
        Assert.True(QuarantineRestorer.RegFileHoldsOnly(real, key));

        var valueDeletion = Write("valuedel.reg", header, "\"Old\"=-");
        var comment = Write("comment.reg", header, "; a comment");
        var unknown = Write("unknown.reg", header, "DeleteKey HKEY_LOCAL_MACHINE");
        var strayHeaderLike = Write("stray.reg", header, "[HKEY_LOCAL_MACHINE\\SOFTWARE\\Evil");
        var danglingContinuation = Write("dangling.reg", header, "\"Blob\"=hex:01,\\", "\"Next\"=\"x\"");
        var junkContinuation = Write("junk.reg", header, "\"Blob\"=hex:01,\\", "  [-HKEY_LOCAL_MACHINE\\SOFTWARE\\Evil]");
        var badData = Write("baddata.reg", header, "\"A\"=notavalue");

        Assert.False(QuarantineRestorer.RegFileHoldsOnly(valueDeletion, key));
        Assert.False(QuarantineRestorer.RegFileHoldsOnly(comment, key));
        Assert.False(QuarantineRestorer.RegFileHoldsOnly(unknown, key));
        Assert.False(QuarantineRestorer.RegFileHoldsOnly(strayHeaderLike, key));
        Assert.False(QuarantineRestorer.RegFileHoldsOnly(danglingContinuation, key));
        Assert.False(QuarantineRestorer.RegFileHoldsOnly(junkContinuation, key));
        Assert.False(QuarantineRestorer.RegFileHoldsOnly(badData, key));
    }

    [Fact]
    public async Task A_quarantined_key_is_imported_back_through_the_verified_export_and_never_when_it_exists_or_holds_more()
    {
        using var tree = new LeftoverTree();
        var program = "UuQuarantineTest" + Guid.NewGuid().ToString("N")[..10];
        var key = $@"HKCU\Software\UuQuarantinePub\{program}";
        var userRoot = QuarantineStore.UserRoot(tree.LocalAppData);
        using var session = QuarantineSession.OpenUser(
            "Contoso Notes", new LoggingService(), userRoot,
            backup: (k, folder, _) =>
            {
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "export.reg");
                File.WriteAllLines(file, ["Windows Registry Editor Version 5.00", string.Empty, $@"[HKEY_CURRENT_USER\Software\UuQuarantinePub\{program}]"], Encoding.Unicode);
                return Task.FromResult<RegistryBackupService.RegistryBackupResult?>(new RegistryBackupService.RegistryBackupResult(file, [k]));
            });

        Assert.True(await session!.TryBackupKeyAsync(new LeftoverItem(LeftoverKind.RegistryKey, LeftoverLane.User, key, program, true, 0), CancellationToken.None));

        var imported = new List<string>();
        var restored = await new QuarantineRestorer(new LoggingService(), tree.Roots, (file, _) =>
        {
            imported.Add(file);
            return Task.FromResult(true);
        }).RestoreAsync(session.Root, LeftoverLane.User, CancellationToken.None);

        Assert.Equal(1, restored.Restored);
        Assert.EndsWith("export.reg", Assert.Single(imported));

        // A tampered manifest that names a key of the other hive is refused outright.
        var manifest = QuarantineStore.ReadManifest(session.Root)!;
        manifest.Items[0].Restored = false;
        manifest.Items[0].OriginalPath = $@"HKLM\Software\UuQuarantinePub\{program}";
        File.WriteAllText(Path.Combine(session.Root, QuarantineStore.ManifestName), JsonSerializer.Serialize(manifest, QuarantineStore.Json));
        imported.Clear();

        var refused = await new QuarantineRestorer(new LoggingService(), tree.Roots, (file, _) =>
        {
            imported.Add(file);
            return Task.FromResult(true);
        }).RestoreAsync(session.Root, LeftoverLane.User, CancellationToken.None);

        Assert.Equal(new QuarantineRestoreResult(0, 0, 1), refused);
        Assert.Empty(imported);
    }

    [Fact]
    public void A_history_row_offers_the_restore_only_for_leftover_entries_inside_their_fourteen_days()
    {
        string Name(int daysAgo) => DateTime.Now.AddDays(-daysAgo).ToString("yyyy-MM-dd_HHmmss");
        var asked = new List<HistoryEntry>();

        HistoryRowViewModel Row(HistoryEntry entry, bool wired = true) =>
            new(entry, onRestoreLeftovers: wired ? e => { asked.Add(e); return Task.CompletedTask; } : null);

        var fresh = new HistoryEntry { SourceId = "uninstaller", ItemId = "x", Action = "leftovers", Success = true, Name = "Contoso Notes", QuarantineUser = Name(1) };
        var machineOnly = new HistoryEntry { SourceId = "uninstaller", ItemId = "x", Action = "leftovers", Success = true, Name = "Contoso Notes", QuarantineMachine = Name(13) };
        var old = new HistoryEntry { SourceId = "uninstaller", ItemId = "x", Action = "leftovers", Success = true, Name = "Contoso Notes", QuarantineUser = Name(15), QuarantineMachine = Name(20) };
        var none = new HistoryEntry { SourceId = "uninstaller", ItemId = "x", Action = "leftovers", Success = true, Name = "Contoso Notes" };
        var wrongAction = new HistoryEntry { SourceId = "uninstaller", ItemId = "x", Action = "uninstall", Success = true, Name = "Contoso Notes", QuarantineUser = Name(1) };

        Assert.True(Row(fresh).CanRestoreLeftovers);
        Assert.True(Row(machineOnly).CanRestoreLeftovers);
        Assert.False(Row(old).CanRestoreLeftovers);
        Assert.False(Row(none).CanRestoreLeftovers);
        Assert.False(Row(wrongAction).CanRestoreLeftovers);
        Assert.False(Row(fresh, wired: false).CanRestoreLeftovers);

        var row = Row(fresh);
        Assert.True(row.RestoreLeftoversCommand.CanExecute(null));
        row.RestoreLeftoversCommand.Execute(null);
        Assert.Same(fresh, Assert.Single(asked));
        Assert.False(Row(old).RestoreLeftoversCommand.CanExecute(null));
        Assert.False(string.IsNullOrWhiteSpace(row.RestoreLeftoversHeader));
    }

    [Fact]
    public void A_folder_whose_parent_is_swapped_for_a_junction_before_the_move_is_not_moved()
    {
        using var tree = new LeftoverTree();
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");
        var roaming = Path.GetDirectoryName(folder)!;
        var decoy = tree.Folder("Elsewhere", "decoy");
        Directory.CreateDirectory(Path.Combine(decoy, "Contoso Notes"));
        File.WriteAllText(Path.Combine(decoy, "Contoso Notes", "decoy.txt"), "x");
        using var session = QuarantineSession.OpenUser("Contoso Notes", new LoggingService(), QuarantineStore.UserRoot(tree.LocalAppData));
        var moved = roaming + ".moved";

        QuarantineMove.BeforeQuarantineMoveForTest.Value = _ =>
        {
            Directory.Move(roaming, moved);
            LeftoverTestLinks.CreateJunction(roaming, decoy);
        };
        try
        {
            Assert.False(session!.TryMove(FolderItem(folder)));

            Assert.True(File.Exists(Path.Combine(decoy, "Contoso Notes", "decoy.txt")));
            Assert.True(File.Exists(Path.Combine(moved, "Contoso Notes", "data.txt")));
            Assert.False(session.HasItems);
            Assert.False(Directory.Exists(Path.Combine(session.Root, "items", "1")));
        }
        finally
        {
            QuarantineMove.BeforeQuarantineMoveForTest.Value = null;
            Directory.Delete(roaming);
        }
    }

    [Fact]
    public void A_quarantine_folder_swapped_for_a_junction_before_the_move_receives_nothing()
    {
        using var tree = new LeftoverTree();
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");
        var outside = tree.Folder("Elsewhere", "outside");
        using var session = QuarantineSession.OpenUser("Contoso Notes", new LoggingService(), QuarantineStore.UserRoot(tree.LocalAppData));
        var items = Path.Combine(session!.Root, "items");

        QuarantineMove.BeforeQuarantineMoveForTest.Value = _ =>
        {
            Directory.Delete(items);
            LeftoverTestLinks.CreateJunction(items, outside);
        };
        try
        {
            Assert.False(session.TryMove(FolderItem(folder)));

            Assert.True(File.Exists(Path.Combine(folder, "data.txt")));
            Assert.Equal(["data.txt"], Directory.GetFileSystemEntries(outside).Select(Path.GetFileName));
            Assert.False(session.HasItems);
        }
        finally
        {
            QuarantineMove.BeforeQuarantineMoveForTest.Value = null;
            Directory.Delete(items);
        }
    }

    [Fact]
    public async Task A_stored_item_whose_folder_is_swapped_for_a_junction_before_the_restore_stays_where_it_is()
    {
        using var tree = new LeftoverTree();
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");
        using var session = QuarantineSession.OpenUser("Contoso Notes", new LoggingService(), QuarantineStore.UserRoot(tree.LocalAppData));
        Assert.True(session!.TryMove(FolderItem(folder)));
        var items = Path.Combine(session.Root, "items");
        var moved = items + ".moved";
        var decoy = tree.Folder("Elsewhere", "decoy");
        Directory.CreateDirectory(Path.Combine(decoy, "1"));
        File.WriteAllText(Path.Combine(decoy, "1", "evil.txt"), "x");

        QuarantineMove.BeforeQuarantineMoveForTest.Value = _ =>
        {
            Directory.Move(items, moved);
            LeftoverTestLinks.CreateJunction(items, decoy);
        };
        try
        {
            var result = await new QuarantineRestorer(new LoggingService(), tree.Roots)
                .RestoreAsync(session.Root, LeftoverLane.User, CancellationToken.None);

            Assert.Equal(new QuarantineRestoreResult(0, 0, 1), result);
            Assert.False(Directory.Exists(folder));
            Assert.True(File.Exists(Path.Combine(decoy, "1", "evil.txt")));
            Assert.True(File.Exists(Path.Combine(moved, "1", "data.txt")));
        }
        finally
        {
            QuarantineMove.BeforeQuarantineMoveForTest.Value = null;
            Directory.Delete(items);
        }
    }

    [Fact]
    public async Task A_restore_target_folder_swapped_for_a_junction_before_the_restore_receives_nothing()
    {
        using var tree = new LeftoverTree();
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");
        var roaming = Path.GetDirectoryName(folder)!;
        using var session = QuarantineSession.OpenUser("Contoso Notes", new LoggingService(), QuarantineStore.UserRoot(tree.LocalAppData));
        Assert.True(session!.TryMove(FolderItem(folder)));
        var outside = tree.Folder("Elsewhere", "outside");

        QuarantineMove.BeforeQuarantineMoveForTest.Value = _ =>
        {
            Directory.Move(roaming, roaming + ".moved");
            LeftoverTestLinks.CreateJunction(roaming, outside);
        };
        try
        {
            var result = await new QuarantineRestorer(new LoggingService(), tree.Roots)
                .RestoreAsync(session.Root, LeftoverLane.User, CancellationToken.None);

            Assert.Equal(new QuarantineRestoreResult(0, 0, 1), result);
            Assert.Equal(["data.txt"], Directory.GetFileSystemEntries(outside).Select(Path.GetFileName));
            Assert.True(File.Exists(Path.Combine(session.Root, "items", "1", "data.txt")));
        }
        finally
        {
            QuarantineMove.BeforeQuarantineMoveForTest.Value = null;
            Directory.Delete(roaming);
        }
    }

    [Fact]
    public async Task A_restore_never_replaces_a_folder_that_appears_just_before_the_move()
    {
        using var tree = new LeftoverTree();
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");
        using var session = QuarantineSession.OpenUser("Contoso Notes", new LoggingService(), QuarantineStore.UserRoot(tree.LocalAppData));
        Assert.True(session!.TryMove(FolderItem(folder)));

        QuarantineMove.BeforeQuarantineMoveForTest.Value = _ =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "new.txt"), "newer");
        };
        try
        {
            var result = await new QuarantineRestorer(new LoggingService(), tree.Roots)
                .RestoreAsync(session.Root, LeftoverLane.User, CancellationToken.None);

            Assert.Equal(new QuarantineRestoreResult(0, 0, 1), result);
            Assert.Equal("newer", File.ReadAllText(Path.Combine(folder, "new.txt")));
            Assert.False(File.Exists(Path.Combine(folder, "data.txt")));
            Assert.True(File.Exists(Path.Combine(session.Root, "items", "1", "data.txt")));
        }
        finally
        {
            QuarantineMove.BeforeQuarantineMoveForTest.Value = null;
        }
    }

    [Fact]
    public async Task A_key_whose_manifest_entry_could_not_be_saved_is_not_listed_by_a_later_save()
    {
        using var tree = new LeftoverTree();
        var program = "UuQuarantineTest" + Guid.NewGuid().ToString("N")[..10];
        var key = $@"HKCU\Software\UuQuarantinePub\{program}";
        using var session = QuarantineSession.OpenUser(
            "Contoso Notes", new LoggingService(), QuarantineStore.UserRoot(tree.LocalAppData),
            backup: (k, folder, _) =>
            {
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "export.reg");
                File.WriteAllLines(file, ["Windows Registry Editor Version 5.00", string.Empty, $@"[HKEY_CURRENT_USER\Software\UuQuarantinePub\{program}]"], Encoding.Unicode);
                return Task.FromResult<RegistryBackupService.RegistryBackupResult?>(new RegistryBackupService.RegistryBackupResult(file, [k]));
            });
        var manifestPath = Path.Combine(session!.Root, QuarantineStore.ManifestName);
        if (File.Exists(manifestPath))
            File.Delete(manifestPath);

        // A directory where the manifest file belongs: the write fails.
        Directory.CreateDirectory(manifestPath);
        Assert.False(await session.TryBackupKeyAsync(new LeftoverItem(LeftoverKind.RegistryKey, LeftoverLane.User, key, program, true, 0), CancellationToken.None));
        Assert.Empty(session.Manifest.Items);

        Directory.Delete(manifestPath);
        Assert.True(session.TryMove(FolderItem(tree.Folder("AppData", "Roaming", "Contoso Notes"))));

        var saved = QuarantineStore.ReadManifest(session.Root)!;
        Assert.Equal(LeftoverKind.Folder, Assert.Single(saved.Items).Kind);
    }

    [Fact]
    public async Task A_second_press_on_restore_leftovers_is_ignored_until_the_first_restore_finished()
    {
        var name = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd_HHmmss");
        var entry = new HistoryEntry { SourceId = "uninstaller", ItemId = "x", Action = "leftovers", Success = true, Name = "Contoso Notes", QuarantineUser = name };
        var release = new TaskCompletionSource();
        var started = 0;
        var row = new HistoryRowViewModel(entry, onRestoreLeftovers: _ =>
        {
            started++;
            return release.Task;
        });

        var first = row.RestoreLeftoversCommand.ExecuteAsync(null);
        Assert.False(row.RestoreLeftoversCommand.CanExecute(null));
        // What a button does: it executes only when the command says it can.
        if (row.RestoreLeftoversCommand.CanExecute(null))
            row.RestoreLeftoversCommand.Execute(null);

        Assert.Equal(1, started);

        release.SetResult();
        await first;
        Assert.True(row.RestoreLeftoversCommand.CanExecute(null));
    }

    [Fact]
    public void The_history_line_of_removed_leftovers_keeps_the_folder_names_and_never_a_path()
    {
        var entry = UninstallerBackend.HistoryForLeftovers(new QuarantinedLeftovers("Contoso Notes", "2026-11-22_100000", "2026-11-22_100001_2"));

        Assert.Equal("leftovers", entry.Action);
        Assert.Equal("Contoso Notes", entry.Name);
        Assert.True(entry.Success);
        Assert.Equal("2026-11-22_100000", entry.QuarantineUser);
        Assert.Equal("2026-11-22_100001_2", entry.QuarantineMachine);
    }

    [Fact]
    public void The_restore_outcome_is_clean_only_without_failures_and_without_a_skipped_machine_part()
    {
        Assert.True(new UninstallRestoreOutcome(new QuarantineRestoreResult(2, 1, 0), false, false).IsClean);
        Assert.False(new UninstallRestoreOutcome(new QuarantineRestoreResult(2, 0, 1), false, false).IsClean);
        Assert.False(new UninstallRestoreOutcome(QuarantineRestoreResult.None, false, true).IsClean);

        var text = new UninstallRestoreOutcome(new QuarantineRestoreResult(7, 5, 3), false, false).Describe();
        Assert.Contains("7", text);
        Assert.Contains("5", text);
        Assert.Contains("3", text);
        Assert.False(string.IsNullOrWhiteSpace(new UninstallRestoreOutcome(QuarantineRestoreResult.None, true, false).Describe()));
        Assert.NotEqual(
            new UninstallRestoreOutcome(QuarantineRestoreResult.None, false, false).Describe(),
            new UninstallRestoreOutcome(QuarantineRestoreResult.None, false, true).Describe());
    }
}

/// <summary>The machine side: a quarantine below a redirected ProgramData with the administrator-only ACL, and the
/// "uninstall-restore" job that accepts only a folder inside that root.</summary>
[Collection("DriverStaging")]
public sealed class MachineQuarantineTests : IDisposable
{
    private readonly LeftoverTree _tree = new();

    public MachineQuarantineTests()
    {
        DriverBackupStaging.ProgramDataRootOverride = _tree.ProgramData;
    }

    public void Dispose()
    {
        DriverBackupStaging.ProgramDataRootOverride = null;
        DriverBackupStaging.ProductionOwnerRulesForTest = false;
        _tree.Dispose();
    }

    private static List<JsonElement> Lines(MemoryStream stream) => Encoding.UTF8.GetString(stream.ToArray())
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("payload").Clone())
        .ToList();

    private static async Task<List<JsonElement>> RunAsync(string? folder, LeftoverRoots roots)
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
        {
            var payload = JsonSerializer.SerializeToElement(new { folder });
            await QuarantineRestoreWorker.RunJobAsync(payload, writer, "job-1", new LoggingService(), CancellationToken.None, roots);
        }

        return Lines(stream);
    }

    /// <summary>A machine quarantine folder named <paramref name="name"/> below <paramref name="root"/> that holds one
    /// moved program folder, as a session would leave it.</summary>
    private string MakeQuarantine(string root, string name, string original, bool protectedChain = true)
    {
        var folder = Path.Combine(root, name);
        if (protectedChain && string.Equals(root, QuarantineStore.MachineRoot(), StringComparison.OrdinalIgnoreCase))
        {
            // As the elevated worker leaves it: the ProgramData levels and the folder created with the protected ACL.
            using var staged = DriverBackupStaging.PrepareStagingRoot(
                new LoggingService(), "Quarantine", usersCanRead: false, folderName: name, label: "test");
            Assert.NotNull(staged);
        }

        var stored = Path.Combine(folder, "items", "1");
        Directory.CreateDirectory(stored);
        File.WriteAllText(Path.Combine(stored, "data.txt"), "x");
        var manifest = new QuarantineManifest
        {
            Lane = LeftoverLane.Machine,
            Program = "Contoso Notes",
            CreatedUtc = DateTime.UtcNow,
            Items = [new QuarantineEntry { Kind = LeftoverKind.Folder, OriginalPath = original, StoredAs = Path.Combine("items", "1") }],
        };
        File.WriteAllText(Path.Combine(folder, QuarantineStore.ManifestName), JsonSerializer.Serialize(manifest, QuarantineStore.Json));
        return folder;
    }

    [Fact]
    public void A_machine_session_lives_below_the_machine_root_and_only_administrators_and_system_can_use_it()
    {
        var program = _tree.Folder("Program Files", "Contoso", "Contoso Notes");
        using var session = QuarantineSession.OpenMachine("Contoso Notes", new LoggingService());

        Assert.NotNull(session);
        Assert.Equal(Path.Combine(_tree.ProgramData, "UltimateUpdater", "Quarantine", session!.FolderName), session.Root);
        Assert.True(session.TryMove(FolderItem(program)));
        Assert.False(Directory.Exists(program));
        Assert.Equal(LeftoverLane.Machine, QuarantineStore.ReadManifest(session.Root)!.Lane);

        var security = new DirectoryInfo(session.Root).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.DoesNotContain(rules, r => r.IdentityReference == users);
        Assert.Contains(rules, r => r.IdentityReference == new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        Assert.Contains(rules, r => r.IdentityReference == new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
    }

    private static LeftoverItem FolderItem(string path) => new(LeftoverKind.Folder, LeftoverLane.Machine, path, "Contoso Notes", true, 10);

    [Fact]
    public void No_machine_quarantine_is_made_when_the_root_leads_through_a_junction()
    {
        var elsewhere = Path.Combine(_tree.Root, "Elsewhere");
        Directory.CreateDirectory(elsewhere);
        var link = Path.Combine(_tree.ProgramData, "UltimateUpdater");
        LeftoverTestLinks.CreateJunction(link, elsewhere);
        try
        {
            using var session = QuarantineSession.OpenMachine("Contoso Notes", new LoggingService());

            Assert.Null(session);
            Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task The_restore_job_puts_back_a_folder_of_the_machine_quarantine_root()
    {
        var original = Path.Combine(_tree.ProgramFiles, "Contoso", "Contoso Notes");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        var folder = MakeQuarantine(QuarantineStore.MachineRoot(), "2026-11-21_100000", original);

        var lines = await RunAsync(folder, _tree.Roots);

        var result = lines.Single(l => l.GetProperty("type").GetString() == "result");
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(1, result.GetProperty("restoredCount").GetInt32());
        Assert.True(File.Exists(Path.Combine(original, "data.txt")));
        Assert.Contains(lines, l => l.GetProperty("type").GetString() == "done");
    }

    [Fact]
    public async Task The_restore_job_refuses_a_quarantine_nobody_protected()
    {
        var original = Path.Combine(_tree.ProgramFiles, "Contoso", "Contoso Notes");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        var folder = MakeQuarantine(QuarantineStore.MachineRoot(), "2026-11-21_100000", original, protectedChain: false);

        AssertRefused(await RunAsync(folder, _tree.Roots), original);
        Assert.True(Directory.Exists(Path.Combine(folder, "items", "1")), "nothing was moved out");
    }

    [Fact]
    public async Task The_restore_job_refuses_a_quarantine_folder_with_a_widened_acl()
    {
        var original = Path.Combine(_tree.ProgramFiles, "Contoso", "Contoso Notes");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        var folder = MakeQuarantine(QuarantineStore.MachineRoot(), "2026-11-21_100000", original);
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);

        AssertRefused(await RunAsync(folder, _tree.Roots), original);
    }

    [Fact]
    public void The_machine_prune_touches_nothing_unless_the_chain_is_the_protected_one()
    {
        var now = new DateTime(2026, 11, 22, 12, 0, 0);
        var plain = MakeQuarantine(QuarantineStore.MachineRoot(), "2026-01-01_000000", @"C:\x", protectedChain: false);

        Assert.Equal(0, QuarantineStore.Prune(QuarantineStore.MachineRoot(), now, new LoggingService(), requireProtectedAcl: true));
        Assert.True(Directory.Exists(plain));
    }

    [Fact]
    public void The_machine_prune_removes_an_expired_folder_of_the_protected_chain_and_keeps_a_recent_one()
    {
        var now = new DateTime(2026, 11, 22, 12, 0, 0);
        var old = MakeQuarantine(QuarantineStore.MachineRoot(), "2026-01-01_000000", @"C:\x");
        var recent = MakeQuarantine(QuarantineStore.MachineRoot(), "2026-11-21_100000", @"C:\x");

        Assert.Equal(1, QuarantineStore.Prune(QuarantineStore.MachineRoot(), now, new LoggingService(), requireProtectedAcl: true));

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public async Task The_restore_job_accepts_a_bare_folder_name_inside_the_root()
    {
        var original = Path.Combine(_tree.ProgramFiles, "Contoso", "Contoso Notes");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        MakeQuarantine(QuarantineStore.MachineRoot(), "2026-11-21_100000", original);

        var lines = await RunAsync("2026-11-21_100000", _tree.Roots);

        Assert.Equal(1, lines.Single(l => l.GetProperty("type").GetString() == "result").GetProperty("restoredCount").GetInt32());
    }

    [Fact]
    public async Task The_restore_job_rejects_a_folder_outside_the_quarantine_root()
    {
        var original = Path.Combine(_tree.ProgramFiles, "Contoso", "Contoso Notes");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        var outside = MakeQuarantine(Path.Combine(_tree.Root, "Elsewhere"), "2026-11-21_100000", original);
        Directory.CreateDirectory(QuarantineStore.MachineRoot());

        var lines = await RunAsync(outside, _tree.Roots);

        AssertRefused(lines, original);
    }

    [Fact]
    public async Task The_restore_job_rejects_a_dotdot_path_that_resolves_outside_the_root()
    {
        var original = Path.Combine(_tree.ProgramFiles, "Contoso", "Contoso Notes");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        var outside = MakeQuarantine(Path.Combine(_tree.Root, "Elsewhere"), "2026-11-21_100000", original);
        Directory.CreateDirectory(QuarantineStore.MachineRoot());
        var sneaky = Path.Combine(QuarantineStore.MachineRoot(), "..", "..", "..", "Elsewhere", "2026-11-21_100000");
        Assert.Equal(outside, Path.GetFullPath(sneaky));

        var lines = await RunAsync(sneaky, _tree.Roots);

        AssertRefused(lines, original);
    }

    [Fact]
    public async Task The_restore_job_rejects_a_folder_reached_through_a_junction()
    {
        var original = Path.Combine(_tree.ProgramFiles, "Contoso", "Contoso Notes");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        var outside = MakeQuarantine(Path.Combine(_tree.Root, "Elsewhere"), "2026-11-21_100000", original);
        var root = QuarantineStore.MachineRoot();
        Directory.CreateDirectory(root);
        var link = Path.Combine(root, "2026-11-20_100000");
        LeftoverTestLinks.CreateJunction(link, outside);
        try
        {
            var lines = await RunAsync(link, _tree.Roots);
            AssertRefused(lines, original);

            var byName = await RunAsync("2026-11-20_100000", _tree.Roots);
            AssertRefused(byName, original);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task The_restore_job_rejects_a_missing_or_empty_folder_and_a_root_that_is_a_junction()
    {
        var original = Path.Combine(_tree.ProgramFiles, "Contoso", "Contoso Notes");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);

        AssertRefused(await RunAsync(null, _tree.Roots), original);
        AssertRefused(await RunAsync(string.Empty, _tree.Roots), original);
        AssertRefused(await RunAsync("2026-11-21_100000", _tree.Roots), original);

        var realRoot = Path.Combine(_tree.Root, "RealRoot");
        MakeQuarantine(realRoot, "2026-11-21_100000", original);
        var link = Path.Combine(_tree.ProgramData, "UltimateUpdater");
        LeftoverTestLinks.CreateJunction(link, realRoot);
        try
        {
            // ProgramData\UltimateUpdater\Quarantine now resolves through the junction to RealRoot\Quarantine.
            Directory.CreateDirectory(Path.Combine(realRoot, "Quarantine"));
            Directory.Move(Path.Combine(realRoot, "2026-11-21_100000"), Path.Combine(realRoot, "Quarantine", "2026-11-21_100000"));

            AssertRefused(await RunAsync("2026-11-21_100000", _tree.Roots), original);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task The_restore_job_does_not_restore_a_user_lane_manifest_or_a_target_outside_the_machine_places()
    {
        var original = Path.Combine(_tree.ProgramFiles, "Contoso", "Contoso Notes");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        var folder = MakeQuarantine(QuarantineStore.MachineRoot(), "2026-11-21_100000", original);

        var manifest = QuarantineStore.ReadManifest(folder)!;
        manifest.Items[0].OriginalPath = Path.Combine(_tree.Documents, "Contoso Notes");
        File.WriteAllText(Path.Combine(folder, QuarantineStore.ManifestName), JsonSerializer.Serialize(manifest, QuarantineStore.Json));

        var lines = await RunAsync(folder, _tree.Roots);

        var result = lines.Single(l => l.GetProperty("type").GetString() == "result");
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(1, result.GetProperty("failedCount").GetInt32());
        Assert.False(Directory.Exists(Path.Combine(_tree.Documents, "Contoso Notes")));
    }

    [Fact]
    public void The_worker_knows_the_restore_kind_and_runs_it_exclusively()
    {
        Assert.True(ElevatedWorker.IsRecognizedKind("uninstall-restore"));
        Assert.False(ElevatedWorker.IsRecognizedKind("uninstall-restore-anything"));
        Assert.True(ElevatedWorker.IsExclusiveJob(new ElevatedJob { Id = "1", Kind = "uninstall-restore", Exclusive = false }));
    }

    private static void AssertRefused(List<JsonElement> lines, string original)
    {
        var result = lines.Single(l => l.GetProperty("type").GetString() == "result");
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(0, result.GetProperty("restoredCount").GetInt32());
        Assert.Contains(lines, l => l.GetProperty("type").GetString() == "done");
        Assert.False(Directory.Exists(original));
    }
}
