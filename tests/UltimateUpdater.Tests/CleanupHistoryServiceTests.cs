using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>Deleting history, one entry or all of them, has to actually rewrite the
/// file on disk, not just clear whatever is in memory. Real temp files, no mocking, same reasoning as
/// every other test in this suite that touches actual persistence.</summary>
public class CleanupHistoryServiceTests
{
    private static string TempHistoryPath() =>
        Path.Combine(Directory.CreateTempSubdirectory("wu-cleanup-history-").FullName, "history.json");

    private static CleanupHistoryEntry Entry(DateTimeOffset timestamp, long freedBytes = 100) => new()
    {
        Timestamp = timestamp,
        FreedBytes = freedBytes,
        Areas = [],
    };

    [Fact]
    public void Load_on_a_file_that_does_not_exist_yet_returns_an_empty_list()
    {
        var service = new CleanupHistoryService(new LoggingService(), TempHistoryPath());

        Assert.Empty(service.Load());
    }

    [Fact]
    public void Append_writes_the_entry_to_disk_and_a_fresh_read_sees_it()
    {
        var path = TempHistoryPath();
        var service = new CleanupHistoryService(new LoggingService(), path);
        var timestamp = DateTimeOffset.UtcNow;

        service.Append(Entry(timestamp, 42));

        Assert.True(File.Exists(path));
        var reloaded = new CleanupHistoryService(new LoggingService(), path).Load();
        Assert.Single(reloaded);
        Assert.Equal(timestamp, reloaded[0].Timestamp);
        Assert.Equal(42, reloaded[0].FreedBytes);
    }

    /// <summary>Same temp-file-then-move contract as SettingsService/HistoryService: a crash or an AV
    /// lock mid-write must never truncate the only record of what a cleanup removed, so the write must
    /// never touch the real file in place.</summary>
    [Fact]
    public void Append_leaves_no_tmp_file_behind_and_writes_exactly_the_expected_content()
    {
        var path = TempHistoryPath();
        var service = new CleanupHistoryService(new LoggingService(), path);
        var timestamp = DateTimeOffset.UtcNow;

        service.Append(Entry(timestamp, 99));

        Assert.False(File.Exists(path + ".tmp"));
        var reloaded = service.Load();
        Assert.Single(reloaded);
        Assert.Equal(99, reloaded[0].FreedBytes);
    }

    [Fact]
    public void Append_puts_the_newest_entry_first()
    {
        var path = TempHistoryPath();
        var service = new CleanupHistoryService(new LoggingService(), path);
        var older = DateTimeOffset.UtcNow.AddDays(-2);
        var newer = DateTimeOffset.UtcNow;

        service.Append(Entry(older));
        service.Append(Entry(newer));

        var loaded = service.Load();
        Assert.Equal(newer, loaded[0].Timestamp);
        Assert.Equal(older, loaded[1].Timestamp);
    }

    /// <summary>A history file that no longer parses must not simply be overwritten by the next run's
    /// entry: every earlier run would be gone for good. It is kept aside, and the new run still lands.</summary>
    [Fact]
    public void Append_over_a_corrupt_file_keeps_the_old_file_aside()
    {
        var path = TempHistoryPath();
        const string corrupt = "[{\"timestamp\": \"2026-01-01T00:00:00+00:00\", \"freedBytes\": 1";
        File.WriteAllText(path, corrupt);
        var service = new CleanupHistoryService(new LoggingService(), path);
        var timestamp = DateTimeOffset.UtcNow;

        service.Append(Entry(timestamp, 7));

        var reloaded = new CleanupHistoryService(new LoggingService(), path).Load();
        Assert.Equal(timestamp, Assert.Single(reloaded).Timestamp);
        var aside = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "history.corrupt-*.json"));
        Assert.Equal(corrupt, File.ReadAllText(aside));
    }

    /// <summary>The service-layer half of the deletion contract: the file on disk actually changes.</summary>
    [Fact]
    public void Remove_deletes_only_the_matching_entry_and_the_file_on_disk_reflects_it()
    {
        var path = TempHistoryPath();
        var service = new CleanupHistoryService(new LoggingService(), path);
        var keep = DateTimeOffset.UtcNow.AddDays(-1);
        var drop = DateTimeOffset.UtcNow;

        service.Append(Entry(keep));
        service.Append(Entry(drop));

        service.Remove(drop);

        var reloaded = new CleanupHistoryService(new LoggingService(), path).Load();
        Assert.Single(reloaded);
        Assert.Equal(keep, reloaded[0].Timestamp);
    }

    [Fact]
    public void Remove_with_an_unknown_timestamp_does_nothing_and_does_not_throw()
    {
        var path = TempHistoryPath();
        var service = new CleanupHistoryService(new LoggingService(), path);
        service.Append(Entry(DateTimeOffset.UtcNow));

        service.Remove(DateTimeOffset.UtcNow.AddYears(-1));

        Assert.Single(service.Load());
    }

    /// <summary>The empty state has to be reachable: the file genuinely stops holding anything rather
    /// than becoming an empty-but-present JSON array forever.</summary>
    [Fact]
    public void Clear_removes_the_file_so_a_fresh_read_is_empty()
    {
        var path = TempHistoryPath();
        var service = new CleanupHistoryService(new LoggingService(), path);
        service.Append(Entry(DateTimeOffset.UtcNow));
        Assert.True(File.Exists(path));

        service.Clear();

        Assert.False(File.Exists(path));
        Assert.Empty(new CleanupHistoryService(new LoggingService(), path).Load());
    }

    /// <summary>A defragmentation area's title carries a formatting argument (the drive display
    /// name), and it must round-trip through a real save/load, not just survive in memory.</summary>
    [Fact]
    public void A_defrag_area_with_a_title_argument_round_trips_through_disk()
    {
        var path = TempHistoryPath();
        var service = new CleanupHistoryService(new LoggingService(), path);
        var entry = new CleanupHistoryEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            FreedBytes = 0,
            Areas =
            [
                new CleanupHistoryArea
                {
                    Id = "defrag-e",
                    TitleKey = "CleanupDefragRowHdd",
                    TitleArguments = ["Games E:\\"],
                },
            ],
        };

        service.Append(entry);

        var reloaded = new CleanupHistoryService(new LoggingService(), path).Load();
        var area = Assert.Single(Assert.Single(reloaded).Areas);
        Assert.Equal(["Games E:\\"], area.TitleArguments);
    }

    /// <summary>An area written before this field existed (or any ordinary, statically-titled area) must
    /// still load as an empty list, never null or a crash.</summary>
    [Fact]
    public void An_area_with_no_title_arguments_loads_as_an_empty_list()
    {
        var path = TempHistoryPath();
        var service = new CleanupHistoryService(new LoggingService(), path);
        service.Append(new CleanupHistoryEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Areas = [new CleanupHistoryArea { Id = "user-temp", TitleKey = "CleanupUserTempName" }],
        });

        var reloaded = new CleanupHistoryService(new LoggingService(), path).Load();
        Assert.All(reloaded.SelectMany(e => e.Areas), area => Assert.Empty(area.TitleArguments));
    }

    /// <summary>A file past the cap is read as corrupt even when its content would parse: it is kept aside
    /// and a new history starts, instead of the whole file being pulled into memory.</summary>
    [Fact]
    public void An_oversized_history_file_is_kept_aside_and_a_new_history_started()
    {
        var path = TempHistoryPath();
        var directory = Path.GetDirectoryName(path)!;
        File.WriteAllText(path, "[" + new string(' ', (int)CleanupHistoryService.MaxFileBytes + 1000) + "]");
        var service = new CleanupHistoryService(new LoggingService(), path);

        Assert.Empty(service.Load());

        service.Append(Entry(DateTimeOffset.UtcNow, 7));

        Assert.Single(Directory.GetFiles(directory, "*.corrupt-*.json"));
        Assert.Single(service.Load());
    }
}
