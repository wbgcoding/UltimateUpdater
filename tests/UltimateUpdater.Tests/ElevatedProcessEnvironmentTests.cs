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
        Func<string, bool>? trusted = null, IReadOnlyDictionary<string, string>? environment = null,
        string? machineChocolatey = null, string? machineTools = null) =>
        ProcessRunner.BuildStartInfo(
            executable, ["/c", "echo"], environment, Encoding.UTF8, elevated, () => machinePath, trusted ?? (_ => true),
            machineVariable: name => name switch
            {
                "ChocolateyInstall" => machineChocolatey,
                "ChocolateyToolsLocation" => machineTools,
                _ => null,
            });

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

    [Fact]
    public void An_elevated_child_never_inherits_the_users_chocolatey_install_root()
    {
        Environment.SetEnvironmentVariable("ChocolateyInstall", @"C:\Planted\choco");
        Environment.SetEnvironmentVariable("ChocolateyToolsLocation", @"C:\Planted\tools");
        try
        {
            using var tool = Pinned(Path.Combine(System32, "cmd.exe"));

            var elevated = Build(tool, elevated: true);
            var unelevated = Build(tool, elevated: false);

            Assert.False(elevated.Environment.ContainsKey("ChocolateyInstall"));
            Assert.False(elevated.Environment.ContainsKey("ChocolateyToolsLocation"));
            Assert.Equal(@"C:\Planted\choco", unelevated.Environment["ChocolateyInstall"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ChocolateyInstall", null);
            Environment.SetEnvironmentVariable("ChocolateyToolsLocation", null);
        }
    }

    [Fact]
    public void A_caller_cannot_hand_an_elevated_child_a_chocolatey_install_root()
    {
        using var tool = Pinned(Path.Combine(System32, "cmd.exe"));

        var info = Build(tool, elevated: true, environment: new Dictionary<string, string>
        {
            ["ChocolateyInstall"] = @"C:\Planted\choco",
            ["chocolateytoolslocation"] = @"C:\Planted\tools",
        });

        Assert.False(info.Environment.ContainsKey("ChocolateyInstall"));
        Assert.False(info.Environment.ContainsKey("ChocolateyToolsLocation"));
    }

    [Fact]
    public void An_elevated_child_gets_the_machine_chocolatey_root_when_every_directory_on_it_is_trusted()
    {
        using var tool = Pinned(Path.Combine(System32, "cmd.exe"));
        Environment.SetEnvironmentVariable("ChocolateyInstall", @"C:\Planted\choco");
        try
        {
            var info = Build(tool, elevated: true, machineChocolatey: @"C:\ProgramData\chocolatey\");

            Assert.Equal(@"C:\ProgramData\chocolatey", info.Environment["ChocolateyInstall"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ChocolateyInstall", null);
        }
    }

    [Theory]
    [InlineData(@"C:\ProgramData\chocolatey", false)]
    [InlineData(@"%USERPROFILE%\choco", true)]
    [InlineData(@"choco", true)]
    [InlineData(null, true)]
    public void An_elevated_child_gets_no_chocolatey_root_the_machine_cannot_vouch_for(string? machineValue, bool trusted)
    {
        using var tool = Pinned(Path.Combine(System32, "cmd.exe"));

        var info = Build(tool, elevated: true, trusted: _ => trusted, machineChocolatey: machineValue);

        Assert.False(info.Environment.ContainsKey("ChocolateyInstall"));
    }

    [Fact]
    public void An_elevated_child_never_inherits_any_chocolatey_variable()
    {
        Environment.SetEnvironmentVariable("ChocolateyBinRoot", @"C:\Planted\bin");
        Environment.SetEnvironmentVariable("chocolateylastpathupdate", "1");
        try
        {
            using var tool = Pinned(Path.Combine(System32, "cmd.exe"));

            var elevated = Build(tool, elevated: true, machineChocolatey: @"C:\ProgramData\chocolatey");

            Assert.DoesNotContain(elevated.Environment.Keys, key => key.Equals("ChocolateyBinRoot", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(elevated.Environment.Keys, key => key.Equals("ChocolateyLastPathUpdate", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(@"C:\ProgramData\chocolatey", elevated.Environment["ChocolateyInstall"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ChocolateyBinRoot", null);
            Environment.SetEnvironmentVariable("chocolateylastpathupdate", null);
        }
    }

    [Fact]
    public void An_elevated_child_keeps_the_machine_chocolatey_tools_folder_under_the_same_trust_rule()
    {
        using var tool = Pinned(Path.Combine(System32, "cmd.exe"));
        Environment.SetEnvironmentVariable("ChocolateyToolsLocation", @"C:\Planted\tools");
        try
        {
            var trusted = Build(tool, elevated: true, machineTools: @"D:\Tools\");
            var untrusted = Build(tool, elevated: true, trusted: _ => false, machineTools: @"D:\Tools");
            var withVariable = Build(tool, elevated: true, machineTools: @"%SystemDrive%\tools");

            Assert.Equal(@"D:\Tools", trusted.Environment["ChocolateyToolsLocation"]);
            Assert.False(untrusted.Environment.ContainsKey("ChocolateyToolsLocation"));
            Assert.False(withVariable.Environment.ContainsKey("ChocolateyToolsLocation"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ChocolateyToolsLocation", null);
        }
    }
}
