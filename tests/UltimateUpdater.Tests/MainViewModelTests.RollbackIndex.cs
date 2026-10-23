using System.Net.Http;
using System.Reflection;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>
    /// The drain runs on the dispatcher once per flush tick and must never parse the history file itself.
    /// With no index built it offers no rollback (and reads nothing); the scan builds the index off the UI
    /// thread before its first drain, and from then on a drain offers it from memory.
    /// </summary>
    [Fact]
    public void The_drain_never_builds_the_rollback_index_itself()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(
                Directory.CreateTempSubdirectory("wu-vm-test-").FullName, "settings.json"));
            settings.Current.Ui.Language = "en";

            var historyPath = Path.Combine(
                Directory.CreateTempSubdirectory("wu-history-test-").FullName, "history.jsonl");
            var sources = new List<IUpdateSource> { new PinningCapableSource() };
            var history = new HistoryService(logging, historyPath);
            history.Append(new HistoryEntry
            {
                Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
                SourceId = "winget", ItemId = "pkg-a", Name = "Package A",
                FromVersion = "1.0", ToVersion = "2.0", Success = true,
            });

            var viewModel = new MainViewModel(
                settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
                new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
                new AutostartService(logging), new NotificationService(settings, logging),
                new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), sources);

            var onItemFound = typeof(MainViewModel).GetMethod("OnItemFound", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var drainPendingItems = typeof(MainViewModel).GetMethod("DrainPendingItems", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var item = new UpdateItem
            {
                Id = "pkg-a", SourceId = "winget", Category = UpdateCategory.Software, Name = "Package A",
                InstalledVersion = "1.5", AvailableVersion = "2.0",
            };

            onItemFound.Invoke(viewModel, [item]);
            var added = (List<UpdateRowViewModel>)drainPendingItems.Invoke(viewModel, [])!;
            var row = Assert.Single(added);

            Assert.Null(row.DowngradeTargetVersion);
            Assert.Equal(0, history.RollbackIndexBuilds);

            // The same item again, now that the index exists: the fast path re-applies the state.
            history.EnsureRollbackIndexAsync().GetAwaiter().GetResult();
            onItemFound.Invoke(viewModel, [item]);
            _ = (List<UpdateRowViewModel>)drainPendingItems.Invoke(viewModel, [])!;

            Assert.Equal("1.0", row.DowngradeTargetVersion);
            Assert.Equal(1, history.RollbackIndexBuilds);
        });
    }
}
