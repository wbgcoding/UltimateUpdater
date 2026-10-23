using System.IO.Compression;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// A single day's log grew to gigabytes because dozens of dev-session test runs kept appending to the
/// same never-rotated file with nothing to cap it.
/// This is the size-based rollover that stops any one file from growing past a few MB regardless of the
/// calendar day, with the rolled-out content zipped into an archive rather than thrown away.
/// </summary>
public class LoggingRotationTests
{
    [Fact]
    public void An_oversized_log_is_archived_and_the_live_file_starts_fresh()
    {
        var logging = new LoggingService();
        var logPath = Path.Combine(AppPaths.LogsDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
        var archiveDir = Path.Combine(AppPaths.LogsDirectory, "archive");
        var before = Directory.Exists(archiveDir) ? Directory.GetFiles(archiveDir, "*.zip") : [];

        try
        {
            // Long lines, well past the 5 MB rollover threshold, and every one DIFFERENT: identical
            // consecutive lines are counted rather than repeated now, so a constant filler would produce
            // one line and prove nothing about rolling. 'x' repeated still compresses to almost nothing,
            // which keeps the cleanup below cheap.
            // Flushed in chunks below the write queue's capacity: the queue drops lines rather than block
            // when it is full, and a busy machine would otherwise lose enough of them to stay under 5 MB.
            var filler = new string('x', 1024);
            for (var i = 0; i < 5200; i++)
            {
                logging.Info($"{i} {filler}");
                if (i % 1000 == 999)
                    logging.Flush();
            }

            // Writes land on a background consumer now (LoggingService's write queue), Flush blocks until
            // everything queued above has actually reached disk, same guarantee the old synchronous
            // append gave for free.
            logging.Flush();

            Assert.True(File.Exists(logPath), "today's log must still exist after rolling");
            Assert.True(new FileInfo(logPath).Length < 5 * 1024 * 1024,
                "the live file must have been rolled, not left oversized");

            var after = Directory.GetFiles(archiveDir, "*.zip");
            var created = after.Except(before).ToList();
            Assert.NotEmpty(created);

            using var archive = ZipFile.OpenRead(created[0]);
            Assert.Single(archive.Entries);
        }
        finally
        {
            // This is the REAL shared log directory (LoggingService has no injectable path), so leave it
            // exactly as this test found it rather than accumulating an archive on every dev-session run.
            foreach (var zip in Directory.Exists(archiveDir) ? Directory.GetFiles(archiveDir, "*.zip").Except(before) : [])
                File.Delete(zip);
        }
    }

    /// <summary>A stale log another process still holds open cannot be read into the archive. The failed
    /// attempt must leave nothing behind: an empty zip under the real name made the next attempt fall back
    /// to a suffixed name and left a 0 byte archive in the folder.</summary>
    [Fact]
    public void A_locked_stale_log_leaves_no_zip_and_no_temp_file_behind()
    {
        var directory = Directory.CreateTempSubdirectory("wu-log-lock-").FullName;
        var stale = Path.Combine(directory, "2020-01-01.log");
        File.WriteAllText(stale, "old line");

        try
        {
            using (new FileStream(stale, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var logging = new LoggingService(directory);
                logging.Info("today");
                logging.Flush();
            }

            var archive = Path.Combine(directory, "archive");
            var leftovers = Directory.Exists(archive) ? Directory.GetFiles(archive) : [];
            Assert.Empty(leftovers);
            Assert.True(File.Exists(stale), "the log must stay in place when it could not be archived");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
