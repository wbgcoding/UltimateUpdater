using System.IO;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// The data folder copy runs while the app keeps writing its history and reports to the old folder until
/// the restart. The sync that follows the copy has to leave the new folder equal to the old one for those.
/// </summary>
public class DataFolderLiveSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-livesync-" + Guid.NewGuid());
    private readonly string _source;
    private readonly string _destination;

    public DataFolderLiveSyncTests()
    {
        _source = Path.Combine(_root, "old");
        _destination = Path.Combine(_root, "new");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_destination);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void History_written_after_the_copy_reaches_the_new_folder()
    {
        File.WriteAllText(Path.Combine(_destination, "history.jsonl"), "one\n");
        File.WriteAllText(Path.Combine(_source, "history.jsonl"), "one\ntwo\n");
        File.WriteAllText(Path.Combine(_source, "cleanup-history.json"), "[1]");

        Assert.True(AppPaths.SyncLiveData(_source, _destination, includeSettings: false));

        Assert.Equal("one\ntwo\n", File.ReadAllText(Path.Combine(_destination, "history.jsonl")));
        Assert.Equal("[1]", File.ReadAllText(Path.Combine(_destination, "cleanup-history.json")));
    }

    [Fact]
    public void A_rotated_history_the_pruning_removed_is_gone_from_the_new_folder_too()
    {
        File.WriteAllText(Path.Combine(_destination, "history.jsonl.1"), "old\n");
        File.WriteAllText(Path.Combine(_source, "history.jsonl"), "merged\n");

        Assert.True(AppPaths.SyncLiveData(_source, _destination, includeSettings: false));

        Assert.False(File.Exists(Path.Combine(_destination, "history.jsonl.1")));
    }

    [Fact]
    public void Reports_follow_in_both_directions()
    {
        Directory.CreateDirectory(Path.Combine(_destination, "reports"));
        File.WriteAllText(Path.Combine(_destination, "reports", "deleted-meanwhile.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_source, "reports"));
        File.WriteAllText(Path.Combine(_source, "reports", "new.txt"), "y");

        Assert.True(AppPaths.SyncLiveData(_source, _destination, includeSettings: false));

        Assert.True(File.Exists(Path.Combine(_destination, "reports", "new.txt")));
        Assert.False(File.Exists(Path.Combine(_destination, "reports", "deleted-meanwhile.txt")));
    }

    [Fact]
    public void Other_files_in_the_new_folder_are_left_alone()
    {
        File.WriteAllText(Path.Combine(_destination, "enrichment-cache.json"), "cache");
        File.WriteAllText(Path.Combine(_source, "history.jsonl"), "h");

        Assert.True(AppPaths.SyncLiveData(_source, _destination, includeSettings: false));

        Assert.Equal("cache", File.ReadAllText(Path.Combine(_destination, "enrichment-cache.json")));
    }

    [Fact]
    public void Settings_are_only_synced_when_asked_for()
    {
        File.WriteAllText(Path.Combine(_destination, "settings.json"), "from the move");
        File.WriteAllText(Path.Combine(_source, "settings.json"), "from the exit");

        Assert.True(AppPaths.SyncLiveData(_source, _destination, includeSettings: false));
        Assert.Equal("from the move", File.ReadAllText(Path.Combine(_destination, "settings.json")));

        Assert.True(AppPaths.SyncLiveData(_source, _destination, includeSettings: true));
        Assert.Equal("from the exit", File.ReadAllText(Path.Combine(_destination, "settings.json")));
    }

    [Fact]
    public void A_file_that_cannot_be_overwritten_makes_the_sync_report_failure()
    {
        File.WriteAllText(Path.Combine(_source, "history.jsonl"), "new");
        var target = Path.Combine(_destination, "history.jsonl");
        File.WriteAllText(target, "old");

        using var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(AppPaths.SyncLiveData(_source, _destination, includeSettings: false));
    }
}
