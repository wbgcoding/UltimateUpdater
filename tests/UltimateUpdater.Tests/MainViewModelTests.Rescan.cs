using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>
    /// WindowsUpdateSource.Install throws "no longer offered" only once the update's own GUID is
    /// genuinely gone from a fresh search, and MainViewModel reacts by rescanning that ONE source so the
    /// stale row disappears without the user having to rescan by hand. This proves the rescan mechanism
    /// itself, scoped to exactly one source: a row that rescan no longer finds is dropped, and every
    /// other source's rows are left completely alone.
    /// </summary>
    [Fact]
    public void RescanSingleSourceAsync_drops_a_row_the_rescan_no_longer_finds_and_leaves_other_sources_alone()
    {
        WpfHarness.OnUiThread(() =>
        {
            var wuSource = new OneShotSource("windowsupdate", Item("wu-guid-1:5", "Cumulative Update", "windowsupdate"));
            var otherSource = new OneShotSource("npm", Item("eslint", "eslint", "npm"));

            var viewModel = Create(sources: [wuSource, otherSource]);
            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            Assert.Contains(viewModel.Updates, u => u.Item.SourceId == "windowsupdate");
            Assert.Contains(viewModel.Updates, u => u.Item.SourceId == "npm");

            // The next search for this source comes back empty. The update is genuinely gone, not just
            // moved to a different revision.
            wuSource.ReportNothingNextScan();

            Pump(viewModel.RescanSingleSourceAsync("windowsupdate"));

            Assert.DoesNotContain(viewModel.Updates, u => u.Item.SourceId == "windowsupdate");
            Assert.Contains(viewModel.Updates, u => u.Item.SourceId == "npm");
        });
    }

    /// <summary>
    /// Before this fix, <see cref="MainViewModel.RescanSingleSourceAsync"/> dropped every row this source's
    /// rescan did not rediscover unconditionally, with no check on how the rescan itself actually ended. A
    /// transient winget/network failure (or the user's own cancel) also finds nothing, but that must never
    /// read as "genuinely no longer offered": every existing row for the source has to survive an Error (or
    /// Cancelled) result, exactly as a full scan's <c>RemoveStaleRows</c> already protects its cache-only
    /// rows for the same reason.
    /// </summary>
    [Fact]
    public void RescanSingleSourceAsync_keeps_existing_rows_when_the_rescan_itself_fails()
    {
        WpfHarness.OnUiThread(() =>
        {
            var wuSource = new OneShotSource("windowsupdate", Item("wu-guid-1:5", "Cumulative Update", "windowsupdate"));
            var otherSource = new OneShotSource("npm", Item("eslint", "eslint", "npm"));

            var viewModel = Create(sources: [wuSource, otherSource]);
            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            Assert.Contains(viewModel.Updates, u => u.Item.SourceId == "windowsupdate");

            // The rescan itself blows up (a transient tool/network failure), not "the update is gone".
            wuSource.ReportErrorNextScan();

            Pump(viewModel.RescanSingleSourceAsync("windowsupdate"));

            Assert.Contains(viewModel.Updates, u => u.Item.SourceId == "windowsupdate");
            Assert.Contains(viewModel.Updates, u => u.Item.SourceId == "npm");
        });
    }

    /// <summary>
    /// The rescan button on a source pill used to run with no visible feedback at all: the top progress
    /// bar and "Scanning..." text are both driven off <see cref="MainViewModel.IsScanning"/>, which a
    /// single-source rescan never touched, so clicking it looked like nothing happened until the row
    /// changed. Proven here with a source that blocks until told to finish, so the state can be read
    /// mid-flight rather than only before and after.
    /// </summary>
    [Fact]
    public void RescanSingleSourceAsync_turns_on_the_scanning_state_and_off_again_when_done()
    {
        WpfHarness.OnUiThread(() =>
        {
            var gated = new GatedSource();
            var viewModel = Create(sources: [gated]);

            Assert.False(viewModel.IsScanning);

            var task = viewModel.RescanSingleSourceAsync("windowsupdate");

            Assert.True(viewModel.IsScanning);

            gated.Release();
            Pump(task);

            Assert.False(viewModel.IsScanning);
        });
    }

    /// <summary>While a single-source rescan runs, the window shows the scanning state and its Stop
    /// button; Stop must actually end that rescan, not only a full scan.</summary>
    [Fact]
    public void Stop_ends_a_running_single_source_rescan()
    {
        WpfHarness.OnUiThread(() =>
        {
            var gated = new GatedSource();
            var viewModel = Create(sources: [gated]);

            var task = viewModel.RescanSingleSourceAsync("windowsupdate");
            Assert.True(viewModel.IsScanning);

            Pump(viewModel.CancelScanCommand.ExecuteAsync(null));
            Pump(task.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.True(task.IsCompleted);
            Assert.False(viewModel.IsScanning);
            gated.Release();
        });
    }

    /// <summary>An interval scan the user stopped must not announce whatever stale rows were on screen, while
    /// one that ran to its clean end still does.</summary>
    [Fact]
    public void RunScheduledScanAsync_notifies_only_after_a_clean_scan()
    {
        WpfHarness.OnUiThread(() =>
        {
            var stopped = RunScheduledScanWithNotificationCount(new GatedSource(), stopMidScan: true);
            Assert.Equal(0, stopped);

            var clean = RunScheduledScanWithNotificationCount(new OneShotSource("npm", Item("eslint", "eslint", "npm")), stopMidScan: false);
            Assert.Equal(1, clean);
        });
    }

    private static int RunScheduledScanWithNotificationCount(IUpdateSource source, bool stopMidScan)
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging, Path.Combine(
            Directory.CreateTempSubdirectory("wu-vm-test-").FullName, "settings.json"));
        settings.Current.Ui.Language = "en";
        settings.Current.SelfUpdate.Enabled = false;

        IReadOnlyList<IUpdateSource> sources = [source];
        var history = new HistoryService(logging);
        var notificationService = new NotificationService(settings, logging);
        var viewModel = new MainViewModel(
            settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
            new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
            new AutostartService(logging), notificationService,
            new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), sources);

        // A stale row from an earlier scan: what a stopped scan would wrongly announce.
        viewModel.Updates.Add(new UpdateRowViewModel(Item("stale", "Stale", source.Id)));

        var notifications = 0;
        notificationService.NotificationRequested += (_, _) => notifications++;

        var run = viewModel.RunScheduledScanAsync();
        if (stopMidScan)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!viewModel.IsScanning && DateTime.UtcNow < deadline)
                Pump(Task.Delay(50));

            viewModel.CancelScanCommand.Execute(null);
        }

        Pump(run);
        return notifications;
    }

    /// <summary>A single-source rescan ending while a full scan is still running must leave the scanning
    /// state on; the full scan's own end, not the rescan's, turns it off.</summary>
    [Fact]
    public void RescanSingleSourceAsync_does_not_clear_the_scanning_state_of_a_running_full_scan()
    {
        WpfHarness.OnUiThread(() =>
        {
            var rescanned = new FirstCallGatedSource("windowsupdate");
            var other = new FirstCallGatedSource("npm");
            var viewModel = Create(sources: [rescanned, other]);

            // The full scan is first: a scan request made while a rescan shows the window as scanning is ignored.
            var fullScan = viewModel.ScanCommand.ExecuteAsync(null);
            var rescan = viewModel.RescanSingleSourceAsync("windowsupdate");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (other.Calls == 0 && DateTime.UtcNow < deadline)
                Pump(Task.Delay(50));

            Assert.True(other.Calls > 0, "the full scan never reached its second source");

            rescanned.Release();
            Pump(rescan);

            Assert.False(fullScan.IsCompleted);
            Assert.True(viewModel.IsScanning);

            other.Release();
            Pump(fullScan);

            Assert.False(viewModel.IsScanning);
        });
    }

    /// <summary>Blocks only its first scan; every later scan of it answers at once.</summary>
    private sealed class FirstCallGatedSource(string id) : IUpdateSource
    {
        private readonly TaskCompletionSource _gate = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Release() => _gate.TrySetResult();

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                await _gate.Task.WaitAsync(cancellationToken);

            yield return Item("KB5041585", "Security update", id);
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
