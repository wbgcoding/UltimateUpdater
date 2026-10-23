using System.Text;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// Directory removal after cleanup is bottom-up and non-recursive, each subdirectory delete only
/// succeeds when the folder is truly empty, so content the file pass did not remove makes the
/// directory removal fail closed instead of disappearing along with it.
/// </summary>
public class CleanupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-cleanup-test-" + Guid.NewGuid());
    private readonly LoggingService _logging = new();

    private CleanupTarget Target(string directory) => new(
        "test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
        RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
    {
        Directories = [directory],
    };

    /// <summary>
    /// Mirrors the real "thumbnail-cache" catalogue entry: the TOP target directory is literally named
    /// "Explorer", so <see cref="CleanupSafety.IsDeletableFile"/> only lets thumbcache*/iconcache* files
    /// through, everything else sharing that folder (Explorer's own settings) is left alone, even nested
    /// a level deeper. A subdirectory holding one of those refused files must survive the bottom-up pass.
    /// </summary>
    [Fact]
    public async Task A_subdirectory_holding_a_file_the_per_file_pass_refused_is_never_removed()
    {
        var explorerDir = Path.Combine(_root, "Explorer");
        var nested = Path.Combine(explorerDir, "nested");
        Directory.CreateDirectory(nested);
        var protectedFile = Path.Combine(nested, "Something else.dat");
        File.WriteAllText(protectedFile, "not a cache file");

        var service = new CleanupService(_logging);
        await service.CleanAsync(Target(explorerDir), progress: null, CancellationToken.None);

        Assert.True(Directory.Exists(nested), "a non-empty subdirectory must survive");
        Assert.True(File.Exists(protectedFile), "a file the per-file pass refused must survive");
    }

    [Fact]
    public async Task An_ordinary_empty_subdirectory_tree_is_fully_removed()
    {
        var nested = Path.Combine(_root, "a", "b", "c");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "leaf.tmp"), "gone soon");

        var service = new CleanupService(_logging);
        var outcome = await service.CleanAsync(Target(_root), progress: null, CancellationToken.None);

        Assert.Equal(1, outcome.DeletedFiles);
        Assert.False(Directory.Exists(Path.Combine(_root, "a")), "the whole now-empty tree should be gone");
    }

    /// <summary>
    /// The mechanism this relies on: a non-recursive <c>Directory.Delete</c> refuses to remove a
    /// directory that still holds ANYTHING, whatever the reason, a locked file included. The exact
    /// timing race (a file written into the target by something else, strictly between the file pass's
    /// snapshot and the subdirectory pass) is not independently reproducible in an automated test without
    /// a hook into that boundary; this proves the mechanism that closes it.
    /// </summary>
    [Fact]
    public async Task A_subdirectory_holding_a_locked_file_is_never_removed()
    {
        var subdir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(subdir);
        var lockedFile = Path.Combine(subdir, "locked.tmp");
        using var handle = new FileStream(lockedFile, FileMode.Create, FileAccess.Write, FileShare.Read);

        var service = new CleanupService(_logging);
        await service.CleanAsync(Target(_root), progress: null, CancellationToken.None);

        Assert.True(Directory.Exists(subdir));
        Assert.True(File.Exists(lockedFile));
    }

    /// <summary>The dialog calls this figure files, so the folders that stay standing around a locked file
    /// must not be counted in it.</summary>
    [Fact]
    public async Task One_locked_file_five_levels_deep_counts_as_one_skipped_file()
    {
        var deep = Path.Combine(_root, "a", "b", "c", "d", "e");
        Directory.CreateDirectory(deep);
        var lockedFile = Path.Combine(deep, "locked.tmp");
        using var handle = new FileStream(lockedFile, FileMode.Create, FileAccess.Write, FileShare.Read);

        var service = new CleanupService(_logging);
        var outcome = await service.CleanAsync(Target(_root), progress: null, CancellationToken.None);

        Assert.Equal(1, outcome.SkippedFiles);
        Assert.True(File.Exists(lockedFile));
    }

    /// <summary>
    /// Progress for a target naming more than one directory ("user-temp" is both %TEMP% and
    /// LocalAppData\Temp) is computed across the whole target, never per directory, so it climbs
    /// monotonically start to finish. A bespoke synchronous IProgress&lt;T&gt; rather than
    /// System.Progress&lt;T&gt;: the latter marshals through a captured SynchronizationContext (or the
    /// thread pool when there is none), which gives no guarantee every report has actually run by the time
    /// CleanAsync's Task completes, this one records inline, on whichever thread reports it.
    /// </summary>
    [Fact]
    public async Task Progress_across_two_directories_never_drops_back_towards_zero()
    {
        var dirA = Path.Combine(_root, "a");
        var dirB = Path.Combine(_root, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        for (var i = 0; i < 30; i++)
            File.WriteAllText(Path.Combine(dirA, $"a{i}.tmp"), "x");
        for (var i = 0; i < 30; i++)
            File.WriteAllText(Path.Combine(dirB, $"b{i}.tmp"), "x");

        var target = new CleanupTarget("test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true, RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        {
            Directories = [dirA, dirB],
        };

        var progress = new SyncProgress<CleanupProgress>();
        var service = new CleanupService(_logging);
        await service.CleanAsync(target, progress, CancellationToken.None);

        Assert.NotEmpty(progress.Reports);
        for (var i = 1; i < progress.Reports.Count; i++)
        {
            Assert.True(
                progress.Reports[i].Percent >= progress.Reports[i - 1].Percent,
                $"percent dropped from {progress.Reports[i - 1].Percent} to {progress.Reports[i].Percent} " +
                $"(report {i}), the old per-directory reset is back.");
        }
    }

    /// <summary>
    /// %TEMP%\chocolatey and %TEMP%\NuGetScratch belong to the package-manager cache but sit
    /// inside "user-temp". The catalogue hands the outer target the inner one's paths as exclusions, and the
    /// service leaves everything at or below them alone, otherwise deselecting the cache would protect
    /// nothing, and selecting both would count the same bytes twice.
    /// </summary>
    [Fact]
    public async Task A_directory_another_target_owns_is_neither_measured_nor_emptied()
    {
        var owned = Path.Combine(_root, "chocolatey");
        Directory.CreateDirectory(owned);
        var theirs = Path.Combine(owned, "theirs.tmp");
        File.WriteAllText(theirs, "belongs to the package-manager cache");
        File.WriteAllText(Path.Combine(_root, "mine.tmp"), "belongs to user-temp");

        var target = new CleanupTarget("outer", "x", "y", CleanupKind.Directories, DefaultEnabled: true, RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        {
            Directories = [_root],
            ExcludedDirectories = [owned],
        };

        var service = new CleanupService(_logging);
        var scan = await service.ScanAsync(target, CancellationToken.None);
        var outcome = await service.CleanAsync(target, progress: null, CancellationToken.None);

        Assert.Equal(1, scan.Files);
        Assert.Equal(1, outcome.DeletedFiles);
        Assert.True(File.Exists(theirs), "the other target's file must survive");
        Assert.True(Directory.Exists(owned), "the other target's directory must survive");
    }

    /// <summary>The catalogue side of the same fix: the nested path is assigned to whoever named it exactly.</summary>
    [Fact]
    public void Nested_catalogue_paths_are_excluded_from_the_target_that_merely_contains_them()
    {
        var temp = Path.Combine(_root, "Temp");
        var inner = Path.Combine(temp, "chocolatey");

        var disjoint = CleanupCatalog.WithDisjointPaths(
        [
            new CleanupTarget("user-temp", "x", "y", CleanupKind.Directories, DefaultEnabled: true, RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x") { Directories = [temp] },
            new CleanupTarget("package-manager-cache", "x", "y", CleanupKind.Directories, DefaultEnabled: true, RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x") { Directories = [inner] },
        ]);

        Assert.Equal([inner], disjoint[0].ExcludedDirectories);
        Assert.Empty(disjoint[1].ExcludedDirectories);
    }

    // ---- Detail list: grouped by folder/program, never file by file ----

    [Fact]
    public async Task ScanAsync_groups_directory_target_files_into_one_entry_per_first_level_subfolder()
    {
        var appA = Path.Combine(_root, "VendorA");
        var appB = Path.Combine(_root, "VendorB");
        Directory.CreateDirectory(appA);
        Directory.CreateDirectory(appB);
        File.WriteAllText(Path.Combine(appA, "cache1.tmp"), "aa");
        File.WriteAllText(Path.Combine(appA, "cache2.tmp"), "aa");
        File.WriteAllText(Path.Combine(appB, "cache1.tmp"), "b");

        var target = new CleanupTarget("test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true, RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        {
            Directories = [_root],
        };

        var service = new CleanupService(_logging);
        var result = await service.ScanAsync(target, CancellationToken.None);

        Assert.NotNull(result.Entries);
        var byPath = result.Entries!.ToDictionary(e => e.Path);
        Assert.Equal(2, byPath[appA].Files);
        Assert.Equal(4, byPath[appA].Bytes);
        Assert.Equal(1, byPath[appB].Files);
        Assert.Equal(1, byPath[appB].Bytes);
    }

    /// <summary>A file sitting directly in the configured root (no vendor subfolder at all, %TEMP% itself
    /// often has some) still needs a row of its own rather than vanishing from the detail list.</summary>
    [Fact]
    public async Task ScanAsync_groups_loose_files_directly_in_the_root_into_their_own_entry()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "loose.tmp"), "loose file, no subfolder");

        var target = new CleanupTarget("test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true, RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        {
            Directories = [_root],
        };

        var service = new CleanupService(_logging);
        var result = await service.ScanAsync(target, CancellationToken.None);

        Assert.NotNull(result.Entries);
        var entry = Assert.Single(result.Entries!);
        Assert.Equal(_root, entry.Path);
        Assert.Equal(1, entry.Files);
    }

    /// <summary>Unchecking a detail-list row must clean everything else in the target while leaving exactly
    /// that row's own files untouched.</summary>
    [Fact]
    public async Task CleanAsync_leaves_an_excluded_entrys_files_untouched_while_cleaning_the_rest()
    {
        var appA = Path.Combine(_root, "VendorA");
        var appB = Path.Combine(_root, "VendorB");
        Directory.CreateDirectory(appA);
        Directory.CreateDirectory(appB);
        var fileA = Path.Combine(appA, "cache1.tmp");
        var fileB = Path.Combine(appB, "cache1.tmp");
        File.WriteAllText(fileA, "a");
        File.WriteAllText(fileB, "b");

        var target = new CleanupTarget("test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true, RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        {
            Directories = [_root],
        };

        var service = new CleanupService(_logging);
        var outcome = await service.CleanAsync(
            target, progress: null, CancellationToken.None, excludedEntryPaths: [appA]);

        Assert.Equal(1, outcome.DeletedFiles);
        Assert.True(File.Exists(fileA), "the excluded entry's file must survive");
        Assert.False(File.Exists(fileB), "the non-excluded entry's file must be gone");
    }

    /// <summary>
    /// The same rule for an excluded row that holds no files at all, only folders: the
    /// empty-subdirectory pass must also honor the exclusion, not just the per-file pass.
    /// </summary>
    [Fact]
    public async Task CleanAsync_leaves_an_excluded_entrys_empty_subdirectories_standing()
    {
        var kept = Path.Combine(_root, "KeptVendor");
        var keptNested = Path.Combine(kept, "nested", "deeper");
        var cleaned = Path.Combine(_root, "CleanedVendor", "nested");
        Directory.CreateDirectory(keptNested);
        Directory.CreateDirectory(cleaned);

        var target = new CleanupTarget("test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true, RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        {
            Directories = [_root],
        };

        var service = new CleanupService(_logging);
        await service.CleanAsync(target, progress: null, CancellationToken.None, excludedEntryPaths: [kept]);

        Assert.True(Directory.Exists(keptNested), "the excluded entry's deepest folder must survive");
        Assert.True(Directory.Exists(kept), "the excluded entry's own folder must survive");
        Assert.False(Directory.Exists(cleaned), "an empty folder outside the excluded entry is still removed");
    }

    /// <summary>The elevated pipe must never carry an unbounded list: anything past the cap folds into a
    /// count/byte total instead of one more row each.</summary>
    [Fact]
    public async Task ScanAsync_caps_the_detail_list_and_sums_the_rest_as_omitted()
    {
        const int subfolderCount = 505;
        for (var i = 0; i < subfolderCount; i++)
        {
            var dir = Path.Combine(_root, $"v{i:000}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "f.tmp"), "x");
        }

        var target = new CleanupTarget("test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true, RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        {
            Directories = [_root],
        };

        var service = new CleanupService(_logging);
        var result = await service.ScanAsync(target, CancellationToken.None);

        Assert.NotNull(result.Entries);
        Assert.Equal(500, result.Entries!.Count);
        Assert.Equal(subfolderCount - 500, result.OmittedEntryCount);
        Assert.Equal(subfolderCount - 500, result.OmittedEntryBytes); // one byte ("x") per omitted entry
        Assert.Equal(subfolderCount, result.Files); // the true total is unaffected by the cap
    }

    // ---- Recycle-bin-instead-of-delete, and its elevated-target exception ----

    /// <summary>The option is honoured unelevated, and a target that needs elevation ignores it no matter
    /// what the user has switched on. Pure, no I/O, so this proves the POLICY without a test having to
    /// move a real file anywhere.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void ShouldUseRecycleBin_is_on_only_when_asked_and_the_target_does_not_need_elevation(
        bool recycleInsteadOfDelete, bool needsElevation, bool expected) =>
        Assert.Equal(expected, CleanupService.ShouldUseRecycleBin(recycleInsteadOfDelete, needsElevation));

    /// <summary>With the option on, a file target's contents land in the recycle bin, not gone for good,
    /// same "moved, not deleted, so nothing is lost even if the assertions are wrong" reasoning as
    /// RecycleBinDeleterTests' own live-run test.</summary>
    [Fact]
    public async Task CleanAsync_with_recycleInsteadOfDelete_moves_files_to_the_recycle_bin_not_a_permanent_delete()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "marker.txt");
        File.WriteAllText(file, "wu-punkt20-recycle-test");

        var service = new CleanupService(_logging);
        var outcome = await service.CleanAsync(
            Target(_root), progress: null, CancellationToken.None, recycleInsteadOfDelete: true);

        Assert.Equal(1, outcome.DeletedFiles);
        Assert.False(File.Exists(file), "the file must be gone from its original location");
    }

    // ---- Registry cleanup must work only what it is given, never scan on its own ----

    private static Models.Settings.RegistryCleanupSettings RegistrySettings() => new()
    {
        BackupFolder = Directory.CreateTempSubdirectory("wu-registry-backup-").FullName,
        RetentionCount = 1,
    };

    private static CleanupTarget RegistryTarget() => new(
        "test-registry-target", "x", "y", CleanupKind.RegistryCleanup, DefaultEnabled: false,
        RiskLevel: CleanupRiskLevel.Risky, ConsequenceKey: "x", NeedsElevation: true)
    {
        RegistryCategories = [RegistryCleanupCategory.AppPaths],
    };

    /// <summary>
    /// A target with nothing to clean must return instantly without ever touching the registry, even
    /// with a real broken App Paths entry present that a live scan of this exact category WOULD find,
    /// proving CleanAsync never falls back to scanning for itself.
    /// </summary>
    [Fact]
    public async Task CleanAsync_with_no_registry_findings_touches_nothing_even_if_a_live_scan_would_find_something()
    {
        const string name = "wu-test-punkt9-untouched.exe";
        const string appPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        var keyPath = $@"HKCU\{appPathsRoot}\{name}";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{appPathsRoot}\{name}"))
            key!.SetValue(string.Empty, @"C:\this-path-does-not-exist-punkt9.exe");

        try
        {
            var service = new CleanupService(_logging);
            var outcome = await service.CleanAsync(
                RegistryTarget(),
                progress: null,
                cancellationToken: CancellationToken.None,
                registrySettings: RegistrySettings(),
                registryFindings: []);

            Assert.Equal(0, outcome.DeletedFiles);
            Assert.True(RegistryCleanupScanner.KeyExists(keyPath), "nothing was given to clean, so nothing may be touched");
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{appPathsRoot}\{name}", throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// CleanAsync cleans ONLY the findings it was given, never "everything broken in the category", a
    /// second, equally broken entry the caller did not pass in must survive.
    /// </summary>
    [Fact]
    public async Task CleanAsync_cleans_only_the_given_registry_findings_not_every_broken_entry_in_the_category()
    {
        const string givenName = "wu-test-punkt9-given.exe";
        const string otherName = "wu-test-punkt9-other.exe";
        const string appPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        var givenKeyPath = $@"HKCU\{appPathsRoot}\{givenName}";
        var otherKeyPath = $@"HKCU\{appPathsRoot}\{otherName}";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{appPathsRoot}\{givenName}"))
            key!.SetValue(string.Empty, @"C:\this-path-does-not-exist-punkt9-a.exe");
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{appPathsRoot}\{otherName}"))
            key!.SetValue(string.Empty, @"C:\this-path-does-not-exist-punkt9-b.exe");

        try
        {
            var given = new RegistryFinding(
                RegistryCleanupCategory.AppPaths, givenKeyPath, null, givenName, @"C:\this-path-does-not-exist-punkt9-a.exe");

            var service = new CleanupService(_logging);
            var outcome = await service.CleanAsync(
                RegistryTarget(),
                progress: null,
                cancellationToken: CancellationToken.None,
                registrySettings: RegistrySettings(),
                registryFindings: [given]);

            Assert.Equal(1, outcome.DeletedFiles);
            Assert.False(RegistryCleanupScanner.KeyExists(givenKeyPath), "the given finding must be deleted");
            Assert.True(RegistryCleanupScanner.KeyExists(otherKeyPath), "an equally broken entry that was NOT given must survive");
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{appPathsRoot}\{givenName}", throwOnMissingSubKey: false);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{appPathsRoot}\{otherName}", throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// The elevated worker loops over every selected target and hands each one the SAME flat finding list,
    /// while the backup only ever covers the roots of the target being cleaned right now. Cleaning one
    /// category must therefore never touch a finding that belongs to another one, it would be gone before
    /// its own target had a chance to back it up, and which of the two lost is decided by catalogue order.
    /// </summary>
    [Fact]
    public async Task CleanAsync_never_deletes_a_registry_finding_belonging_to_another_category()
    {
        const string ownName = "wu-test-scope-own.exe";
        const string appPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        var ownKeyPath = $@"HKCU\{appPathsRoot}\{ownName}";

        var clsid = $"{{{Guid.NewGuid()}}}";
        var clsidSubPath = $@"SOFTWARE\Classes\CLSID\{clsid}";
        var clsidKeyPath = $@"HKCU\{clsidSubPath}";
        const string brokenServer = @"C:\this-dll-does-not-exist-scope.dll";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{appPathsRoot}\{ownName}"))
            key!.SetValue(string.Empty, @"C:\this-path-does-not-exist-scope.exe");
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{clsidSubPath}\InprocServer32"))
            key!.SetValue(string.Empty, brokenServer);

        try
        {
            var own = new RegistryFinding(
                RegistryCleanupCategory.AppPaths, ownKeyPath, null, ownName, @"C:\this-path-does-not-exist-scope.exe");
            var foreign = new RegistryFinding(
                RegistryCleanupCategory.ActiveXCom, clsidKeyPath, null, clsid, brokenServer);

            var service = new CleanupService(_logging);
            var outcome = await service.CleanAsync(
                RegistryTarget(),
                progress: null,
                cancellationToken: CancellationToken.None,
                registrySettings: RegistrySettings(),
                registryFindings: [own, foreign]);

            Assert.Equal(1, outcome.DeletedFiles);
            Assert.False(RegistryCleanupScanner.KeyExists(ownKeyPath), "this target's own finding must be deleted");

            Assert.True(
                RegistryCleanupScanner.KeyExists(clsidKeyPath),
                "a finding outside this target's roots must survive, its own target has not backed it up yet");
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{appPathsRoot}\{ownName}", throwOnMissingSubKey: false);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(clsidSubPath, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// "orphaned references" merges 4 real scanners (App Paths, Shared DLLs, ActiveX/COM,
    /// Autostart) into one catalogue row and one settings id. Plants a real broken entry in two of the
    /// four (App Paths and Autostart, both writable under HKCU without admin; Shared DLLs lives under HKLM
    /// and is exercised the same way it always was, unchanged by this merge) and scans the REAL catalogue
    /// entry end to end, proving a single scan of the merged row genuinely aggregates more than one
    /// underlying category rather than only ever reporting whichever came first.
    /// </summary>
    [Fact]
    public async Task ScanAsync_on_the_merged_orphaned_references_target_reports_findings_from_more_than_one_category()
    {
        const string appPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        const string autostartRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string exeName = "wu-test-item95-apppaths.exe";
        const string valueName = "wu-test-item95-autostart";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{appPathsRoot}\{exeName}"))
            key!.SetValue(string.Empty, @"C:\this-path-does-not-exist-item95-a.exe");
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(autostartRoot))
            key!.SetValue(valueName, @"C:\this-path-does-not-exist-item95-b.exe");

        try
        {
            var target = RegistryCleanupCatalog.Build().Single(t => t.Id == "registry-orphaned-references");
            Assert.Equal(
                [RegistryCleanupCategory.AppPaths, RegistryCleanupCategory.SharedDlls,
                 RegistryCleanupCategory.ActiveXCom, RegistryCleanupCategory.Autostart],
                target.RegistryCategories);

            var service = new CleanupService(_logging);
            var result = await service.ScanAsync(target, CancellationToken.None);

            Assert.Contains(
                result.RegistryFindings!,
                f => f.Category == RegistryCleanupCategory.AppPaths && f.DisplayName == exeName);
            Assert.Contains(
                result.RegistryFindings!,
                f => f.Category == RegistryCleanupCategory.Autostart && f.DisplayName == valueName);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{appPathsRoot}\{exeName}", throwOnMissingSubKey: false);
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(autostartRoot, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Cleaning the merged row must delete findings from every category it covers in one pass,
    /// backed up together, never just the first category it happens to see.</summary>
    [Fact]
    public async Task CleanAsync_on_the_merged_orphaned_references_target_deletes_findings_from_more_than_one_category()
    {
        const string appPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        const string autostartRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string exeName = "wu-test-item95-clean-apppaths.exe";
        const string valueName = "wu-test-item95-clean-autostart";
        var appPathsKeyPath = $@"HKCU\{appPathsRoot}\{exeName}";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{appPathsRoot}\{exeName}"))
            key!.SetValue(string.Empty, @"C:\this-path-does-not-exist-item95-clean-a.exe");
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(autostartRoot))
            key!.SetValue(valueName, @"C:\this-path-does-not-exist-item95-clean-b.exe");

        try
        {
            var target = RegistryCleanupCatalog.Build().Single(t => t.Id == "registry-orphaned-references");
            var appPathsFinding = new RegistryFinding(
                RegistryCleanupCategory.AppPaths, appPathsKeyPath, null, exeName, @"C:\this-path-does-not-exist-item95-clean-a.exe");
            var autostartFinding = new RegistryFinding(
                RegistryCleanupCategory.Autostart, $@"HKCU\{autostartRoot}", valueName, valueName,
                @"C:\this-path-does-not-exist-item95-clean-b.exe");

            var service = new CleanupService(_logging);
            var outcome = await service.CleanAsync(
                target, progress: null, CancellationToken.None,
                registrySettings: RegistrySettings(), registryFindings: [appPathsFinding, autostartFinding]);

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(autostartRoot);

            Assert.Equal(2, outcome.DeletedFiles);
            Assert.False(RegistryCleanupScanner.KeyExists(appPathsKeyPath), "the App Paths finding must be deleted");
            Assert.True(key?.GetValue(valueName) is null, "the Autostart finding must be deleted");
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{appPathsRoot}\{exeName}", throwOnMissingSubKey: false);
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(autostartRoot, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Unticking a registry row, for this run, or permanently through the context menu, which also
    /// lowers the count the dialog shows, decides what is cleaned exactly as it does for a file row.
    /// </summary>
    [Fact]
    public async Task CleanAsync_leaves_an_excluded_registry_finding_untouched()
    {
        const string keptName = "wu-test-excluded-kept.exe";
        const string cleanedName = "wu-test-excluded-cleaned.exe";
        const string appPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        var keptKeyPath = $@"HKCU\{appPathsRoot}\{keptName}";
        var cleanedKeyPath = $@"HKCU\{appPathsRoot}\{cleanedName}";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{appPathsRoot}\{keptName}"))
            key!.SetValue(string.Empty, @"C:\this-path-does-not-exist-excluded-a.exe");
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{appPathsRoot}\{cleanedName}"))
            key!.SetValue(string.Empty, @"C:\this-path-does-not-exist-excluded-b.exe");

        try
        {
            var kept = new RegistryFinding(
                RegistryCleanupCategory.AppPaths, keptKeyPath, null, keptName, @"C:\this-path-does-not-exist-excluded-a.exe");
            var cleaned = new RegistryFinding(
                RegistryCleanupCategory.AppPaths, cleanedKeyPath, null, cleanedName, @"C:\this-path-does-not-exist-excluded-b.exe");

            var service = new CleanupService(_logging);
            var outcome = await service.CleanAsync(
                RegistryTarget(),
                progress: null,
                cancellationToken: CancellationToken.None,
                registrySettings: RegistrySettings(),
                registryFindings: [kept, cleaned],
                // the detail list keys a registry row by its key path, and matches case-insensitively
                excludedEntryPaths: [keptKeyPath.ToUpperInvariant()]);

            Assert.True(RegistryCleanupScanner.KeyExists(keptKeyPath), "the excluded finding must survive");
            Assert.Equal(1, outcome.DeletedFiles);
            Assert.False(RegistryCleanupScanner.KeyExists(cleanedKeyPath), "the rest of the target must still be cleaned");
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{appPathsRoot}\{keptName}", throwOnMissingSubKey: false);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{appPathsRoot}\{cleanedName}", throwOnMissingSubKey: false);
        }
    }

    // ---- Recycle bin: one row per drive ----

    [Theory]
    [InlineData("D:\\", null, "D:\\")]
    [InlineData("D:\\", "", "D:\\")]
    [InlineData("D:\\", "  ", "D:\\")]
    [InlineData("D:\\", "Data", "D:\\ (Data)")]
    public void DriveEntryDisplayName_falls_back_to_the_bare_letter_without_a_real_label(string drive, string? label, string expected)
    {
        Assert.Equal(expected, CleanupService.DriveEntryDisplayName(drive, label));
    }

    private static CleanupEntry RecycledFile(string contentPath, string originalPath, long bytes) => new(
        contentPath, Path.GetFileName(originalPath), bytes, 1,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, CleanupEntryType.Other, originalPath);

    /// <summary>
    /// The decision the shell's whole-bin call hangs on. Only a drive whose every listed row is still
    /// ticked AND which had nothing left off the list may take it, it removes items past the per-drive
    /// budget too, which the user never saw. Everything else deletes exactly the ticked rows.
    /// </summary>
    [Fact]
    public void PlanRecycleBinEmpty_takes_the_shell_path_only_for_a_fully_selected_complete_drive()
    {
        CleanupEntry[] entries =
        [
            RecycledFile(@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RAAA.txt", @"C:\Users\x\a.txt", 100),
            RecycledFile(@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RBBB.txt", @"C:\Users\x\b.txt", 200),
            RecycledFile(@"D:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RCCC.txt", @"D:\data\c.txt", 300),
        ];

        RecycleBinDriveSummary[] drives =
        [
            new(@"C:\", @"C:\", 2, 2, 300, 300, DetailsAvailable: true),
            new(@"D:\", @"D:\ (Data)", 1, 1, 300, 300, DetailsAvailable: true),
        ];

        // Nothing unticked, nothing left out: both drives go the fast way, and carry no row list with them.
        var all = CleanupService.PlanRecycleBinEmpty(entries, drives, null);
        Assert.Equal(2, all.Count);
        Assert.All(all, plan => Assert.True(plan.UseShellEmpty));
        Assert.All(all, plan => Assert.Empty(plan.Entries));

        // One row unticked on C:, that drive falls to the per-row path with exactly the rest, D: is untouched.
        var partial = CleanupService.PlanRecycleBinEmpty(
            entries, drives, [@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RAAA.txt"]);
        var cDrive = Assert.Single(partial, plan => plan.Root == @"C:\");
        Assert.False(cDrive.UseShellEmpty);
        Assert.Equal([@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RBBB.txt"], cDrive.Entries.Select(e => e.Path));
        Assert.True(Assert.Single(partial, plan => plan.Root == @"D:\").UseShellEmpty);
    }

    /// <summary>Only what the user confirmed in the dialog may go. An item recycled after the dialog's scan was
    /// never shown, so neither the per-row path nor the shell's whole-bin call may take it.</summary>
    [Fact]
    public void PlanRecycleBinEmpty_deletes_only_the_confirmed_items_when_the_bin_gained_one_since_the_scan()
    {
        const string a = @"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RAAA.txt";
        const string b = @"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RBBB.txt";
        const string c = @"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RCCC.txt";
        CleanupEntry[] fresh =
        [
            RecycledFile(a, @"C:\Users\x\a.txt", 100),
            RecycledFile(b, @"C:\Users\x\b.txt", 100),
            RecycledFile(c, @"C:\Users\x\c.txt", 100),
        ];
        RecycleBinDriveSummary[] drives = [new(@"C:\", @"C:\", 3, 3, 300, 300, DetailsAvailable: true)];

        var plan = Assert.Single(CleanupService.PlanRecycleBinEmpty(fresh, drives, null, confirmedEntryPaths: [a, b]));

        Assert.False(plan.UseShellEmpty);
        Assert.Equal([a, b], plan.Entries.Select(e => e.Path).Order());
    }

    /// <summary>The fast path stays available when nothing was added since the scan.</summary>
    [Fact]
    public void PlanRecycleBinEmpty_still_takes_the_shell_path_when_the_bin_matches_what_was_confirmed()
    {
        const string a = @"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RAAA.txt";
        const string b = @"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RBBB.txt";
        CleanupEntry[] fresh = [RecycledFile(a, @"C:\Users\x\a.txt", 100), RecycledFile(b, @"C:\Users\x\b.txt", 100)];
        RecycleBinDriveSummary[] drives = [new(@"C:\", @"C:\", 2, 2, 200, 200, DetailsAvailable: true)];

        var plan = Assert.Single(CleanupService.PlanRecycleBinEmpty(fresh, drives, null, confirmedEntryPaths: [a, b]));

        Assert.True(plan.UseShellEmpty);
    }

    /// <summary>Nothing confirmed means nothing deleted, however much the bin holds now.</summary>
    [Fact]
    public void PlanRecycleBinEmpty_deletes_nothing_when_nothing_was_confirmed()
    {
        CleanupEntry[] fresh = [RecycledFile(@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RAAA.txt", @"C:\Users\x\a.txt", 100)];
        RecycleBinDriveSummary[] drives = [new(@"C:\", @"C:\", 1, 1, 100, 100, DetailsAvailable: true)];

        Assert.Empty(CleanupService.PlanRecycleBinEmpty(fresh, drives, null, confirmedEntryPaths: []));
    }

    /// <summary>A drive whose bin holds more than the list shows can never take the fast path, even with
    /// every shown row still ticked: the rows past the budget were never on screen.</summary>
    [Fact]
    public void PlanRecycleBinEmpty_never_takes_the_shell_path_when_rows_were_left_out()
    {
        CleanupEntry[] entries = [RecycledFile(@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RAAA.txt", @"C:\Users\x\a.txt", 100)];
        RecycleBinDriveSummary[] drives = [new(@"C:\", @"C:\", 9, 1, 900, 100, DetailsAvailable: true)];

        var plan = Assert.Single(CleanupService.PlanRecycleBinEmpty(entries, drives, null));
        Assert.False(plan.UseShellEmpty);
        Assert.Single(plan.Entries);
    }

    /// <summary>The age filter hides younger files from the list, but they are still in the bin: a drive
    /// whose every listed row is ticked must still go row by row, or the shell's whole-bin call deletes the
    /// very files the filter promised to keep.</summary>
    [Fact]
    public void PlanRecycleBinEmpty_never_takes_the_shell_path_when_the_age_filter_hid_files()
    {
        CleanupEntry[] entries = [RecycledFile(@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RAAA.txt", @"C:\Users\x\a.txt", 100)];
        RecycleBinDriveSummary[] drives =
            [new(@"C:\", @"C:\", 1, 1, 100, 100, DetailsAvailable: true, AgeFilterHidItems: true)];

        var plan = Assert.Single(CleanupService.PlanRecycleBinEmpty(entries, drives, null));
        Assert.False(plan.UseShellEmpty);
        Assert.Equal([@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RAAA.txt"], plan.Entries.Select(e => e.Path));
    }

    /// <summary>An aggregate row carries no per-item date, so under an active age filter neither the shell
    /// call nor a per-row delete can honour it: the drive is left alone.</summary>
    [Fact]
    public void PlanRecycleBinEmpty_leaves_a_listing_less_drive_alone_under_an_age_filter()
    {
        CleanupEntry[] entries =
        [
            new(@"C:\", @"C:\", 900, 9, DateTimeOffset.MinValue, DateTimeOffset.MinValue, CleanupEntryType.Other),
        ];
        RecycleBinDriveSummary[] drives =
            [new(@"C:\", @"C:\", 1, 1, 900, 900, DetailsAvailable: false, AgeFilterHidItems: true)];

        Assert.Empty(CleanupService.PlanRecycleBinEmpty(entries, drives, null));
    }

    /// <summary>A drive the user unticked entirely is not in the plan at all, it is kept, not skipped,
    /// and must never show up in the run's "could not remove" figure.</summary>
    [Fact]
    public void PlanRecycleBinEmpty_leaves_out_a_drive_with_nothing_selected()
    {
        CleanupEntry[] entries = [RecycledFile(@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RAAA.txt", @"C:\Users\x\a.txt", 100)];
        RecycleBinDriveSummary[] drives = [new(@"C:\", @"C:\", 1, 1, 100, 100, DetailsAvailable: true)];

        Assert.Empty(CleanupService.PlanRecycleBinEmpty(
            entries, drives, [@"c:\$recycle.bin\s-1-5-21-1-2-3-1001\$raaa.txt"])); // case-insensitive, as Windows paths are
    }

    /// <summary>
    /// The clean-time re-scan can fail to enumerate a bin the dialog's own scan listed file by file (an
    /// unreadable bin folder, or no own SID this pass). That drive then arrives as a single aggregate row
    /// for the whole bin, which the user's per-file unticks cannot match, so every check for "is anything
    /// still excluded here" comes up empty and the drive looks completely selected. Taking the shell's
    /// whole-bin call on the strength of that would permanently delete exactly the files the user kept.
    /// </summary>
    [Fact]
    public void PlanRecycleBinEmpty_never_empties_a_listing_less_drive_the_user_excluded_something_on()
    {
        CleanupEntry[] entries =
        [
            new(@"C:\", @"C:\", 900, 9, DateTimeOffset.MinValue, DateTimeOffset.MinValue, CleanupEntryType.Other),
        ];
        RecycleBinDriveSummary[] drives = [new(@"C:\", @"C:\", 1, 1, 900, 900, DetailsAvailable: false)];

        // The path the user unticked while the list still had one, it names a file, the row names the drive.
        var plans = CleanupService.PlanRecycleBinEmpty(
            entries, drives, [@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RKEEP.txt"]);

        Assert.Empty(plans);
    }

    /// <summary>The same drive with nothing excluded is still emptied, a bin that cannot be listed is
    /// exactly the case the shell's own call exists for, and refusing it there would leave the user unable
    /// to empty their recycle bin at all.</summary>
    [Fact]
    public void PlanRecycleBinEmpty_still_empties_a_listing_less_drive_when_nothing_was_excluded()
    {
        CleanupEntry[] entries =
        [
            new(@"C:\", @"C:\", 900, 9, DateTimeOffset.MinValue, DateTimeOffset.MinValue, CleanupEntryType.Other),
        ];
        RecycleBinDriveSummary[] drives = [new(@"C:\", @"C:\", 1, 1, 900, 900, DetailsAvailable: false)];

        Assert.True(Assert.Single(CleanupService.PlanRecycleBinEmpty(entries, drives, null)).UseShellEmpty);

        // An exclusion on a DIFFERENT drive says nothing about this one.
        Assert.True(Assert.Single(CleanupService.PlanRecycleBinEmpty(
            entries, drives, [@"D:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RKEEP.txt"])).UseShellEmpty);
    }

    /// <summary>The row budget: an equal share per drive with content, never under the floor, and what a
    /// small drive does not need goes to the drives that do.</summary>
    [Fact]
    public void SplitEntryBudget_shares_the_cap_and_hands_on_what_a_drive_does_not_need()
    {
        var even = CleanupService.SplitEntryBudget(new Dictionary<string, int> { [@"C:\"] = 400, [@"D:\"] = 400 });
        Assert.Equal(250, even[@"C:\"]);
        Assert.Equal(250, even[@"D:\"]);

        // D: needs 10 of its 250, so C: may keep going past its own share up to what it actually has.
        var lopsided = CleanupService.SplitEntryBudget(new Dictionary<string, int> { [@"C:\"] = 900, [@"D:\"] = 10 });
        Assert.Equal(490, lopsided[@"C:\"]);
        Assert.Equal(10, lopsided[@"D:\"]);

        // A drive never drops below the floor, however many drives there are.
        var many = CleanupService.SplitEntryBudget(
            Enumerable.Range(0, 20).ToDictionary(i => $@"{(char)('C' + i)}:\", _ => 999));
        Assert.All(many.Values, budget => Assert.Equal(50, budget));

        Assert.Empty(CleanupService.SplitEntryBudget(new Dictionary<string, int> { [@"C:\"] = 0 }));
    }

    /// <summary>
    /// Real smoke test against this machine's actual recycle bin (same "runs for real, asserts only
    /// structural invariants" spirit as <c>RecycleBinDeleterTests.cs</c>), the per-drive enumeration and
    /// the <c>SHQueryRecycleBin</c> fallback have no other test that actually executes them. Never asserts
    /// on WHAT is in the bin (unknown, machine-dependent), only that the shape holds: a non-negative total,
    /// a summary per drive that has rows, and every detail row naming a real original path.
    /// </summary>
    [Fact]
    public async Task ScanAsync_for_the_recycle_bin_lists_real_files_without_throwing()
    {
        var target = new CleanupTarget(
            "recycle-bin", "x", "y", CleanupKind.RecycleBin, DefaultEnabled: false,
            RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x");

        var result = await new CleanupService(_logging).ScanAsync(target, CancellationToken.None);

        Assert.True(result.Bytes >= 0);
        Assert.True(result.Files >= 0);
        var roots = DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName).ToList();

        foreach (var entry in result.Entries ?? [])
        {
            Assert.Contains(roots, root => root.Equals(Path.GetPathRoot(entry.Path), StringComparison.OrdinalIgnoreCase));

            if (entry.OriginalPath is null)
            {
                // the aggregate fallback row for a drive that could not be enumerated
                Assert.Contains(roots, root => root.Equals(entry.Path, StringComparison.OrdinalIgnoreCase));
                continue;
            }

            Assert.True(Path.IsPathFullyQualified(entry.OriginalPath));
            Assert.Contains(@"$Recycle.Bin", entry.Path, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var drive in result.RecycleBinDrives ?? [])
        {
            Assert.True(drive.ListedCount <= drive.FoundCount);
            Assert.Contains(roots, root => root.Equals(drive.Root, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// End-to-end proof that the recycle bin's age filter actually reaches <c>ScanAsync</c>: an entry this
    /// test fabricates itself has an age of zero, so a minimum of one day must hide it, and a minimum of
    /// zero (the filter off) must show it again. This fix: this used to recycle a real file into the
    /// HOST's real bin and assert against the unfiltered scan of it, which asserted something the
    /// production code cannot promise (CleanupService keeps only its biggest 500 rows per drive, so on a
    /// host whose real bin already holds more than that the smallest file is always dropped) and left the
    /// file behind. It now builds a fake drive root and a fake "$Recycle.Bin" folder under it (same
    /// fabricate-your-own-directory style as <see cref="_root"/> above) and points
    /// <see cref="CleanupService.RecycleBinDriveRootsOverride"/> (the seam this fix added) at it, so the
    /// scan never reads the host's real bin at all. The smoke test above is still the one real-machine
    /// proof that the real bin's own enumeration and shell fallback work.
    /// </summary>
    [Fact]
    public async Task ScanAsync_hides_a_freshly_recycled_file_once_a_minimum_age_is_set()
    {
        var ownSid = RecycleBinScanner.TryGetOwnSid();
        Assert.NotNull(ownSid);

        var fakeDriveRoot = Path.Combine(Path.GetTempPath(), "wu-recyclebin-age-" + Guid.NewGuid());
        var binFolder = RecycleBinScanner.OwnBinFolder(fakeDriveRoot, ownSid!);
        Directory.CreateDirectory(binFolder);

        try
        {
            const string originalPath = @"C:\wu-recyclebin-age-test.txt";
            File.WriteAllText(Path.Combine(binFolder, "$RAGE0001.txt"), "x");
            File.WriteAllBytes(
                Path.Combine(binFolder, "$IAGE0001.txt"),
                FakeRecycleBinIndexEntry(sizeBytes: 1, DateTimeOffset.UtcNow, originalPath));

            CleanupService.RecycleBinDriveRootsOverride = [(fakeDriveRoot, "Fake")];
            try
            {
                var target = new CleanupTarget(
                    "recycle-bin", "x", "y", CleanupKind.RecycleBin, DefaultEnabled: false,
                    RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x");

                var service = new CleanupService(_logging);

                var filtered = await service.ScanAsync(target, CancellationToken.None, recycleBinMinAgeDays: 1);
                Assert.DoesNotContain(
                    filtered.Entries ?? [], entry => string.Equals(entry.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase));

                var unfiltered = await service.ScanAsync(target, CancellationToken.None, recycleBinMinAgeDays: 0);
                Assert.Contains(
                    unfiltered.Entries ?? [], entry => string.Equals(entry.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                CleanupService.RecycleBinDriveRootsOverride = null;
            }
        }
        finally
        {
            Directory.Delete(fakeDriveRoot, recursive: true);
        }
    }

    /// <summary>An index file that cannot be parsed is reported on the drive summary, because the unreadable
    /// index may stand for an item that is not on the list. Same fake-bin setup as the age filter test
    /// above; the plan decision itself is proven in RecycleBinSkippedIndexTests, since a fake drive root
    /// cannot be matched to entry paths.</summary>
    [Fact]
    public async Task A_scan_of_a_bin_with_an_unparseable_index_reports_it_on_the_drive_summary()
    {
        var ownSid = RecycleBinScanner.TryGetOwnSid();
        Assert.NotNull(ownSid);

        var fakeDriveRoot = Path.Combine(Path.GetTempPath(), "wu-recyclebin-skipped-" + Guid.NewGuid());
        var binFolder = RecycleBinScanner.OwnBinFolder(fakeDriveRoot, ownSid!);
        Directory.CreateDirectory(binFolder);

        try
        {
            File.WriteAllText(Path.Combine(binFolder, "$RSKP0001.txt"), "x");
            File.WriteAllBytes(
                Path.Combine(binFolder, "$ISKP0001.txt"),
                FakeRecycleBinIndexEntry(sizeBytes: 1, DateTimeOffset.UtcNow, @"C:\wu-recyclebin-skip-test.txt"));
            File.WriteAllText(Path.Combine(binFolder, "$RSKP0002.txt"), "y");
            File.WriteAllBytes(Path.Combine(binFolder, "$ISKP0002.txt"), [1, 2, 3]);

            CleanupService.RecycleBinDriveRootsOverride = [(fakeDriveRoot, "Fake")];
            try
            {
                var target = new CleanupTarget(
                    "recycle-bin", "x", "y", CleanupKind.RecycleBin, DefaultEnabled: false,
                    RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x");

                var scan = await new CleanupService(_logging).ScanAsync(target, CancellationToken.None);

                var drive = Assert.Single(scan.RecycleBinDrives!);
                Assert.Equal(1, drive.SkippedIndexCount);
                Assert.Equal(drive.FoundCount, drive.ListedCount); // only the skipped index stands between this drive and the shell path
            }
            finally
            {
                CleanupService.RecycleBinDriveRootsOverride = null;
            }
        }
        finally
        {
            Directory.Delete(fakeDriveRoot, recursive: true);
        }
    }

    /// <summary>
    /// <see cref="CleanupService.RecycleBinDriveRootsOverride"/> is documented as steering only the scan,
    /// but <c>EmptyRecycleBin</c> used to re-scan through the very same seam and would have carried a faked
    /// drive root straight into the shell's whole-bin delete call. Proves the fail-closed guard: while the
    /// override is set, cleaning a recycle-bin target refuses outright rather than acting on it.
    /// </summary>
    [Fact]
    public async Task CleanAsync_for_recycle_bin_refuses_while_the_scan_only_override_is_set()
    {
        CleanupService.RecycleBinDriveRootsOverride = [(Path.GetTempPath(), "Fake")];
        try
        {
            var target = new CleanupTarget(
                "recycle-bin", "x", "y", CleanupKind.RecycleBin, DefaultEnabled: false,
                RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x");

            var service = new CleanupService(_logging);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.CleanAsync(target, progress: null, CancellationToken.None));
        }
        finally
        {
            CleanupService.RecycleBinDriveRootsOverride = null;
        }
    }

    /// <summary>A minimal version-2 "$I" index entry, the same byte layout Windows itself writes and
    /// <see cref="RecycleBinIndexParserTests"/> proves the parser against.</summary>
    private static byte[] FakeRecycleBinIndexEntry(long sizeBytes, DateTimeOffset deletedAt, string originalPath)
    {
        var pathBytes = Encoding.Unicode.GetBytes(originalPath + "\0");
        var buffer = new byte[28 + pathBytes.Length];
        BitConverter.GetBytes(2L).CopyTo(buffer, 0);
        BitConverter.GetBytes(sizeBytes).CopyTo(buffer, 8);
        BitConverter.GetBytes(deletedAt.UtcDateTime.ToFileTimeUtc()).CopyTo(buffer, 16);
        BitConverter.GetBytes(originalPath.Length + 1).CopyTo(buffer, 24);
        pathBytes.CopyTo(buffer, 28);
        return buffer;
    }

    /// <summary>The scan side of the same rule: ScanAsync's result must carry the actual registry findings,
    /// not just their count, so the caller has something to hand back to CleanAsync later.</summary>
    [Fact]
    public async Task ScanAsync_carries_the_actual_registry_findings_not_just_a_count()
    {
        const string name = "wu-test-punkt9-scan.exe";
        const string appPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        var keyPath = $@"HKCU\{appPathsRoot}\{name}";

        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{appPathsRoot}\{name}"))
            key!.SetValue(string.Empty, @"C:\this-path-does-not-exist-punkt9-scan.exe");

        try
        {
            var service = new CleanupService(_logging);
            var result = await service.ScanAsync(RegistryTarget(), CancellationToken.None);

            Assert.NotNull(result.RegistryFindings);
            Assert.Contains(result.RegistryFindings!, f => f.KeyPath == keyPath);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($@"{appPathsRoot}\{name}", throwOnMissingSubKey: false);
        }
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];

        public void Report(T value) => Reports.Add(value);
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
