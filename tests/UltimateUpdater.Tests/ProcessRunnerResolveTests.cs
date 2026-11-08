using System.IO;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// Tool lookup for an elevated launch. PATH and PATHEXT of the elevated worker come from the user's own
/// registry hive, so neither may steer the lookup to a different binary.
/// </summary>
public class ProcessRunnerResolveTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("wu-resolve-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    [Fact]
    public void A_pathext_with_a_parent_step_does_not_turn_a_tool_name_into_another_binary()
    {
        // "<System32>\tool" + "\..\cmd.exe" normalises to "<System32>\cmd.exe".
        Assert.Null(ProcessRunner.Resolve("tool", System32, @"\..\cmd.exe", aliasDirectory: null));
        Assert.Null(ProcessRunner.ResolveTrusted("tool", System32, aliasDirectory: null));
    }

    [Fact]
    public void The_elevated_lookup_never_reads_pathext()
    {
        File.WriteAllBytes(Path.Combine(_directory, "tool.weird"), [1]);

        Assert.Null(ProcessRunner.Resolve("tool", _directory, ".EXE", aliasDirectory: null));
        Assert.Equal(Path.Combine(_directory, "tool.weird"), ProcessRunner.Resolve("tool", _directory, ".weird", null));
        Assert.Null(ProcessRunner.ResolveTrusted("tool", _directory, aliasDirectory: null));
    }

    [Fact]
    public void A_name_with_an_extension_resolves_as_written()
    {
        var tool = Path.Combine(_directory, "tool.exe");
        File.WriteAllBytes(tool, [1]);

        Assert.Equal(tool, ProcessRunner.ResolveTrusted("tool.exe", _directory, aliasDirectory: null));
        Assert.Equal(tool, ProcessRunner.ResolveTrusted("tool", _directory, aliasDirectory: null));
    }

    [Fact]
    public void A_name_carrying_a_separator_is_never_found()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "sub"));
        File.WriteAllBytes(Path.Combine(_directory, "sub", "tool.exe"), [1]);

        Assert.Null(ProcessRunner.ResolveTrusted(@"sub\tool.exe", _directory, aliasDirectory: null));
        Assert.Null(ProcessRunner.ResolveTrusted(@"..\tool.exe", Path.Combine(_directory, "sub"), aliasDirectory: null));
    }

    [Fact]
    public void A_relative_path_entry_is_not_searched()
    {
        Assert.Null(ProcessRunner.ResolveTrusted("notepad", ".", aliasDirectory: null));
        Assert.Null(ProcessRunner.ResolveTrusted("notepad", "System32", aliasDirectory: null));
    }

    [Fact]
    public void An_unresolved_relative_name_is_refused_when_elevated()
    {
        Assert.Throws<UntrustedToolException>(
            () => ProcessRunner.ResolveForPrivilege("wu-no-such-tool-" + Guid.NewGuid().ToString("N"), elevated: true));

        // Unelevated keeps the old fallback to the bare name.
        using var unelevated = ProcessRunner.ResolveForPrivilege("wu-no-such-tool-x", elevated: false);
        Assert.Equal("wu-no-such-tool-x", unelevated.Path);
    }
}
