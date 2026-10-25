using System.Diagnostics;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// The settings window lets the data folder be moved. This covers the pure parts of the
/// mechanism, validation, the pointer round trip and the copy, all through explicit paths a test controls;
/// none of it ever touches the real, fixed pointer location or the process's own <see
/// cref="AppPaths.DataDirectory"/>. The one real move on the target machine is outside what this session
/// can do and stays outstanding.
/// </summary>
public class DataFolderTests
{
    /// <summary>
    /// Fixtures used to live directly under <c>%TEMP%</c>, which this fix now refuses as a data folder
    /// target on purpose: every ordinary "just some writable folder" fixture moved here instead, so the
    /// temp/cache tests further down are the only ones that deliberately still reach into <c>%TEMP%</c>.
    /// </summary>
    private static readonly string TestRoot = EnsureTestRoot();

    /// <summary>A folder that is never the current data folder, used wherever a test needs SOME value for
    /// that new parameter but is not itself testing this fix.</summary>
    private static readonly string DummyCurrentDataDirectory =
        Path.Combine(TestRoot, "not-the-current-data-folder-" + Guid.NewGuid());

    /// <summary>A path that is never actually under any real temp path, used wherever a test needs SOME
    /// value for that new parameter but is not itself testing this fix.</summary>
    private static readonly string DummyTempPath =
        Path.Combine(Path.GetTempPath(), "wu-datafolder-not-really-temp-" + Guid.NewGuid());

    private static string EnsureTestRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wu-datafolder-tests");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string NewTempDir(string suffix)
    {
        var dir = Path.Combine(TestRoot, suffix + "-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

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

    [Fact]
    public void An_ordinary_writable_folder_is_accepted()
    {
        var folder = NewTempDir("ok");
        try
        {
            Assert.Equal(
                AppPaths.DataFolderRejection.None,
                AppPaths.ValidateNewDataFolder(
                    folder, isInstalled: false, baseDirectory: Path.GetTempPath(),
                    currentDataDirectory: DummyCurrentDataDirectory, tempPath: DummyTempPath));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_folder_reached_through_a_junction_is_rejected()
    {
        var real = NewTempDir("junction-target");
        var link = Path.Combine(Path.GetTempPath(), "wu-datafolder-junction-link-" + Guid.NewGuid());
        CreateJunction(link, real);

        try
        {
            Assert.Equal(
                AppPaths.DataFolderRejection.Link,
                AppPaths.ValidateNewDataFolder(
                    link, isInstalled: false, baseDirectory: Path.GetTempPath(),
                    currentDataDirectory: DummyCurrentDataDirectory, tempPath: DummyTempPath));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(real, recursive: true);
        }
    }

    [Fact]
    public void A_windows_system_folder_is_rejected()
    {
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.Equal(
            AppPaths.DataFolderRejection.SystemFolder,
            AppPaths.ValidateNewDataFolder(
                windowsFolder, isInstalled: false, baseDirectory: Path.GetTempPath(),
                currentDataDirectory: DummyCurrentDataDirectory, tempPath: DummyTempPath));
    }

    [Fact]
    public void A_folder_inside_the_program_folder_is_rejected_when_installed()
    {
        var programFolder = NewTempDir("program");
        var dataFolder = Path.Combine(programFolder, "data");
        Directory.CreateDirectory(dataFolder);

        try
        {
            Assert.Equal(
                AppPaths.DataFolderRejection.InsideProgramFolder,
                AppPaths.ValidateNewDataFolder(
                    dataFolder, isInstalled: true, baseDirectory: programFolder,
                    currentDataDirectory: DummyCurrentDataDirectory, tempPath: DummyTempPath));
        }
        finally
        {
            Directory.Delete(programFolder, recursive: true);
        }
    }

    [Fact]
    public void The_same_folder_inside_the_program_folder_is_fine_when_portable()
    {
        var programFolder = NewTempDir("portable");
        var dataFolder = Path.Combine(programFolder, "data");
        Directory.CreateDirectory(dataFolder);

        try
        {
            Assert.Equal(
                AppPaths.DataFolderRejection.None,
                AppPaths.ValidateNewDataFolder(
                    dataFolder, isInstalled: false, baseDirectory: programFolder,
                    currentDataDirectory: DummyCurrentDataDirectory, tempPath: DummyTempPath));
        }
        finally
        {
            Directory.Delete(programFolder, recursive: true);
        }
    }

    [Fact]
    public void A_non_writable_folder_is_rejected()
    {
        // A file where a folder is expected can never be prepared as a directory.
        var blockingFile = Path.Combine(TestRoot, "wu-datafolder-block-" + Guid.NewGuid());
        File.WriteAllBytes(blockingFile, []);

        try
        {
            Assert.Equal(
                AppPaths.DataFolderRejection.NotWritable,
                AppPaths.ValidateNewDataFolder(
                    blockingFile, isInstalled: false, baseDirectory: Path.GetTempPath(),
                    currentDataDirectory: DummyCurrentDataDirectory, tempPath: DummyTempPath));
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public void The_pointer_round_trips_through_an_explicit_file()
    {
        var pointerFile = Path.Combine(NewTempDir("pointer"), "datapath.txt");
        var target = NewTempDir("pointer-target");

        try
        {
            Assert.True(AppPaths.WriteDataFolderPointer(target, pointerFile));
            Assert.Equal(target, AppPaths.TryReadPointer(pointerFile));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(pointerFile)!, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void A_missing_pointer_file_resolves_to_nothing()
    {
        var pointerFile = Path.Combine(Path.GetTempPath(), "wu-datafolder-missing-" + Guid.NewGuid(), "datapath.txt");

        Assert.Null(AppPaths.TryReadPointer(pointerFile));
    }

    [Fact]
    public void Copying_carries_files_and_subfolders_but_never_the_pointer_or_the_probe_file()
    {
        var source = NewTempDir("copy-source");
        var destination = NewTempDir("copy-dest");
        try
        {
            File.WriteAllText(Path.Combine(source, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(source, "datapath.txt"), "should not travel");
            File.WriteAllBytes(Path.Combine(source, ".writetest"), []);
            Directory.CreateDirectory(Path.Combine(source, "logs"));
            File.WriteAllText(Path.Combine(source, "logs", "today.log"), "hello");

            Assert.True(AppPaths.TryCopyDirectory(source, destination, out var skipped));
            Assert.Empty(skipped);

            Assert.True(File.Exists(Path.Combine(destination, "settings.json")));
            Assert.True(File.Exists(Path.Combine(destination, "logs", "today.log")));
            Assert.False(File.Exists(Path.Combine(destination, "datapath.txt")));
            Assert.False(File.Exists(Path.Combine(destination, ".writetest")));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(destination, recursive: true);
        }
    }

    // ---- move into a dedicated subfolder, never over foreign files ----

    [Fact]
    public void A_non_empty_dedicated_subfolder_at_the_target_is_rejected()
    {
        var picked = NewTempDir("nonempty-pick");
        var existingSubfolder = Path.Combine(picked, "UltimateUpdater");
        Directory.CreateDirectory(existingSubfolder);
        File.WriteAllText(Path.Combine(existingSubfolder, "settings.json"), "already here");

        try
        {
            Assert.Equal(
                AppPaths.DataFolderRejection.NonEmptyTarget,
                AppPaths.ValidateNewDataFolder(
                    picked, isInstalled: false, baseDirectory: Path.GetTempPath(),
                    currentDataDirectory: DummyCurrentDataDirectory, tempPath: DummyTempPath));
        }
        finally
        {
            Directory.Delete(picked, recursive: true);
        }
    }

    [Fact]
    public void An_empty_existing_dedicated_subfolder_is_accepted()
    {
        var picked = NewTempDir("empty-subfolder-pick");
        Directory.CreateDirectory(Path.Combine(picked, "UltimateUpdater"));

        try
        {
            Assert.Equal(
                AppPaths.DataFolderRejection.None,
                AppPaths.ValidateNewDataFolder(
                    picked, isInstalled: false, baseDirectory: Path.GetTempPath(),
                    currentDataDirectory: DummyCurrentDataDirectory, tempPath: DummyTempPath));
        }
        finally
        {
            Directory.Delete(picked, recursive: true);
        }
    }

    [Fact]
    public void The_move_lands_in_the_dedicated_subfolder_and_never_touches_foreign_files_there()
    {
        var picked = NewTempDir("pick-foreign");
        File.WriteAllText(Path.Combine(picked, "settings.json"), "foreign");
        Directory.CreateDirectory(Path.Combine(picked, "logs"));
        File.WriteAllText(Path.Combine(picked, "logs", "x.txt"), "foreign log");

        var source = NewTempDir("own-data");
        File.WriteAllText(Path.Combine(source, "settings.json"), "{}");

        try
        {
            Assert.Equal(
                AppPaths.DataFolderRejection.None,
                AppPaths.ValidateNewDataFolder(
                    picked, isInstalled: false, baseDirectory: Path.GetTempPath(),
                    currentDataDirectory: DummyCurrentDataDirectory, tempPath: DummyTempPath));

            var effectiveTarget = AppPaths.EffectiveDataFolderTarget(picked);
            Assert.Equal(Path.Combine(picked, "UltimateUpdater"), effectiveTarget);

            Assert.True(AppPaths.TryCopyDirectory(source, effectiveTarget, out var skipped));
            Assert.Empty(skipped);
            Assert.True(File.Exists(Path.Combine(effectiveTarget, "settings.json")));

            // the foreign files sitting directly in the picked folder were never touched by the move
            Assert.Equal("foreign", File.ReadAllText(Path.Combine(picked, "settings.json")));
            Assert.True(File.Exists(Path.Combine(picked, "logs", "x.txt")));

            // nor by DeleteCachesAndHistory, which only ever knows about the app-owned subfolder
            Assert.False(AppDataCleaner.DeleteOwnedContent(effectiveTarget, new LoggingService()));
            Assert.Equal("foreign", File.ReadAllText(Path.Combine(picked, "settings.json")));
            Assert.True(File.Exists(Path.Combine(picked, "logs", "x.txt")));
        }
        finally
        {
            Directory.Delete(picked, recursive: true);
            Directory.Delete(source, recursive: true);
        }
    }

    // ---- refuse the current data folder, a folder inside it, or an ancestor ----

    [Fact]
    public void The_target_that_equals_the_current_data_folder_is_rejected()
    {
        var parent = NewTempDir("current-parent");
        var current = Path.Combine(parent, "UltimateUpdater");
        Directory.CreateDirectory(current);

        try
        {
            Assert.Equal(
                AppPaths.DataFolderRejection.InsideCurrentDataFolder,
                AppPaths.ValidateNewDataFolder(
                    parent, isInstalled: false, baseDirectory: Path.GetTempPath(),
                    currentDataDirectory: current, tempPath: DummyTempPath));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void A_target_inside_the_current_data_folder_is_rejected()
    {
        var current = NewTempDir("current-inside");
        var picked = Path.Combine(current, "somesub");
        Directory.CreateDirectory(picked);

        try
        {
            Assert.Equal(
                AppPaths.DataFolderRejection.InsideCurrentDataFolder,
                AppPaths.ValidateNewDataFolder(
                    picked, isInstalled: false, baseDirectory: Path.GetTempPath(),
                    currentDataDirectory: current, tempPath: DummyTempPath));
        }
        finally
        {
            Directory.Delete(current, recursive: true);
        }
    }

    [Fact]
    public void A_target_that_is_an_ancestor_of_the_current_data_folder_is_rejected()
    {
        var picked = NewTempDir("current-ancestor-pick");
        var current = Path.Combine(picked, "UltimateUpdater", "nested");
        Directory.CreateDirectory(current);

        try
        {
            Assert.Equal(
                AppPaths.DataFolderRejection.InsideCurrentDataFolder,
                AppPaths.ValidateNewDataFolder(
                    picked, isInstalled: false, baseDirectory: Path.GetTempPath(),
                    currentDataDirectory: current, tempPath: DummyTempPath));
        }
        finally
        {
            Directory.Delete(picked, recursive: true);
        }
    }

    // ---- refuse temp and cache locations ----

    [Fact]
    public void A_folder_under_the_temp_path_is_rejected()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "wu-datafolder-realtemp-" + Guid.NewGuid());
        var picked = Path.Combine(tempRoot, "x");
        Directory.CreateDirectory(picked);

        try
        {
            Assert.Equal(
                AppPaths.DataFolderRejection.TempOrCache,
                AppPaths.ValidateNewDataFolder(
                    picked, isInstalled: false, baseDirectory: Path.GetTempPath(),
                    currentDataDirectory: DummyCurrentDataDirectory, tempPath: Path.GetTempPath()));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void A_cleanup_catalogue_cache_directory_is_rejected()
    {
        // thumbnail-cache is always in the catalogue's Directories list, regardless of whether that
        // folder actually exists on the machine running the suite (unlike the Existing()-filtered
        // entries), so this is a deterministic real catalogue path, not a synthetic stand-in.
        var catalogueDirectory = CleanupCatalog.Build()
            .Single(target => target.Id == "thumbnail-cache")
            .Directories.Single();

        Assert.Equal(
            AppPaths.DataFolderRejection.TempOrCache,
            AppPaths.ValidateNewDataFolder(
                catalogueDirectory, isInstalled: false, baseDirectory: Path.GetTempPath(),
                currentDataDirectory: DummyCurrentDataDirectory, tempPath: DummyTempPath));
    }

    // ---- report files skipped during the move ----

    [Fact]
    public void A_locked_non_log_file_fails_the_whole_move_and_cleans_up_the_partial_copy()
    {
        var source = NewTempDir("move-source-locked");
        var logsDir = Path.Combine(source, "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(Path.Combine(source, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(logsDir, "today.log"), "hello");

        var destinationParent = NewTempDir("move-dest-parent-locked");
        var destination = Path.Combine(destinationParent, "UltimateUpdater");

        try
        {
            using (new FileStream(
                Path.Combine(source, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var moved = AppPaths.TryMoveDataFolder(source, logsDir, destination, out var blocking);

                Assert.False(moved);
                Assert.Single(blocking);
                Assert.Equal("settings.json", Path.GetFileName(blocking[0]));
                Assert.False(Directory.Exists(destination));
            }
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(destinationParent, recursive: true);
        }
    }

    [Fact]
    public void A_locked_log_file_does_not_block_the_move()
    {
        var source = NewTempDir("move-source-log-locked");
        var logsDir = Path.Combine(source, "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(Path.Combine(source, "settings.json"), "{}");
        var lockedLog = Path.Combine(logsDir, "today.log");
        File.WriteAllText(lockedLog, "hello");

        var destinationParent = NewTempDir("move-dest-parent-log-locked");
        var destination = Path.Combine(destinationParent, "UltimateUpdater");

        try
        {
            using (new FileStream(lockedLog, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var moved = AppPaths.TryMoveDataFolder(source, logsDir, destination, out var blocking);

                Assert.True(moved);
                Assert.Empty(blocking);
                Assert.True(File.Exists(Path.Combine(destination, "settings.json")));
                Assert.False(File.Exists(Path.Combine(destination, "logs", "today.log")));
            }
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(destinationParent, recursive: true);
        }
    }

    /// <summary>A file the running app deletes between the listing and the copy is gone, not locked:
    /// counting it as a skipped file would refuse the whole move over something that no longer exists.</summary>
    [Fact]
    public void A_file_deleted_after_it_was_listed_is_gone_not_locked()
    {
        var source = NewTempDir("move-source-vanished-file");
        var logsDir = Path.Combine(source, "logs");
        File.WriteAllText(Path.Combine(source, "kept.json"), "{}");
        File.WriteAllText(Path.Combine(source, "gone.json"), "{}");

        var destinationParent = NewTempDir("move-dest-parent-vanished-file");
        var destination = Path.Combine(destinationParent, "UltimateUpdater");

        try
        {
            var moved = AppPaths.TryMoveDataFolder(
                source, logsDir, destination, out var blocking,
                afterEnumerated: path =>
                {
                    if (Path.GetFileName(path) == "gone.json")
                        File.Delete(path);
                });

            Assert.True(moved);
            Assert.Empty(blocking);
            Assert.True(File.Exists(Path.Combine(destination, "kept.json")));
            Assert.False(File.Exists(Path.Combine(destination, "gone.json")));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(destinationParent, recursive: true);
        }
    }

    [Fact]
    public void A_subfolder_deleted_after_it_was_listed_neither_throws_nor_blocks_the_move()
    {
        var source = NewTempDir("move-source-vanished-dir");
        var logsDir = Path.Combine(source, "logs");
        File.WriteAllText(Path.Combine(source, "kept.json"), "{}");
        var vanishing = Path.Combine(source, "reports");
        Directory.CreateDirectory(vanishing);
        File.WriteAllText(Path.Combine(vanishing, "r.txt"), "x");

        var destinationParent = NewTempDir("move-dest-parent-vanished-dir");
        var destination = Path.Combine(destinationParent, "UltimateUpdater");

        try
        {
            var moved = AppPaths.TryMoveDataFolder(
                source, logsDir, destination, out var blocking,
                afterEnumerated: path =>
                {
                    if (Path.GetFileName(path) == "reports" && Directory.Exists(path))
                        Directory.Delete(path, recursive: true);
                });

            Assert.True(moved);
            Assert.Empty(blocking);
            Assert.True(File.Exists(Path.Combine(destination, "kept.json")));
            Assert.False(Directory.Exists(Path.Combine(destination, "reports")));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(destinationParent, recursive: true);
        }
    }

    // ---- one-time %AppData% folder migration after the WindowsUpdater -> UltimateUpdater rename ----

    [Fact]
    public void An_old_folder_is_copied_into_the_new_one_when_the_new_one_does_not_exist_yet()
    {
        var legacy = NewTempDir("legacy");
        var roaming = Path.Combine(Path.GetTempPath(), "wu-datafolder-roaming-" + Guid.NewGuid());
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");

        try
        {
            AppPaths.TryMigrateLegacyDataFolder(roaming, legacy);

            Assert.True(File.Exists(Path.Combine(roaming, "settings.json")));
            // the old folder is left in place, a copy never a move
            Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
            Assert.NotNull(AppPaths.ConsumeLegacyDataFolderMigrationMessage());
        }
        finally
        {
            Directory.Delete(legacy, recursive: true);
            if (Directory.Exists(roaming))
                Directory.Delete(roaming, recursive: true);
        }
    }

    [Fact]
    public void Migration_is_skipped_when_the_new_folder_already_exists()
    {
        var legacy = NewTempDir("legacy-existing");
        var roaming = NewTempDir("roaming-existing");
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");

        try
        {
            // The static message field is process-wide and something elsewhere in this run may already
            // have set it (e.g. AppPaths.DataDirectory's own real, unrelated migration on the machine
            // running the suite); clear it first so this test only observes its OWN call below.
            AppPaths.ConsumeLegacyDataFolderMigrationMessage();

            AppPaths.TryMigrateLegacyDataFolder(roaming, legacy);

            Assert.False(File.Exists(Path.Combine(roaming, "settings.json")));
            Assert.Null(AppPaths.ConsumeLegacyDataFolderMigrationMessage());
        }
        finally
        {
            Directory.Delete(legacy, recursive: true);
            Directory.Delete(roaming, recursive: true);
        }
    }

    [Fact]
    public void Migration_is_skipped_when_there_is_no_old_folder()
    {
        var legacy = Path.Combine(Path.GetTempPath(), "wu-datafolder-no-legacy-" + Guid.NewGuid());
        var roaming = Path.Combine(Path.GetTempPath(), "wu-datafolder-roaming-none-" + Guid.NewGuid());

        // See the comment in the test above: clear any leftover state from elsewhere in the process first.
        AppPaths.ConsumeLegacyDataFolderMigrationMessage();

        AppPaths.TryMigrateLegacyDataFolder(roaming, legacy);

        Assert.False(Directory.Exists(roaming));
        Assert.Null(AppPaths.ConsumeLegacyDataFolderMigrationMessage());
    }

    // ---- the rename migration honours a legacy data folder pointer and retries ----

    [Fact]
    public void A_legacy_pointer_redirects_the_migration_to_the_folder_it_names()
    {
        var legacy = NewTempDir("legacy-pointer");
        var realData = NewTempDir("legacy-pointer-target");
        var roaming = Path.Combine(TestRoot, "wu-datafolder-roaming-" + Guid.NewGuid());
        File.WriteAllText(Path.Combine(legacy, "datapath.txt"), realData);
        File.WriteAllText(Path.Combine(realData, "settings.json"), "{}");

        AppPaths.ConsumeLegacyDataFolderMigrationMessage();
        try
        {
            AppPaths.TryMigrateLegacyDataFolder(roaming, legacy);

            Assert.True(File.Exists(Path.Combine(roaming, "settings.json")));
            // the pointer file itself never travels with the copy
            Assert.False(File.Exists(Path.Combine(roaming, "datapath.txt")));
            Assert.NotNull(AppPaths.ConsumeLegacyDataFolderMigrationMessage());
        }
        finally
        {
            Directory.Delete(legacy, recursive: true);
            Directory.Delete(realData, recursive: true);
            if (Directory.Exists(roaming))
                Directory.Delete(roaming, recursive: true);
        }
    }

    [Fact]
    public void An_invalid_legacy_pointer_falls_back_to_the_legacy_folder_itself()
    {
        var legacy = NewTempDir("legacy-bad-pointer");
        var roaming = Path.Combine(TestRoot, "wu-datafolder-roaming-" + Guid.NewGuid());
        File.WriteAllText(
            Path.Combine(legacy, "datapath.txt"),
            Path.Combine(TestRoot, "wu-does-not-exist-" + Guid.NewGuid()));
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");

        AppPaths.ConsumeLegacyDataFolderMigrationMessage();
        try
        {
            AppPaths.TryMigrateLegacyDataFolder(roaming, legacy);

            Assert.True(File.Exists(Path.Combine(roaming, "settings.json")));
            Assert.NotNull(AppPaths.ConsumeLegacyDataFolderMigrationMessage());
        }
        finally
        {
            Directory.Delete(legacy, recursive: true);
            if (Directory.Exists(roaming))
                Directory.Delete(roaming, recursive: true);
        }
    }

    [Fact]
    public void A_failed_copy_leaves_no_partial_folder_and_a_retry_can_still_succeed()
    {
        var missingSource = Path.Combine(TestRoot, "wu-datafolder-missing-source-" + Guid.NewGuid());
        var destination = Path.Combine(TestRoot, "wu-datafolder-migrate-dest-" + Guid.NewGuid());

        Assert.False(AppPaths.TryMigrateInto(missingSource, destination));
        Assert.False(Directory.Exists(destination));

        var realSource = NewTempDir("migrate-real-source");
        File.WriteAllText(Path.Combine(realSource, "settings.json"), "{}");
        try
        {
            Assert.True(AppPaths.TryMigrateInto(realSource, destination));
            Assert.True(File.Exists(Path.Combine(destination, "settings.json")));
        }
        finally
        {
            Directory.Delete(realSource, recursive: true);
            Directory.Delete(destination, recursive: true);
        }
    }

    /// <summary>A locked data file in the old folder must fail the migration rather than land a copy without
    /// it: the new folder existing stops every later retry, so the file would be lost for good.</summary>
    [Fact]
    public void A_locked_data_file_fails_the_migration_and_leaves_no_folder()
    {
        var source = NewTempDir("migrate-locked-source");
        var destination = Path.Combine(TestRoot, "wu-datafolder-migrate-locked-dest-" + Guid.NewGuid());
        var settings = Path.Combine(source, "settings.json");
        File.WriteAllText(settings, "{}");
        try
        {
            using (new FileStream(settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False(AppPaths.TryMigrateInto(source, destination));
            }

            Assert.False(Directory.Exists(destination));
            Assert.True(AppPaths.TryMigrateInto(source, destination));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
        }
    }

    /// <summary>A locked log is not worth blocking the migration for: logs are rewritten from scratch.</summary>
    [Fact]
    public void A_locked_log_does_not_block_the_migration()
    {
        var source = NewTempDir("migrate-locked-log-source");
        var destination = Path.Combine(TestRoot, "wu-datafolder-migrate-log-dest-" + Guid.NewGuid());
        File.WriteAllText(Path.Combine(source, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(source, "logs"));
        var log = Path.Combine(source, "logs", "app.log");
        File.WriteAllText(log, "x");
        try
        {
            using (new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.True(AppPaths.TryMigrateInto(source, destination));
            }

            Assert.True(File.Exists(Path.Combine(destination, "settings.json")));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
        }
    }

    /// <summary>A junction inside the folder being moved must not be followed: what it points at is not part
    /// of the data and may be anyone's folder. It is reported as skipped, which fails the move.</summary>
    [Fact]
    public void A_junction_inside_the_source_copies_nothing_from_its_target()
    {
        var source = NewTempDir("junction-source");
        var outside = NewTempDir("junction-outside");
        File.WriteAllText(Path.Combine(source, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "not part of the data");
        var link = Path.Combine(source, "linked");
        CreateJunction(link, outside);
        var destination = Path.Combine(TestRoot, "wu-datafolder-junction-dest-" + Guid.NewGuid());

        try
        {
            Assert.True(AppPaths.TryCopyDirectory(source, destination, out var skipped));

            Assert.True(File.Exists(Path.Combine(destination, "settings.json")));
            Assert.False(Directory.Exists(Path.Combine(destination, "linked")));
            Assert.False(File.Exists(Path.Combine(destination, "linked", "secret.txt")));
            Assert.Equal([link], skipped);
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(source, recursive: true);
            Directory.Delete(outside, recursive: true);
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public void A_junction_inside_the_source_fails_the_whole_move()
    {
        var source = NewTempDir("junction-move-source");
        var outside = NewTempDir("junction-move-outside");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "not part of the data");
        var link = Path.Combine(source, "linked");
        CreateJunction(link, outside);
        var destination = Path.Combine(TestRoot, "wu-datafolder-junction-move-dest-" + Guid.NewGuid());

        try
        {
            var moved = AppPaths.TryMoveDataFolder(source, Path.Combine(source, "logs"), destination, out var blocking);

            Assert.False(moved);
            Assert.Equal([link], blocking);
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(source, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void A_file_symlink_inside_the_source_is_skipped_not_copied()
    {
        var source = NewTempDir("symlink-source");
        var outside = NewTempDir("symlink-outside");
        var target = Path.Combine(outside, "secret.txt");
        File.WriteAllText(target, "not part of the data");
        var link = Path.Combine(source, "settings.json");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // creating a symlink needs developer mode or an elevated token; the junction tests cover the rule
            Directory.Delete(source, recursive: true);
            Directory.Delete(outside, recursive: true);
            return;
        }

        var destination = Path.Combine(TestRoot, "wu-datafolder-symlink-dest-" + Guid.NewGuid());
        try
        {
            Assert.True(AppPaths.TryCopyDirectory(source, destination, out var skipped));

            Assert.False(File.Exists(Path.Combine(destination, "settings.json")));
            Assert.Equal([link], skipped);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(outside, recursive: true);
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
        }
    }
}
