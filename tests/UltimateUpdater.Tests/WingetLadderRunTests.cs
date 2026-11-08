using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// Runs the real install ladder against scripted winget answers: which rungs run for which exit codes.
/// </summary>
public class WingetLadderRunTests
{
    private const int NothingToDo = unchecked((int)0x8A15002B);
    private const int NoInstalledPackage = unchecked((int)0x8A150014);

    private static UpdateItem Item() => new()
    {
        Id = "Vendor.Runtime",
        SourceId = "winget",
        Category = UpdateCategory.Software,
        Name = "Runtime",
    };

    private static (WingetSource Source, List<string> Calls) Scripted(
        int plainUpgrade, int userUpgrade, int plainInstall, int pinnedInstall)
    {
        var calls = new List<string>();
        var source = new WingetSource(new LoggingService())
        {
            RunOverride = args =>
            {
                var line = string.Join(' ', args);
                calls.Add(line);
                if (args[0] == "show")
                    return Task.FromResult(new ProcessResult(0, "Version\n-------\n2.0.0\n1.0.0\n", ""));
                if (args.Contains("--version"))
                    return Task.FromResult(new ProcessResult(pinnedInstall, "", ""));
                if (args[0] == "install")
                    return Task.FromResult(new ProcessResult(plainInstall, "", ""));
                return Task.FromResult(new ProcessResult(args.Contains("--scope") ? userUpgrade : plainUpgrade, "", ""));
            },
        };
        return (source, calls);
    }

    private static Task Install(WingetSource source) =>
        source.InstallAsync(Item(), new Progress<InstallProgress>(), CancellationToken.None);

    [Fact]
    public async Task A_machine_wide_package_reaches_the_install_rung_and_then_the_pinned_attempt()
    {
        var (source, calls) = Scripted(NothingToDo, NoInstalledPackage, NothingToDo, 0);

        await Install(source);

        Assert.Equal(5, calls.Count);
        Assert.StartsWith("upgrade", calls[0]);
        Assert.Contains("--scope user", calls[1]);
        Assert.StartsWith("install", calls[2]);
        Assert.DoesNotContain("--version", calls[2]);
        Assert.StartsWith("show", calls[3]);
        Assert.Contains("--version 2.0.0", calls[4]);
    }

    [Fact]
    public async Task The_pinned_attempt_runs_when_only_an_earlier_rung_did_nothing()
    {
        var (source, calls) = Scripted(NothingToDo, NoInstalledPackage, NoInstalledPackage, 0);

        await Install(source);

        Assert.Contains(calls, c => c.Contains("--version 2.0.0"));
    }

    [Fact]
    public async Task An_install_rung_that_genuinely_failed_is_not_rerun_pinned()
    {
        var (source, calls) = Scripted(NothingToDo, NoInstalledPackage, 1603, 0);

        await Assert.ThrowsAnyAsync<Exception>(() => Install(source));

        Assert.Equal(3, calls.Count);
        Assert.DoesNotContain(calls, c => c.Contains("--version"));
    }

    [Fact]
    public async Task A_failing_first_rung_that_is_no_refusal_stops_at_once()
    {
        var (source, calls) = Scripted(1603, 0, 0, 0);

        await Assert.ThrowsAnyAsync<Exception>(() => Install(source));

        Assert.Single(calls);
    }

    [Fact]
    public async Task The_no_installed_package_code_from_the_plain_rung_does_not_escalate()
    {
        var (source, calls) = Scripted(NoInstalledPackage, 0, 0, 0);

        await Assert.ThrowsAnyAsync<Exception>(() => Install(source));

        Assert.Single(calls);
    }

    [Theory]
    [InlineData(NothingToDo, "upgrade", true)]
    [InlineData(NoInstalledPackage, "upgrade --scope user", true)]
    [InlineData(NoInstalledPackage, "upgrade", false)]
    [InlineData(NoInstalledPackage, "install", false)]
    [InlineData(1603, "upgrade --scope user", false)]
    public void Escalation_follows_the_rung_that_answered(int exitCode, string label, bool expected) =>
        Assert.Equal(expected, WingetSource.ShouldEscalate(exitCode, label, needsUserScopeRetry: false));
}
