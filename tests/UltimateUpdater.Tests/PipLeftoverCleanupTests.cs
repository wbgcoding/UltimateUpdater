using System.Diagnostics;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>Removing one pip leftover folder goes through the same verified handle delete as every other
/// cleanup target, never a second path walk.</summary>
public class PipLeftoverCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-pip-leftover-" + Guid.NewGuid());

    public PipLeftoverCleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string WriteFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void A_plain_leftover_is_removed_and_its_bytes_counted()
    {
        var leftover = Path.Combine(_root, "~ip");
        WriteFile(Path.Combine(leftover, "a.py"), 100);
        WriteFile(Path.Combine(leftover, "sub", "deep", "b.py"), 200);

        var (removed, freed) = new CleanupService(new LoggingService()).DeletePipLeftoverTree(leftover);

        Assert.True(removed);
        Assert.Equal(300, freed);
        Assert.False(Directory.Exists(leftover));
    }

    /// <summary>One file in use must not wipe out the account of what did go.</summary>
    [Fact]
    public void A_locked_file_keeps_the_folder_but_the_rest_is_counted_as_freed()
    {
        var leftover = Path.Combine(_root, "~ip");
        WriteFile(Path.Combine(leftover, "gone.py"), 100);
        var locked = WriteFile(Path.Combine(leftover, "sub", "locked.pyd"), 50);
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);

        var (removed, freed) = new CleanupService(new LoggingService()).DeletePipLeftoverTree(leftover);

        Assert.False(removed);
        Assert.Equal(100, freed);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(Path.Combine(leftover, "gone.py")));
    }

    /// <summary>A junction inside the leftover is removed as a link at most; what it points at survives.
    /// (Contrast test: the old recursive path delete did not follow it either, so this stays green across
    /// the change.)</summary>
    [Fact]
    public void A_junction_inside_the_leftover_is_not_followed()
    {
        var outside = Path.Combine(_root, "outside");
        var precious = WriteFile(Path.Combine(outside, "precious.txt"), 10);
        var leftover = Path.Combine(_root, "~ip");
        WriteFile(Path.Combine(leftover, "a.py"), 100);
        using (var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{Path.Combine(leftover, "link")}\" \"{outside}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            mklink.WaitForExit(5000);
            Assert.Equal(0, mklink.ExitCode);
        }

        new CleanupService(new LoggingService()).DeletePipLeftoverTree(leftover);

        Assert.True(File.Exists(precious));
    }
}
