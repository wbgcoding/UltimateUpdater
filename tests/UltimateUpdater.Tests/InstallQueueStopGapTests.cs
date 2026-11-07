using System.IO;
using System.IO.Pipes;
using System.Text;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Moments in which a stop click used to find a row nowhere: while a stopped waiting row was settled by
/// the click and by the end of the run, between leaving the pending list and registering its stop source,
/// and while the elevation prompt was still open.
/// </summary>
public class InstallQueueStopGapTests
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
        var temp = Directory.CreateTempSubdirectory("wu-install-stopgap-").FullName;
        var settings = new SettingsService(logging, Path.Combine(temp, "settings.json"));
        settings.Current.Install.CreateRestorePoint = false;
        var history = new HistoryService(logging, Path.Combine(temp, "history.jsonl"));
        return new InstallQueue(sources, settings, history, logging, elevated);
    }

    /// <summary>Installs for the id "block" wait for a release; every other id is recorded as installed once
    /// its stop token is known to be clear.</summary>
    private sealed class GatedSource(string id) : IUpdateSource
    {
        private readonly List<string> _installed = [];

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string[] Installed
        {
            get
            {
                lock (_installed)
                    return [.. _installed];
            }
        }

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.DeveloperTools;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) => AsyncEnumerable.Empty<UpdateItem>();

        public async Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
        {
            if (item.Id == "block")
            {
                Started.TrySetResult();
                await Release.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_installed)
                _installed.Add(item.Id);
        }
    }

    private static List<InstallItemStatus> Collect(InstallQueue queue)
    {
        var statuses = new List<InstallItemStatus>();
        queue.StatusChanged += status =>
        {
            lock (statuses)
                statuses.Add(status);
        };
        return statuses;
    }

    private static InstallItemStatus[] Of(List<InstallItemStatus> statuses, string itemId)
    {
        lock (statuses)
            return statuses.Where(s => s.ItemId == itemId).ToArray();
    }

    /// <summary>A stop takes a waiting row out of the queue and settles it a moment later; the run ending in
    /// between must not report the same row as well (two final reports, two history lines).</summary>
    [Fact]
    public async Task A_stopped_waiting_row_is_reported_once_even_when_the_run_ends_while_it_is_being_settled()
    {
        var source = new GatedSource("scoop");
        var queue = Create(null, source);
        var statuses = Collect(queue);

        var run = queue.RunAsync([Item("block", "scoop"), Item("two", "scoop")], CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        queue.AfterPendingStopForTest = _ =>
        {
            source.Release.SetResult();
            run.Wait(TimeSpan.FromSeconds(20));
        };
        queue.CancelItem("scoop", "two");
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        var reports = Of(statuses, "two");
        Assert.Single(reports);
        Assert.True(reports[0].IsCancelled);
        Assert.DoesNotContain("two", source.Installed);
    }

    /// <summary>A stop that lands right after the row left the pending list must still reach it.</summary>
    [Fact]
    public async Task A_stop_right_after_a_row_left_the_pending_list_still_stops_it()
    {
        var source = new GatedSource("scoop");
        var queue = Create(null, source);
        var statuses = Collect(queue);
        queue.AfterItemTakenForTest = item => queue.CancelItem(item.SourceId, item.Id);

        await queue.RunAsync([Item("one", "scoop")], CancellationToken.None);

        Assert.Empty(source.Installed);
        var reports = Of(statuses, "one");
        Assert.Contains(reports, s => s.IsCancelled);
        Assert.DoesNotContain(reports, s => s.Success == true);
    }

    private sealed class RecordingStream : Stream
    {
        private readonly MemoryStream _sink = new();
        private readonly object _lock = new();

        public string Written
        {
            get
            {
                lock (_lock)
                    return Encoding.UTF8.GetString(_sink.ToArray());
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_lock)
                _sink.Write(buffer, offset, count);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>The rows are out of the pending list and not yet with the helper while the elevation prompt
    /// is open; a stop clicked then used to be dropped and the row installed anyway.</summary>
    [Fact]
    public async Task A_stop_during_the_elevation_prompt_is_sent_to_the_helper_once_the_job_is_out()
    {
        var prompting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new ElevatedHost(new LoggingService())
        {
            EnsureStartedOverrideForTest = async _ =>
            {
                prompting.TrySetResult();
                await gate.Task;
                return true;
            },
        };
        using var workerToApp = new AnonymousPipeServerStream(PipeDirection.In);
        using var workerEnd = new AnonymousPipeClientStream(PipeDirection.Out, workerToApp.ClientSafePipeHandle);
        var written = new RecordingStream();
        host.AttachSessionForTest(
            new StreamReader(workerToApp, Encoding.UTF8),
            new StreamWriter(written, new UTF8Encoding(false)) { AutoFlush = true });
        var queue = Create(host);

        var run = queue.RunAsync([Item("x", "winget")], CancellationToken.None);
        await prompting.Task.WaitAsync(TimeSpan.FromSeconds(20));

        queue.CancelItem("winget", "x");
        gate.SetResult();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !written.Written.Contains("cancel-item", StringComparison.Ordinal))
            await Task.Delay(20);

        var lines = written.Written.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("\"kind\":\"install\"", lines[0]);
        Assert.Contains(lines.Skip(1), line => line.Contains("\"action\":\"cancel-item\"") && line.Contains("\"itemId\":\"x\""));

        workerEnd.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(20));
    }

    /// <summary>A stop that lands right after the admin batch took its rows out of the pending list, before
    /// the prompt is even shown, must reach the helper as well.</summary>
    [Fact]
    public async Task A_stop_right_after_the_admin_batch_took_its_rows_is_sent_to_the_helper()
    {
        var host = new ElevatedHost(new LoggingService())
        {
            EnsureStartedOverrideForTest = _ => Task.FromResult(true),
        };
        using var workerToApp = new AnonymousPipeServerStream(PipeDirection.In);
        using var workerEnd = new AnonymousPipeClientStream(PipeDirection.Out, workerToApp.ClientSafePipeHandle);
        var written = new RecordingStream();
        host.AttachSessionForTest(
            new StreamReader(workerToApp, Encoding.UTF8),
            new StreamWriter(written, new UTF8Encoding(false)) { AutoFlush = true });
        var queue = Create(host);
        queue.AfterAdminRowsTakenForTest = items => queue.CancelItem(items[0].SourceId, items[0].Id);

        var run = queue.RunAsync([Item("x", "winget")], CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !written.Written.Contains("cancel-item", StringComparison.Ordinal))
            await Task.Delay(20);

        var lines = written.Written.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("\"kind\":\"install\"", lines[0]);
        Assert.Contains(lines.Skip(1), line => line.Contains("\"action\":\"cancel-item\"") && line.Contains("\"itemId\":\"x\""));

        workerEnd.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
