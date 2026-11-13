using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// Launch is the single ShellExecute call of the app. A target it refuses must come back as false with a
/// warning and must never reach Process.Start: these inputs would otherwise open a program or a protocol
/// handler on a click.
/// </summary>
public class UriLauncherRefusalTests
{
    [Theory]
    [InlineData("notepad.exe")]                         // bare name: ShellExecute would search the working directory
    [InlineData("calc")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")] // scheme not on the allow list
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("javascript:alert(1)")]
    [InlineData("..\\relative\\tool.exe")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_refused_target_returns_false_and_logs_a_warning(string target)
    {
        var lines = new List<string>();

        var launched = UriLauncher.Launch(target, new LoggingService(lines.Add));

        Assert.False(launched);
        Assert.Contains(lines, l => l.Contains("Refusing to open"));
    }

    [Fact]
    public void A_missing_absolute_path_is_refused_not_handed_to_the_shell()
    {
        var missing = Path.Combine(Path.GetTempPath(), "uu-no-such-" + Guid.NewGuid().ToString("N") + ".exe");
        var lines = new List<string>();

        Assert.False(UriLauncher.Launch(missing, new LoggingService(lines.Add)));
        Assert.Contains(lines, l => l.Contains("Refusing to open"));
    }

    [Theory]
    [InlineData("https://example.com/x")]
    [InlineData("HTTP://example.com")]
    [InlineData("steam://open/downloads")]
    [InlineData("  battlenet://  ")]
    public void The_allowed_launcher_schemes_are_accepted_ignoring_case_and_padding(string target) =>
        Assert.True(UriLauncher.IsAllowedUri(target));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("mailto:a@b.c")]
    public void Anything_else_is_not_an_allowed_uri(string? target) =>
        Assert.False(UriLauncher.IsAllowedUri(target));
}
