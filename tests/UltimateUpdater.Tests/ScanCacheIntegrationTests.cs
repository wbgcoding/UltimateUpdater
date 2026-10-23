using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Install;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// End to end coverage for how MainViewModel.Scan.cs wires up Services/ScanResultCache.cs: a session's
/// finished scan is what the NEXT session's first scan shows before it has found anything of its own.
/// Reads the same effective path MainViewModel's own parameterless ScanResultCache resolves to
/// (ScanResultCache.EffectivePath: WpfHarness's redirected test file, never the real
/// AppPaths.DataDirectory last-scan.json); deleted before and after so this never leaks into or is
/// affected by another test.
/// WpfHarness.RedirectUserDataForTest sets ScanResultCache.PathOverride (and HistoryService.PathOverride)
/// on the CALLING thread before the redirected work is dispatched to the one shared UI thread, so two
/// classes calling OnUiThread from different, concurrently running xunit collections can race on that
/// shared static: one class's dispatched action can run after another class's concurrent OnUiThread call
/// already overwrote PathOverride to ITS OWN test directory. [Collection("wpf")] serialises this class
/// against every other class that touches the redirected cache/history files, closing that window.
/// </summary>
[Collection("wpf")]
public class ScanCacheIntegrationTests
{
    private static string CachePath => ScanResultCache.EffectivePath;

    private sealed class FlakySource(string id, IReadOnlyList<UpdateItem> items, bool fails) : IUpdateSource
    {
        public string Id => id;
        public UpdateCategory Category => UpdateCategory.Software;
        public SourceTier Tier => SourceTier.A;
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (fails)
                throw new InvalidOperationException("Flaky source failure (expected).");

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            await Task.CompletedTask;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class GatedSource(string id) : IUpdateSource
    {
        private readonly TaskCompletionSource _gate = new();
        public string Id => id;
        public UpdateCategory Category => UpdateCategory.Software;
        public SourceTier Tier => SourceTier.A;
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public void Release() => _gate.TrySetResult();

        public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            yield break;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// A source whose scan always reports the SAME item (exactly like a real package manager
    /// still mid-install, before it has anything new to say) and whose install blocks until released, so
    /// a test can hold a row "Queued"/"Running" for as long as it needs while driving a real rescan
    /// against it.
    /// </summary>
    private sealed class InstallGateSource(string id, UpdateItem item) : IUpdateSource
    {
        private readonly TaskCompletionSource _installGate = new();
        public string Id => id;
        public UpdateCategory Category => UpdateCategory.Software;
        public SourceTier Tier => SourceTier.A;
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public void ReleaseInstall() => _installGate.TrySetResult();

        public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return item;
            await Task.CompletedTask;
        }

        public Task InstallAsync(UpdateItem installItem, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            _installGate.Task.WaitAsync(cancellationToken);
    }

    private static void DrainDispatcher()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Normal, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static UpdateItem MakeItem(string id, string sourceId) => new()
    {
        Id = id,
        SourceId = sourceId,
        Category = UpdateCategory.Software,
        Name = "Cache Test Package",
        InstalledVersion = "1.0.0",
        AvailableVersion = "1.1.0",
    };

    private static MainViewModel Create(IReadOnlyList<IUpdateSource> sources)
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging, Path.Combine(
            Directory.CreateTempSubdirectory("wu-scancache-test-").FullName, "settings.json"));
        settings.Current.Ui.Language = "en";
        var history = new HistoryService(logging);

        return new MainViewModel(
            settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
            new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
            new AutostartService(logging), new NotificationService(settings, logging),
            new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), sources);
    }

    private static void Pump(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(
            _ => frame.Continue = false,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// A real, timed wait, not a tight <see cref="DrainDispatcher"/> spin: blocker detection now hops to
    /// the thread pool (<see cref="Services.Install.InstallBlockers.CaptureBatchSnapshot"/>), and a busy
    /// loop with no actual delay between dispatcher pumps can spin through every iteration before that
    /// pool thread ever gets scheduled, especially under load from other work on the same machine.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was never met.");

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Loading a big cached scan used to call Updates.Add once per row, and Updates.CollectionChanged
    /// runs a full ApplyFilter every time it fires, so a 500-row cache load cost 500 filter passes. It now
    /// batches into one Updates.AddRange, which raises a single Reset. Measured directly (not "green
    /// either way"): before this fix the delta below was 500, one per row.
    /// </summary>
    [Fact]
    public void Loading_a_large_cached_scan_costs_one_filter_pass_not_one_per_row()
    {
        File.Delete(CachePath);
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                const int rowCount = 500;
                var items = Enumerable.Range(0, rowCount)
                    .Select(i => MakeItem($"pkg{i}", "flaky"))
                    .ToList();

                var firstViewModel = Create([new FlakySource("flaky", items, fails: false)]);
                Pump(firstViewModel.ScanCommand.ExecuteAsync(null));
                Assert.True(File.Exists(CachePath));

                var gatedSource = new GatedSource("flaky");
                var secondViewModel = Create([gatedSource]);
                var before = secondViewModel.ApplyFilterRunCountForTest;

                // The cache load hops to a background thread now, so pump until the whole load has actually
                // landed before reading the filter-run count either side of it.
                var scan = secondViewModel.ScanCommand.ExecuteAsync(null);
                Pump(WaitUntilAsync(() => secondViewModel.Updates.Count == rowCount, TimeSpan.FromSeconds(5)));
                var after = secondViewModel.ApplyFilterRunCountForTest;

                Assert.Equal(rowCount, secondViewModel.Updates.Count);
                Assert.True(
                    after - before <= 3,
                    $"expected O(1) ApplyFilter runs for a {rowCount}-row cache load, got {after - before} (before={before}, after={after})");

                gatedSource.Release();
                Pump(scan);
            });
        }
        finally
        {
            File.Delete(CachePath);
        }
    }

    [Fact]
    public void A_cached_row_appears_before_the_next_sessions_own_scan_finds_anything()
    {
        File.Delete(CachePath);
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                // First "session": a quick source finds one item; the finished scan saves the cache.
                var firstViewModel = Create([new FlakySource("flaky", [MakeItem("pkg1", "flaky")], fails: false)]);
                Pump(firstViewModel.ScanCommand.ExecuteAsync(null));
                Assert.True(File.Exists(CachePath));

                // Second "session": a fresh view model, own gated source that never reports anything on
                // its own until released. The cache load hops to a background thread now, so pump until
                // the row actually lands rather than assuming it is there the instant
                // ExecuteAsync returns.
                var gatedSource = new GatedSource("flaky");
                var secondViewModel = Create([gatedSource]);
                var scan = secondViewModel.ScanCommand.ExecuteAsync(null);
                Pump(WaitUntilAsync(() => secondViewModel.Updates.Any(r => r.Item.Id == "pkg1"), TimeSpan.FromSeconds(5)));

                Assert.Single(secondViewModel.Updates, r => r.Item.Id == "pkg1");
                Assert.True(secondViewModel.HasCachedResult);

                gatedSource.Release();
                Pump(scan);
            });
        }
        finally
        {
            File.Delete(CachePath);
        }
    }

    [Fact]
    public void A_cached_row_survives_when_its_source_fails_on_the_rescan()
    {
        File.Delete(CachePath);
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var firstViewModel = Create([new FlakySource("flaky", [MakeItem("pkg1", "flaky")], fails: false)]);
                Pump(firstViewModel.ScanCommand.ExecuteAsync(null));

                // Same source id, but this session's copy of it fails outright.
                var secondViewModel = Create([new FlakySource("flaky", [], fails: true)]);
                Pump(secondViewModel.ScanCommand.ExecuteAsync(null));

                Assert.Single(secondViewModel.Updates, r => r.Item.Id == "pkg1");
                Assert.True(secondViewModel.HasCachedResult);
            });
        }
        finally
        {
            File.Delete(CachePath);
        }
    }

    [Fact]
    public void A_cached_row_is_dropped_when_its_source_runs_and_no_longer_reports_it()
    {
        File.Delete(CachePath);
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var firstViewModel = Create([new FlakySource("flaky", [MakeItem("pkg1", "flaky")], fails: false)]);
                Pump(firstViewModel.ScanCommand.ExecuteAsync(null));

                // Same source, ran fine, but the package is no longer offered: a genuine "it is gone".
                var secondViewModel = Create([new FlakySource("flaky", [], fails: false)]);
                Pump(secondViewModel.ScanCommand.ExecuteAsync(null));

                Assert.DoesNotContain(secondViewModel.Updates, r => r.Item.Id == "pkg1");
                Assert.False(secondViewModel.HasCachedResult);
            });
        }
        finally
        {
            File.Delete(CachePath);
        }
    }

    /// <summary>
    /// This fix, the measured stale-row symptom reproduced without a real pip/winget: an install still in
    /// flight used to be discarded and replaced by DrainPendingItems whenever a rescan found the source
    /// still reporting its pre-install data (unavoidable, the real install has not finished). The
    /// replacement was a fresh, Idle look-alike row for the SAME package, installable again, while the
    /// original row's own install was still actually running, never receiving another status update
    /// (status is matched by source+id against Updates, never by object identity), a real path to
    /// installing the same package twice. Fixed in DrainPendingItems: a Queued/Running row is left
    /// completely untouched by a rescan.
    /// </summary>
    [Fact]
    public void A_row_queued_for_install_survives_a_rescan_reporting_its_own_pre_install_data()
    {
        File.Delete(CachePath);
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var item = MakeItem("pkg1", "gate");
                var source = new InstallGateSource("gate", item);
                var viewModel = Create([source]);

                Pump(viewModel.ScanCommand.ExecuteAsync(null));
                var row = Assert.Single(viewModel.Updates, r => r.Item.Id == "pkg1");

                ((IRowActions)viewModel).InstallSingle(row);
                // Blocker detection now takes its process snapshot on the thread pool; a real timed wait,
                // not a tight spin, or a busy machine can starve that pool thread past every spin iteration.
                Pump(WaitUntilAsync(() => row.InstallState != RowInstallState.Idle, TimeSpan.FromSeconds(5)));
                Assert.True(row.InstallState is RowInstallState.Queued or RowInstallState.Running);

                // A rescan while the install above is still gated open, the source reports the exact same
                // item it always does; a real package manager would do the same until the install it is
                // still running actually finishes.
                Pump(viewModel.ScanCommand.ExecuteAsync(null));

                var afterRescan = Assert.Single(viewModel.Updates, r => r.Item.Id == "pkg1");
                Assert.Same(row, afterRescan);
                Assert.True(afterRescan.InstallState is RowInstallState.Queued or RowInstallState.Running);
                Assert.False(afterRescan.CanInstall);

                // Let the gated install finish and the batch loop close out cleanly, so nothing from this
                // test leaks into the next one.
                source.ReleaseInstall();
                Pump(WaitUntilAsync(() => !viewModel.IsInstalling, TimeSpan.FromSeconds(5)));
                Assert.Equal(RowInstallState.Succeeded, row.InstallState);
            });
        }
        finally
        {
            File.Delete(CachePath);
        }
    }

    /// <summary>
    /// This fix's other half: once an install genuinely finishes and the next scan's own live result no
    /// longer includes the package, the row must actually be gone, not merely re-labelled, so the same
    /// package id is free to be discovered fresh rather than silently staying stuck.
    /// </summary>
    [Fact]
    public void A_row_is_gone_after_scan_install_rescan_finds_nothing_new()
    {
        File.Delete(CachePath);
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var item = MakeItem("pkg1", "instant");
                var source = new FlakySource("instant", [item], fails: false);
                var viewModel = Create([source]);

                Pump(viewModel.ScanCommand.ExecuteAsync(null));
                var row = Assert.Single(viewModel.Updates, r => r.Item.Id == "pkg1");

                ((IRowActions)viewModel).InstallSingle(row);
                // Blocker detection now takes its process snapshot off the UI thread, so the row can still
                // read Idle for a moment right after the click; wait that gap out before waiting on
                // IsInstalling, or a fast mock install could look finished before it ever started. A real
                // timed wait, not a tight spin, since that snapshot hops to the thread pool.
                Pump(WaitUntilAsync(() => row.InstallState != RowInstallState.Idle, TimeSpan.FromSeconds(5)));
                Pump(WaitUntilAsync(() => !viewModel.IsInstalling, TimeSpan.FromSeconds(5)));
                Assert.Equal(RowInstallState.Succeeded, row.InstallState);

                var beforeInstallableCount = viewModel.Updates.Count(r => r.CanInstall);

                // The package is genuinely gone from the source's own next report, exactly like a real
                // package manager after a successful update.
                var secondSource = new FlakySource("instant", [], fails: false);
                var secondViewModel = Create([secondSource]);
                Pump(secondViewModel.ScanCommand.ExecuteAsync(null));

                Assert.DoesNotContain(secondViewModel.Updates, r => r.Item.Id == "pkg1");
                Assert.Equal(beforeInstallableCount, secondViewModel.Updates.Count(r => r.CanInstall));
            });
        }
        finally
        {
            File.Delete(CachePath);
        }
    }

    /// <summary>
    /// A row whose own data already shows installed and available at the same version is
    /// refused rather than run, so a click that lands on stale UI or a race that outpaces a rescan never
    /// starts a real, pointless install.
    /// </summary>
    [Fact]
    public void An_already_current_row_is_refused_rather_than_installed()
    {
        File.Delete(CachePath);
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var item = new UpdateItem
                {
                    Id = "pkg1",
                    SourceId = "instant",
                    Category = UpdateCategory.Software,
                    Name = "Cache Test Package",
                    InstalledVersion = "2.0.0",
                    AvailableVersion = "2.0.0",
                };
                var source = new FlakySource("instant", [item], fails: false);
                var viewModel = Create([source]);

                Pump(viewModel.ScanCommand.ExecuteAsync(null));
                var row = Assert.Single(viewModel.Updates, r => r.Item.Id == "pkg1");

                ((IRowActions)viewModel).InstallSingle(row);
                // Blocker detection now takes its process snapshot off the UI thread (a real thread pool
                // hop, so a real timed wait is needed here too), and the refusal itself lands a few
                // dispatcher pumps later; InstallStatusText only gets set once it does.
                Pump(WaitUntilAsync(() => !string.IsNullOrEmpty(row.InstallStatusText), TimeSpan.FromSeconds(5)));

                Assert.Equal(RowInstallState.Idle, row.InstallState);
                Assert.False(viewModel.IsInstalling);
                Assert.Contains("2.0.0", row.InstallStatusText);
            });
        }
        finally
        {
            File.Delete(CachePath);
        }
    }
}
