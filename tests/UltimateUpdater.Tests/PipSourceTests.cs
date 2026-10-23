using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

/// <summary>
/// pip is scanned via whichever "pip" resolves on PATH. The self-upgrade/repair paths must derive the
/// interpreter from pip's own resolved location (`OwnPython`) rather than resolving "python"
/// independently, with more than one Python installation on the machine, an independent lookup could
/// land on a completely different interpreter, silently "fixing" the wrong one while reporting success.
/// Mutates the process PATH for the duration of each test, safe because the suite runs serially
/// (AssemblyInfo.cs), and always restores it.
/// </summary>
public class PipSourceTests : IDisposable
{
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
    private readonly string _tempDir = Directory.CreateTempSubdirectory("wu-pip-test-").FullName;

    /// <summary>Python prints to a pipe in the ANSI code page unless told otherwise, and one emoji in a
    /// package description then ends 'pip inspect' with UnicodeEncodeError. Every pip call carries UTF-8.</summary>
    [Fact]
    public void Every_pip_process_call_runs_with_utf8_output()
    {
        Assert.Equal("utf-8", PipSource.Utf8Environment["PYTHONIOENCODING"]);
        Assert.Equal("1", PipSource.Utf8Environment["PYTHONUTF8"]);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var source = File.ReadAllText(Path.Combine(
            directory!.FullName, "src", "UltimateUpdater", "Services", "Sources", "PackageManagers", "PipSource.cs"));
        var calls = System.Text.RegularExpressions.Regex.Matches(source, @"ProcessRunner\.Run(Streaming)?Async\((?<args>[^;]*);");

        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.Contains("Utf8Environment", call.Groups["args"].Value));
    }

    [Fact]
    public void Finds_python_next_to_pip_in_the_same_directory_a_venv_layout()
    {
        var scriptsDir = Path.Combine(_tempDir, "venv-scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllBytes(Path.Combine(scriptsDir, "pip.exe"), [0]);
        File.WriteAllBytes(Path.Combine(scriptsDir, "python.exe"), [0]);

        Environment.SetEnvironmentVariable("PATH", scriptsDir);

        Assert.Equal(Path.Combine(scriptsDir, "python.exe"), PipSource.OwnPython());
    }

    [Fact]
    public void Finds_python_one_directory_up_a_standard_CPython_layout()
    {
        var installRoot = Path.Combine(_tempDir, "Python312");
        var scriptsDir = Path.Combine(installRoot, "Scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllBytes(Path.Combine(scriptsDir, "pip.exe"), [0]);
        File.WriteAllBytes(Path.Combine(installRoot, "python.exe"), [0]);

        Environment.SetEnvironmentVariable("PATH", scriptsDir);

        Assert.Equal(Path.Combine(installRoot, "python.exe"), PipSource.OwnPython());
    }

    [Fact]
    public void Falls_back_to_the_bare_name_when_nothing_can_be_resolved()
    {
        Environment.SetEnvironmentVariable("PATH", _tempDir);

        Assert.Equal("python", PipSource.OwnPython());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
