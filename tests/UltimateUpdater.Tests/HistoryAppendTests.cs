using UltimateUpdater.Models;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>The write side of history.jsonl: what an entry records, size rollover, and a file that cannot be written.</summary>
public sealed class HistoryAppendTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uu-hist-append-").FullName;

    private string HistoryPath => Path.Combine(_dir, "history.jsonl");

    private static UpdateItem Item(bool rollback = false) => new()
    {
        Id = "Vendor.App",
        SourceId = "winget",
        Category = UpdateCategory.Software,
        Name = "App",
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
        DownloadSize = 1234,
        IsRollback = rollback,
    };

    private static HistoryEntry Plain(string id) => new()
    {
        Timestamp = DateTimeOffset.Now, SourceId = "winget", ItemId = id, Name = id, Success = true,
    };

    [Fact]
    public void An_install_entry_records_the_target_version_size_and_action()
    {
        var entry = HistoryEntry.ForItem(Item(), InstallAction.Install, success: true, error: null);

        Assert.Equal("install", entry.Action);
        Assert.Equal("1.0", entry.FromVersion);
        Assert.Equal("2.0", entry.ToVersion);
        Assert.Equal(1234, entry.SizeBytes);
        Assert.True(entry.Success);
        Assert.False(entry.IsRollback);
    }

    [Fact]
    public void An_uninstall_entry_has_no_target_version()
    {
        var entry = HistoryEntry.ForItem(Item(), InstallAction.Uninstall, success: true, error: null);

        Assert.Equal("uninstall", entry.Action);
        Assert.Equal("1.0", entry.FromVersion);
        Assert.Null(entry.ToVersion);
    }

    [Fact]
    public void A_fresh_install_after_a_reinstall_is_recorded_as_an_install()
    {
        var entry = HistoryEntry.ForItem(Item(), InstallAction.FreshInstall, success: true, error: null);

        Assert.Equal("install", entry.Action);
        Assert.Equal("2.0", entry.ToVersion);
    }

    [Fact]
    public void The_outcome_flags_and_error_are_carried_over()
    {
        var entry = HistoryEntry.ForItem(Item(), InstallAction.Install, success: false, error: "boom",
            isManualHandoff: true, isNotApplicable: true, detail: "tool output", isCancelled: true, versionUnchanged: true);

        Assert.False(entry.Success);
        Assert.Equal("boom", entry.Error);
        Assert.Equal("tool output", entry.Detail);
        Assert.True(entry.IsManualHandoff);
        Assert.True(entry.IsNotApplicable);
        Assert.True(entry.IsCancelled);
        Assert.True(entry.VersionUnchanged);
    }

    [Fact]
    public void A_rollback_item_is_recorded_as_a_rollback()
    {
        var entry = HistoryEntry.ForItem(Item(rollback: true), InstallAction.Install, true, null);

        Assert.True(entry.IsRollback);
    }

    [Fact]
    public void Read_returns_the_newest_entries_first_and_honours_the_cap()
    {
        var service = new HistoryService(new LoggingService(), HistoryPath);
        foreach (var id in new[] { "a", "b", "c", "d" })
            service.Append(Plain(id));

        Assert.Equal(["d", "c", "b", "a"], service.Read().Select(e => e.ItemId));
        Assert.Equal(["d", "c"], service.Read(maxEntries: 2).Select(e => e.ItemId));
    }

    [Fact]
    public void An_oversized_active_file_rolls_into_the_dot_one_file_and_nothing_is_lost()
    {
        var service = new HistoryService(new LoggingService(), HistoryPath);
        service.Append(Plain("before-roll"));
        // grow the active file past the 5 MB rollover with one well-formed padding line per entry
        using (var stream = new FileStream(HistoryPath, FileMode.Append))
        using (var writer = new StreamWriter(stream))
        {
            var pad = new string('x', 900);
            for (var i = 0; i < 6000; i++)
                writer.WriteLine($"{{\"timestamp\":\"2020-01-01T00:00:00+00:00\",\"sourceId\":\"s\",\"itemId\":\"pad{i}\",\"name\":\"{pad}\",\"success\":true}}");
        }

        Assert.True(new FileInfo(HistoryPath).Length > 5 * 1024 * 1024);

        service.Append(Plain("after-roll"));

        Assert.True(File.Exists(HistoryPath + ".1"));
        Assert.True(new FileInfo(HistoryPath).Length < 5 * 1024);
        var all = service.Read(int.MaxValue);
        Assert.Equal("after-roll", all[0].ItemId);
        Assert.Contains(all, e => e.ItemId == "before-roll");
        Assert.Equal(6002, all.Count);
    }

    [Fact]
    public void A_rollover_that_cannot_move_the_file_still_records_the_new_entry()
    {
        var service = new HistoryService(new LoggingService(), HistoryPath);
        File.WriteAllBytes(HistoryPath, new byte[5 * 1024 * 1024 + 10]);
        File.WriteAllText(HistoryPath + ".1", "archive");

        // the archive is held open by a scanner or an editor, so the replacing move fails
        using (new FileStream(HistoryPath + ".1", FileMode.Open, FileAccess.Read, FileShare.None))
            service.Append(Plain("kept"));

        Assert.Contains("\"itemId\":\"kept\"", File.ReadAllText(HistoryPath));
        Assert.Equal("archive", File.ReadAllText(HistoryPath + ".1"));
    }

    [Fact]
    public void An_unwritable_history_file_is_logged_and_never_throws()
    {
        // a directory where the file should be: the append fails with an access error
        Directory.CreateDirectory(HistoryPath);
        var lines = new List<string>();
        var service = new HistoryService(new LoggingService(lines.Add), HistoryPath);

        service.Append(Plain("lost"));

        Assert.Contains(lines, l => l.Contains("Failed to append history entry"));
    }

    [Fact]
    public void A_locked_history_file_is_logged_and_never_throws()
    {
        File.WriteAllText(HistoryPath, "");
        var lines = new List<string>();
        var service = new HistoryService(new LoggingService(lines.Add), HistoryPath);

        using (new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None))
            service.Append(Plain("lost"));

        Assert.Contains(lines, l => l.Contains("Failed to append history entry"));
        Assert.DoesNotContain("lost", File.ReadAllText(HistoryPath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
