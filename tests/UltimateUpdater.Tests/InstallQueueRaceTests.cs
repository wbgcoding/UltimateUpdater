using System.IO;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Two races in <see cref="InstallQueue"/>, made deterministic through the queue's own test seams: a stop
/// click reaching a row's stop source after its owner released it, and the declined-prompt branch touching
/// the shared reported set without the lock every other writer takes.
/// </summary>
public class InstallQueueRaceTests
{
    private static UpdateItem Item(string id, string sourceId) => new()
    {
        Id = id,
        SourceId = sourceId,
        Category = UpdateCategory.DeveloperTools,
        Name = id,
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
    };

    private static InstallQueue Create(ElevatedHost? elevated, params IUpdateSource[] sources)
    {
        var logging = new LoggingService();
        var temp = Directory.CreateTempSubdirectory("wu-install-race-").FullName;
        var settings = new SettingsService(logging, Path.Combine(temp, "settings.json"));
        settings.Current.Install.CreateRestorePoint = false;
        var history = new HistoryService(logging, Path.Combine(temp, "history.jsonl"));
        return new InstallQueue(sources, settings, history, logging, elevated);
    }

    /// <summary>Waits for a release signal and ignores the stop token, so a stop that was swallowed leaves
    /// the run alive instead of ending it.</summary>
    private sealed class ReleasableSource(string id) : IUpdateSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.DeveloperTools;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) => AsyncEnumerable.Empty<UpdateItem>();

        public async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task;
        }
    }

    [Fact]
    public async Task A_stop_that_finds_its_source_already_released_is_a_no_op_not_an_exception()
    {
        var source = new ReleasableSource("scoop");
        var queue = Create(null, source);
        var seen = 0;
        queue.BeforeItemCancelForTest = cts =>
        {
            Interlocked.Increment(ref seen);

            // What the owner's release looked like before the fix: the source is gone by the time Cancel runs.
            cts.Dispose();
        };

        var run = queue.RunAsync([Item("one", "scoop")], CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var failure = Record.Exception(() => queue.CancelItem("scoop", "one"));

        Assert.Null(failure);
        Assert.Equal(1, seen);

        source.Release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_finished_row_leaves_nothing_behind_for_a_stop_to_find()
    {
        var source = new ReleasableSource("scoop");
        var queue = Create(null, source);
        var seen = 0;
        queue.BeforeItemCancelForTest = _ => Interlocked.Increment(ref seen);

        var run = queue.RunAsync([Item("one", "scoop")], CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        source.Release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        queue.CancelItem("scoop", "one");

        Assert.Equal(0, seen);
    }

    [Fact]
    public async Task A_declined_prompt_records_each_row_under_the_report_lock()
    {
        var elevated = new ElevatedHost(new LoggingService())
        {
            EnsureStartedOverrideForTest = _ => Task.FromResult(false),
        };
        var queue = Create(elevated);
        var heldFor = new List<string>();
        queue.DeclinedItemRecordedForTest = item =>
        {
            if (queue.HoldsReportLock)
                heldFor.Add(item.Id);
        };
        var statuses = new List<InstallItemStatus>();
        queue.StatusChanged += status =>
        {
            lock (statuses)
                statuses.Add(status);
        };

        await queue.RunAsync([Item("a", "winget"), Item("b", "winget")], CancellationToken.None);

        Assert.Equal(["a", "b"], heldFor.Order());
        lock (statuses)
            Assert.All(["a", "b"], id => Assert.Contains(statuses, s => s.ItemId == id && s.Success == false));
    }
}
