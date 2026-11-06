using System.Windows.Threading;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// Probe for the main window's install progress path: several rows report whole-percent progress from their
/// own threads, the way a package manager's output does, while the UI thread pumps. It counts the work that
/// lands on the dispatcher (every posted operation, including the blocking hops a worker thread makes), so
/// a regression that puts a dispatcher hop back on every report, or a filter or header pass on every
/// progress tick, shows up as a number instead of as an owned window that lags during an install.
/// </summary>
[Collection("wpf")]
public class InstallProgressLoadTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int RowCount = 6;
    private const int ReportsPerRow = 100;
    private static readonly TimeSpan ReportGap = TimeSpan.FromMilliseconds(10);

    [Fact]
    public void Progress_reports_cost_few_dispatcher_operations_and_no_filter_or_header_passes()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var rows = Enumerable.Range(0, RowCount)
                .Select(i => new UpdateRowViewModel(new UpdateItem
                {
                    Id = $"pkg{i}",
                    SourceId = "winget",
                    Category = UpdateCategory.Software,
                    Name = $"Package {i}",
                    InstalledVersion = "1.0",
                    AvailableVersion = "2.0",
                }))
                .ToList();
            foreach (var row in rows)
                viewModel.Updates.Add(row);

            var dispatcher = Dispatcher.CurrentDispatcher;
            var operations = 0;
            DispatcherHookEventHandler posted = (_, _) => Interlocked.Increment(ref operations);
            dispatcher.Hooks.OperationPosted += posted;

            var filterRunsBefore = viewModel.ApplyFilterRunCountForTest;
            var headerRunsBefore = viewModel.RefreshHeaderRunCountForTest;
            var startedAt = DateTime.UtcNow;
            var producersTook = TimeSpan.Zero;

            var frame = new DispatcherFrame();
            var producers = Enumerable.Range(0, RowCount).Select(i => Task.Run(() =>
            {
                for (var percent = 0; percent < ReportsPerRow; percent++)
                {
                    viewModel.OnInstallStatusChanged(new InstallItemStatus(
                        "winget", $"pkg{i}", percent, $"Downloading {percent}%", null, null));
                    Thread.Sleep(ReportGap);
                }

                viewModel.OnInstallStatusChanged(new InstallItemStatus("winget", $"pkg{i}", 100, null, true, null));
            })).ToArray();

            _ = Task.WhenAll(producers).ContinueWith(async _ =>
            {
                producersTook = DateTime.UtcNow - startedAt;
                // Lets a throttled last flush land before the counts are read.
                await Task.Delay(MainViewModel.InstallProgressFlushInterval * 2);
                frame.Continue = false;
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

            Dispatcher.PushFrame(frame);
            dispatcher.Hooks.OperationPosted -= posted;

            var perSecond = operations / producersTook.TotalSeconds;
            var filterRuns = viewModel.ApplyFilterRunCountForTest - filterRunsBefore;
            var headerRuns = viewModel.RefreshHeaderRunCountForTest - headerRunsBefore;
            output.WriteLine(
                $"[probe] dispatcher ops={operations} over {producersTook.TotalSeconds:F2}s = {perSecond:F0}/s, "
                + $"ApplyFilter={filterRuns}, RefreshHeader={headerRuns}");

            Assert.All(rows, row =>
            {
                Assert.Equal(RowInstallState.Succeeded, row.InstallState);
                Assert.Equal(100, row.InstallPercent);
            });

            // The unthrottled path posted one blocking operation per report: about 380 a second here; the throttled path stays under a fifth of that.
            Assert.True(perSecond <= 75, $"{perSecond:F0} dispatcher operations per second during the install");
            Assert.True(filterRuns <= RowCount, $"{filterRuns} filter passes during the install");
            Assert.True(headerRuns <= RowCount, $"{headerRuns} header refreshes during the install");
        });
    }

    private static MainViewModel Create()
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging, System.IO.Path.Combine(
            System.IO.Directory.CreateTempSubdirectory("wu-progress-test-").FullName, "settings.json"));
        settings.Current.Ui.Language = "en";

        var sources = MockUpdateSource.CreateAll();
        var history = new HistoryService(logging);

        return new MainViewModel(
            settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
            new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
            new AutostartService(logging), new NotificationService(settings, logging),
            new Services.SelfUpdate.SelfUpdateService(
                new System.Net.Http.HttpClient(), logging, new Services.SelfUpdate.PublicUpdateChannel(logging)),
            sources);
    }
}
