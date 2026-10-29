using System.Text;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// Reading history.jsonl line-by-line must cap a single line's length: otherwise a corrupt file
/// with one giant unterminated "line" (or no newline at all) gets buffered whole before the JSON parser's
/// own error path can ever run. <see cref="HistoryService.ReadLinesBounded"/> is the pure part of that
/// guard and is exercised directly here; the JSONL parsing around it is unchanged.
/// </summary>
public class HistoryServiceTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("wu-history-test-").FullName;

    [Fact]
    public void An_oversized_line_yields_null_and_scanning_resumes_on_the_next_line()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var oversized = new string('x', 200_000);
        File.WriteAllText(path, $"first\n{oversized}\nlast\n");

        var lines = HistoryService.ReadLinesBounded(path, Encoding.UTF8, maxLineChars: 64 * 1024).ToList();

        Assert.Equal(["first", null, "last"], lines);
    }

    private static HistoryEntry Install(string from, string to, bool unchanged = true, bool success = true,
        string source = "winget", string id = "Vendor.App") => new()
    {
        Timestamp = DateTimeOffset.Now, SourceId = source, ItemId = id, Name = id,
        FromVersion = from, ToVersion = to, Success = success, VersionUnchanged = unchanged,
    };

    [Fact]
    public void Unchanged_installs_are_counted_per_package_and_version_pair()
    {
        List<HistoryEntry> entries =
        [
            Install("1.0", "2.0"),
            Install("1.0", "2.0"),
            Install("1.0", "3.0"),                       // another target
            Install("1.0", "2.0", unchanged: false),     // a normal success
            Install("1.0", "2.0", success: false, unchanged: false),
            Install("1.0", "2.0", id: "Other.App"),
            Install("1.0", "2.0", source: "npm"),
        ];

        Assert.Equal(2, HistoryService.CountUnchangedInstalls(entries, "winget", "Vendor.App", "1.0", "2.0"));
        Assert.Equal(1, HistoryService.CountUnchangedInstalls(entries, "WINGET", "vendor.app", "1.0", "3.0"));
        Assert.Equal(0, HistoryService.CountUnchangedInstalls(entries, "winget", "Vendor.App", "0.9", "2.0"));
    }

    [Fact]
    public void Ordinary_lines_read_normally()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        File.WriteAllText(path, "one\ntwo\nthree\n");

        var lines = HistoryService.ReadLinesBounded(path, Encoding.UTF8, maxLineChars: 64 * 1024).ToList();

        Assert.Equal(["one", "two", "three"], lines);
    }

    [Fact]
    public void A_file_with_no_trailing_newline_still_yields_its_last_line()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        File.WriteAllText(path, "one\ntwo");

        var lines = HistoryService.ReadLinesBounded(path, Encoding.UTF8, maxLineChars: 64 * 1024).ToList();

        Assert.Equal(["one", "two"], lines);
    }

    [Fact]
    public void An_entirely_oversized_file_with_no_newline_at_all_yields_one_null_not_a_giant_string()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        File.WriteAllText(path, new string('x', 200_000));

        var lines = HistoryService.ReadLinesBounded(path, Encoding.UTF8, maxLineChars: 64 * 1024).ToList();

        Assert.Equal([null], lines);
    }

    // ---- stored error detail ----

    [Fact]
    public void A_failed_entrys_detail_survives_a_write_and_read_round_trip()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        service.Append(new HistoryEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            SourceId = "winget", ItemId = "pkg", Name = "pkg",
            Success = false, Error = "Installation fehlgeschlagen.", Detail = "exit code 1603\nfull tool output",
        });

        var reloaded = new HistoryService(new LoggingService(), path).Read();

        Assert.Single(reloaded);
        Assert.Equal("exit code 1603\nfull tool output", reloaded[0].Detail);
    }

    /// <summary>An entry written before Detail existed has no such property in its JSON line at all,
    /// so deserialization must not choke on it, and Detail must read back as null, not throw.</summary>
    [Fact]
    public void An_entry_written_before_detail_existed_still_loads_with_a_null_detail()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        File.WriteAllText(path,
            """{"timestamp":"2026-01-01T00:00:00+00:00","sourceId":"winget","itemId":"pkg","name":"pkg","success":false,"error":"failed"}""" + "\n");

        var reloaded = new HistoryService(new LoggingService(), path).Read();

        Assert.Single(reloaded);
        Assert.Null(reloaded[0].Detail);
        Assert.Equal("failed", reloaded[0].Error);
    }

    [Fact]
    public void A_stored_detail_is_redacted_before_it_reaches_disk()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        service.Append(new HistoryEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            SourceId = "winget", ItemId = "pkg", Name = "pkg",
            Success = false, Error = "failed", Detail = $"reading {home}\\AppData\\config.json",
        });

        var onDisk = File.ReadAllText(path);

        Assert.DoesNotContain(home, onDisk, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", onDisk);
    }

    /// <summary>Error used to reach disk exactly as given, Detail already went through
    /// <see cref="HistoryService.CapDetail"/>; a caller that (as several real call sites do) puts raw tool
    /// output straight into Error rather than Detail must get the same redaction and cap.</summary>
    [Fact]
    public void A_stored_error_is_redacted_and_capped_before_it_reaches_disk()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var longError = new string('e', 5000) + $" reading {home}\\AppData\\config.json";

        service.Append(new HistoryEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            SourceId = "winget", ItemId = "pkg", Name = "pkg",
            Success = false, Error = longError,
        });

        var onDisk = File.ReadAllText(path);
        Assert.DoesNotContain(home, onDisk, StringComparison.OrdinalIgnoreCase);

        var reloaded = new HistoryService(new LoggingService(), path).Read();
        Assert.Single(reloaded);
        Assert.True(reloaded[0].Error!.Length <= 4000);
    }

    [Fact]
    public void An_oversized_detail_keeps_the_head_and_the_tail_with_a_marker_between_them()
    {
        var head = new string('a', 5000);
        var tail = new string('b', 5000);

        var capped = HistoryService.CapDetail(head + tail)!;

        Assert.True(capped.Length <= 4000);
        Assert.StartsWith("a", capped);
        Assert.EndsWith("b", capped);
        Assert.Contains("truncated", capped);
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

    // ---- retention cleanup, run once at start-up ----

    private static HistoryEntry Entry(DateTimeOffset timestamp, string id = "pkg") => new()
    {
        Timestamp = timestamp,
        SourceId = "winget",
        ItemId = id,
        Name = id,
        Success = true,
    };

    /// <summary>Retention has to actually discriminate: a mix of old and new rows, and only the old one
    /// is gone afterwards.</summary>
    [Fact]
    public void CleanupOldEntries_drops_rows_older_than_the_retention_window_and_keeps_the_rest()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        service.Append(Entry(DateTimeOffset.UtcNow.AddDays(-40), "old-pkg"));
        service.Append(Entry(DateTimeOffset.UtcNow.AddDays(-1), "recent-pkg"));

        service.CleanupOldEntries(retentionDays: 30);

        var reloaded = new HistoryService(new LoggingService(), path).Read();
        Assert.Single(reloaded);
        Assert.Equal("recent-pkg", reloaded[0].ItemId);
    }

    [Fact]
    public void CleanupOldEntries_with_nothing_old_enough_leaves_the_file_untouched()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        service.Append(Entry(DateTimeOffset.UtcNow.AddDays(-1)));
        var before = File.ReadAllText(path);

        service.CleanupOldEntries(retentionDays: 30);

        Assert.Equal(before, File.ReadAllText(path));
    }

    /// <summary>A prune whose delete of the rotated file failed leaves its rows in both files; the next
    /// prune must fold them back into one copy instead of keeping the duplicate for good.</summary>
    [Fact]
    public void CleanupOldEntries_folds_rows_left_in_both_files_back_into_one()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var shared = Entry(DateTimeOffset.UtcNow.AddDays(-2), "shared-pkg");
        new HistoryService(new LoggingService(), path + ".1").Append(shared);
        var service = new HistoryService(new LoggingService(), path);
        service.Append(shared);
        service.Append(Entry(DateTimeOffset.UtcNow.AddDays(-1), "recent-pkg"));

        service.CleanupOldEntries(retentionDays: 30);

        var reloaded = new HistoryService(new LoggingService(), path).Read();
        Assert.Equal(["recent-pkg", "shared-pkg"], reloaded.Select(e => e.ItemId).Order().ToArray());
    }

    [Fact]
    public void CleanupOldEntries_also_prunes_the_rotated_file_and_consolidates_it_into_the_active_one()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");

        // A HistoryService pointed straight at the ".1" name writes there (the same Append production
        // code uses), without needing 5 MB of real data to trigger an actual rotation.
        new HistoryService(new LoggingService(), path + ".1").Append(Entry(DateTimeOffset.UtcNow.AddDays(-40), "old-pkg"));
        var service = new HistoryService(new LoggingService(), path);
        service.Append(Entry(DateTimeOffset.UtcNow.AddDays(-1), "recent-pkg"));

        service.CleanupOldEntries(retentionDays: 30);

        Assert.False(File.Exists(path + ".1"));
        var reloaded = new HistoryService(new LoggingService(), path).Read();
        Assert.Single(reloaded);
        Assert.Equal("recent-pkg", reloaded[0].ItemId);
    }

    /// <summary>
    /// A hand-edited "historyRetentionDays": 0 puts the cutoff at "now" and would discard every row ever
    /// recorded, including the one written seconds earlier. Same guard as the log housekeeping.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CleanupOldEntries_with_a_non_positive_retention_deletes_nothing(int retentionDays)
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        service.Append(Entry(DateTimeOffset.UtcNow.AddDays(-400), "ancient-pkg"));
        service.Append(Entry(DateTimeOffset.UtcNow.AddMinutes(-1), "fresh-pkg"));
        var before = File.ReadAllText(path);

        service.CleanupOldEntries(retentionDays);

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(2, new HistoryService(new LoggingService(), path).Read().Count);
    }

    /// <summary>
    /// The rotation writes the survivors somewhere else and replaces the active file with one move, so
    /// there is no moment in which the active file is half-written and the rotated one already gone.
    /// The temp file must not survive the run either.
    /// </summary>
    [Fact]
    public void CleanupOldEntries_replaces_the_active_file_atomically_and_leaves_no_temp_file_behind()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        new HistoryService(new LoggingService(), path + ".1").Append(Entry(DateTimeOffset.UtcNow.AddDays(-40), "old-rotated"));
        var service = new HistoryService(new LoggingService(), path);
        service.Append(Entry(DateTimeOffset.UtcNow.AddDays(-40), "old-active"));
        service.Append(Entry(DateTimeOffset.UtcNow.AddDays(-2), "kept-a"));
        service.Append(Entry(DateTimeOffset.UtcNow.AddDays(-1), "kept-b"));

        // Holding the rotated file open the way an AV scanner briefly does makes its delete fail, the last
        // step of the sequence. The survivors must already be safe in the active file by then.
        using (new FileStream(path + ".1", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            service.CleanupOldEntries(retentionDays: 30);
        }

        Assert.False(File.Exists(path + ".tmp"));
        var survivors = File.ReadAllLines(path);
        Assert.Equal(2, survivors.Length);
        Assert.Contains("kept-a", survivors[0]);
        Assert.Contains("kept-b", survivors[1]);
    }

    // ---- LastSuccessfulInstallByItem: cached rollback-eligibility index ----

    private static HistoryEntry InstallEntry(
        DateTimeOffset timestamp, string itemId, string fromVersion, string toVersion,
        bool success = true, bool manualHandoff = false, bool notApplicable = false, bool cancelled = false) => new()
    {
        Timestamp = timestamp,
        SourceId = "winget",
        ItemId = itemId,
        Name = itemId,
        FromVersion = fromVersion,
        ToVersion = toVersion,
        Success = success,
        IsManualHandoff = manualHandoff,
        IsNotApplicable = notApplicable,
        IsCancelled = cancelled,
    };

    /// <summary>Only a genuine successful install with a real FromVersion counts, and the newest one
    /// per "{SourceId}:{ItemId}" wins.</summary>
    [Fact]
    public void Returns_the_newest_successful_install_per_item_and_skips_ineligible_entries()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        var now = DateTimeOffset.UtcNow;

        service.Append(InstallEntry(now.AddMinutes(-30), "pkg", "1.0", "1.1"));
        service.Append(InstallEntry(now.AddMinutes(-20), "pkg", "1.1", "1.2"));
        service.Append(InstallEntry(now.AddMinutes(-10), "pkg", "1.2", "1.3", success: false)); // failed, skipped
        service.Append(InstallEntry(now.AddMinutes(-5), "other", "2.0", "2.1", manualHandoff: true)); // skipped

        var index = service.LastSuccessfulInstallByItem();

        // The failed -10min entry must be skipped entirely, leaving the -20min one (to "1.2") as the
        // newest SUCCESSFUL install on record, not the older -30min one.
        var entry = Assert.Single(index.Values, e => e.ItemId == "pkg");
        Assert.Equal("1.2", entry.ToVersion);
        Assert.DoesNotContain(index.Values, e => e.ItemId == "other");
    }

    /// <summary>The whole point: a caller asking twice with nothing in between must not re-read and
    /// re-deserialise the file a second time.</summary>
    [Fact]
    public void A_second_call_with_no_change_in_between_returns_the_same_cached_instance()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        service.Append(InstallEntry(DateTimeOffset.UtcNow, "pkg", "1.0", "1.1"));

        var first = service.LastSuccessfulInstallByItem();
        var second = service.LastSuccessfulInstallByItem();

        Assert.Same(first, second);
    }

    /// <summary>Append must invalidate the cache: a rollback offered right after installing a NEWER version
    /// must target that new version, not whatever was newest before this install ran.</summary>
    [Fact]
    public void Append_invalidates_the_cache_so_the_next_call_sees_the_new_entry()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        service.Append(InstallEntry(DateTimeOffset.UtcNow.AddMinutes(-10), "pkg", "1.0", "1.1"));

        var before = service.LastSuccessfulInstallByItem();
        Assert.Equal("1.1", before["winget:pkg"].ToVersion);

        service.Append(InstallEntry(DateTimeOffset.UtcNow, "pkg", "1.1", "1.2"));
        var after = service.LastSuccessfulInstallByItem();

        Assert.Equal("1.2", after["winget:pkg"].ToVersion);
    }

    /// <summary>CleanupOldEntries must invalidate the cache too: once the only successful install on record
    /// for a package has aged out, it must no longer be offered as a rollback target.</summary>
    [Fact]
    public void CleanupOldEntries_invalidates_the_cache_for_a_pruned_entry()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        service.Append(InstallEntry(DateTimeOffset.UtcNow.AddDays(-40), "pkg", "1.0", "1.1"));

        Assert.Contains("winget:pkg", service.LastSuccessfulInstallByItem().Keys);

        service.CleanupOldEntries(retentionDays: 30);

        Assert.DoesNotContain("winget:pkg", service.LastSuccessfulInstallByItem().Keys);
    }

    /// <summary>Read() stops at the newest 500 rows, which is right for the window and wrong for rollback
    /// eligibility: a package whose only successful install is older than that must still be found.</summary>
    [Fact]
    public void The_index_covers_the_whole_history_not_only_the_newest_500_rows()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        var start = DateTimeOffset.UtcNow.AddDays(-5);

        service.Append(InstallEntry(start, "oldest", "1.0", "1.1"));
        for (var i = 1; i < 600; i++)
            service.Append(InstallEntry(start.AddMinutes(i), $"filler-{i}", "1.0", "1.1", success: false));

        Assert.DoesNotContain(service.Read(), e => e.ItemId == "oldest");

        var index = new HistoryService(new LoggingService(), path).LastSuccessfulInstallByItem();

        Assert.Contains("winget:oldest", index.Keys);
    }

    [Fact]
    public void The_index_also_covers_the_rotated_file()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var writer = new HistoryService(new LoggingService(), path);
        writer.Append(InstallEntry(DateTimeOffset.UtcNow.AddDays(-3), "rotated", "1.0", "1.1"));
        File.Move(path, path + ".1");
        writer.Append(InstallEntry(DateTimeOffset.UtcNow.AddDays(-1), "active", "2.0", "2.1"));

        var index = new HistoryService(new LoggingService(), path).LastSuccessfulInstallByItem();

        Assert.Contains("winget:rotated", index.Keys);
        Assert.Contains("winget:active", index.Keys);
    }

    [Fact]
    public void Nothing_is_built_until_asked_and_the_peek_never_parses()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        service.Append(InstallEntry(DateTimeOffset.UtcNow, "pkg", "1.0", "1.1"));

        Assert.Null(service.RollbackIndex);
        Assert.Equal(0, service.RollbackIndexBuilds);
    }

    /// <summary>An append used to throw the index away, so the next reader re-parsed the whole file.</summary>
    [Fact]
    public void Append_updates_a_built_index_without_reading_the_files_again()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        service.Append(InstallEntry(DateTimeOffset.UtcNow.AddMinutes(-10), "pkg", "1.0", "1.1"));
        var built = service.LastSuccessfulInstallByItem();
        Assert.Equal(1, service.RollbackIndexBuilds);

        service.Append(InstallEntry(DateTimeOffset.UtcNow, "pkg", "1.1", "1.2"));
        service.Append(InstallEntry(DateTimeOffset.UtcNow, "fresh", "5.0", "5.1"));
        service.Append(InstallEntry(DateTimeOffset.UtcNow, "failed", "1.0", "1.1", success: false));

        Assert.Equal(1, service.RollbackIndexBuilds);
        Assert.Same(built, service.RollbackIndex);
        Assert.Equal("1.2", built["winget:pkg"].ToVersion);
        Assert.Contains("winget:fresh", built.Keys);
        Assert.DoesNotContain("winget:failed", built.Keys);
    }

    [Fact]
    public void Cleanup_refreshes_a_built_index_from_the_survivors_without_another_read()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        service.Append(InstallEntry(DateTimeOffset.UtcNow.AddDays(-40), "old", "1.0", "1.1"));
        service.Append(InstallEntry(DateTimeOffset.UtcNow.AddDays(-1), "recent", "1.0", "1.1"));
        _ = service.LastSuccessfulInstallByItem();
        Assert.Equal(1, service.RollbackIndexBuilds);

        service.CleanupOldEntries(retentionDays: 30);

        Assert.Equal(1, service.RollbackIndexBuilds);
        Assert.NotNull(service.RollbackIndex);
        Assert.DoesNotContain("winget:old", service.RollbackIndex!.Keys);
        Assert.Contains("winget:recent", service.RollbackIndex.Keys);
    }

    [Fact]
    public async Task EnsureRollbackIndexAsync_builds_on_another_thread_and_only_once()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        service.Append(InstallEntry(DateTimeOffset.UtcNow, "pkg", "1.0", "1.1"));
        var callerThread = Environment.CurrentManagedThreadId;
        var builderThreads = new List<int>();
        service.RollbackIndexBuildObserver = () => builderThreads.Add(Environment.CurrentManagedThreadId);

        var first = await service.EnsureRollbackIndexAsync();
        var second = await service.EnsureRollbackIndexAsync();

        Assert.Same(first, second);
        var builder = Assert.Single(builderThreads);
        Assert.NotEqual(callerThread, builder);
    }

    // ---- a failed read must not become state ----

    private static HistoryEntry RollbackCandidate(string id) => new()
    {
        Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
        SourceId = "winget",
        ItemId = id,
        Name = id,
        Success = true,
        FromVersion = "1.0",
        ToVersion = "2.0",
    };

    [Fact]
    public void An_index_built_from_a_locked_history_file_is_not_cached_and_the_next_call_rebuilds_it()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        var service = new HistoryService(new LoggingService(), path);
        service.Append(RollbackCandidate("pkg"));

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = service.LastSuccessfulInstallByItem();
            Assert.Empty(failed);
            Assert.Null(service.RollbackIndex);
        }

        var rebuilt = service.LastSuccessfulInstallByItem();
        Assert.Contains("winget:pkg", rebuilt.Keys);
        Assert.NotNull(service.RollbackIndex);
        Assert.Equal(2, service.RollbackIndexBuilds);
    }

    [Fact]
    public void A_locked_rotated_file_leaves_both_files_unchanged_when_the_retention_sweep_runs()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        new HistoryService(new LoggingService(), path + ".1").Append(Entry(DateTimeOffset.UtcNow.AddDays(-40), "old-pkg"));
        var service = new HistoryService(new LoggingService(), path);
        service.Append(Entry(DateTimeOffset.UtcNow.AddDays(-40), "older-pkg"));
        service.Append(Entry(DateTimeOffset.UtcNow.AddDays(-1), "recent-pkg"));
        var activeBefore = File.ReadAllText(path);
        var rotatedBefore = File.ReadAllText(path + ".1");

        using (new FileStream(path + ".1", FileMode.Open, FileAccess.Read, FileShare.None))
            service.CleanupOldEntries(retentionDays: 30);

        Assert.Equal(activeBefore, File.ReadAllText(path));
        Assert.Equal(rotatedBefore, File.ReadAllText(path + ".1"));
    }

    [Fact]
    public void A_locked_active_file_leaves_the_rotated_file_in_place_when_the_retention_sweep_runs()
    {
        var path = Path.Combine(_tempDir, "history.jsonl");
        new HistoryService(new LoggingService(), path + ".1").Append(Entry(DateTimeOffset.UtcNow.AddDays(-40), "old-pkg"));
        var service = new HistoryService(new LoggingService(), path);
        service.Append(Entry(DateTimeOffset.UtcNow.AddDays(-1), "recent-pkg"));
        var rotatedBefore = File.ReadAllText(path + ".1");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            service.CleanupOldEntries(retentionDays: 30);

        Assert.Equal(rotatedBefore, File.ReadAllText(path + ".1"));
        Assert.Contains("recent-pkg", File.ReadAllText(path));
    }
}
