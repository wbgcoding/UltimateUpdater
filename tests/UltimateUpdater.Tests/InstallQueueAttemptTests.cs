using System.IO;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>A progress callback that outlives its attempt must not show up in the next attempt of the same row.</summary>
public class InstallQueueAttemptTests
{
    private sealed class TwoAttemptSource(string id) : IUpdateSource
    {
        private int _attempts;
        public IProgress<InstallProgress>? FirstAttemptProgress { get; private set; }
        public TaskCompletionSource SecondAttemptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecondAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => id;
        public UpdateCategory Category => UpdateCategory.DeveloperTools;
        public SourceTier Tier => SourceTier.A;
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) => AsyncEnumerable.Empty<UpdateItem>();

        public async Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                FirstAttemptProgress = progress;
                await Task.Delay(100, cancellationToken);
                return;
            }

            SecondAttemptStarted.TrySetResult();
            await ReleaseSecondAttempt.Task.WaitAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task A_progress_callback_from_attempt_one_does_not_touch_attempt_two()
    {
        var logging = new LoggingService();
        var temp = Directory.CreateTempSubdirectory("wu-attempt-test-").FullName;
        var settings = new SettingsService(logging, Path.Combine(temp, "settings.json"));
        var history = new HistoryService(logging, Path.Combine(temp, "history.jsonl"));
        var source = new TwoAttemptSource("npm");
        var queue = new InstallQueue([source], settings, history, logging);

        var statuses = new List<InstallItemStatus>();
        queue.StatusChanged += status => { lock (statuses) statuses.Add(status); };

        var item = new UpdateItem
        {
            Id = "eslint", SourceId = "npm", Category = UpdateCategory.DeveloperTools, Name = "eslint",
            InstalledVersion = "1.0", AvailableVersion = "2.0",
        };

        await queue.RunAsync([item], CancellationToken.None);

        var second = Task.Run(() => queue.RunAsync([item], CancellationToken.None));
        await source.SecondAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int before;
        lock (statuses)
            before = statuses.Count;

        // Attempt one's callback, delivered only now that attempt two is running.
        source.FirstAttemptProgress!.Report(new InstallProgress
        {
            PercentComplete = 70, BytesReceived = 70, BytesTotal = 100, StatusText = "stale-from-attempt-1",
        });
        await Task.Delay(500);

        try
        {
            lock (statuses)
                Assert.DoesNotContain(statuses.Skip(before), s => s.Status == "stale-from-attempt-1");
        }
        finally
        {
            source.ReleaseSecondAttempt.TrySetResult();
            await second.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
