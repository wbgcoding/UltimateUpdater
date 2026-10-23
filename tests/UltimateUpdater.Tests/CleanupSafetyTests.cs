using System.Diagnostics;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// The cleanup feature deletes files, so its guard is the most important thing about it. These tests
/// encode the required promise: temp and cache locations may be emptied, and a real user folder must NEVER
/// be, not Downloads, not Documents, not a profile, not a drive root.
/// </summary>
public class CleanupSafetyTests
{
    [Fact]
    public void The_users_own_temp_folder_is_safe_to_empty() =>
        Assert.True(CleanupSafety.IsSafeToEmpty(Path.GetTempPath()));

    /// <summary>
    /// AppCacheDiscovery walks real machine folders, this asserts the one invariant that
    /// matters regardless of what happens to be on the machine running the test: everything it returns
    /// still passes the same gate every other cleanup target passes through.
    /// </summary>
    [Fact]
    public void App_cache_discovery_never_returns_a_directory_the_safety_gate_would_refuse()
    {
        var found = AppCacheDiscovery.DiscoverCacheDirectories();

        Assert.All(found, dir => Assert.True(CleanupSafety.IsSafeToEmpty(dir)));
    }

    /// <summary>A program folder directly under AppData that only ends in "cache" holds the program's own data.</summary>
    [Theory]
    [InlineData("Geocache")]
    [InlineData("SmartCache")]
    public void App_cache_discovery_rejects_a_top_level_folder_that_merely_ends_in_cache(string name) =>
        Assert.False(AppCacheDiscovery.NameQualifies(name, depth: 1));

    [Theory]
    [InlineData("Cache")]
    [InlineData("cache")]
    [InlineData("Logs")]
    [InlineData("CrashDumps")]
    public void App_cache_discovery_accepts_the_exact_cache_names_at_top_level(string name) =>
        Assert.True(AppCacheDiscovery.NameQualifies(name, depth: 1));

    [Fact]
    public void App_cache_discovery_keeps_the_loose_cache_rule_one_level_deeper()
    {
        Assert.True(AppCacheDiscovery.NameQualifies(Path.Combine("Vendor", "DXCache"), depth: 2));
        Assert.False(AppCacheDiscovery.NameQualifies(Path.Combine("Vendor", "Data"), depth: 2));
        // The cache-like name belongs to the program folder here, not to the child.
        Assert.False(AppCacheDiscovery.NameQualifies(Path.Combine("Geocache", "Data"), depth: 2));
    }

    // ---- the app must never delete a running program out from under itself ----

    /// <summary>
    /// A single-file.NET app extracts its native libraries into %TEMP%\.net and loads them from there for
    /// its whole life, so emptying Temp took this app's own DLLs with it and it crashed with a
    /// DllNotFoundException. The whole extraction root is off limits, not only our own subfolder: every
    /// other single-file app on the machine has exactly the same dependency on it.
    /// </summary>
    [Fact]
    public void The_single_file_extraction_root_is_never_emptied()
    {
        var extraction = Path.Combine(Path.GetTempPath(), ".net");

        Assert.True(CleanupSafety.IsOwnPath(extraction));
        Assert.True(CleanupSafety.IsOwnPath(Path.Combine(extraction, "Ultimate Updater", "abc123")));
        Assert.False(CleanupSafety.IsSafeToEmpty(extraction));
        Assert.False(CleanupSafety.IsDeletableFile(
            Path.GetTempPath(), Path.Combine(extraction, "Ultimate Updater", "abc123", "wpfgfx.dll")));
    }

    /// <summary>
    /// Temp itself must stay cleanable even though it CONTAINS a protected path, the protection applies to
    /// the files and subdirectories being removed, never to the target folder.
    /// </summary>
    [Fact]
    public void Containing_a_protected_path_does_not_make_temp_unclearable()
    {
        Assert.True(CleanupSafety.IsSafeToEmpty(Path.GetTempPath()));
        Assert.True(CleanupSafety.ContainsOwnPath(Path.GetTempPath()));
        Assert.False(CleanupSafety.IsOwnPath(Path.GetTempPath()));
    }

    [Fact]
    public void An_ordinary_temp_file_is_still_deletable()
    {
        var file = Path.Combine(Path.GetTempPath(), "some-installer-log.txt");

        Assert.True(CleanupSafety.IsDeletableFile(Path.GetTempPath(), file));
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.MyDocuments)]
    [InlineData(Environment.SpecialFolder.MyPictures)]
    [InlineData(Environment.SpecialFolder.MyMusic)]
    [InlineData(Environment.SpecialFolder.MyVideos)]
    [InlineData(Environment.SpecialFolder.DesktopDirectory)]
    [InlineData(Environment.SpecialFolder.UserProfile)]
    [InlineData(Environment.SpecialFolder.Windows)]
    [InlineData(Environment.SpecialFolder.System)]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    [InlineData(Environment.SpecialFolder.LocalApplicationData)]
    [InlineData(Environment.SpecialFolder.ApplicationData)]
    public void A_protected_folder_is_never_safe_to_empty(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(path))
            return;      // not present on this machine, nothing to protect

        Assert.False(CleanupSafety.IsSafeToEmpty(path));
    }

    /// <summary>The one folder named explicitly in the requirement: the Downloads folder must never be emptied.</summary>
    [Fact]
    public void The_downloads_folder_is_never_safe_to_empty()
    {
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        Assert.False(CleanupSafety.IsSafeToEmpty(downloads));
    }

    [Fact]
    public void A_drive_root_is_never_safe_to_empty()
    {
        var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Assert.False(CleanupSafety.IsSafeToEmpty(root!));
    }

    /// <summary>An ancestor of a protected folder would take it with it.</summary>
    [Fact]
    public void A_parent_of_a_protected_folder_is_never_safe_to_empty()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var usersRoot = Path.GetDirectoryName(profile);

        Assert.False(CleanupSafety.IsSafeToEmpty(usersRoot!));
    }

    /// <summary>
    /// An ordinary directory outside any temp/cache path is refused even though it exists and is writable,
    /// the guard demands a temp/cache location, so a mistyped catalogue entry cannot delete a project folder.
    /// (Anything genuinely UNDER a Temp folder is fair game, which is why this test stays out of %TEMP%.)
    /// </summary>
    [Fact]
    public void A_directory_that_is_not_a_temp_or_cache_location_is_refused()
    {
        var plain = Path.Combine(AppContext.BaseDirectory, "wu-safety-plain");
        Directory.CreateDirectory(plain);
        try
        {
            Assert.False(CleanupSafety.IsSafeToEmpty(plain));
        }
        finally
        {
            Directory.Delete(plain, recursive: true);
        }
    }

    [Fact]
    public void A_missing_or_empty_path_is_refused()
    {
        Assert.False(CleanupSafety.IsSafeToEmpty(string.Empty));
        Assert.False(CleanupSafety.IsSafeToEmpty("   "));
        Assert.False(CleanupSafety.IsSafeToEmpty(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())));
    }

    /// <summary>In the Explorer folder only the cache files may go, Explorer's own settings live there too.</summary>
    [Fact]
    public void Only_cache_files_are_deletable_in_the_explorer_folder()
    {
        var explorer = Path.Combine("C:", "Users", "someone", "AppData", "Local", "Microsoft", "Windows", "Explorer");

        Assert.True(CleanupSafety.IsDeletableFile(explorer, Path.Combine(explorer, "thumbcache_256.db")));
        Assert.True(CleanupSafety.IsDeletableFile(explorer, Path.Combine(explorer, "iconcache_32.db")));
        Assert.False(CleanupSafety.IsDeletableFile(explorer, Path.Combine(explorer, "Something else.dat")));
    }

    [Fact]
    public void Every_catalogue_directory_passes_the_guard_or_does_not_exist()
    {
        foreach (var target in CleanupCatalog.Build())
        {
            foreach (var directory in target.Directories)
            {
                // The catalogue is hard-coded, so anything it lists that DOES exist must be acceptable,
                // a mismatch here means the catalogue and the guard have drifted apart. Except a path that
                // leads through a link, refused ON PURPOSE (a junction could aim anywhere):
                // "all-users-temp" walks every C:\Users\* entry, which on every real Windows install
                // includes "Default User"/"All Users", OS-created backward-compatibility junctions, not
                // something this app should ever trust just because Windows made it.
                if (Directory.Exists(directory) && !CleanupSafety.AncestryLeadsThroughLink(directory))
                    Assert.True(CleanupSafety.IsSafeToEmpty(directory), $"catalogue path refused: {directory}");
            }
        }
    }

    /// <summary>
    /// Everything starts switched ON except the three whose contents someone might still want: the recycle
    /// bin, the kernel crash dumps and Windows' own logs, the last two are precisely what
    /// a person diagnosing a blue screen or a failed update needs, so they are offered rather than assumed.
    /// </summary>
    [Fact]
    public void Only_the_targets_someone_might_still_want_start_switched_off()
    {
        // The 2 registry-cleanup rows join this list too: a registry writer earns no
        // default trust the way an obviously-safe temp folder does. So does the app-cache
        // sweep, broader and less individually reviewed than the rest of this table.
        string[] offByDefault =
        [
            "recycle-bin", "memory-dumps", "windows-logs",
            // The two repair rows free nothing and run for a long time: opt-in, never part of a default run.
            "system-repair-dism", "system-repair-sfc",
            "registry-orphaned-references", "registry-orphaned-uninstall",
            "driver-cleanup",
            "old-python-versions",
            "app-cache-sweep",
            // ResetBase removes the ability to uninstall an installed Windows update, the most
            // consequential single row in the whole catalogue; it must be a deliberate opt-in.
            "component-resetbase",
        ];

        var targets = CleanupCatalog.Build();

        // This fix's per-drive defragmentation targets are dynamic ids (one per fixed drive on THIS
        // machine), never in the fixed list above, so they are matched by kind instead: a multi-hour
        // optimisation earns no default trust either, same reasoning as the registry writers above.
        bool IsOffByDefault(CleanupTarget t) => offByDefault.Contains(t.Id) || t.Kind == CleanupKind.Defragmentation;

        Assert.All(targets.Where(IsOffByDefault), t => Assert.False(t.DefaultEnabled));
        Assert.All(targets.Where(t => !IsOffByDefault(t)), t => Assert.True(t.DefaultEnabled));
    }

    /// <summary>Folders that are called a cache but hold things a program needs later (installer copies for
    /// repair or uninstall, Store app data, the browser history database) are never a temp or cache location.</summary>
    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Package Cache")]
    [InlineData(@"C:\ProgramData\Package Cache\{abc}")]
    [InlineData(@"C:\Users\x\AppData\Local\Packages\App_x\LocalCache")]
    [InlineData(@"C:\Users\x\AppData\Local\Packages\App_x\LocalCache\Local\cache")]
    [InlineData(@"C:\Users\x\AppData\Local\Microsoft\Windows\WebCache")]
    public void Caches_that_hold_data_a_program_needs_are_not_cache_locations(string path) =>
        Assert.False(CleanupSafety.LooksLikeTempOrCacheLocation(path));

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\npm-cache")]
    [InlineData(@"C:\Users\x\AppData\Local\NVIDIA\DXCache")]
    [InlineData(@"C:\Users\x\AppData\Local\D3DSCache")]
    public void Ordinary_caches_are_still_cache_locations(string path) =>
        Assert.True(CleanupSafety.LooksLikeTempOrCacheLocation(path));

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Vendor\Cache", true, @"Vendor\Cache")]
    [InlineData(@"C:\Users\x\AppData\Local\Vendor\Cache\", true, @"Vendor\Cache")]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\foo", false, "foo")]
    public void A_swept_cache_row_is_named_after_its_parent_folder_too(string path, bool sweep, string expected) =>
        Assert.Equal(expected, CleanupService.EntryDisplayName(path, sweep));

    // Every directory the catalogue names has to pass the safety gate, or the target silently cleans
    // nothing and looks exactly like an empty folder, including caches like D3DSCache, npm-cache and
    // .gradle\caches (this relies on the whole-segment "cache" match).
    // ---- junction detection: a link anywhere in the ancestry chain, not just the checked leaf ----

    private static void CreateJunction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo
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
    }

    /// <summary>
    /// Before the fix, only the LEAF directory handed to <c>IsSafeToEmpty</c> was checked for being a
    /// reparse point, a junction one level further up went undetected entirely, because path resolution
    /// follows it silently. A user can plant one right where the catalogue expects an ordinary folder.
    /// </summary>
    [Fact]
    public void A_junction_higher_up_the_chain_is_detected_not_just_the_leaf()
    {
        var root = Path.Combine(Path.GetTempPath(), "wu-safety-junction-" + Guid.NewGuid());
        var target = Path.Combine(Path.GetTempPath(), "wu-safety-junction-target-" + Guid.NewGuid());
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        try
        {
            CreateJunction(link, target);
            var leafUnderLink = Path.Combine(link, "cache");

            Assert.True(CleanupSafety.AncestryLeadsThroughLink(leafUnderLink));
            Assert.False(CleanupSafety.IsSafeToEmpty(leafUnderLink));
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(target, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The whole point of holding the directory open: while the handle lives, Windows itself
    /// refuses to rename or delete that filesystem object, which is exactly how a user would try to swap a
    /// checked-safe directory for a junction between the check and a deletion pass that can take minutes.
    /// </summary>
    [Fact]
    public void A_locked_directory_cannot_be_renamed_out_from_under_the_handle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wu-safety-lock-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            using var handle = CleanupSafety.TryLockForUse(dir);
            Assert.NotNull(handle);

            var renamed = dir + "-renamed";
            Assert.ThrowsAny<IOException>(() => Directory.Move(dir, renamed));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryLockForUse_refuses_a_directory_behind_a_junction()
    {
        var root = Path.Combine(Path.GetTempPath(), "wu-safety-junction2-" + Guid.NewGuid());
        var target = Path.Combine(Path.GetTempPath(), "wu-safety-junction2-target-" + Guid.NewGuid());
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        try
        {
            CreateJunction(link, target);

            Assert.Null(CleanupSafety.TryLockForUse(link));
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(target, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryLockForUse_refuses_a_root_swapped_for_a_junction_after_the_ancestry_check()
    {
        var parent = Path.Combine(Path.GetTempPath(), "wu-safety-rootswap-" + Guid.NewGuid());
        var root = Path.Combine(parent, "root");
        var outside = Path.Combine(parent, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "victim.tmp"), "outside");
        try
        {
            CleanupSafety.BeforeLockOpenForTest.Value = path =>
            {
                Directory.Delete(path);
                CreateJunction(path, outside);
            };

            Assert.Null(CleanupSafety.TryLockForUse(root));
            Assert.True(File.Exists(Path.Combine(outside, "victim.tmp")));
        }
        finally
        {
            CleanupSafety.BeforeLockOpenForTest.Value = null;
            try { Directory.Delete(root); } catch { }
            try { Directory.Delete(parent, recursive: true); } catch { }
        }
    }

    // The reparse check alone passes a plain directory reached through a junction ANCESTOR; only the final
    // path comparison refuses it.
    [Fact]
    public void A_handle_reached_through_a_junction_ancestor_is_not_the_candidate_directory()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "wu-safety-ancestor-" + Guid.NewGuid());
        var real = Path.Combine(scratch, "real");
        var link = Path.Combine(scratch, "link");
        Directory.CreateDirectory(Path.Combine(real, "root"));
        try
        {
            CreateJunction(link, real);

            using var handle = OpenDirectoryForTest(Path.Combine(link, "root"));
            Assert.False(handle.IsInvalid);

            Assert.False(CleanupSafety.HandleIsCandidateDirectory(handle, Path.Combine(link, "root")));
            Assert.True(CleanupSafety.HandleIsCandidateDirectory(handle, Path.Combine(real, "root")));
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectoryForTest(string path) =>
        CreateFileW(path, 0x80000000, 0x7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [Fact]
    public void TryLockForUse_locks_a_plain_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "wu-safety-plainroot-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            using var handle = CleanupSafety.TryLockForUse(root);
            Assert.NotNull(handle);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryLockForUse_accepts_a_root_spelled_in_8_3_form()
    {
        var root = Path.Combine(Path.GetTempPath(), "wu-safety-shortname-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var shortForm = ShortPathOf(root);
            if (shortForm is null || string.Equals(shortForm, root, StringComparison.OrdinalIgnoreCase))
                return; // 8.3 names are switched off on this volume

            using var handle = CleanupSafety.TryLockForUse(shortForm);
            Assert.NotNull(handle);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ExcludeOwnTemp_keeps_only_folders_that_are_not_the_own_temp_by_identity()
    {
        var own = Path.Combine(Path.GetTempPath(), "wu-owntemp-" + Guid.NewGuid());
        var other = Path.Combine(Path.GetTempPath(), "wu-othertemp-" + Guid.NewGuid());
        var missing = Path.Combine(Path.GetTempPath(), "wu-missingtemp-" + Guid.NewGuid());
        Directory.CreateDirectory(own);
        Directory.CreateDirectory(other);
        try
        {
            var candidates = new List<string> { own.ToUpperInvariant(), other, missing };
            var shortForm = ShortPathOf(own);
            if (shortForm is not null && !string.Equals(shortForm, own, StringComparison.OrdinalIgnoreCase))
                candidates.Add(shortForm);

            var kept = CleanupCatalog.ExcludeOwnTemp(candidates, own);

            Assert.Equal([other], kept);
        }
        finally
        {
            try { Directory.Delete(own, recursive: true); } catch { }
            try { Directory.Delete(other, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ExcludeOwnTemp_offers_nothing_when_the_own_temp_identity_cannot_be_read()
    {
        var other = Path.Combine(Path.GetTempPath(), "wu-othertemp2-" + Guid.NewGuid());
        Directory.CreateDirectory(other);
        try
        {
            var missing = Path.Combine(Path.GetTempPath(), "wu-missingtemp2-" + Guid.NewGuid());

            Assert.Empty(CleanupCatalog.ExcludeOwnTemp([other], missing));
        }
        finally
        {
            try { Directory.Delete(other, recursive: true); } catch { }
        }
    }

    private static string? ShortPathOf(string path)
    {
        var buffer = new System.Text.StringBuilder(512);
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
        return length == 0 || length >= buffer.Capacity ? null : buffer.ToString();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string lpszLongPath, System.Text.StringBuilder lpszShortPath, uint cchBuffer);

    private const string OwnSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string OtherSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";

    /// <summary>
    /// The gate that authorizes deleting a single recycled file. Everything outside this user's own bin
    /// folder is refused, including the folder itself, another account's folder, and any path that
    /// normalizes its way back out. Fail closed is the whole point: this decides what gets deleted.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Users\someone\Documents\report.docx")]
    [InlineData(@"C:\$Recycle.Bin")]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21-1111111111-2222222222-3333333333-1001")]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21-1111111111-2222222222-3333333333-1001\..\..\Windows\x.dll")]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21-1111111111-2222222222-3333333333-1001\..\S-1-5-18\$RAAA.txt")]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21-1111111111-2222222222-3333333333-1002\$RAAA.txt")]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21-1111111111-2222222222-3333333333-10011\$RAAA.txt")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsInsideOwnRecycleBin_refuses_everything_outside_this_users_own_bin(string path)
    {
        Assert.False(CleanupSafety.IsInsideOwnRecycleBin(path, OwnSid));
    }

    [Fact]
    public void IsInsideOwnRecycleBin_accepts_a_real_item_in_the_users_own_bin()
    {
        Assert.True(CleanupSafety.IsInsideOwnRecycleBin($@"C:\$Recycle.Bin\{OwnSid}\$RAAA.txt", OwnSid));
        Assert.True(CleanupSafety.IsInsideOwnRecycleBin($@"C:\$Recycle.Bin\{OwnSid}\$IAAA.txt", OwnSid));
        Assert.True(CleanupSafety.IsInsideOwnRecycleBin($@"D:\$Recycle.Bin\{OwnSid}\$RBBB\nested\file.txt", OwnSid));

        // Same folder, spelled as Windows itself would resolve it.
        Assert.True(CleanupSafety.IsInsideOwnRecycleBin($@"c:\$recycle.bin\{OwnSid.ToLowerInvariant()}\$raaa.txt", OwnSid));

        // A path under one SID is never inside another's.
        Assert.False(CleanupSafety.IsInsideOwnRecycleBin($@"C:\$Recycle.Bin\{OwnSid}\$RAAA.txt", OtherSid));
    }

    /// <summary>Without a real SID there is no own folder to be inside of, and a caller-composed wildcard
    /// or relative segment must never widen it to every account on the machine.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("..")]
    [InlineData(@"S-1-5-21-1\..\S-1-5-18")]
    [InlineData("not-a-sid")]
    public void IsInsideOwnRecycleBin_refuses_anything_that_is_not_a_sid(string? sid)
    {
        Assert.False(CleanupSafety.IsInsideOwnRecycleBin($@"C:\$Recycle.Bin\{sid}\$RAAA.txt", sid));
        Assert.False(CleanupSafety.IsInsideOwnRecycleBin(@"C:\$Recycle.Bin\S-1-5-18\$RAAA.txt", sid));
    }

    [Fact]
    public void Every_catalogued_directory_passes_the_safety_gate()
    {
        foreach (var target in CleanupCatalog.Build())
        {
            // A folder this machine simply does not have is refused for that reason alone, which says
            // nothing about the catalogue entry. Same "Default User"/"All Users" exception as the sibling
            // test above, those are OS-created junctions, correctly refused.
            foreach (var directory in target.Directories.Where(Directory.Exists)
                         .Where(d => !CleanupSafety.AncestryLeadsThroughLink(d)))
            {
                Assert.True(
                    CleanupSafety.IsSafeToEmpty(directory),
                    $"'{target.Id}' names '{directory}', which CleanupSafety refuses, it would clean nothing.");
            }
        }
    }
}

/// <summary>
/// The adversarial cleanup-path audit: every numbered guard gets its own fixture-backed test here,
/// never a re-read of the code that only agrees with it. Kept in this file on purpose, it is the one
/// file this batch owns for tests, even though several of these exercise <see cref="CleanupService"/>,
/// <see cref="CleanupWorker"/> and <see cref="RegistryBackupService"/> rather than <see cref="CleanupSafety"/>
/// alone.
/// </summary>
public class CleanupAdversarialAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-audit-test-" + Guid.NewGuid());
    private readonly LoggingService _logging = new();

    private static CleanupTarget DirTarget(string id, string directory) => new(
        id, "x", "y", CleanupKind.Directories, DefaultEnabled: true,
        RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
    {
        Directories = [directory],
    };

    // ---- 49.1: every catalogue entry passes the gate, elevation included ----

    /// <summary>
    /// The sibling tests in <see cref="CleanupSafetyTests"/> iterate <c>CleanupCatalog.Build()</c> without
    /// filtering by <see cref="CleanupTarget.NeedsElevation"/>, this proves that coverage is not vacuous:
    /// the catalogue really does carry elevated, directory-bearing entries (windows-temp,
    /// windows-update-cache, delivery-optimization, all-users-temp), so their being checked there is a real
    /// assertion, not an empty loop.
    /// </summary>
    [Fact]
    public void The_catalogue_directory_check_actually_covers_elevated_targets()
    {
        var targets = CleanupCatalog.Build();
        Assert.Contains(targets, t => t.NeedsElevation && t.Directories.Count > 0);
    }

    // ---- 49.3: the empty-directory pass is a snapshot, not a live view ----

    /// <summary>
    /// <c>CleanDirectories</c> takes its file list for a directory ONCE (<c>.ToList()</c>), before deleting
    /// anything from it, and only afterwards walks subdirectories bottom-up for the (separate) empty-folder
    /// pass. A file written into a subdirectory strictly AFTER that snapshot, simulated here through the
    /// progress callback, which only fires from inside the file-deletion loop, after the snapshot was
    /// already taken, is never seen by the file pass and must make the later non-recursive
    /// <c>Directory.Delete</c> on that subdirectory fail, keeping it (and the late file) standing.
    /// </summary>
    [Fact]
    public async Task A_file_arriving_after_the_enumeration_snapshot_keeps_its_directory()
    {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        for (var i = 0; i < 15; i++)
            File.WriteAllText(Path.Combine(sub, $"f{i}.tmp"), "x");

        var lateFile = Path.Combine(sub, "arrived-after-snapshot.tmp");
        var injected = false;
        var progress = new CallbackProgress<CleanupProgress>(_ =>
        {
            if (injected)
                return;
            injected = true;
            File.WriteAllText(lateFile, "late arrival, never in the snapshot");
        });

        var service = new CleanupService(_logging);
        await service.CleanAsync(DirTarget("test-target", _root), progress, CancellationToken.None);

        Assert.True(injected, "the test setup itself failed to trigger, not a finding about the product");
        Assert.True(Directory.Exists(sub), "a directory that gained new content after the snapshot must survive");
        Assert.True(File.Exists(lateFile));
    }

    // ---- 49.4: an exclusion survives a second, later run too ----

    /// <summary>
    /// The "permanently excluded" case (the context-menu action) is the same exclusion list handed to
    /// CleanAsync again on a LATER run, proving it is not a one-shot skip that only holds for the run that
    /// first excluded it.
    /// </summary>
    [Fact]
    public async Task An_excluded_entry_survives_being_cleaned_a_second_time_with_the_same_exclusion()
    {
        var kept = Path.Combine(_root, "KeptVendor");
        Directory.CreateDirectory(kept);
        var keptFile = Path.Combine(kept, "cache1.tmp");
        File.WriteAllText(keptFile, "keep me");
        File.WriteAllText(Path.Combine(_root, "loose.tmp"), "delete me");

        var target = DirTarget("test-target", _root);
        var service = new CleanupService(_logging);

        await service.CleanAsync(target, progress: null, CancellationToken.None, excludedEntryPaths: [kept]);
        Assert.True(File.Exists(keptFile), "must survive the first run");

        await service.CleanAsync(target, progress: null, CancellationToken.None, excludedEntryPaths: [kept]);
        Assert.True(File.Exists(keptFile), "a permanently excluded path must survive a second run too");
    }

    // ---- 49.6: the elevated pipe's target-id resolution ----

    /// <summary>
    /// <see cref="CleanupWorker.ResolveElevatedTargets"/> iterates the fixed catalogue and tests membership
    /// in the ids the pipe carried, never the other way round, which is what makes an id the catalogue does
    /// not recognise, a path handed in where an id belongs, and the same real id repeated several times all
    /// resolve to AT MOST one real target each, with nothing extra. No path ever becomes a target: the
    /// method's return type is drawn entirely from <see cref="CleanupCatalog.Build"/>, never from the input.
    /// </summary>
    [Fact]
    public void ResolveElevatedTargets_rejects_unknown_path_shaped_and_duplicate_ids()
    {
        var targets = CleanupWorker.ResolveElevatedTargets(
        [
            "not-a-real-catalogue-id",
            @"C:\Windows\System32",
            @"C:\Users\someone\Documents\report.docx",
            "windows-temp",
            "windows-temp",
            "windows-temp",
        ]);

        var target = Assert.Single(targets);
        Assert.Equal("windows-temp", target.Id);
    }

    /// <summary>A real catalogue id that does NOT need elevation must still never reach the elevated
    /// worker's target list, an elevated process has no business doing work the app could do itself.</summary>
    [Fact]
    public void ResolveElevatedTargets_ignores_a_real_id_that_does_not_need_elevation()
    {
        Assert.Empty(CleanupWorker.ResolveElevatedTargets(["user-temp"]));
    }

    [Fact]
    public void ResolveElevatedTargets_is_empty_for_no_ids_at_all() =>
        Assert.Empty(CleanupWorker.ResolveElevatedTargets([]));

    // ---- 49.8: repair steps, DISM before sfc, stopping never kills a running step, a stopped run
    // never reads as done ----

    [Fact]
    public void DISM_runs_before_sfc_in_catalogue_order()
    {
        var targets = CleanupCatalog.Build().Select(t => t.Id).ToList();
        var dism = targets.IndexOf("system-repair-dism");
        var sfc = targets.IndexOf("system-repair-sfc");

        Assert.True(dism >= 0 && sfc >= 0, "both repair rows must be in the catalogue");
        Assert.True(dism < sfc, "DISM must run before sfc: DISM repairs the store sfc restores from");
    }

    /// <summary>The two component-store rows come after sfc (catalogue order is run order), the plain
    /// cleanup defaults on, ResetBase off and carries the red risk level, and both run their own DISM
    /// command through the elevated worker's ordinary repair-step path (no special-casing by id anywhere
    /// the worker resolves a job).</summary>
    [Fact]
    public void Component_store_targets_come_after_sfc_with_the_right_defaults()
    {
        var targets = CleanupCatalog.Build();
        var ids = targets.Select(t => t.Id).ToList();
        var sfc = ids.IndexOf("system-repair-sfc");
        var cleanup = ids.IndexOf("component-cleanup");
        var resetBase = ids.IndexOf("component-resetbase");

        Assert.True(sfc >= 0 && cleanup >= 0 && resetBase >= 0);
        Assert.True(sfc < cleanup, "component cleanup must come after the repair rows");
        Assert.True(cleanup < resetBase);

        var cleanupTarget = targets.Single(t => t.Id == "component-cleanup");
        var resetBaseTarget = targets.Single(t => t.Id == "component-resetbase");

        Assert.True(cleanupTarget.DefaultEnabled);
        Assert.Equal(CleanupRiskLevel.Considered, cleanupTarget.RiskLevel);
        Assert.True(cleanupTarget.NeedsElevation);
        Assert.Equal(["/Online", "/Cleanup-Image", "/StartComponentCleanup"], Assert.Single(cleanupTarget.RepairSteps).Arguments);

        Assert.False(resetBaseTarget.DefaultEnabled);
        Assert.Equal(CleanupRiskLevel.Risky, resetBaseTarget.RiskLevel);
        Assert.True(resetBaseTarget.NeedsElevation);
        Assert.Equal(
            ["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase"],
            Assert.Single(resetBaseTarget.RepairSteps).Arguments);

        // The elevated worker's own target resolution is generic over every SystemRepair row (no
        // id-specific branch anywhere it decides what may run elevated); both new ids prove it here rather
        // than trusting that by inspection alone.
        var resolved = CleanupWorker.ResolveElevatedTargets(["component-cleanup", "component-resetbase"]);
        Assert.Equal(2, resolved.Count);
    }

    private static string CmdExePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    /// <summary>
    /// A repair step already running must be allowed to finish, killing DISM or sfc mid-write can leave
    /// the component store inconsistent, while Stop must still keep the NEXT step from ever starting. Uses a
    /// real short-lived process rather than a fake, so the assertion is about what
    /// <c>CleanupService.RunSystemRepairAsync</c> actually does with the token
    /// (<c>CancellationToken.None</c> for the running step, checked only BETWEEN steps), not a mock's idea
    /// of it.
    /// </summary>
    [Fact]
    public async Task Stopping_between_repair_steps_lets_the_running_step_finish_but_never_starts_the_next_one()
    {
        Directory.CreateDirectory(_root);
        var marker1 = Path.Combine(_root, "step1.txt");
        var marker2 = Path.Combine(_root, "step2.txt");

        var target = new CleanupTarget(
            "test-repair", "x", "y", CleanupKind.SystemRepair, DefaultEnabled: true,
            RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x", NeedsElevation: true)
        {
            RepairSteps =
            [
                new RepairStep(CmdExePath, ["/c", $"ping -n 2 127.0.0.1 >nul && echo done > {marker1}"], "step1"),
                new RepairStep(CmdExePath, ["/c", $"echo done > {marker2}"], "step2"),
            ],
        };

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300)); // fires while step1's ~1s ping is still running

        var service = new CleanupService(_logging);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CleanAsync(target, progress: null, cts.Token));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(marker1) && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(File.Exists(marker1), "a repair step already running must be allowed to finish, never killed by Stop");
        Assert.False(File.Exists(marker2), "no further repair step may start once Stop was requested");
    }

    /// <summary>
    /// A run that was ALREADY stopped before a repair target ever started must never run a step at all,
    /// <c>cancellationToken.ThrowIfCancellationRequested()</c> fires at the top of the loop, before the
    /// first step's process is even started. This is what keeps the caller's fallback path (an elevated
    /// target whose result never arrived falls back to an unelevated retry with the SAME token,
    /// CleanupViewModel.Run.cs) safe: with the token already cancelled, that retry throws immediately
    /// instead of quietly repeating a step. CleanAsync throwing OperationCanceledException, never returning
    /// a normal outcome, is exactly what lets every caller, the ViewModel's own catch and the worker's
    /// per-target catch in CleanupWorker.RunCommandAsync, read this as "stopped", never "done".
    /// </summary>
    [Fact]
    public async Task CleanAsync_for_a_repair_target_never_runs_a_step_when_already_stopped()
    {
        Directory.CreateDirectory(_root);
        var marker = Path.Combine(_root, "should-not-run.txt");

        var target = new CleanupTarget(
            "test-repair", "x", "y", CleanupKind.SystemRepair, DefaultEnabled: true,
            RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x", NeedsElevation: true)
        {
            RepairSteps = [new RepairStep(CmdExePath, ["/c", $"echo ran > \"{marker}\""], "step")],
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var service = new CleanupService(_logging);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CleanAsync(target, progress: null, cts.Token));

        // A little slack: proves absence, not just immediate absence.
        await Task.Delay(300);
        Assert.False(File.Exists(marker), "a stopped run must never start a step, not even report it as done");
    }

    private sealed class CallbackProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
