using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="LoggingService.ReadAround"/> is the read side of this fix's history detail fallback: when an
/// entry has no stored <c>Detail</c> (written before this fix, or a path that never captured one), the
/// history window falls back to whatever the log itself still has around that entry's timestamp. Exercised
/// against a temp directory rather than the real AppPaths location, via the same optional-path constructor
/// convention HistoryService already uses.
/// </summary>
public class LoggingReadBackTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("wu-logging-test-").FullName;

    private LoggingService Logging() => new(_tempDir);

    private void WriteLog(DateTime day, params string[] lines) =>
        File.WriteAllLines(Path.Combine(_tempDir, $"{day:yyyy-MM-dd}.log"), lines);

    [Fact]
    public void ReadAround_returns_only_the_lines_inside_the_window()
    {
        var day = new DateTime(2026, 7, 19);
        WriteLog(day,
            "23:14:00 I inst too early",
            "23:14:45 I inst just inside",
            "23:15:00 I inst on the mark",
            "23:15:15 I inst just inside too",
            "23:16:00 I inst too late");

        var lines = Logging().ReadAround(
            new DateTimeOffset(day.AddHours(23).AddMinutes(15), DateTimeOffset.Now.Offset), TimeSpan.FromSeconds(30));

        Assert.Equal(
            ["23:14:45 I inst just inside", "23:15:00 I inst on the mark", "23:15:15 I inst just inside too"],
            lines);
    }

    /// <summary>The first write of every day zips and deletes each earlier day's plain log, and a size rollover
    /// zips today's earlier lines, so the archives are where everything but the newest lines lives.</summary>
    [Fact]
    public void ReadAround_reads_the_archived_logs_of_that_day_in_order()
    {
        var day = new DateTime(2026, 7, 19);
        var archiveDir = Directory.CreateDirectory(Path.Combine(_tempDir, "archive")).FullName;
        void Zip(string name, string line)
        {
            using var zip = System.IO.Compression.ZipFile.Open(Path.Combine(archiveDir, name), System.IO.Compression.ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry($"{day:yyyy-MM-dd}.log").Open());
            writer.WriteLine(line);
        }

        Zip($"{day:yyyy-MM-dd}.zip", "23:15:00 I inst first rollover");
        Zip($"{day:yyyy-MM-dd}_1.zip", "23:15:01 I inst second rollover");
        WriteLog(day, "23:15:02 I inst still in the plain file");

        var lines = Logging().ReadAround(
            new DateTimeOffset(day.AddHours(23).AddMinutes(15), DateTimeOffset.Now.Offset), TimeSpan.FromSeconds(30));

        Assert.Equal(
            ["23:15:00 I inst first rollover", "23:15:01 I inst second rollover", "23:15:02 I inst still in the plain file"],
            lines);
    }

    [Fact]
    public void ReadAround_filters_to_the_requested_category()
    {
        var day = new DateTime(2026, 7, 19);
        WriteLog(day,
            "23:15:00 I inst the install line",
            "23:15:01 I scan an unrelated scan line",
            "23:15:02 W inst the failure line");

        var lines = Logging().ReadAround(
            new DateTimeOffset(day.AddHours(23).AddMinutes(15), DateTimeOffset.Now.Offset), TimeSpan.FromSeconds(30), "inst");

        Assert.Equal(["23:15:00 I inst the install line", "23:15:02 W inst the failure line"], lines);
    }

    [Fact]
    public void ReadAround_crosses_midnight_into_the_previous_days_file()
    {
        var day = new DateTime(2026, 7, 19);
        WriteLog(day, "23:59:50 I inst just before midnight");
        WriteLog(day.AddDays(1), "00:00:10 I inst just after midnight");

        var lines = Logging().ReadAround(
            new DateTimeOffset(day.AddDays(1), DateTimeOffset.Now.Offset), TimeSpan.FromSeconds(30));

        Assert.Equal(["23:59:50 I inst just before midnight", "00:00:10 I inst just after midnight"], lines);
    }

    [Fact]
    public void ReadAround_with_no_log_file_for_that_day_returns_empty()
    {
        var lines = Logging().ReadAround(DateTimeOffset.Now, TimeSpan.FromSeconds(30));

        Assert.Empty(lines);
    }

    /// <summary>
    /// Sink mode (the elevated worker) has no real log directory, <c>_logsDirectory</c> stays
    /// empty on purpose. Without an explicit guard this would read around the process's own current
    /// directory (an empty string is a relative path) instead of simply returning nothing; there is no
    /// directory to point a test at that is not itself a real, working, misleading answer, so this proves
    /// the guard by working from a current directory that DOES hold a real log file with today's name and
    /// asserting sink mode still returns nothing, exactly the same file a non-sink instance in that same
    /// directory would have found.
    /// </summary>
    [Fact]
    public void ReadAround_in_sink_mode_never_reads_the_current_directory()
    {
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _tempDir;
            WriteLog(DateTime.Now, $"{DateTime.Now:HH:mm:ss} I inst present in the process's own directory");

            var sinkLogging = new LoggingService(_ => { });
            Assert.Empty(sinkLogging.ReadAround(DateTimeOffset.Now, TimeSpan.FromMinutes(5)));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    /// <summary>Same reasoning as the ReadAround guard just above, for the retention sweep.</summary>
    [Fact]
    public void CleanupOldLogs_in_sink_mode_never_touches_the_current_directory()
    {
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _tempDir;
            var stalePath = Path.Combine(_tempDir, "2000-01-01.log");
            File.WriteAllText(stalePath, "stale");

            var sinkLogging = new LoggingService(_ => { });
            sinkLogging.CleanupOldLogs(retentionDays: 1);

            Assert.True(File.Exists(stalePath), "sink mode must never sweep the current directory");
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
