using System.IO;
using System.Text;

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
            "DOTNET_EnableEventPipe", "DOTNET_EventPipeConfig", "DOTNET_EventPipeOutputPath",
            "DOTNET_EventPipeOutputStreaming", "COMPlus_EnableEventPipe", "COMPlus_EventPipeConfig",
            "COMPlus_EventPipeOutputPath", "COMPlus_EventPipeOutputStreaming",
            "DOTNET_DbgEnableMiniDump", "DOTNET_DbgMiniDumpName", "DOTNET_CreateDumpDiagnostics",
            "DOTNET_CreateDumpVerboseDiagnostics", "DOTNET_CreateDumpLogToFile",
            "COMPlus_DbgEnableMiniDump", "COMPlus_DbgMiniDumpName", "COMPlus_CreateDumpDiagnostics",
            "COMPlus_CreateDumpVerboseDiagnostics", "COMPlus_CreateDumpLogToFile",
            "CORECLR_PROFILER_PATH_ARM32", "CORECLR_PROFILER_PATH_ARM64",
            "CORECLR_ENABLE_NOTIFICATION_PROFILERS", "CORECLR_NOTIFICATION_PROFILERS",
            "CORECLR_NOTIFICATION_PROFILERS_32", "CORECLR_NOTIFICATION_PROFILERS_64",
            "CORECLR_NOTIFICATION_PROFILERS_ARM32", "CORECLR_NOTIFICATION_PROFILERS_ARM64",
            "DOTNET_DiagnosticPorts", "COMPlus_DiagnosticPorts",
            "DOTNET_DefaultDiagnosticPortSuspend", "COMPlus_DefaultDiagnosticPortSuspend",
            "DOTNET_StressLog", "DOTNET_StressLogFilename", "COMPlus_StressLog", "COMPlus_StressLogFilename",
            "DOTNET_JitStdOutFile", "COMPlus_JitStdOutFile",
        ];

        Assert.Equal(expected.OrderBy(n => n), NativeLoadEnvironment.Names.OrderBy(n => n));
    }

    /// <summary>The runtime writes a trace or a dump to a path the user picked, as the administrator.</summary>
    [Theory]
    [InlineData("DOTNET_EventPipeOutputPath")]
    [InlineData("COMPlus_EventPipeOutputPath")]
    [InlineData("DOTNET_DbgMiniDumpName")]
    [InlineData("COMPlus_DbgMiniDumpName")]
    [InlineData("CORECLR_NOTIFICATION_PROFILERS")]
    [InlineData("DOTNET_DiagnosticPorts")]
    [InlineData("DOTNET_JitStdOutFile")]
    public void A_variable_that_names_a_runtime_output_file_is_scrubbed(string name)
    {
        var env = new Dictionary<string, string?> { [name] = @"C:\Windows\System32\planted.bin" };

        var removed = NativeLoadEnvironment.Scrub(n => env.GetValueOrDefault(n), n => env.Remove(n));

        Assert.Equal([name], removed);
        Assert.Empty(env);
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

    /// <summary>A runas child gets its block from HKCU\Environment, so a value planted there must be found
    /// before the start, whatever the case of its name.</summary>
    [Theory]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("coreclr_profiler_path")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("DOTNET_GCName")]
    [InlineData("CORECLR_SOMETHING_NEW")]
    [InlineData("COR_ENABLE_PROFILING")]
    public void A_planted_runtime_variable_in_the_user_registry_environment_is_found(string name)
    {
        var userValues = new Dictionary<string, string?>
        {
            [name] = "1",
            ["Path"] = @"C:\Tools",
            ["TEMP"] = @"%USERPROFILE%\AppData\Local\Temp",
        };

        var found = NativeLoadEnvironment.FindSetIn(userValues);

        Assert.Equal([name], found);
    }

    [Fact]
    public void An_ordinary_or_empty_user_registry_environment_is_clean()
    {
        var userValues = new Dictionary<string, string?>
        {
            ["Path"] = @"C:\Tools",
            ["DOTNET_ROOT"] = @"C:\Program Files\dotnet",
            ["CORECLR_ENABLE_PROFILING"] = "",
            ["DOTNET_STARTUP_HOOKS"] = null,
        };

        Assert.Empty(NativeLoadEnvironment.FindSetIn(userValues));
    }

    [Fact]
    public void Both_user_registry_environment_keys_are_checked()
    {
        Assert.Equal(["Environment", "Volatile Environment"], NativeLoadEnvironment.UserEnvironmentKeys);
    }

    /// <summary>Builds a scratch tree that stands in for HKCU and runs the registry check on it.</summary>
    private static IReadOnlyList<string> CheckScratchRegistry(Action<Microsoft.Win32.RegistryKey> plant)
    {
        var relative = @"Software\WuNativeLoad" + Guid.NewGuid().ToString("N");
        try
        {
            using (var scratch = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(relative))
                plant(scratch);

            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(relative)!;
            return NativeLoadEnvironment.FindSetInUserRegistry(root);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(relative, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void A_clean_user_registry_environment_reports_nothing()
    {
        var found = CheckScratchRegistry(root =>
        {
            using var env = root.CreateSubKey("Environment");
            env.SetValue("Path", @"C:\Tools");
            using var session = root.CreateSubKey(@"Volatile Environment\1");
            session.SetValue("SESSIONNAME", "Console");
        });

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("Environment")]
    [InlineData("Volatile Environment")]
    public void A_profiler_planted_in_a_user_environment_key_is_found(string keyName)
    {
        var found = CheckScratchRegistry(root =>
        {
            using var key = root.CreateSubKey(keyName);
            key.SetValue("CORECLR_PROFILER_PATH", @"C:\planted\evil.dll");
        });

        Assert.Equal(["CORECLR_PROFILER_PATH"], found);
    }

    /// <summary>Windows also loads HKCU\Volatile Environment\&lt;SessionId&gt; into a new process's block, and
    /// the user can write it, so a value planted there reaches the runas worker like any other.</summary>
    [Theory]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("COR_PROFILER_PATH")]
    public void A_profiler_or_startup_hook_planted_in_a_session_subkey_is_found(string name)
    {
        var found = CheckScratchRegistry(root =>
        {
            using (var clean = root.CreateSubKey(@"Volatile Environment\1"))
                clean.SetValue("SESSIONNAME", "Console");
            using var session = root.CreateSubKey(@"Volatile Environment\2");
            session.SetValue(name, "planted");
        });

        Assert.Equal([name], found);
    }

    [Fact]
    public void The_registry_check_runs_before_the_elevated_start()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "UltimateUpdater", "Services", "Elevation", "ElevationService.cs"));
        var start = source.IndexOf("public static Process? StartWorker(string workerArgument", StringComparison.Ordinal);

        var check = source.IndexOf("FindSetInUserRegistry()", start, StringComparison.Ordinal);
        var launch = source.IndexOf("Process.Start(startInfo)", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && check > start && launch > check);
    }

    /// <summary>A value written while the consent prompt is open is only seen by the worker itself, after the
    /// runtime may have loaded it; the worker's refusal then says so in the log the app merges.</summary>
    [Fact]
    public void The_worker_logs_the_cause_when_it_refuses_with_exit_code_6()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "UltimateUpdater", "Services", "Elevation", "ElevatedWorker.cs"));
        var check = source.IndexOf("NativeLoadEnvironment.FindSet()", StringComparison.Ordinal);
        var exit = source.IndexOf("return 6;", check, StringComparison.Ordinal);

        Assert.True(check >= 0 && exit > check);
        var block = source[check..exit];
        Assert.Contains("may already be", block);
        Assert.Contains("exit code 6", block);
        Assert.Contains("logging.Flush();", block);
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

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, "the repository root is not above the test binaries any more");
        return directory!.FullName;
    }
}
