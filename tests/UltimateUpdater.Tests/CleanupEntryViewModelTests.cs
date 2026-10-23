using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// A detail row's own risk level is the STRONGER of the owning target's level and the entry
/// TYPE's own level (crash reports, logs and install leftovers count as considered, everything else as
/// safe) - never weaker than the target it belongs to.
/// </summary>
public class CleanupEntryViewModelTests
{
    private static CleanupEntryViewModel MakeEntry(CleanupEntryType type, CleanupRiskLevel targetRiskLevel) =>
        new(
            new CleanupEntry(@"C:\Temp\Item", "Item", 1024, 1,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), type),
            _ => { },
            targetRiskLevel: targetRiskLevel);

    [Fact]
    public void A_crash_reports_entry_inside_a_safe_target_reports_considered()
    {
        var entry = MakeEntry(CleanupEntryType.CrashReports, CleanupRiskLevel.Safe);

        Assert.Equal(CleanupRiskLevel.Considered, entry.RiskLevel);
    }

    [Theory]
    [InlineData(CleanupEntryType.Logs)]
    [InlineData(CleanupEntryType.InstallLeftovers)]
    public void Logs_and_install_leftovers_also_count_as_considered_inside_a_safe_target(CleanupEntryType type)
    {
        var entry = MakeEntry(type, CleanupRiskLevel.Safe);

        Assert.Equal(CleanupRiskLevel.Considered, entry.RiskLevel);
    }

    [Fact]
    public void An_otherwise_safe_entry_type_never_reports_weaker_than_its_target()
    {
        var entry = MakeEntry(CleanupEntryType.Cache, CleanupRiskLevel.Risky);

        Assert.Equal(CleanupRiskLevel.Risky, entry.RiskLevel);
    }

    [Fact]
    public void A_considered_entry_type_never_reports_weaker_than_a_considered_target()
    {
        var entry = MakeEntry(CleanupEntryType.CrashReports, CleanupRiskLevel.Considered);

        Assert.Equal(CleanupRiskLevel.Considered, entry.RiskLevel);
    }

    [Fact]
    public void A_cache_entry_inside_a_safe_target_stays_safe()
    {
        var entry = MakeEntry(CleanupEntryType.Cache, CleanupRiskLevel.Safe);

        Assert.Equal(CleanupRiskLevel.Safe, entry.RiskLevel);
    }
}
