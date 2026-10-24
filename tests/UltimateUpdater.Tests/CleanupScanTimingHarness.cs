using System.Diagnostics;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// Manual timing harness for the two slowest measuring walks (user-temp and app-cache-sweep). Read-only:
/// it only calls ScanAsync, never a Clean method. It does nothing unless the marker file
/// <c>uu-scan-timing.on</c> exists in the temp folder, so the gate runs it as an instant no-op; run it by
/// hand with <c>--filter "Category=ScanTiming"</c> after creating that file. Numbers go to the test
/// output and are appended to <c>uu-scan-timing.txt</c> in the temp folder.
/// </summary>
[Trait("Category", "ScanTiming")]
public class CleanupScanTimingHarness(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData("user-temp")]
    [InlineData("app-cache-sweep")]
    public async Task Time_one_scan(string targetId)
    {
        if (!File.Exists(Path.Combine(Path.GetTempPath(), "uu-scan-timing.on")))
            return;

        var target = CleanupCatalog.Build().Single(t => t.Id == targetId);
        var service = new CleanupService(new LoggingService());
        var lines = new List<string>();

        if (target.Kind == CleanupKind.AppCacheSweep)
        {
            var discovery = Stopwatch.StartNew();
            var dirs = AppCacheDiscovery.DiscoverCacheDirectories();
            lines.Add($"{targetId} discovery only: {discovery.ElapsedMilliseconds} ms, {dirs.Count} directories");
        }

        for (var run = 1; run <= 3; run++)
        {
            var watch = Stopwatch.StartNew();
            var result = await service.ScanAsync(target, CancellationToken.None);
            lines.Add($"{targetId} run {run}: {watch.ElapsedMilliseconds} ms, {result.Files} files, {result.Bytes} bytes, {result.Skipped} skipped");
        }

        foreach (var line in lines)
            output.WriteLine(line);

        File.AppendAllLines(Path.Combine(Path.GetTempPath(), "uu-scan-timing.txt"), lines);
    }
}
