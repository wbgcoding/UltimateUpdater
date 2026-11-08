using System.IO;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// The program's own folder is protected as a subtree to skip. When the program sits in a folder that is too
/// broad to protect whole (profile root, drive root, local app data), the user's Temp below it must still be
/// cleanable and only the exe and its data folder stay off limits.
/// </summary>
public class CleanupOwnPathTests : IDisposable
{
    private readonly string _profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string _temp = Path.GetTempPath();

    public void Dispose() => CleanupSafety.OwnPathsOverrideForTest.Value = null;

    private static List<string> ProtectedPaths() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
    ];

    private string[] OwnPathsFor(string baseDirectory) =>
        CleanupSafety.BuildOwnPaths(baseDirectory, Path.Combine(baseDirectory, "UltimateUpdater.exe"), _temp, ProtectedPaths());

    [Fact]
    public void An_exe_in_the_profile_root_leaves_temp_cleanable_and_protects_itself_and_its_data_folder()
    {
        CleanupSafety.OwnPathsOverrideForTest.Value = OwnPathsFor(_profile);

        var folder = Path.Combine(_temp, "wu-ownpath-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            Assert.True(CleanupSafety.IsSafeToEmpty(_temp));
            Assert.True(CleanupSafety.IsSafeToEmpty(folder));
            Assert.False(CleanupSafety.IsDeletableFile(folder, Path.Combine(_profile, "UltimateUpdater.exe")));
            Assert.False(CleanupSafety.IsDeletableFile(folder, Path.Combine(_profile, "data", "history.json")));
            Assert.True(CleanupSafety.IsOwnPath(Path.Combine(_profile, "data")));
            Assert.False(CleanupSafety.IsOwnPath(Path.Combine(_profile, "Other")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void An_exe_in_the_local_app_data_folder_or_a_drive_root_is_treated_the_same()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var driveRoot = Path.GetPathRoot(_profile)!;

        foreach (var baseDirectory in new[] { local, driveRoot, Path.GetDirectoryName(local)! })
        {
            CleanupSafety.OwnPathsOverrideForTest.Value = OwnPathsFor(baseDirectory);

            Assert.True(CleanupSafety.IsSafeToEmpty(_temp), baseDirectory);
            Assert.True(CleanupSafety.IsOwnPath(Path.Combine(baseDirectory, "UltimateUpdater.exe")), baseDirectory);
            Assert.True(CleanupSafety.IsOwnPath(Path.Combine(baseDirectory, "data")), baseDirectory);
        }
    }

    [Fact]
    public void An_exe_in_a_normal_folder_still_protects_the_whole_folder()
    {
        var appFolder = Path.Combine(_temp, "wu-ownpath-app-" + Guid.NewGuid(), "app");
        CleanupSafety.OwnPathsOverrideForTest.Value = OwnPathsFor(appFolder);

        Assert.True(CleanupSafety.IsOwnPath(appFolder));
        Assert.True(CleanupSafety.IsOwnPath(Path.Combine(appFolder, "lib", "x.dll")));
        Assert.False(CleanupSafety.IsSafeToEmpty(appFolder));
    }

    [Fact]
    public void The_extraction_folder_stays_protected_in_every_case()
    {
        CleanupSafety.OwnPathsOverrideForTest.Value = OwnPathsFor(_profile);

        Assert.True(CleanupSafety.IsOwnPath(Path.Combine(_temp, ".net", "App", "abc")));
        Assert.True(CleanupSafety.IsOwnPath(Path.Combine(_temp, "UltimateUpdater")));
    }

    [Fact]
    public void A_refused_existing_target_names_the_rule_that_refused_it()
    {
        var folder = Path.Combine(_temp, "wu-ownpath-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            CleanupSafety.OwnPathsOverrideForTest.Value = [folder];
            var logging = new LoggingService();
            var target = new CleanupTarget("test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x") { Directories = [folder] };

            var result = new CleanupService(logging).ScanAsync(target, CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(0, result.Files);
            Assert.False(CleanupSafety.IsSafeToEmpty(folder, out var reason));
            Assert.Contains("own folder", reason);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
