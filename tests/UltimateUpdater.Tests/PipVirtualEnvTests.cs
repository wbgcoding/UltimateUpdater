using System.IO;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

/// <summary>pip inside a virtual environment refuses --user, so a permission failure there must be reported
/// as it is instead of being retried into a second, less useful error.</summary>
public class PipVirtualEnvTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wu-pyvenv-" + Guid.NewGuid());

    public PipVirtualEnvTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static readonly ProcessResult AccessDenied =
        new(1, "", "ERROR: Could not install packages due to an OSError: [WinError 5] Access is denied");

    private string Interpreter(string relative, string? configRelative = null)
    {
        var exe = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "x");
        if (configRelative is not null)
            File.WriteAllText(Path.Combine(_folder, configRelative), "home = C:/Python312");
        return exe;
    }

    private static UpdateItem Item(string python) => new()
    {
        Id = "requests",
        SourceId = "pip",
        Category = UpdateCategory.Software,
        Name = "requests",
        InstalledVersion = "1",
        AvailableVersion = "2",
        Meta = new Dictionary<string, string> { ["pythonPath"] = python, ["pipPackage"] = "requests" },
    };

    [Fact]
    public void A_pyvenv_cfg_beside_the_interpreter_marks_a_virtual_environment()
    {
        var exe = Interpreter(@"env\python.exe", configRelative: @"env\pyvenv.cfg");

        Assert.True(PipSource.IsVirtualEnv(exe));
    }

    [Fact]
    public void A_pyvenv_cfg_one_level_above_a_Scripts_folder_marks_a_virtual_environment()
    {
        var exe = Interpreter(@"env\Scripts\python.exe", configRelative: @"env\pyvenv.cfg");

        Assert.True(PipSource.IsVirtualEnv(exe));
    }

    [Fact]
    public void A_plain_interpreter_is_not_a_virtual_environment()
    {
        var exe = Interpreter(@"Python312\python.exe");

        Assert.False(PipSource.IsVirtualEnv(exe));
    }

    [Fact]
    public void A_permission_failure_in_a_venv_is_not_retried_with_user()
    {
        var venv = Interpreter(@"env\Scripts\python.exe", configRelative: @"env\pyvenv.cfg");
        var machine = Interpreter(@"Python312\python.exe");

        Assert.Null(PipSourceRetry(Item(venv)));
        Assert.NotNull(PipSourceRetry(Item(machine)));
    }

    private static IReadOnlyList<string>? PipSourceRetry(UpdateItem item) =>
        PipSource.RetryArgumentsFor(item, AccessDenied);

    [Fact]
    public void The_failure_after_a_retry_still_contains_the_first_attempts_output()
    {
        var first = new ProcessResult(1, "", "ERROR: Could not install packages due to an OSError: [WinError 5] Access is denied");
        var retry = new ProcessResult(1, "", "ERROR: Can not perform a '--user' install.");

        var detail = PipSource.FailureDetail(first, retry);

        Assert.Contains("[WinError 5] Access is denied", detail);
        Assert.Contains("Can not perform a '--user' install.", detail);
    }

    [Fact]
    public void Without_a_retry_the_failure_is_just_the_first_attempts_output()
    {
        var first = new ProcessResult(1, "", "boom");

        Assert.Equal("boom (exit code 1)", PipSource.FailureDetail(first, null));
    }
}
