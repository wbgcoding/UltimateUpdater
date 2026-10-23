using System.Diagnostics;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// A recycled folder can hold a junction or symlink somebody planted. Emptying the bin must remove the
/// link itself and never touch what it points at.
/// </summary>
public class RecycledLinkUnlinkTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wu-unlink-test-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

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

    private string MakeTarget()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "must survive");
        return target;
    }

    [Fact]
    public void A_junction_inside_the_tree_is_unlinked_and_its_target_survives()
    {
        var target = MakeTarget();
        var recycled = Path.Combine(_root, "recycled");
        Directory.CreateDirectory(Path.Combine(recycled, "sub"));
        File.WriteAllText(Path.Combine(recycled, "sub", "own.txt"), "x");
        CreateJunction(Path.Combine(recycled, "sub", "link"), target);

        CleanupService.UnlinkReparsePoints(recycled);

        Assert.False(Directory.Exists(Path.Combine(recycled, "sub", "link")));
        Assert.True(File.Exists(Path.Combine(recycled, "sub", "own.txt")));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));

        Directory.Delete(recycled, recursive: true);
        Assert.False(Directory.Exists(recycled));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void A_junction_as_the_recycled_item_itself_is_unlinked_and_its_target_survives()
    {
        var target = MakeTarget();
        var link = Path.Combine(_root, "recycled-link");
        CreateJunction(link, target);

        CleanupService.UnlinkReparsePoints(link);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void A_file_symlink_inside_the_tree_is_unlinked_and_its_target_survives()
    {
        var target = MakeTarget();
        var recycled = Path.Combine(_root, "recycled");
        Directory.CreateDirectory(recycled);
        var link = Path.Combine(recycled, "filelink.txt");
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(target, "keep.txt"));
        }
        catch (UnauthorizedAccessException)
        {
            return; // Creating a symlink needs developer mode or admin; the junction tests cover the walk.
        }
        catch (IOException)
        {
            return;
        }

        CleanupService.UnlinkReparsePoints(recycled);

        Assert.False(File.Exists(link));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }
}
