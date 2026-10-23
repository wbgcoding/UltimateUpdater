using System.Diagnostics;
using System.IO;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

public class SafeEnumerateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-safeenum-" + Guid.NewGuid());

    public SafeEnumerateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var d in Directory.EnumerateDirectories(_root).Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0))
        {
            try { Directory.Delete(d); } catch { }
        }

        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void A_missing_folder_lists_nothing_instead_of_throwing()
    {
        Assert.Empty(SafeEnumerate.Files(Path.Combine(_root, "nope")));
        Assert.Empty(SafeEnumerate.Directories(Path.Combine(_root, "nope")));
    }

    [Fact]
    public void Files_and_plain_directories_are_listed()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        Assert.Equal([Path.Combine(_root, "a.txt")], SafeEnumerate.Files(_root));
        Assert.Equal([Path.Combine(_root, "sub")], SafeEnumerate.Directories(_root));
    }

    [Fact]
    public void A_junction_is_never_listed_as_a_directory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        using var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{Path.Combine(_root, "link")}\" \"{Path.Combine(_root, "real")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit(5000);
        Assert.Equal(0, mklink.ExitCode);

        Assert.Equal([Path.Combine(_root, "real")], SafeEnumerate.Directories(_root));
    }
}
