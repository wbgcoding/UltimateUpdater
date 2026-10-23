using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// winget refuses to touch a package that was installed for the current user while it is running as an
/// administrator, and the shared elevated helper always is. Recognising that refusal is what lets the app
/// retry in-process instead of handing the user a message they can do nothing about. The
/// exact wording came from a real failure in the app's own log.
/// </summary>
public class UserScopeUninstallTests
{
    [Fact]
    public void The_real_winget_refusal_is_recognised()
    {
        const string output =
            "winget uninstall failed for 'BitcoinCoreProject.BitcoinCore' (exit 0x8A15007D). "
            + "Gefunden Bitcoin Core [BitcoinCoreProject.BitcoinCore] "
            + "The package installed for user scope cannot be uninstalled when running with administrator privileges.";

        Assert.True(WingetSource.NeedsUserScopeRetry(output));
    }

    /// <summary>
    /// The same code is what an UPGRADE hits: winget calls it
    /// APPINSTALLER_CLI_ERROR_ADMIN_CONTEXT_ACTION_PROHIBITED, "the requested operation is not permitted
    /// from an administrator context on packages installed within the user scope". Bitcoin Core is installed
    /// that way on a real observed machine, which is why its update never went through.
    /// </summary>
    [Fact]
    public void An_upgrade_blocked_by_the_same_rule_is_recognised()
    {
        const string output =
            "winget install failed for 'BitcoinCoreProject.BitcoinCore' (exit 0x8A15007D). "
            + "The requested operation is not permitted from an administrator context "
            + "on packages installed within the user scope.";

        Assert.True(WingetSource.NeedsUserScopeRetry(output));
    }

    /// <summary>An installer that refuses to run elevated at all needs the very same answer.</summary>
    [Fact]
    public void An_installer_that_refuses_elevation_is_recognised() =>
        Assert.True(WingetSource.NeedsUserScopeRetry(
            "winget install failed (exit 0x8A150056). The installer cannot be run from an administrator context."));

    /// <summary>A localized Windows prints the sentence in its own language, so the code alone must do.</summary>
    [Theory]
    [InlineData("Deinstallation fehlgeschlagen (0x8A15007D).")]
    [InlineData("Aktualisierung fehlgeschlagen (exit code -1978335107).")]
    public void The_error_code_alone_is_enough(string output) =>
        Assert.True(WingetSource.NeedsUserScopeRetry(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("winget uninstall failed: the package is not installed.")]
    [InlineData("Access is denied. (exit 5)")]
    public void An_unrelated_failure_is_not_retried_unelevated(string? output) =>
        Assert.False(WingetSource.NeedsUserScopeRetry(output));
}
