using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// The registry cleaner deletes registry values/keys, so, same standard as <see cref="CleanupSafetyTests"/>
/// for the file cleanup, the "is this really broken" judgment is pure and gets its own tests. The core
/// promise: never guess. An unparseable or ambiguous value is never treated as broken, even when it looks
/// suspicious, because a wrong delete here cannot be undone by anything short of the mandatory backup.
/// </summary>
public class RegistryCleanupSafetyTests
{
    [Fact]
    public void A_bare_existing_path_is_not_broken() =>
        Assert.False(RegistryCleanupSafety.IsBrokenFileReference(@"C:\Windows\explorer.exe", _ => true));

    [Fact]
    public void A_bare_missing_path_is_broken() =>
        Assert.True(RegistryCleanupSafety.IsBrokenFileReference(@"C:\Foo\Bar\gone.exe", _ => false));

    [Fact]
    public void A_quoted_missing_path_is_broken() =>
        Assert.True(RegistryCleanupSafety.IsBrokenFileReference(@"""C:\Foo\Bar\gone.exe""", _ => false));

    [Fact]
    public void A_quoted_path_with_trailing_arguments_reads_only_the_quoted_part()
    {
        var path = RegistryCleanupSafety.ExtractFilePath(@"""C:\Foo\Bar\app.exe"" --minimized --flag value", _ => true);
        Assert.Equal(@"C:\Foo\Bar\app.exe", path);
    }

    [Fact]
    public void An_icon_index_suffix_is_stripped()
    {
        var path = RegistryCleanupSafety.ExtractFilePath(@"C:\Foo\Bar\app.exe,0", _ => true);
        Assert.Equal(@"C:\Foo\Bar\app.exe", path);
    }

    /// <summary>The proven splitter: an unquoted value with a space is only parseable when a prefix ends on
    /// a token boundary AND carries a known executable extension, existence of that candidate anywhere
    /// then decides the whole judgment, never a guess about where the argument boundary really is.</summary>
    [Fact]
    public void An_unquoted_path_with_a_space_that_exists_is_kept()
    {
        var path = RegistryCleanupSafety.ExtractFilePath(
            @"C:\Program Files\Foo\foo.exe --minimized", p => p == @"C:\Program Files\Foo\foo.exe");
        Assert.Equal(@"C:\Program Files\Foo\foo.exe", path);
        Assert.False(RegistryCleanupSafety.IsBrokenFileReference(
            @"C:\Program Files\Foo\foo.exe --minimized", p => p == @"C:\Program Files\Foo\foo.exe"));
    }

    [Fact]
    public void An_unquoted_path_with_a_space_that_is_missing_is_reported()
    {
        var path = RegistryCleanupSafety.ExtractFilePath(@"C:\Program Files\Foo\foo.exe --minimized", _ => false);
        Assert.Equal(@"C:\Program Files\Foo\foo.exe", path); // the longest candidate, none of them exist
        Assert.True(RegistryCleanupSafety.IsBrokenFileReference(
            @"C:\Program Files\Foo\foo.exe --minimized", _ => false));
    }

    /// <summary>No token boundary in the value carries a known executable extension at all (a plain
    /// sentence, or arguments that just happen to contain spaces), still never guessed.</summary>
    [Fact]
    public void An_unquoted_value_with_a_space_but_no_executable_extension_is_never_guessed()
    {
        Assert.Null(RegistryCleanupSafety.ExtractFilePath(@"C:\Program Files\Foo\readme.txt and more", _ => true));
        Assert.False(RegistryCleanupSafety.IsBrokenFileReference(@"C:\Program Files\Foo\readme.txt and more", _ => false));
    }

    [Fact]
    public void An_unterminated_quote_is_never_guessed() =>
        Assert.Null(RegistryCleanupSafety.ExtractFilePath(@"""C:\Foo\Bar\app.exe", _ => true));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_or_missing_value_is_never_broken(string? value) =>
        Assert.False(RegistryCleanupSafety.IsBrokenFileReference(value, _ => false));

    [Fact]
    public void A_bare_value_that_is_only_an_icon_index_with_nothing_before_it_is_never_guessed() =>
        Assert.Null(RegistryCleanupSafety.ExtractFilePath(",0", _ => true));

    [Fact]
    public void All_broken_paths_requires_every_present_field_to_be_confidently_dead()
    {
        var ok = RegistryCleanupSafety.TryGetAllBrokenPaths(
            [@"C:\Foo\gone.dll", @"C:\Bar\gone.exe"], _ => false, out var paths);

        Assert.True(ok);
        Assert.Equal([@"C:\Foo\gone.dll", @"C:\Bar\gone.exe"], paths);
    }

    [Fact]
    public void One_still_working_field_keeps_the_whole_entry()
    {
        // e.g. a CLSID whose InprocServer32 is a stale leftover but LocalServer32 still activates fine.
        var ok = RegistryCleanupSafety.TryGetAllBrokenPaths(
            [@"C:\Foo\gone.dll", @"C:\Bar\still-here.exe"], path => path.EndsWith("still-here.exe"), out _);

        Assert.False(ok);
    }

    [Fact]
    public void An_ambiguous_field_aborts_the_whole_judgment_even_if_the_other_field_is_broken()
    {
        // e.g. DisplayIcon points nowhere, but UninstallString is an unquoted "path with a space" that
        // never reaches a known executable extension at any token boundary, still not confidently
        // parseable, so it aborts the judgment even though the other field is broken.
        var ok = RegistryCleanupSafety.TryGetAllBrokenPaths(
            [@"C:\Missing\app.ico", @"C:\Program Files\App\uninstall notes and more"], _ => false, out var paths);

        Assert.False(ok);
        Assert.Empty(paths);
    }

    [Fact]
    public void No_fields_present_at_all_is_never_broken() =>
        Assert.False(RegistryCleanupSafety.TryGetAllBrokenPaths([null, ""], _ => false, out _));

    // ---- A bare, unqualified name is never confidently a path ----

    [Fact]
    public void A_bare_unqualified_dll_name_is_never_guessed_as_a_path() =>
        // mscoree.dll: a perfectly normal COM registration resolved through the OS's own DLL search path,
        // not a literal path, checking File.Exists("mscoree.dll") against the current directory would
        // almost always say "missing" and delete a working registration.
        Assert.Null(RegistryCleanupSafety.ExtractFilePath("mscoree.dll", _ => true));

    [Fact]
    public void A_bare_unqualified_dll_name_is_never_broken_even_if_fileExists_says_no() =>
        Assert.False(RegistryCleanupSafety.IsBrokenFileReference("mscoree.dll", _ => false));

    [Fact]
    public void A_drive_relative_path_without_a_drive_letter_is_never_guessed() =>
        // Rooted (starts with \), but not FULLY qualified, still depends on "whichever drive is current".
        Assert.Null(RegistryCleanupSafety.ExtractFilePath(@"\Windows\System32\mscoree.dll", _ => true));

    [Fact]
    public void A_quoted_but_unqualified_name_is_never_guessed() =>
        Assert.Null(RegistryCleanupSafety.ExtractFilePath("\"mscoree.dll\"", _ => true));

    [Fact]
    public void A_fully_qualified_quoted_path_still_works() =>
        Assert.Equal(@"C:\Windows\System32\mscoree.dll",
            RegistryCleanupSafety.ExtractFilePath("\"C:\\Windows\\System32\\mscoree.dll\"", _ => true));

    // ---- MSI product-code references (item 8.3) ----

    [Theory]
    [InlineData("MsiExec.exe /X{AC76BA86-7AD7-1033-7B44-AC0F074E4100}", "{AC76BA86-7AD7-1033-7B44-AC0F074E4100}")]
    [InlineData("MsiExec.exe /I{AC76BA86-7AD7-1033-7B44-AC0F074E4100}", "{AC76BA86-7AD7-1033-7B44-AC0F074E4100}")]
    [InlineData("\"MsiExec.exe\" /X{ac76ba86-7ad7-1033-7b44-ac0f074e4100}", "{ac76ba86-7ad7-1033-7b44-ac0f074e4100}")]
    public void ExtractMsiProductCode_reads_the_guid_from_an_uninstall_or_repair_command(string command, string expected) =>
        Assert.Equal(expected, RegistryCleanupSafety.ExtractMsiProductCode(command));

    [Fact]
    public void ExtractMsiProductCode_is_null_for_a_non_msi_command() =>
        Assert.Null(RegistryCleanupSafety.ExtractMsiProductCode(@"C:\Program Files\App\uninstall.exe /uninstall"));

    [Fact]
    public void ExtractMsiProductCode_is_null_for_an_empty_or_missing_command() =>
        Assert.Null(RegistryCleanupSafety.ExtractMsiProductCode(null));

    /// <summary>Hand-verified against the standard Windows Installer compressed-GUID algorithm: the first
    /// three fields byte-order reversed, the last two nibble-swapped per byte with the byte order kept,
    /// "01234567" becoming "67452301" is the textbook illustration of the first step.</summary>
    [Fact]
    public void PackProductCode_matches_the_documented_windows_installer_algorithm()
    {
        var packed = RegistryCleanupSafety.PackProductCode("01234567-89ab-cdef-0123-456789abcdef");
        Assert.Equal("67452301ab89efcd1032547698badcfe", packed);
    }

    [Fact]
    public void PackProductCode_is_case_insensitive_and_ignores_braces() =>
        Assert.Equal(
            RegistryCleanupSafety.PackProductCode("01234567-89ab-cdef-0123-456789abcdef"),
            RegistryCleanupSafety.PackProductCode("{01234567-89AB-CDEF-0123-456789ABCDEF}"));

    [Fact]
    public void PackProductCode_is_null_for_something_that_is_not_a_guid() =>
        Assert.Null(RegistryCleanupSafety.PackProductCode("not-a-guid"));
}

/// <summary>The pure parts of the mandatory pre-write backup: merging exports and same-day filenames.</summary>
public class RegistryBackupServiceTests
{
    [Fact]
    public void Merging_keeps_exactly_one_header_line()
    {
        var merged = RegistryBackupService.MergeSections(
        [
            "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\Foo]\r\n\"a\"=\"1\"\r\n",
            "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\Bar]\r\n\"b\"=\"2\"\r\n",
        ]);

        var occurrences = merged.Split("Windows Registry Editor Version 5.00").Length - 1;
        Assert.Equal(1, occurrences);
        Assert.Contains("[HKEY_LOCAL_MACHINE\\Foo]", merged);
        Assert.Contains("[HKEY_LOCAL_MACHINE\\Bar]", merged);
    }

    /// <summary>A VALUE's own data can legitimately contain the header text (a path, a description, any
    /// string). Only the section's own leading header line may be stripped, a blind Replace over the
    /// whole section would destroy that data too.</summary>
    [Fact]
    public void A_header_string_inside_a_values_data_survives_merging()
    {
        var merged = RegistryBackupService.MergeSections(
        [
            "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\Foo]\r\n\"a\"=\"1\"\r\n",
            "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\Bar]\r\n" +
            "\"note\"=\"see Windows Registry Editor Version 5.00 docs\"\r\n",
        ]);

        var occurrences = merged.Split("Windows Registry Editor Version 5.00").Length - 1;
        Assert.Equal(2, occurrences); // the one merged header line, plus the value's own data intact
        Assert.Contains("\"note\"=\"see Windows Registry Editor Version 5.00 docs\"", merged);
    }

    [Fact]
    public void First_backup_of_the_day_gets_a_plain_date_name()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"wu-regbackup-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var now = new DateTime(2026, 7, 31, 9, 0, 0);
            var path = RegistryBackupService.BuildBackupPath(folder, now);
            Assert.EndsWith("ultimate updater registry backup 31.07.2026.reg", path);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_same_day_rerun_gets_a_time_suffix_instead_of_overwriting()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"wu-regbackup-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var now = new DateTime(2026, 7, 31, 14, 30, 0);
            var firstRun = RegistryBackupService.BuildBackupPath(folder, now);
            File.WriteAllText(firstRun, "already here");

            var secondRun = RegistryBackupService.BuildBackupPath(folder, now);

            Assert.NotEqual(firstRun, secondRun);
            Assert.EndsWith("ultimate updater registry backup 31.07.2026 14-30.reg", secondRun);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// two categories backed up in the same minute both built the same
    /// "date HH-mm.reg" name and the second silently overwrote the first. The collision suffix is what
    /// FileMode.CreateNew's retry loop in BackupBeforeWriteAsync falls back to.
    /// </summary>
    [Fact]
    public void A_collision_suffix_produces_a_distinct_name_from_the_same_moment()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"wu-regbackup-test-{Guid.NewGuid():N}");
        var now = new DateTime(2026, 7, 31, 14, 30, 0);

        var plain = RegistryBackupService.BuildBackupPath(folder, now);
        var suffixed2 = RegistryBackupService.BuildBackupPath(folder, now, collisionSuffix: 2);
        var suffixed3 = RegistryBackupService.BuildBackupPath(folder, now, collisionSuffix: 3);

        Assert.NotEqual(plain, suffixed2);
        Assert.NotEqual(suffixed2, suffixed3);
        Assert.EndsWith("ultimate updater registry backup 31.07.2026 14-30 (2).reg", suffixed2);
        Assert.EndsWith("ultimate updater registry backup 31.07.2026 14-30 (3).reg", suffixed3);
    }

    /// <summary>The new default (%APPDATA%\UltimateUpdater\registry-backups) must itself still pass the
    /// same profile-boundary gate every OTHER backup folder is held to, a default that failed its own
    /// safety check would silently stop registry cleanup from ever backing anything up.</summary>
    [Fact]
    public void The_new_default_registry_backup_folder_is_within_the_user_profile() =>
        Assert.True(RegistryBackupService.IsWithinUserProfile(RegistryCleanupSettings.DefaultBackupFolder));

    [Theory]
    [InlineData(@"C:\Users\TestUser", @"C:\Users\TestUser", true)]
    [InlineData(@"C:\Users\TestUser\Documents\Backups", @"C:\Users\TestUser", true)]
    [InlineData(@"C:\Users\TestUser2", @"C:\Users\TestUser", false)] // sibling folder that merely starts with the same text
    [InlineData(@"C:\Windows\System32", @"C:\Users\TestUser", false)]
    [InlineData(@"C:\Users\Other\Documents", @"C:\Users\TestUser", false)]
    [InlineData("", @"C:\Users\TestUser", false)]
    public void Backup_folder_boundary_matches_only_the_users_own_profile_tree(string backupFolder, string profile, bool expected) =>
        Assert.Equal(expected, RegistryBackupService.IsWithinUserProfile(backupFolder, profile));

    /// <summary>
    /// The backup folder must be checked for a junction before it is created or written to, not
    /// after; this proves the ordering holds for a junction planted in the profile ahead of time.
    /// </summary>
    [Fact]
    public async Task Backup_refuses_a_folder_behind_a_junction_and_never_creates_it()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(profile, "wu-regbackup-test-" + Guid.NewGuid());
        var target = Path.Combine(profile, "wu-regbackup-target-" + Guid.NewGuid());
        var link = Path.Combine(root, "link");
        var backupFolder = Path.Combine(link, "sub");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);

        try
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
            mklink.WaitForExit(5000);
            Assert.Equal(0, mklink.ExitCode);

            var result = await RegistryBackupService.BackupBeforeWriteAsync(
                ["HKEY_CURRENT_USER\\Software\\UltimateUpdaterTestDoesNotExist"],
                backupFolder, 5, new UltimateUpdater.Services.LoggingService(), CancellationToken.None);

            Assert.Null(result);
            // must never exist, the folder is checked before any create or write touches it
            Assert.False(Directory.Exists(backupFolder));
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(target, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Whether a key exists is checked independently, via the registry itself, before reg.exe ever runs,
    /// never inferred from whether reg.exe happened to leave a temp file behind, which cannot distinguish
    /// "the key does not exist" from "the export genuinely failed and left nothing to back up".
    /// </summary>
    [Fact]
    public void KeyExists_is_true_for_a_real_key_and_false_once_removed()
    {
        const string subPath = @"Software\UltimateUpdaterTests\KeyExists";
        using (var created = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subPath))
            Assert.NotNull(created);

        try
        {
            Assert.True(RegistryCleanupScanner.KeyExists($@"HKCU\{subPath}"));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(subPath, throwOnMissingSubKey: false);
        }

        Assert.False(RegistryCleanupScanner.KeyExists($@"HKCU\{subPath}"));
    }

    [Fact]
    public void KeyExists_is_false_for_a_key_that_never_existed() =>
        Assert.False(RegistryCleanupScanner.KeyExists(@"HKCU\Software\UltimateUpdaterTests\DoesNotExist" + Guid.NewGuid()));

    /// <summary>
    /// Delete re-judges each finding with the same logic the scan used, immediately before removing it:
    /// a program can repair an entry in the real wall-clock time between scan and delete, while the user
    /// reviews the confirmation dialog.
    /// </summary>
    [Fact]
    public void Delete_skips_an_App_Paths_entry_that_was_repaired_since_the_scan()
    {
        const string name = "wu-test-repaired.exe";
        const string appPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        var keyPath = $@"HKCU\{appPathsRoot}\{name}";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{appPathsRoot}\{name}"))
            key!.SetValue(string.Empty, @"C:\this-path-does-not-exist-anymore.exe");

        try
        {
            // The finding as the SCAN would have recorded it, broken at the time.
            var finding = new RegistryFinding(
                RegistryCleanupCategory.AppPaths, keyPath, null, name, @"C:\this-path-does-not-exist-anymore.exe");

            // The entry gets fixed before the delete actually runs.
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{appPathsRoot}\{name}", writable: true))
                key!.SetValue(string.Empty, Environment.GetFolderPath(Environment.SpecialFolder.System) + @"\notepad.exe");

            var deleted = RegistryCleanupScanner.Delete([finding], new UltimateUpdater.Services.LoggingService());

            Assert.Equal(0, deleted);
            Assert.True(RegistryCleanupScanner.KeyExists(keyPath));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{appPathsRoot}\{name}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Delete_removes_an_App_Paths_entry_that_is_still_broken()
    {
        const string name = "wu-test-stillbroken.exe";
        const string appPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        var keyPath = $@"HKCU\{appPathsRoot}\{name}";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{appPathsRoot}\{name}"))
            key!.SetValue(string.Empty, @"C:\this-path-does-not-exist-either.exe");

        try
        {
            var finding = new RegistryFinding(
                RegistryCleanupCategory.AppPaths, keyPath, null, name, @"C:\this-path-does-not-exist-either.exe");

            var deleted = RegistryCleanupScanner.Delete([finding], new UltimateUpdater.Services.LoggingService());

            Assert.Equal(1, deleted);
            Assert.False(RegistryCleanupScanner.KeyExists(keyPath));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{appPathsRoot}\{name}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Delete_with_a_cancelled_token_deletes_nothing_and_does_not_throw()
    {
        const string name = "wu-test-cancelled.exe";
        const string appPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        var keyPath = $@"HKCU\{appPathsRoot}\{name}";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{appPathsRoot}\{name}"))
            key!.SetValue(string.Empty, @"C:\this-path-does-not-exist-cancelled.exe");

        try
        {
            var finding = new RegistryFinding(
                RegistryCleanupCategory.AppPaths, keyPath, null, name, @"C:\this-path-does-not-exist-cancelled.exe");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var deleted = RegistryCleanupScanner.Delete([finding], new UltimateUpdater.Services.LoggingService(), cts.Token);

            Assert.Equal(0, deleted);
            Assert.True(RegistryCleanupScanner.KeyExists(keyPath));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{appPathsRoot}\{name}", throwOnMissingSubKey: false);
        }
    }
}

/// <summary>
/// The MSI-specific half of <see cref="RegistryCleanupScanner.TryJudgeUninstallEntryBroken"/> (item 8.3):
/// an entry whose UninstallString is a MsiExec command is judged by whether its product code is still
/// registered, not by file existence. Uses the same real-HKCU-test-key pattern as the App Paths tests above
///, the product-code judgment reads the actual registry, there is no fake registry seam for it.
/// </summary>
public class RegistryCleanupScannerMsiEntryTests
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ProductsRoot = @"SOFTWARE\Microsoft\Installer\Products";

    [Fact]
    public void An_msi_entry_with_a_registered_product_code_is_kept()
    {
        var guid = Guid.NewGuid().ToString();
        var packed = RegistryCleanupSafety.PackProductCode(guid)!;
        var name = "wu-test-msi-registered-" + Guid.NewGuid().ToString("N");
        var keyPath = $@"HKCU\{UninstallRoot}\{name}";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{UninstallRoot}\{name}"))
        {
            key!.SetValue("DisplayName", "Test MSI App");
            key.SetValue("UninstallString", $"MsiExec.exe /X{{{guid}}}");
        }
        using var productKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{ProductsRoot}\{packed}");

        try
        {
            Assert.False(RegistryCleanupScanner.TryJudgeUninstallEntryBroken(keyPath, out _));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{UninstallRoot}\{name}", throwOnMissingSubKey: false);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{ProductsRoot}\{packed}", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void An_msi_entry_with_an_unregistered_product_code_and_no_install_location_is_reported()
    {
        var guid = Guid.NewGuid().ToString(); // never registered anywhere
        var name = "wu-test-msi-unregistered-" + Guid.NewGuid().ToString("N");
        var keyPath = $@"HKCU\{UninstallRoot}\{name}";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{UninstallRoot}\{name}"))
        {
            key!.SetValue("DisplayName", "Test MSI App");
            key.SetValue("UninstallString", $"MsiExec.exe /X{{{guid}}}");
        }

        try
        {
            Assert.True(RegistryCleanupScanner.TryJudgeUninstallEntryBroken(keyPath, out var claimedPath));
            Assert.Contains(guid, claimedPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{UninstallRoot}\{name}", throwOnMissingSubKey: false);
        }
    }

    /// <summary>Existence anywhere means keep: an unregistered product code is still not broken when the
    /// entry's own InstallLocation still points somewhere, the same "one field still confirms it" rule
    /// the file-path side already follows.</summary>
    [Fact]
    public void An_msi_entry_with_an_unregistered_product_code_but_a_present_install_location_is_kept()
    {
        var guid = Guid.NewGuid().ToString();
        var name = "wu-test-msi-installloc-" + Guid.NewGuid().ToString("N");
        var keyPath = $@"HKCU\{UninstallRoot}\{name}";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{UninstallRoot}\{name}"))
        {
            key!.SetValue("DisplayName", "Test MSI App");
            key.SetValue("UninstallString", $"MsiExec.exe /X{{{guid}}}");
            key.SetValue("InstallLocation", @"C:\Program Files\Test MSI App");
        }

        try
        {
            Assert.False(RegistryCleanupScanner.TryJudgeUninstallEntryBroken(keyPath, out _));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{UninstallRoot}\{name}", throwOnMissingSubKey: false);
        }
    }
}

/// <summary>
/// <see cref="RegistryCleanupScanner.IsUnderRoots"/> is the one check standing between a finding
/// and CleanupService actually deleting it. A finding whose KeyPath equals the bare root, with no
/// ValueName, means "delete this whole root key" - legitimate ONLY for the two categories whose real
/// findings ARE values living directly inside the root (SharedDlls, Autostart); for every other category a
/// real finding is always a genuine subkey, and the equals branch must not validate one. This fix: the
/// equals branch itself now also requires a genuine, non-empty ValueName even for those two categories,
/// closing the one shape that check used to wave through on the key path alone.
/// </summary>
public class RegistryCleanupScannerIsUnderRootsTests
{
    private const string SomeValueName = "some-value";

    [Theory]
    [InlineData(RegistryCleanupCategory.AppPaths, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\notepad.exe", null, true)]
    [InlineData(RegistryCleanupCategory.AppPaths, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", null, false)]
    [InlineData(RegistryCleanupCategory.ActiveXCom, @"HKLM\SOFTWARE\Classes\CLSID\{00000000-0000-0000-0000-000000000000}", null, true)]
    [InlineData(RegistryCleanupCategory.ActiveXCom, @"HKLM\SOFTWARE\Classes\CLSID", null, false)]
    [InlineData(RegistryCleanupCategory.UninstallEntries, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SomeApp", null, true)]
    [InlineData(RegistryCleanupCategory.UninstallEntries, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", null, false)]
    [InlineData(RegistryCleanupCategory.SharedDlls, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", SomeValueName, true)]
    [InlineData(RegistryCleanupCategory.Autostart, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", SomeValueName, true)]
    // The "delete the whole key" shape (root key path, no value name) must never validate, not
    // even for the two categories whose ordinary findings ARE the root key plus a value name.
    [InlineData(RegistryCleanupCategory.SharedDlls, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", null, false)]
    [InlineData(RegistryCleanupCategory.SharedDlls, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", "", false)]
    [InlineData(RegistryCleanupCategory.Autostart, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", null, false)]
    [InlineData(RegistryCleanupCategory.Autostart, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "", false)]
    [InlineData(RegistryCleanupCategory.AppPaths, "", null, false)]
    [InlineData(RegistryCleanupCategory.AppPaths, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App PathsButNotReally\x", null, false)]
    public void IsUnderRoots_requires_a_real_child_key_except_where_the_root_itself_is_a_value_holder(
        RegistryCleanupCategory category, string keyPath, string? valueName, bool expected) =>
        Assert.Equal(expected, RegistryCleanupScanner.IsUnderRoots(category, keyPath, valueName));

    [Fact]
    public void Undeletable_findings_are_logged_as_one_summary_line_with_a_short_sample()
    {
        var one = RegistryCleanupScanner.NotOfferedSummary(RegistryCleanupCategory.AppPaths, [@"HKLM\A"]);
        Assert.Contains("1 entry cannot be deleted", one);
        Assert.Contains(@"is not offered: HKLM\A.", one);

        var five = RegistryCleanupScanner.NotOfferedSummary(
            RegistryCleanupCategory.AppPaths, [@"HKLM\A", @"HKLM\B", @"HKLM\C", @"HKLM\D", @"HKLM\E"]);
        Assert.Contains("5 entries cannot be deleted", five);
        Assert.Contains(@"are not offered: HKLM\A, HKLM\B, HKLM\C and 2 more.", five);
        Assert.DoesNotContain(@"HKLM\D", five);
    }
}

/// <summary>
/// A file on a drive or share that is not available right now is unknown, never broken
/// (<see cref="RegistryCleanupScanner.ExistsInAnyView(string, Func{string, bool})"/>).
/// </summary>
public class RegistryCleanupUnavailableLocationTests
{
    [Fact]
    public void A_missing_file_on_an_unavailable_drive_counts_as_existing()
    {
        Assert.True(RegistryCleanupScanner.ExistsInAnyView(@"Q:\Tools\app.exe", _ => false));
    }

    [Fact]
    public void A_missing_file_on_a_ready_drive_is_still_missing()
    {
        Assert.False(RegistryCleanupScanner.ExistsInAnyView(@"Q:\Tools\definitely-missing-4711.exe", _ => true));
    }

    [Fact]
    public void A_unc_path_counts_as_existing_without_asking_for_the_root()
    {
        var asked = false;
        Assert.True(RegistryCleanupScanner.ExistsInAnyView(@"\\server\share\app.exe", _ => { asked = true; return true; }));
        Assert.False(asked);
    }

    [Fact]
    public void A_relative_path_names_no_drive_and_is_not_judged_unavailable()
    {
        Assert.False(RegistryCleanupScanner.IsOnUnavailableLocation("app.exe", _ => false));
    }

    [Fact]
    public void An_existing_file_is_found_whatever_the_drive_state_says()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "UltimateUpdater.Tests.dll");
        Assert.True(RegistryCleanupScanner.ExistsInAnyView(file, _ => false));
    }
}
