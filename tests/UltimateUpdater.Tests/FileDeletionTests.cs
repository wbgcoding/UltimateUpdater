using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>Shared by <c>IconCacheService</c>, <c>SelfUpdateService</c>, <c>RegistryBackupService</c> and
/// <c>DriverSources</c>, all four had byte-identical delete-and-swallow loops before
/// this consolidation; only <c>DriverSources</c> used the <c>onFailed</c> callback, the other three call
/// with none and stay silent, matching their prior behavior.</summary>
public class FileDeletionTests
{
    [Fact]
    public void An_existing_file_is_deleted()
    {
        var path = Path.GetTempFileName();

        FileDeletion.TryDelete(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_missing_file_is_a_silent_no_op()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tmp");

        FileDeletion.TryDelete(path);
    }

    [Fact]
    public void A_locked_file_invokes_onFailed_and_does_not_throw()
    {
        var path = Path.GetTempFileName();
        using var handle = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Exception? caught = null;

        FileDeletion.TryDelete(path, ex => caught = ex);

        Assert.NotNull(caught);
        Assert.IsAssignableFrom<IOException>(caught);
    }

    [Fact]
    public void A_locked_file_with_no_onFailed_callback_just_stays_silent()
    {
        var path = Path.GetTempFileName();
        using var handle = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        FileDeletion.TryDelete(path);
    }

    [Fact]
    public void A_staging_folder_path_is_fresh_and_random_below_the_root()
    {
        var root = Path.GetTempPath();

        var first = FileDeletion.NewStagingFolderPath(root, "demo-");
        var second = FileDeletion.NewStagingFolderPath(root, "demo-");

        Assert.NotEqual(first, second);
        Assert.Equal(Path.TrimEndingDirectorySeparator(root), Path.GetDirectoryName(first));
        Assert.StartsWith("demo-", Path.GetFileName(first));
        Assert.Equal("demo-".Length + 32, Path.GetFileName(first).Length);
        Assert.False(Directory.Exists(first));
    }

    [Fact]
    public void A_staged_file_and_its_empty_folder_are_both_removed()
    {
        var folder = FileDeletion.NewStagingFolderPath(Path.GetTempPath(), "wu-stage-");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "setup.exe");
        File.WriteAllBytes(file, [1]);

        FileDeletion.TryDeleteStaged(file, "wu-stage-");

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Anything_else_in_the_staging_folder_keeps_the_folder_and_is_never_taken_along()
    {
        var folder = FileDeletion.NewStagingFolderPath(Path.GetTempPath(), "wu-stage-");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "setup.exe");
        var planted = Path.Combine(folder, "version.dll");
        File.WriteAllBytes(file, [1]);
        File.WriteAllBytes(planted, [2]);
        try
        {
            FileDeletion.TryDeleteStaged(file, "wu-stage-");

            Assert.False(File.Exists(file));
            Assert.True(File.Exists(planted));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_locked_staged_file_leaves_its_folder_in_place()
    {
        var folder = FileDeletion.NewStagingFolderPath(Path.GetTempPath(), "wu-stage-");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "setup.exe");
        File.WriteAllBytes(file, [1]);
        try
        {
            using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                FileDeletion.TryDeleteStaged(file, "wu-stage-");

            Assert.True(File.Exists(file));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_file_outside_a_staging_folder_never_costs_its_folder()
    {
        var folder = Directory.CreateTempSubdirectory("wu-plain-").FullName;
        var file = Path.Combine(folder, "setup.exe");
        File.WriteAllBytes(file, [1]);
        try
        {
            FileDeletion.TryDeleteStaged(file, "wu-stage-");

            Assert.False(File.Exists(file));
            Assert.True(Directory.Exists(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
