using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>The title bar's "Portable" badge reads this flag directly; it must stay the exact opposite
/// of <see cref="AppPaths.IsInstalled"/>, the two are never allowed to independently drift.</summary>
public class AppPathsTests
{
    [Fact]
    public void IsPortable_is_the_opposite_of_IsInstalled() =>
        Assert.Equal(!AppPaths.IsInstalled, AppPaths.IsPortable);

    private const string Profile = @"C:\Users\Test";
    private const string Temp = @"C:\Users\Test\AppData\Local\Temp\";
    private const string ProgramFolder = @"C:\Users\Test\AppData\Local\Programs\UltimateUpdater\";

    private static AppPaths.DataFolderRejection Validate(string path, bool installed = true) =>
        AppPaths.ValidateRegistryBackupFolder(path, installed, ProgramFolder, Temp, Profile);

    /// <summary>The data-folder rules reject this one (it lies inside the current data folder); the backup
    /// folder rules must not, or the factory default would be refused the first time it is edited.</summary>
    [Fact]
    public void The_default_registry_backup_folder_is_accepted() =>
        Assert.Equal(AppPaths.DataFolderRejection.None,
            Validate(@"C:\Users\Test\AppData\Roaming\UltimateUpdater\registry-backups"));

    [Fact]
    public void A_folder_inside_the_profile_is_accepted() =>
        Assert.Equal(AppPaths.DataFolderRejection.None, Validate(@"C:\Users\Test\Documents\Registry backups"));

    [Theory]
    [InlineData(@"D:\Backups")]
    [InlineData(@"C:\Users\Other\backups")]
    [InlineData(@"C:\Windows\System32\backups")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_folder_outside_the_profile_is_rejected_as_such(string path) =>
        Assert.Equal(AppPaths.DataFolderRejection.OutsideUserProfile, Validate(path));

    [Fact]
    public void A_temp_or_cache_folder_inside_the_profile_is_rejected() =>
        Assert.Equal(AppPaths.DataFolderRejection.TempOrCache, Validate(@"C:\Users\Test\AppData\Local\Temp\backups"));

    [Fact]
    public void The_program_folder_is_rejected_only_for_an_installed_build()
    {
        var inside = @"C:\Users\Test\AppData\Local\Programs\UltimateUpdater\backups";

        Assert.Equal(AppPaths.DataFolderRejection.InsideProgramFolder, Validate(inside, installed: true));
        Assert.Equal(AppPaths.DataFolderRejection.None, Validate(inside, installed: false));
    }

    [Fact]
    public void An_oversized_pointer_file_reads_as_no_pointer_and_a_normal_one_is_trimmed()
    {
        var directory = Directory.CreateTempSubdirectory("uu-pointer-").FullName;
        try
        {
            var huge = Path.Combine(directory, "huge.txt");
            File.WriteAllText(huge, new string('a', 200 * 1024));
            var normal = Path.Combine(directory, "normal.txt");
            File.WriteAllText(normal, "  D:\\Data\\UltimateUpdater \r\n");

            Assert.Null(AppPaths.TryReadPointer(huge));
            Assert.Equal(@"D:\Data\UltimateUpdater", AppPaths.TryReadPointer(normal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"D:\Data\UltimateUpdater", true)]
    [InlineData(@"D:\Data\UltimateUpdater\", true)]
    [InlineData(@"D:\Data\ultimateupdater", true)]
    [InlineData(@"D:\Data", false)]
    [InlineData(@"D:\", false)]
    [InlineData(@"D:\Data\UltimateUpdater2", false)]
    [InlineData(@"D:\UltimateUpdater\logs", false)]
    [InlineData("", false)]
    public void A_pointer_target_is_accepted_only_as_a_dedicated_subfolder(string target, bool expected) =>
        Assert.Equal(expected, AppPaths.IsDedicatedDataFolder(target));

    [Theory]
    [InlineData(@"C:\Roam\UltimateUpdater", true)]
    [InlineData(@"C:\Apps\UU\data", true)]
    [InlineData(@"C:\Apps\UU\data\", true)]
    [InlineData(@"D:\Data\UltimateUpdater", true)]
    [InlineData(@"D:\Data", false)]
    [InlineData(@"C:\Roam\Documents", false)]
    public void Delete_my_data_refuses_a_folder_the_app_did_not_create(string dataFolder, bool expected) =>
        Assert.Equal(expected, AppDataCleaner.IsDeletableDataFolder(
            dataFolder, @"C:\Roam\UltimateUpdater", @"C:\Apps\UU\data"));
}
