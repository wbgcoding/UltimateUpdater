using System.Diagnostics;
using System.IO;
using System.Text;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// What an elevated child inherits: its working directory and PATH are the next things a phantom or
/// delay-loaded DLL lookup (and a .cmd tool's sub-commands) would reach after the tool's own directory, and
/// both normally come from the unelevated user.
/// </summary>
public class ElevatedProcessEnvironmentTests
{
    private static readonly string System32 = Environment.SystemDirectory;
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static ProcessStartInfo Build(
        ResolvedExecutable executable, bool elevated, string? machinePath = null,
        Func<string, bool>? trusted = null, IReadOnlyDictionary<string, string>? environment = null) =>
        ProcessRunner.BuildStartInfo(
            executable, ["/c", "echo"], environment, Encoding.UTF8, elevated, () => machinePath, trusted ?? (_ => true));

    private static ResolvedExecutable Pinned(string path) =>
        new(path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));

    [Fact]
    public void An_elevated_child_never_inherits_a_runtime_code_loading_variable()
    {
        const string inherited = "DOTNET_STARTUP_HOOKS_WU_TEST";
        Environment.SetEnvironmentVariable(inherited, @"C:\planted\hook.dll");
        try
        {
            using var tool = Pinned(Path.Combine(System32, "cmd.exe"));

            var elevated = Build(tool, elevated: true, environment: new Dictionary<string, string> { ["DOTNET_NOLOGO"] = "1" });
            var unelevated = Build(tool, elevated: false);

            Assert.False(elevated.Environment.ContainsKey(inherited));
            Assert.Equal("1", elevated.Environment["DOTNET_NOLOGO"]);
            Assert.True(unelevated.Environment.ContainsKey(inherited));
        }
        finally
        {
            Environment.SetEnvironmentVariable(inherited, null);
        }
    }

    [Theory]
    [InlineData("NODE_OPTIONS")]
    [InlineData("PYTHONPATH")]
    [InlineData("COR_PROFILER_PATH")]
    [InlineData("CORECLR_PROFILER")]
    [InlineData("JAVA_TOOL_OPTIONS")]
    public void Code_loading_variables_are_removed(string name)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [name] = "x", ["TEMP"] = "y" };

        ProcessRunner.RemoveCodeLoadingVariables(environment);

        Assert.False(environment.ContainsKey(name));
        Assert.True(environment.ContainsKey("TEMP"));
    }

    [Fact]
    public void An_elevated_child_starts_in_the_verified_tools_own_directory()
    {
        using var tool = Pinned(Path.Combine(System32, "cmd.exe"));

        var info = Build(tool, elevated: true);

        Assert.Equal(System32, info.WorkingDirectory, ignoreCase: true);
    }

    [Fact]
    public void An_elevated_child_without_a_pinned_tool_starts_in_system32()
    {
        // the app-execution alias cannot be opened, so nothing was pinned and its directory is user-writable
        var alias = new ResolvedExecutable(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe"),
            null);

        var info = Build(alias, elevated: true);

        Assert.Equal(System32, info.WorkingDirectory, ignoreCase: true);
    }

    [Fact]
    public void An_elevated_childs_path_is_the_machine_path_without_untrusted_directories()
    {
        using var tool = Pinned(Path.Combine(System32, "cmd.exe"));
        var userDirectory = Path.Combine(Path.GetTempPath(), "wu-userbin");
        var userPath = userDirectory + ";" + System32;

        var info = Build(
            tool, elevated: true,
            machinePath: $@"%SystemRoot%\system32;C:\Planted;%SystemRoot%;{Windows}\System32\Wbem;;",
            trusted: directory => !directory.Contains("Planted", StringComparison.OrdinalIgnoreCase),
            environment: new Dictionary<string, string> { ["PATH"] = userPath });

        var entries = info.Environment["PATH"]!.Split(';');

        Assert.Equal([System32, Windows, Path.Combine(Windows, "System32", "Wbem")], entries, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(entries, e => e.Contains("Planted", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.Equals(userDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_elevated_childs_path_never_falls_back_to_the_users_path_when_the_machine_path_is_unreadable()
    {
        using var tool = Pinned(Path.Combine(System32, "cmd.exe"));

        var info = Build(tool, elevated: true, machinePath: null);

        Assert.Equal(System32, info.Environment["PATH"], ignoreCase: true);
    }

    [Fact]
    public void An_unelevated_launch_keeps_the_inherited_directory_and_path()
    {
        using var tool = Pinned(Path.Combine(System32, "cmd.exe"));

        var info = Build(tool, elevated: false, machinePath: @"C:\Planted");

        Assert.Equal(string.Empty, info.WorkingDirectory);
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), info.Environment["PATH"]);
    }
}
