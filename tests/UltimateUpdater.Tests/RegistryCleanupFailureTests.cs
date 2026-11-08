using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// A registry target whose mandatory backup cannot be made deletes nothing, and says so: the findings
/// count as kept and the outcome is failed, so the row never reads as done.
/// </summary>
public class RegistryCleanupFailureTests
{
    private static CleanupTarget Target() => new(
        "test-registry-target", "x", "y", CleanupKind.RegistryCleanup, DefaultEnabled: false,
        RiskLevel: CleanupRiskLevel.Risky, ConsequenceKey: "x", NeedsElevation: true)
    {
        RegistryCategories = [RegistryCleanupCategory.AppPaths],
    };

    private static RegistryFinding Finding(string name) => new(
        RegistryCleanupCategory.AppPaths,
        $@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{name}", null, name, @"C:\does-not-exist\x.exe");

    [Fact]
    public async Task A_refused_backup_keeps_every_finding_and_fails_the_target()
    {
        // A backup folder outside the user profile is refused before anything touches the registry.
        var outside = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "wu-no-backup");
        var settings = new RegistryCleanupSettings { BackupFolder = outside, RetentionCount = 1 };

        var outcome = await new CleanupService(new LoggingService()).CleanAsync(
            Target(), progress: null, CancellationToken.None,
            registrySettings: settings, registryFindings: [Finding("wu-a.exe"), Finding("wu-b.exe")]);

        Assert.True(outcome.Failed);
        Assert.Equal(0, outcome.DeletedFiles);
        Assert.Equal(2, outcome.SkippedFiles);
        Assert.False(Directory.Exists(outside));
    }

    [Fact]
    public async Task Missing_backup_settings_keep_every_finding_and_fail_the_target()
    {
        var outcome = await new CleanupService(new LoggingService()).CleanAsync(
            Target(), progress: null, CancellationToken.None,
            registrySettings: null, registryFindings: [Finding("wu-a.exe")]);

        Assert.True(outcome.Failed);
        Assert.Equal(0, outcome.DeletedFiles);
        Assert.Equal(1, outcome.SkippedFiles);
    }

    [Fact]
    public async Task A_target_with_no_findings_stays_a_plain_success()
    {
        var outcome = await new CleanupService(new LoggingService()).CleanAsync(
            Target(), progress: null, CancellationToken.None,
            registrySettings: null, registryFindings: []);

        Assert.False(outcome.Failed);
        Assert.Equal(0, outcome.SkippedFiles);
    }
}
