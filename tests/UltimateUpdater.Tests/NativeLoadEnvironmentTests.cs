using System.IO;
using System.Text;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

public class NativeLoadEnvironmentTests
{
    [Fact]
    public void Every_runtime_load_variable_is_listed()
    {
        string[] expected =
        [
            "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH", "CORECLR_PROFILER_PATH_32",
            "CORECLR_PROFILER_PATH_64", "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH",
            "DOTNET_GCName", "DOTNET_GCPath", "COMPlus_GCName", "COMPlus_GCPath", "DOTNET_STARTUP_HOOKS",
        ];

        Assert.Equal(expected.OrderBy(n => n), NativeLoadEnvironment.Names.OrderBy(n => n));
    }

    [Fact]
    public void FindSet_reports_only_variables_with_a_value()
    {
        var env = new Dictionary<string, string?>
        {
            ["COR_PROFILER"] = "{guid}",
            ["DOTNET_GCName"] = "",
            ["DOTNET_GCPath"] = "evil.dll",
        };

        var found = NativeLoadEnvironment.FindSet(name => env.GetValueOrDefault(name));

        Assert.Equal(["COR_PROFILER", "DOTNET_GCPath"], found.OrderBy(n => n));
    }

    [Fact]
    public void Scrub_removes_every_listed_variable_and_names_the_ones_that_were_set()
    {
        var env = new Dictionary<string, string?>
        {
            ["CORECLR_PROFILER_PATH"] = "evil.dll",
            ["DOTNET_STARTUP_HOOKS"] = "hook.dll",
            ["PATH"] = "x",
        };

        var removed = NativeLoadEnvironment.Scrub(name => env.GetValueOrDefault(name), name => env.Remove(name));

        Assert.Equal(["CORECLR_PROFILER_PATH", "DOTNET_STARTUP_HOOKS"], removed.OrderBy(n => n));
        Assert.Equal(["PATH"], env.Keys);
    }

    [Fact]
    public void Scrub_on_the_real_environment_leaves_nothing_set()
    {
        const string name = "COR_PROFILER_PATH";
        var before = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, @"C:\planted\evil.dll");
        try
        {
            NativeLoadEnvironment.Scrub();

            Assert.Empty(NativeLoadEnvironment.FindSet());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, before);
        }
    }

    /// <summary>An app started with "Run as administrator" is not the worker, yet its children are scrubbed
    /// too; the untrusted-binary gate and the PATH rewrite stay with the worker.</summary>
    [Fact]
    public void A_scrub_without_worker_mode_removes_the_variables_but_keeps_the_callers_path()
    {
        const string inherited = "DOTNET_STARTUP_HOOKS";
        var before = Environment.GetEnvironmentVariable(inherited);
        Environment.SetEnvironmentVariable(inherited, @"C:\planted\hook.dll");
        try
        {
            using var tool = new ResolvedExecutable(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                new FileStream(Path.Combine(Environment.SystemDirectory, "cmd.exe"), FileMode.Open, FileAccess.Read, FileShare.Read));

            var info = ProcessRunner.BuildStartInfo(
                tool, ["/c", "echo"], null, Encoding.UTF8, elevated: false, scrubEnvironment: true);

            Assert.False(info.Environment.ContainsKey(inherited));
            Assert.Equal(
                Environment.GetEnvironmentVariable("PATH"), info.Environment["PATH"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(inherited, before);
        }
    }
}
