using System.Diagnostics;

namespace UltimateUpdater.Tests;

/// <summary>
/// The junction helper replaced sixteen copies of <c>cmd /c mklink /J</c>. These pin that what it writes is
/// what mklink writes, so every test that builds a link with it still exercises the real thing.
/// </summary>
public sealed class TestLinksTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("uu-links-").FullName;

    public void Dispose()
    {
        foreach (var link in Directory.GetDirectories(_root))
        {
            if (new DirectoryInfo(link).LinkTarget is not null)
                Directory.Delete(link);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_junction_resolves_to_the_target_and_reads_its_content()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.txt"), "hello");
        var link = Path.Combine(_root, "link");

        TestLinks.CreateJunction(link, target);

        Assert.Equal(target, new DirectoryInfo(link).LinkTarget);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(link, "a.txt")));
    }

    [Trait("Layer", "Integration")]
    [Fact]
    public void A_junction_is_the_same_record_mklink_writes()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var ours = Path.Combine(_root, "ours");
        var theirs = Path.Combine(_root, "theirs");

        TestLinks.CreateJunction(ours, target);
        RunMklink($"/J \"{theirs}\" \"{target}\"");

        var ourInfo = new DirectoryInfo(ours);
        var theirInfo = new DirectoryInfo(theirs);
        Assert.Equal(theirInfo.LinkTarget, ourInfo.LinkTarget);
        Assert.Equal(theirInfo.Attributes, ourInfo.Attributes);
        Assert.Equal(theirInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName,
            ourInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName);
    }

    [Fact]
    public void A_junction_to_a_missing_target_is_created_like_mklink_does()
    {
        var link = Path.Combine(_root, "dangling");

        TestLinks.CreateJunction(link, Path.Combine(_root, "not-there"));

        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
        Assert.False(Directory.Exists(Path.Combine(link, "x")));
    }

    [Fact]
    public void A_junction_over_an_existing_path_is_refused_and_leaves_it_alone()
    {
        var existing = Path.Combine(_root, "existing");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "x");

        Assert.Throws<IOException>(() => TestLinks.CreateJunction(existing, _root));

        Assert.Null(new DirectoryInfo(existing).LinkTarget);
        Assert.True(File.Exists(Path.Combine(existing, "keep.txt")));
    }

    [Trait("Layer", "Integration")]
    [Fact]
    public void A_hard_link_shares_the_content_of_the_original()
    {
        var original = Path.Combine(_root, "original.txt");
        File.WriteAllText(original, "one");
        var link = Path.Combine(_root, "second.txt");

        TestLinks.CreateHardLink(link, original);
        File.WriteAllText(original, "changed");

        Assert.Equal("changed", File.ReadAllText(link));
    }

    private static void RunMklink(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c mklink " + arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "mklink did not finish");
        Assert.Equal(0, process.ExitCode);
    }
}
