using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// No live pyenv-win install exists on this dev box. `install -l`'s fixture is the literal example from
/// pyenv-win's own README; `global`'s output is transcribed straight from pyenv-win's own source
/// (`pyenv.vbs`'s <c>CommandGlobal</c>, read on GitHub 2026-08-01) rather than assumed.
/// </summary>
public class PyenvWinParsersTests
{
    private const string RealInstallListFixture = """
        3.8.0-win32
        3.8.0
        3.8.1rc1-win32
        3.8.1rc1
        3.8.1-win32
        3.8.1
        3.8.2-win32
        3.8.2
        3.9.0-win32
        3.9.0
        """;

    [Fact]
    public void Only_bare_stable_version_lines_are_kept()
    {
        var versions = PyenvWinParsers.ParseInstallableVersions(RealInstallListFixture);

        Assert.Equal(["3.8.0", "3.8.1", "3.8.2", "3.9.0"], versions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_install_list_yields_nothing(string? stdout) =>
        Assert.Empty(PyenvWinParsers.ParseInstallableVersions(stdout));

    [Fact]
    public void A_single_global_version_line_is_read() =>
        Assert.Equal("3.9.0", PyenvWinParsers.ParseGlobalVersion("3.9.0\n"));

    [Fact]
    public void No_global_version_configured_is_never_guessed() =>
        Assert.Null(PyenvWinParsers.ParseGlobalVersion("no global version configured\n"));

    [Fact]
    public void More_than_one_global_pin_is_never_guessed() =>
        // pyenv.vbs's CommandGlobal can print multiple lines when several tools are pinned at once,
        // this app is only confident enough to compare a single, unambiguous global version.
        Assert.Null(PyenvWinParsers.ParseGlobalVersion("3.9.0\n3.10.2\n"));

    [Fact]
    public void The_highest_patch_in_the_same_minor_line_is_offered()
    {
        var installable = PyenvWinParsers.ParseInstallableVersions(RealInstallListFixture);

        Assert.Equal("3.8.2", PyenvWinParsers.FindSameLinePatchUpdate("3.8.0", installable));
    }

    [Fact]
    public void A_newer_minor_or_major_line_is_never_offered()
    {
        var installable = PyenvWinParsers.ParseInstallableVersions(RealInstallListFixture);

        // 3.9.0 exists in the fixture but is a different minor line from 3.8.x, never a "safe" bump.
        Assert.Null(PyenvWinParsers.FindSameLinePatchUpdate("3.9.0", installable));
    }

    [Fact]
    public void Already_on_the_newest_patch_offers_nothing()
    {
        var installable = PyenvWinParsers.ParseInstallableVersions(RealInstallListFixture);

        Assert.Null(PyenvWinParsers.FindSameLinePatchUpdate("3.8.2", installable));
    }
}
