using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

/// <summary>
/// What the install path hands to the tools. An update applies the update and does nothing else, in
/// particular it never leaves the updated application open in front of the user, who asked for an
/// update and not for the program to start.
/// </summary>
public class InstallCommandTests
{
    [Fact]
    public void Winget_installs_silently_and_asks_nothing()
    {
        var arguments = WingetSource.InstallArguments("Mozilla.Firefox");

        // --silent hands the vendor's installer its own quiet switch, which is what suppresses the
        // finish page and its "launch now" box
        Assert.Contains("--silent", arguments);
        Assert.Contains("--disable-interactivity", arguments);
        Assert.Contains("--exact", arguments);
        Assert.Contains("Mozilla.Firefox", arguments);

        // each of these would put a window in front of the user mid-batch
        Assert.DoesNotContain("--interactive", arguments);
        Assert.DoesNotContain("--wait", arguments);
        Assert.DoesNotContain("--open-logs", arguments);
    }

    [Fact]
    public void Chocolatey_installs_silently_and_asks_nothing()
    {
        var arguments = ChocolateySource.UpgradeArguments("firefox");

        Assert.Contains("-y", arguments);
        Assert.Contains("--accept-license", arguments);
        Assert.Contains("firefox", arguments);

        // Chocolatey is silent unless told otherwise, and being told otherwise lets the packaged
        // installer open the application it just updated
        Assert.DoesNotContain("--not-silent", arguments);
    }

    /// <summary>
    /// Chocolatey 1.x `list` reads the remote feed unless told `--local-only`; 2.x has removed that option
    /// and lists local packages only.
    /// </summary>
    [Fact]
    public void Chocolatey_recheck_stays_local_on_both_major_versions()
    {
        Assert.Equal(["list", "git", "--exact", "--limit-output", "--local-only"], ChocolateySource.ListArguments("git", version1: true));
        Assert.Equal(["list", "git", "--exact", "--limit-output"], ChocolateySource.ListArguments("git", version1: false));
    }

    [Theory]
    [InlineData("1.4.0\r\n", 1)]
    [InlineData("2.2.2\n", 2)]
    [InlineData("Chocolatey v0.10.15\n", 0)]
    [InlineData("", null)]
    [InlineData("garbage", null)]
    public void Chocolatey_major_version_is_read_from_its_version_output(string stdout, int? expected) =>
        Assert.Equal(expected, ChocolateySource.ParseMajorVersion(stdout));

    /// <summary>
    /// `npm update -g` stops at the caret range's `wanted` version, while the scan offers `latest`; the
    /// exact version is installed instead so a new major is actually applied.
    /// </summary>
    [Theory]
    [InlineData("typescript", "5.6.3", "typescript@5.6.3")]
    [InlineData("@angular/cli", "19.0.0", "@angular/cli@19.0.0")]
    [InlineData("pkg", "2.0.0-beta.1", "pkg@2.0.0-beta.1")]
    public void Npm_installs_the_exact_offered_version(string id, string version, string expected)
    {
        Assert.Equal(["install", "-g", expected], NpmSource.UpgradeArguments(id, version));
        Assert.Equal(["add", "-g", expected], PnpmSource.UpgradeArguments(id, version));
    }

    /// <summary>A version that is not a plain release number is never pasted into the package spec.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("github:evil/pkg")]
    [InlineData("file:../x")]
    [InlineData("1.0.0 --registry=http://evil")]
    [InlineData("1.0.0&calc")]
    public void Npm_falls_back_to_the_in_range_update_for_an_unusual_version(string? version)
    {
        Assert.Equal(["update", "-g", "typescript"], NpmSource.UpgradeArguments("typescript", version));
        Assert.Equal(["update", "-g", "typescript"], PnpmSource.UpgradeArguments("typescript", version));
    }

    [Fact]
    public void Composer_moves_the_constraint_only_for_an_update_beyond_it()
    {
        Assert.Equal(
            ["global", "require", "phpunit/phpunit:~10.5", "--update-with-dependencies", "--no-interaction"],
            ComposerSource.UpgradeArguments("phpunit/phpunit", "10.5.2", beyondConstraint: true));

        Assert.Equal(
            ["global", "update", "phpunit/phpunit", "--no-interaction"],
            ComposerSource.UpgradeArguments("phpunit/phpunit", "9.6.20", beyondConstraint: false));

        // nothing usable to build a constraint from: the plain update, never a malformed require
        Assert.Equal(
            ["global", "update", "phpunit/phpunit", "--no-interaction"],
            ComposerSource.UpgradeArguments("phpunit/phpunit", "dev-main", beyondConstraint: true));
    }

    /// <summary>No caret: composer is a .bat on Windows and cmd.exe would strip it.</summary>
    [Theory]
    [InlineData("10.5.2", "~10.5")]
    [InlineData("v3.2.1", "~3.2")]
    [InlineData("4", "~4.0")]
    [InlineData("2.0.0+build5", "~2.0")]
    [InlineData("2.0.0-RC1", null)]
    [InlineData("2.0.0-beta1", null)]
    [InlineData("2.0.0RC1", null)]
    [InlineData("dev-main", null)]
    [InlineData("^1.0", null)]
    public void Composer_tilde_constraint(string version, string? expected) =>
        Assert.Equal(expected, ComposerSource.TildeConstraint(version));

    /// <summary>
    /// Triggering a Steam update from outside the client proved unreliable in practice,
    /// so the row is information only: no install path exists, and the table must never offer a checkbox
    /// for something that would sometimes silently do nothing.
    /// </summary>
    [Fact]
    public async Task Steam_offers_no_install_path()
    {
        var source = new SteamSource(new LoggingService());
        var progress = new Progress<InstallProgress>();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => source.InstallAsync(Item("440"), progress, CancellationToken.None));
    }

    /// <summary>
    /// CanInstall is derived from Tier alone (UpdateItem), so this is what actually keeps the checkbox
    /// off a Steam row, verified against the real source, not a hand-set test fixture.
    /// </summary>
    [Fact]
    public void A_steam_game_is_never_selectable()
    {
        var source = new SteamSource(new LoggingService());
        Assert.Equal(SourceTier.C, source.Tier);

        var item = Item("440", source.Tier);
        Assert.False(item.CanInstall);
    }

    private static UpdateItem Item(string id, SourceTier tier = SourceTier.C) => new()
    {
        Id = id,
        SourceId = "steam",
        Category = UpdateCategory.Games,
        Name = "Team Fortress 2",
        Tier = tier,
    };
}
