using System.Reflection;

namespace UltimateUpdater.Tests;

/// <summary>
/// The review harness opens every window with fake data and writes shots; it is a Debug tool and must not
/// exist in a shipped build. Three layers hold that: every harness file is wrapped in a Debug-only
/// conditional, the project drops the folder from Release compiles, and the startup hook sits inside a
/// Debug-only block. A Release assembly is also checked directly when the tests run against one.
/// </summary>
public class ReviewHarnessReleaseTests
{
    private const string ReviewNamespace = "UltimateUpdater.Review";

    [Fact]
    public void Every_harness_file_is_wrapped_in_a_debug_only_conditional()
    {
        var folder = Path.Combine(RepoRoot(), "src", "UltimateUpdater", "Review");
        var files = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var unguarded = files.Where(file => !IsWhollyDebugOnly(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(RepoRoot(), file)).ToList();
        Assert.True(unguarded.Count == 0, $"harness file not wrapped in #if DEBUG:{Environment.NewLine}{string.Join(Environment.NewLine, unguarded)}");
    }

    [Fact]
    public void The_project_leaves_the_harness_folder_out_of_a_release_compile()
    {
        var project = File.ReadAllText(Path.Combine(RepoRoot(), "src", "UltimateUpdater", "UltimateUpdater.csproj"));

        Assert.Matches(@"<ItemGroup Condition=""'\$\(Configuration\)' == 'Release'"">\s*<Compile Remove=""Review\\\*\*"" />", project);
    }

    [Fact]
    public void The_startup_hook_is_inside_a_debug_only_block()
    {
        var source = File.ReadAllLines(Path.Combine(RepoRoot(), "src", "UltimateUpdater", "App.xaml.cs"));
        var uses = new List<int>();
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i].Contains("Review.ReviewMode", StringComparison.Ordinal))
                uses.Add(i);
        }

        Assert.NotEmpty(uses);
        foreach (var line in uses)
            Assert.True(IsInsideDebugBlock(source, line), $"App.xaml.cs:{line + 1} reaches the harness outside #if DEBUG");
    }

    [Fact]
    public void A_release_assembly_carries_no_harness_type()
    {
        var assembly = typeof(App).Assembly;
        var optimized = assembly.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>() is { IsJITOptimizerDisabled: false };
        var harnessTypes = assembly.GetTypes().Where(type => type.Namespace?.StartsWith(ReviewNamespace, StringComparison.Ordinal) == true).ToList();

        if (optimized)
            Assert.Empty(harnessTypes);
        else
            Assert.NotEmpty(harnessTypes);
    }

    /// <summary>A detector that never fires would keep this guard green whatever the files hold.</summary>
    [Fact]
    public void The_guard_checks_flag_an_unwrapped_file_and_an_unguarded_hook()
    {
        Assert.True(IsWhollyDebugOnly("#if DEBUG\nclass A { }\n#endif\n"));
        Assert.False(IsWhollyDebugOnly("class A { }\n"));
        Assert.False(IsWhollyDebugOnly("#if DEBUG\nclass A { }\n#endif\nclass B { }\n"));

        string[] guarded = ["#if DEBUG", "Review.ReviewMode.Run();", "#endif"];
        string[] bare = ["Review.ReviewMode.Run();"];
        string[] closed = ["#if DEBUG", "x();", "#endif", "Review.ReviewMode.Run();"];
        Assert.True(IsInsideDebugBlock(guarded, 1));
        Assert.False(IsInsideDebugBlock(bare, 0));
        Assert.False(IsInsideDebugBlock(closed, 3));
    }

    private static bool IsWhollyDebugOnly(string source)
    {
        var lines = source.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        if (lines.Count < 2 || lines[0] != "#if DEBUG" || lines[^1] != "#endif")
            return false;

        // The opening conditional must not be closed before the end of the file.
        var depth = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith("#if", StringComparison.Ordinal))
                depth++;
            else if (lines[i].StartsWith("#endif", StringComparison.Ordinal))
                depth--;

            if (depth == 0 && i < lines.Count - 1)
                return false;
        }

        return depth == 0;
    }

    private static bool IsInsideDebugBlock(string[] lines, int index)
    {
        var stack = new Stack<bool>();
        for (var i = 0; i < index; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#if", StringComparison.Ordinal))
                stack.Push(line == "#if DEBUG");
            else if (line.StartsWith("#endif", StringComparison.Ordinal) && stack.Count > 0)
                stack.Pop();
        }

        return stack.Count > 0 && stack.Peek();
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, "the repository root is not above the test binaries any more");
        return directory!.FullName;
    }
}
