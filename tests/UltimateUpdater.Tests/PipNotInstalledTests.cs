using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources.PackageManagers;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// An interpreter installed without pip is a tool that is absent, not a source that failed; a pip that
/// is on disk but fails to import stays an error so the repair path still gets to run.
/// </summary>
public class PipNotInstalledTests
{
    private static readonly PythonInterpreter Python = new(
        @"C:\Users\testuser\AppData\Local\Programs\Python\Python313\python.exe", new Version(3, 13), PerUser: true, Is64Bit: true);

    private const string NoPipOutput = @"C:\Users\testuser\AppData\Local\Programs\Python\Python313\python.exe: No module named pip";

    [Fact]
    public void No_pip_on_disk_and_no_module_output_is_an_absent_tool()
    {
        string? probed = null;
        var absent = PipSource.IsPipNotInstalled(Python, new ProcessResult(1, "", NoPipOutput), site =>
        {
            probed = site;
            return false;
        });

        Assert.True(absent);
        Assert.EndsWith(@"Python313\Lib\site-packages", probed);
    }

    [Fact]
    public void The_quoted_import_error_form_is_recognised_too()
    {
        Assert.True(PipSource.IsPipNotInstalled(
            Python, new ProcessResult(1, "", "ModuleNotFoundError: No module named 'pip'"), _ => false));
    }

    [Fact]
    public void A_pip_that_is_on_disk_but_fails_to_import_stays_an_error()
    {
        Assert.False(PipSource.IsPipNotInstalled(Python, new ProcessResult(1, "", NoPipOutput), _ => true));
    }

    [Fact]
    public void Another_failure_is_never_taken_for_a_missing_pip()
    {
        Assert.False(PipSource.IsPipNotInstalled(Python, new ProcessResult(1, "", "ERROR: network is unreachable"), _ => false));
        Assert.False(PipSource.IsPipNotInstalled(Python, new ProcessResult(0, "[]", ""), _ => false));
    }

    [Fact]
    public void Every_interpreter_without_pip_combines_to_an_empty_result()
    {
        IReadOnlyList<UpdateItem> empty = [];
        var combined = PipSource.CombineInterpreterResults([(empty, null), (empty, null)]);

        Assert.Empty(combined);
    }
}
