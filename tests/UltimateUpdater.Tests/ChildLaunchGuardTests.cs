using System.IO;
using System.Text.RegularExpressions;

namespace UltimateUpdater.Tests;

/// <summary>
/// Every launch that inherits handles must hold <c>ProcessRunner.ChildStartLock</c>, or a parallel launch
/// can inherit the pipe ends of a suspended one. Only the two files that take the lock may start such a
/// child, so a third launcher cannot appear unnoticed.
/// </summary>
public class ChildLaunchGuardTests
{
    private static readonly string[] Launchers = ["ProcessRunner.cs", "SuspendedChildProcess.cs"];

    [Fact]
    public void No_other_source_file_starts_a_handle_inheriting_child()
    {
        var root = RepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => !Launchers.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
            .SelectMany(file => FindUnlockedLaunches(Path.GetRelativePath(root, file), File.ReadAllText(file)))
            .ToList();

        Assert.True(offenders.Count == 0, "starts a child outside the shared launch lock: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("Process.Start(\"x.exe\");", 1)]
    [InlineData("var p = new ProcessStartInfo { FileName = \"x\" };\nProcess.Start(p);", 1)]
    [InlineData("Process.Start(path, \"-a\");", 1)]
    [InlineData("new Process { StartInfo = s }.Start();", 1)]
    [InlineData("var s = new ProcessStartInfo { FileName = f, UseShellExecute = false };", 1)]
    [InlineData("// Process.Start(\"x\")", 0)]
    [InlineData("var s = new ProcessStartInfo { FileName = f, UseShellExecute = true }; Process.Start(s);", 0)]
    public void The_launch_scan_flags_every_start_that_is_not_a_shell_launch(string source, int expectedOffenders)
    {
        Assert.Equal(expectedOffenders, FindUnlockedLaunches("x.cs", source).Count);
    }

    /// <summary>
    /// "file:line" for every launch in <paramref name="text"/> that could inherit handles outside the lock.
    /// UseShellExecute defaults to false, so a start is only safe when it visibly sets it to true.
    /// </summary>
    internal static IReadOnlyList<string> FindUnlockedLaunches(string fileName, string text)
    {
        var lines = text.Split('\n');
        var offenders = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                continue;

            var hit = false;

            foreach (Match _ in StartInfoCreation.Matches(line))
            {
                var window = string.Join('\n', lines.Skip(i).Take(16));
                if (!ShellExecuteTrue.IsMatch(window))
                    hit = true;
            }

            hit |= PlainProcess.IsMatch(line) || ShellExecuteFalse.IsMatch(line) || CreateProcessCall.IsMatch(line);

            foreach (Match start in ProcessStart.Matches(line))
            {
                var rest = line[(start.Index + start.Length)..].Trim();
                if (rest.StartsWith("new ", StringComparison.Ordinal))
                    continue;

                var argument = Regex.Match(rest, @"^(\w+)\)");
                var fromStartInfo = argument.Success
                    && Regex.IsMatch(text, $@"\b{argument.Groups[1].Value}\s*=\s*new\s+(System\.Diagnostics\.)?ProcessStartInfo\b");
                if (!fromStartInfo)
                    hit = true;
            }

            if (hit)
                offenders.Add($"{fileName}:{i + 1}");
        }

        return offenders;
    }

    private static readonly Regex StartInfoCreation = new(@"new\s+(System\.Diagnostics\.)?ProcessStartInfo\b");
    private static readonly Regex ShellExecuteTrue = new(@"UseShellExecute\s*=\s*true");
    private static readonly Regex ShellExecuteFalse = new(@"UseShellExecute\s*=\s*false");
    private static readonly Regex PlainProcess = new(@"\bnew\s+(System\.Diagnostics\.)?Process\s*[({]");
    private static readonly Regex ProcessStart = new(@"Process\.Start\(");
    private static readonly Regex CreateProcessCall = new(@"\bCreateProcess\w*\s*\(");

    [Fact]
    public void Both_launchers_take_the_shared_lock()
    {
        var services = Path.Combine(RepoRoot(), "src", "UltimateUpdater", "Services");

        foreach (var name in Launchers)
            Assert.Contains("ChildStartLock", File.ReadAllText(Path.Combine(services, name)));
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
