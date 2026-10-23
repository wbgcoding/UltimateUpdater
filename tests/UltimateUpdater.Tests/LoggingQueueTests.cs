using System.Diagnostics;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// LoggingService writes through a background queue rather than an open/append/close per call
/// (Services/LoggingService.cs: the per-line file-open cost, and rotation/compression running on
/// whatever thread happened to log, both used to sit on the caller). These prove the two things that
/// design change must still guarantee: nothing queued is ever lost, and <see cref="LoggingService.Flush"/>
/// really does wait for the background writer rather than returning early.
/// </summary>
public class LoggingQueueTests
{
    private static string NewTempLogsDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uu-logging-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Flush_waits_for_every_queued_line_to_actually_be_on_disk()
    {
        var dir = NewTempLogsDirectory();
        try
        {
            var logging = new LoggingService(dir);
            var logPath = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log");

            for (var i = 0; i < 300; i++)
                logging.Info($"line {i}");

            logging.Flush();

            Assert.True(File.Exists(logPath));
            Assert.Equal(300, File.ReadAllLines(logPath).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Flush returning means the file is closed too, not merely flushed: a caller may delete the log
    /// folder right away. Repeated because the handle used to stay open for the rest of the batch, a
    /// window too short to hit on every try.
    /// </summary>
    [Fact]
    public void Flush_leaves_the_log_file_closed()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var dir = NewTempLogsDirectory();
            var logging = new LoggingService(dir);
            var logPath = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log");

            for (var i = 0; i < 200; i++)
                logging.Info($"attempt {attempt} line {i}");
            logging.Flush();

            using (new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }

            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Same production shape as SettingsService's deferred save race (Finding 1): several threads calling
    /// into the same service at once. Every line here is distinct across all threads, so the
    /// identical-consecutive-line collapsing (Write's own _pendingBody check) never removes one, letting
    /// this assert an exact count.
    /// </summary>
    [Fact]
    public async Task Concurrent_writers_from_multiple_threads_all_land_without_loss()
    {
        var dir = NewTempLogsDirectory();
        try
        {
            var logging = new LoggingService(dir);
            var logPath = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log");

            const int writerCount = 8;
            const int perWriter = 250;

            var writers = Enumerable.Range(0, writerCount).Select(w => Task.Run(() =>
            {
                for (var i = 0; i < perWriter; i++)
                    logging.Info($"writer {w} line {i}");
            })).ToArray();

            await Task.WhenAll(writers);
            logging.Flush();

            Assert.Equal(writerCount * perWriter, File.ReadAllLines(logPath).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A queued line right before the process dies used to be simply lost: an AppDomain.UnhandledException
    /// handler firing on the way down bypasses the app's normal shutdown path entirely, so nothing else
    /// would ever have flushed the consumer. Flush itself must also never turn a genuinely stuck disk (or,
    /// here, a write held open by the gate below) into a hang on the way out, so it is bounded rather than
    /// waiting forever.
    /// </summary>
    [Fact]
    public void Flush_with_a_blocked_writer_still_returns_within_the_bound()
    {
        var dir = NewTempLogsDirectory();
        var gate = new ManualResetEventSlim(false);
        LoggingService? logging = null;

        try
        {
            logging = new LoggingService(dir) { TestWriteGate = gate };
            logging.Info("stuck behind a disk that never answers");

            var stopwatch = Stopwatch.StartNew();
            logging.Flush();
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4),
                $"Flush must be bounded, took {stopwatch.Elapsed}");
        }
        finally
        {
            // Let the stuck line actually land (a real, unblocked Flush this time) before the directory
            // it sits in is deleted out from under the consumer's still-open file.
            gate.Set();
            logging?.Flush();
            gate.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }
    /// <summary>
    /// A stalled writer with a full queue must not turn every later log call (UI thread, crash handler)
    /// into a hang: the overflow is dropped and counted, and the count lands once the writer recovers.
    /// </summary>
    [Fact]
    public async Task A_full_queue_behind_a_stalled_writer_never_blocks_the_caller()
    {
        var dir = NewTempLogsDirectory();
        var gate = new ManualResetEventSlim(false);
        LoggingService? logging = null;

        try
        {
            logging = new LoggingService(dir) { TestWriteGate = gate };
            var service = logging;
            var flood = Task.Run(() =>
            {
                for (var i = 0; i < 6000; i++)
                    service.Info($"line {i}");
                service.Flush();
            });

            var finished = await Task.WhenAny(flood, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(finished == flood, "logging into a full queue must never block");

            gate.Set();
            logging.Flush();
            logging.Info("after recovery");
            logging.Flush();

            var text = File.ReadAllText(Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log"));
            Assert.Contains("lines dropped", text);
            Assert.Contains("after recovery", text);
        }
        finally
        {
            gate.Set();
            logging?.Flush();
            gate.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }
}
