using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// Proves the fix for the scan-cache test-isolation leak actually holds: every MainViewModel test in this
/// suite runs through <see cref="WpfHarness.OnUiThread"/>, which redirects <see cref="ScanResultCache"/>
/// and <see cref="HistoryService"/> away from the real %APPDATA%\UltimateUpdater\ files, into a
/// subdirectory named after the calling test, before the harness thread or any MainViewModel exists.
/// Before that redirect existed, <c>MainViewModel</c> built its own <see cref="ScanResultCache"/> from the
/// parameterless constructor with no seam for a test to inject a different path, so finishing a scan in
/// ANY MainViewModel test saved the real user's own last-scan.json, and a later test in the same run
/// loaded that pollution back (the "Security update" row from
/// <c>MainViewModelTests.A_row_offers_a_downgrade_when_its_source_can_pin_a_version_and_history_has_a_success</c>).
/// The per-test-name subdirectory closes a second leak the same fix would otherwise reopen: two DIFFERENT
/// MainViewModel tests sharing one redirected file, one contaminating the other's fresh scan the same way
/// the real file used to contaminate the whole run (found live, "Security update"/"Cumulative Update" rows
/// bleeding across two unrelated MainViewModel tests).
/// This test builds a real MainViewModel and checks the override is already active and test-specific: if
/// the redirect is ever removed, reordered, or collapsed back to one shared file, this fails instead of
/// silently reaching the real files or another test's leftovers.
/// </summary>
[Collection("wpf")]
public class ScanCacheIsolationGuardTests
{
    [Fact]
    public void A_MainViewModel_never_reaches_the_real_scan_cache_or_history_files()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(
                Directory.CreateTempSubdirectory("wu-vm-test-").FullName, "settings.json"));
            var history = new HistoryService(logging);
            var sources = new List<IUpdateSource>();

            _ = new MainViewModel(
                settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
                new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
                new AutostartService(logging), new NotificationService(settings, logging),
                new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), sources);

            Assert.NotNull(ScanResultCache.PathOverride);
            Assert.NotEqual(ScanResultCache.DefaultPath, ScanResultCache.PathOverride);
            Assert.Equal(ScanResultCache.PathOverride, ScanResultCache.EffectivePath);
            Assert.Contains(nameof(A_MainViewModel_never_reaches_the_real_scan_cache_or_history_files), ScanResultCache.PathOverride);

            Assert.NotNull(HistoryService.PathOverride);
            Assert.NotEqual(HistoryService.DefaultPath, HistoryService.PathOverride);
            Assert.Contains(nameof(A_MainViewModel_never_reaches_the_real_scan_cache_or_history_files), HistoryService.PathOverride);
        });
    }
}
