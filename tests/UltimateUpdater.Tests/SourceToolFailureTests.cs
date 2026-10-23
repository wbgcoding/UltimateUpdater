using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

/// <summary>
/// A tool that ran and failed (timeout, error exit, nothing readable) is a failed source, never a machine
/// with nothing to update. A tool that ran and legitimately has nothing to report is zero rows.
/// </summary>
public class SourceToolFailureTests
{
    private static ProcessResult Ok(string stdout) => new(0, stdout, string.Empty);

    private static ProcessResult Failed(int exitCode = 1) => new(exitCode, string.Empty, "boom");

    private static ProcessResult TimedOut() => new(-1, string.Empty, string.Empty) { TimedOut = true };

    // ---- Flutter

    private const string FlutterVersionJson = """{"frameworkVersion":"3.44.1","channel":"stable"}""";

    [Fact]
    public void Flutter_failed_runs_throw()
    {
        Assert.Throws<TimeoutException>(() => FlutterSource.ReadInstalled(TimedOut()));
        Assert.Throws<InvalidOperationException>(() => FlutterSource.ReadInstalled(Failed()));
        Assert.Throws<InvalidOperationException>(() => FlutterSource.ReadInstalled(Ok("not json")));
    }

    [Fact]
    public void Flutter_a_readable_version_is_returned()
    {
        var installed = FlutterSource.ReadInstalled(Ok(FlutterVersionJson));

        Assert.Equal("3.44.1", installed.Version);
        Assert.Equal("stable", installed.Channel);
    }

    // ---- Volta

    [Fact]
    public void Volta_failed_runs_throw()
    {
        Assert.Throws<TimeoutException>(() => VoltaSource.ReadDefaultTools(TimedOut()));
        Assert.Throws<InvalidOperationException>(() => VoltaSource.ReadDefaultTools(Failed()));
    }

    [Fact]
    public void Volta_a_list_without_any_default_tool_is_zero_rows()
    {
        Assert.Empty(VoltaSource.ReadDefaultTools(Ok(string.Empty)));
        Assert.Single(VoltaSource.ReadDefaultTools(Ok("runtime node@18.19.0 (default)\n")));
    }

    // ---- pyenv-win

    [Fact]
    public void Pyenv_a_timed_out_global_read_throws()
    {
        Assert.Throws<TimeoutException>(() => PyenvWinSource.ReadGlobalVersion(TimedOut()));
    }

    [Fact]
    public void Pyenv_no_global_version_is_a_valid_state_not_a_failure()
    {
        Assert.Null(PyenvWinSource.ReadGlobalVersion(Failed()));
        Assert.Null(PyenvWinSource.ReadGlobalVersion(Ok("no global version configured\n")));
        Assert.Equal("3.9.0", PyenvWinSource.ReadGlobalVersion(Ok("3.9.0\n")));
    }

    [Fact]
    public void Pyenv_a_failed_install_list_throws()
    {
        Assert.Throws<TimeoutException>(() => PyenvWinSource.ReadInstallableVersions(TimedOut()));
        Assert.Throws<InvalidOperationException>(() => PyenvWinSource.ReadInstallableVersions(Failed()));
    }

    [Fact]
    public void Pyenv_an_empty_install_list_is_zero_rows() =>
        Assert.Empty(PyenvWinSource.ReadInstallableVersions(Ok(string.Empty)));

    // ---- self-updating tools

    [Fact]
    public void A_self_updating_tool_without_a_readable_version_throws()
    {
        var source = new BunSource(new LoggingService(), new HttpClient());

        Assert.Throws<InvalidOperationException>(() => source.ReadInstalledVersion(Ok("no number here")));
    }

    [Fact]
    public void A_self_updating_tool_reads_its_version_from_either_stream()
    {
        var source = new BunSource(new LoggingService(), new HttpClient());

        Assert.Equal("1.3.14", source.ReadInstalledVersion(Ok("1.3.14\n")));
        Assert.Equal("1.3.14", source.ReadInstalledVersion(new ProcessResult(0, string.Empty, "bun 1.3.14")));
    }

    // ---- pip

    private static UpdateItem Row(string id) => new()
    {
        Id = id, SourceId = "pip", Category = UpdateCategory.DeveloperTools, Name = id,
    };

    [Fact]
    public void Pip_when_every_interpreter_failed_the_first_failure_is_rethrown()
    {
        var first = new InvalidOperationException("first");
        var second = new TimeoutException("second");

        var thrown = Assert.Throws<InvalidOperationException>(() => PipSource.CombineInterpreterResults(
            [(null, first), (null, second)]));

        Assert.Same(first, thrown);
    }

    [Fact]
    public void Pip_a_partial_failure_keeps_the_other_interpreters_rows()
    {
        var rows = PipSource.CombineInterpreterResults(
            [(null, new InvalidOperationException("broken")), ([Row("requests")], null)]);

        Assert.Equal("requests", Assert.Single(rows).Id);
    }

    [Fact]
    public void Pip_interpreters_that_all_answered_with_nothing_are_zero_rows() =>
        Assert.Empty(PipSource.CombineInterpreterResults([([], null), ([], null)]));

    [Fact]
    public void Pip_no_interpreters_at_all_is_zero_rows() =>
        Assert.Empty(PipSource.CombineInterpreterResults([]));
}
