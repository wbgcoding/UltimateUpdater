using System.Diagnostics;
using System.IO;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

public class DirectorySizeWalkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-sizewalk-" + Guid.NewGuid());

    public DirectorySizeWalkerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var link in Directory.EnumerateDirectories(_root, "*", SearchOption.TopDirectoryOnly)
                     .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0))
        {
            try { Directory.Delete(link); } catch { }
        }

        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void WriteFile(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
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
    public void A_junction_back_to_the_parent_returns_the_plain_total_and_terminates()
    {
        WriteFile(Path.Combine(_root, "a.bin"), 10);
        WriteFile(Path.Combine(_root, "sub", "b.bin"), 5);
        CreateJunction(Path.Combine(_root, "loop"), _root);
        CreateJunction(Path.Combine(_root, "sub", "loop2"), _root);

        var total = DirectorySizeWalker.Measure(_root, DirectorySizeWalker.DefaultMaxDepth, 20000);

        Assert.Equal(15, total);
    }

    [Fact]
    public void An_empty_directory_chain_deeper_than_the_bound_is_not_followed_further()
    {
        var path = _root;
        for (var i = 0; i < 6; i++)
            path = Path.Combine(path, "d" + i);
        WriteFile(Path.Combine(path, "deep.bin"), 7);
        WriteFile(Path.Combine(_root, "d0", "shallow.bin"), 3);

        Assert.Equal(3, DirectorySizeWalker.Measure(_root, maxDepth: 2, maxFiles: 100));
        Assert.Equal(10, DirectorySizeWalker.Measure(_root, maxDepth: 6, maxFiles: 100));
    }

    [Fact]
    public void The_file_cap_ends_the_walk()
    {
        for (var i = 0; i < 10; i++)
            WriteFile(Path.Combine(_root, $"f{i}.bin"), 1);

        Assert.Equal(4, DirectorySizeWalker.Measure(_root, maxDepth: 3, maxFiles: 4));
    }

    [Fact]
    public void A_cancelled_token_stops_the_walk()
    {
        WriteFile(Path.Combine(_root, "a.bin"), 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => DirectorySizeWalker.Measure(_root, 3, 100, cts.Token));
    }

    [Fact]
    public void A_missing_root_measures_zero()
    {
        Assert.Equal(0, DirectorySizeWalker.Measure(Path.Combine(_root, "nope"), 3, 100));
    }
}
