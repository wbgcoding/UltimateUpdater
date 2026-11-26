using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>A folder whose contents could not be fully read must never look old: offering it for removal
/// because part of it was unreadable would delete something in use.</summary>
public class LeftoverAgeWalkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-leftover-age-" + Guid.NewGuid());
    private readonly List<string> _denied = [];

    public LeftoverAgeWalkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var path in _denied)
            Allow(path);

        foreach (var link in Directory.EnumerateDirectories(_root).Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0))
        {
            try { Directory.Delete(link); } catch (IOException) { }
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Deny(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        info.SetAccessControl(security);
        _denied.Add(directory);
    }

    private static void Allow(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();
            security.RemoveAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            info.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // best effort; the temp folder is removed by the OS eventually
        }
    }

    [Fact]
    public void A_listing_error_inside_the_folder_makes_it_recent_not_old()
    {
        var candidate = Path.Combine(_root, "App");
        var sub = Path.Combine(candidate, "data");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "busy.log"), "x");
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(sub, "busy.log"), old);
        Directory.SetLastWriteTimeUtc(sub, old);
        Directory.SetLastWriteTimeUtc(candidate, old);

        Deny(sub);

        Assert.Equal(DateTimeOffset.MaxValue, AppDataLeftoverDiscovery.NewestWriteTime(candidate, CancellationToken.None));
    }

    [Fact]
    public void A_folder_that_listed_cleanly_keeps_its_real_age()
    {
        var candidate = Path.Combine(_root, "App");
        Directory.CreateDirectory(candidate);
        var file = Path.Combine(candidate, "old.dll");
        File.WriteAllText(file, "x");
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, old);
        Directory.SetLastWriteTimeUtc(candidate, old);

        Assert.Equal(new DateTimeOffset(old, TimeSpan.Zero), AppDataLeftoverDiscovery.NewestWriteTime(candidate, CancellationToken.None));
    }

    [Fact]
    public void A_folder_that_vanished_before_its_age_was_read_is_recent_not_infinitely_old()
    {
        Assert.Equal(
            DateTimeOffset.MaxValue,
            AppDataLeftoverDiscovery.NewestWriteTime(Path.Combine(_root, "gone"), CancellationToken.None));
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static void AgeFile(string file, DateTime time)
    {
        File.SetCreationTimeUtc(file, time);
        File.SetLastWriteTimeUtc(file, time);
        File.SetLastAccessTimeUtc(file, time);
    }

    [Fact]
    public void A_folder_of_only_old_files_is_idle_and_a_recent_one_in_a_sub_folder_makes_it_recent()
    {
        var folder = Path.Combine(_root, "App");
        var sub = Path.Combine(folder, "data");
        Directory.CreateDirectory(sub);
        var oldFile = Path.Combine(folder, "old.dll");
        var deepFile = Path.Combine(sub, "deep.dat");
        File.WriteAllText(oldFile, "x");
        File.WriteAllText(deepFile, "x");
        var old = Now.UtcDateTime.AddDays(-400);
        AgeFile(oldFile, old);
        AgeFile(deepFile, old);

        Assert.Equal(AppDataLeftoverDiscovery.FolderUse.Idle, AppDataLeftoverDiscovery.UseOf(folder, Now, CancellationToken.None));

        File.SetLastAccessTimeUtc(deepFile, Now.UtcDateTime.AddDays(-2));
        Assert.Equal(AppDataLeftoverDiscovery.FolderUse.Recent, AppDataLeftoverDiscovery.UseOf(folder, Now, CancellationToken.None));
    }

    [Fact]
    public void A_recent_hidden_system_file_counts_as_recent_use()
    {
        var folder = Path.Combine(_root, "App");
        Directory.CreateDirectory(folder);
        var hidden = Path.Combine(folder, "state.dat");
        File.WriteAllText(hidden, "x");
        AgeFile(hidden, Now.UtcDateTime.AddDays(-1));
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        try
        {
            Assert.Equal(AppDataLeftoverDiscovery.FolderUse.Recent, AppDataLeftoverDiscovery.UseOf(folder, Now, CancellationToken.None));
        }
        finally
        {
            File.SetAttributes(hidden, FileAttributes.Normal);
        }
    }

    [Fact]
    public void An_empty_folder_is_empty_not_idle()
    {
        var folder = Path.Combine(_root, "App");
        Directory.CreateDirectory(Path.Combine(folder, "inner"));

        Assert.Equal(AppDataLeftoverDiscovery.FolderUse.Empty, AppDataLeftoverDiscovery.UseOf(folder, Now, CancellationToken.None));
    }

    [Fact]
    public void A_root_that_is_itself_a_reparse_point_measures_zero()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        File.WriteAllBytes(Path.Combine(real, "a.bin"), new byte[10]);
        var link = Path.Combine(_root, "link");
        TestLinks.CreateJunction(link, real);

        Assert.Equal(10, DirectorySizeWalker.Measure(real, 3, 100));
        Assert.Equal(0, DirectorySizeWalker.Measure(link, 3, 100));
    }

    [Fact]
    public void The_reporting_listing_overloads_call_back_on_a_failure_and_stay_silent_otherwise()
    {
        var calls = 0;
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "d"));

        Assert.Single(SafeEnumerate.Files(_root, () => calls++));
        Assert.Single(SafeEnumerate.Directories(_root, () => calls++));
        Assert.Equal(0, calls);

        Assert.Empty(SafeEnumerate.Files(Path.Combine(_root, "missing"), () => calls++));
        Assert.Empty(SafeEnumerate.Directories(Path.Combine(_root, "missing"), () => calls++));
        Assert.Equal(2, calls);
    }
}
