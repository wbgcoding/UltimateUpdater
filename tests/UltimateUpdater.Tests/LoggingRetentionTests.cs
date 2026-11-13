using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// The retention sweep deletes old logs and archives only. A bad retention value (hand-edited settings.json)
/// must never wipe the log written seconds ago.
/// </summary>
public sealed class LoggingRetentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uu-logret-" + Guid.NewGuid().ToString("N"));

    public LoggingRetentionTests() => Directory.CreateDirectory(_dir);

    private string Touch(string relative, int ageDays)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-ageDays));
        return path;
    }

    [Fact]
    public void Logs_and_archives_older_than_the_window_are_deleted_and_newer_ones_kept()
    {
        var oldLog = Touch("2020-01-01.log", 400);
        var freshLog = Touch("today.log", 0);
        var oldZip = Touch(Path.Combine("archive", "old.zip"), 400);
        var freshZip = Touch(Path.Combine("archive", "fresh.zip"), 2);

        new LoggingService(_dir).CleanupOldLogs(30);

        Assert.False(File.Exists(oldLog));
        Assert.False(File.Exists(oldZip));
        Assert.True(File.Exists(freshLog));
        Assert.True(File.Exists(freshZip));
    }

    [Fact]
    public void A_file_just_inside_the_window_survives_and_one_just_outside_goes()
    {
        var inside = Touch("inside.log", 29);
        var outside = Touch("outside.log", 31);

        new LoggingService(_dir).CleanupOldLogs(30);

        Assert.True(File.Exists(inside));
        Assert.False(File.Exists(outside));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_non_positive_retention_deletes_nothing(int days)
    {
        var old = Touch("2020-01-01.log", 400);

        new LoggingService(_dir).CleanupOldLogs(days);

        Assert.True(File.Exists(old));
    }

    [Fact]
    public void A_huge_retention_value_neither_throws_nor_deletes_recent_logs()
    {
        var recent = Touch("recent.log", 1);

        new LoggingService(_dir).CleanupOldLogs(int.MaxValue);

        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Other_file_types_and_a_missing_archive_folder_are_left_alone()
    {
        var note = Touch("notes.txt", 400);

        // no archive folder exists at all: the sweep must just return
        new LoggingService(_dir).CleanupOldLogs(30);

        Assert.True(File.Exists(note));
    }

    [Fact]
    public void A_locked_old_log_is_skipped_without_throwing_and_the_rest_still_goes()
    {
        var locked = Touch("locked.log", 400);
        var other = Touch("other.log", 400);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            new LoggingService(_dir).CleanupOldLogs(30);

        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(other));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
