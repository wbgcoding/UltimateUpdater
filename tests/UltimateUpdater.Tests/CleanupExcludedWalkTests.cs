using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>A discovered cache root that is itself excluded (or lies inside an excluded folder) is never
/// listed: every file in it would be dropped one by one, so the whole walk is wasted.</summary>
public class CleanupExcludedWalkTests : IDisposable
{
    public CleanupExcludedWalkTests() => CleanupService.EnumerateFilesHookForTest.Value = null;

    public void Dispose() => CleanupService.EnumerateFilesHookForTest.Value = null;

    [Fact]
    public async Task The_cache_sweep_does_not_walk_a_root_that_the_target_excludes()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var temp = Path.Combine(localAppData, "Temp");
        Assert.True(Directory.Exists(temp), "the test machine has no per-user Temp folder");
        Assert.Contains(temp, AppCacheDiscovery.DiscoverCacheDirectories(), StringComparer.OrdinalIgnoreCase);

        var walked = new List<string>();
        CleanupService.EnumerateFilesHookForTest.Value = walked.Add;

        var target = new CleanupTarget("app-cache-sweep", "x", "y", CleanupKind.AppCacheSweep, DefaultEnabled: false,
            RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x")
        {
            ExcludedDirectories = [temp],
        };

        await new CleanupService(new LoggingService()).ScanAsync(target, CancellationToken.None);

        Assert.DoesNotContain(walked, w =>
            string.Equals(w, temp, StringComparison.OrdinalIgnoreCase)
            || w.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}
