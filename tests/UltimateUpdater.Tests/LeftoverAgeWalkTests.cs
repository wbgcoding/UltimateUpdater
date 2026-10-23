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

        Assert.Equal(DateTimeOffset.MaxValue, LeftoverInstallDiscovery.NewestWriteTime(candidate, CancellationToken.None));
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

        Assert.Equal(new DateTimeOffset(old, TimeSpan.Zero), LeftoverInstallDiscovery.NewestWriteTime(candidate, CancellationToken.None));
    }

    [Fact]
    public void A_folder_that_vanished_before_its_age_was_read_is_recent_not_infinitely_old()
    {
        Assert.Equal(
            DateTimeOffset.MaxValue,
            LeftoverInstallDiscovery.NewestWriteTime(Path.Combine(_root, "gone"), CancellationToken.None));
    }

    [Fact]
    public void A_root_that_is_itself_a_reparse_point_measures_zero()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        File.WriteAllBytes(Path.Combine(real, "a.bin"), new byte[10]);
        var link = Path.Combine(_root, "link");
        using var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{real}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit(5000);
        Assert.Equal(0, mklink.ExitCode);

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
