using System.IO;

namespace UltimateUpdater.Tests;

/// <summary>
/// Logs, the icon cache, error reports and crash dumps live in the local app data folder of an installed copy;
/// settings, history and the quarantine manifests stay in the roaming one, and a portable copy keeps everything
/// in its data folder. The one-time move from the old place never loses a file and never blocks a start.
/// </summary>
public sealed class LocalFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uu-local-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly string _local;

    public LocalFolderTests()
    {
        _data = Path.Combine(_root, "roaming", "UltimateUpdater");
        _local = Path.Combine(_root, "local", "UltimateUpdater");
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // a scanner may hold a file for a moment; the temp folder is not worth failing a test
        }
    }

    private string Write(string root, string relative, string content = "x")
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ----- path resolution

    [Fact]
    public void An_installed_copy_takes_a_folder_under_the_local_app_data()
    {
        var picked = AppPaths.PickLocalDirectory(installed: true, @"C:\Users\x\AppData\Local", @"C:\Users\x\AppData\Roaming\UltimateUpdater");

        Assert.Equal(@"C:\Users\x\AppData\Local\UltimateUpdater", picked);
    }

    [Fact]
    public void A_portable_copy_keeps_everything_in_its_data_folder()
    {
        var picked = AppPaths.PickLocalDirectory(installed: false, @"C:\Users\x\AppData\Local", @"D:\Tools\UU\data");

        Assert.Equal(@"D:\Tools\UU\data", picked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_a_known_local_app_data_the_data_folder_is_used(string? localAppData)
    {
        Assert.Equal(@"C:\data", AppPaths.PickLocalDirectory(installed: true, localAppData, @"C:\data"));
    }

    [Fact]
    public void Under_the_test_data_folder_logs_cache_and_reports_stay_inside_it()
    {
        Assert.StartsWith(AppPaths.DataDirectory, AppPaths.LocalDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(AppPaths.LocalDirectory, AppPaths.LogsDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(AppPaths.LocalDirectory, AppPaths.IconCacheDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(AppPaths.LocalDirectory, "reports"), ErrorReportBuilder.ReportsDirectory);
        Assert.False(AppPaths.LocalDirectoryIsSeparate);
    }

    // ----- migration

    [Fact]
    public void The_old_logs_cache_and_reports_move_to_the_local_folder()
    {
        Write(_data, @"logs\2026-01-01.log", "log");
        Write(_data, @"logs\archive\old.zip", "zip");
        Write(_data, @"cache\icons\a.png", "png");
        Write(_data, @"reports\r.txt", "report");
        Write(_data, "settings.json", "{}");
        Write(_data, "history.jsonl", "h");

        var (moved, skipped) = AppPaths.MigrateToLocalDirectory(_data, _local);

        Assert.Equal(4, moved);
        Assert.Equal(0, skipped);
        Assert.Equal("log", File.ReadAllText(Path.Combine(_local, "logs", "2026-01-01.log")));
        Assert.Equal("zip", File.ReadAllText(Path.Combine(_local, "logs", "archive", "old.zip")));
        Assert.Equal("png", File.ReadAllText(Path.Combine(_local, "cache", "icons", "a.png")));
        Assert.Equal("report", File.ReadAllText(Path.Combine(_local, "reports", "r.txt")));
        Assert.False(Directory.Exists(Path.Combine(_data, "logs")));
        Assert.False(Directory.Exists(Path.Combine(_data, "cache")));
        Assert.False(Directory.Exists(Path.Combine(_data, "reports")));

        // settings, history and the rest stay in the roaming folder
        Assert.True(File.Exists(Path.Combine(_data, "settings.json")));
        Assert.True(File.Exists(Path.Combine(_data, "history.jsonl")));
        Assert.False(File.Exists(Path.Combine(_local, "settings.json")));
    }

    [Fact]
    public void A_second_run_finds_nothing_to_do()
    {
        Write(_data, @"logs\a.log");
        AppPaths.MigrateToLocalDirectory(_data, _local);

        Assert.Equal((0, 0), AppPaths.MigrateToLocalDirectory(_data, _local));
    }

    [Fact]
    public void Nothing_to_move_is_not_an_error()
    {
        Assert.Equal((0, 0), AppPaths.MigrateToLocalDirectory(_data, _local));
    }

    [Fact]
    public void A_file_in_use_stays_and_everything_else_still_moves()
    {
        var locked = Write(_data, @"logs\today.log", "busy");
        Write(_data, @"logs\yesterday.log", "done");
        Write(_data, @"cache\icons\a.png", "png");

        (int Moved, int Skipped) result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            result = AppPaths.MigrateToLocalDirectory(_data, _local);

        Assert.Equal(2, result.Moved);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("busy", File.ReadAllText(locked));
        Assert.True(File.Exists(Path.Combine(_local, "logs", "yesterday.log")));
        Assert.True(File.Exists(Path.Combine(_local, "cache", "icons", "a.png")));
    }

    [Fact]
    public void A_name_that_already_exists_at_the_target_is_never_overwritten()
    {
        Write(_data, @"logs\same.log", "old");
        Write(_local, @"logs\same.log", "new");

        var (moved, skipped) = AppPaths.MigrateToLocalDirectory(_data, _local);

        Assert.Equal(0, moved);
        Assert.Equal(1, skipped);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_data, "logs", "same.log")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_local, "logs", "same.log")));
    }

    [Fact]
    public void A_target_that_cannot_be_created_skips_without_throwing()
    {
        Write(_data, @"logs\a.log");
        var blocker = Write(_root, @"local\blocked", "a file where the folder should be");

        var exception = Record.Exception(() => AppPaths.MigrateToLocalDirectory(_data, Path.Combine(blocker, "UltimateUpdater")));

        Assert.Null(exception);
        Assert.True(File.Exists(Path.Combine(_data, "logs", "a.log")));
    }

    // ----- crash dumps

    [Fact]
    public void Only_the_newest_three_dumps_are_kept_and_other_files_are_left_alone()
    {
        var dir = Path.Combine(_root, "dumps");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 5; i++)
        {
            var path = Write(dir, $"crash-2026010{i}-120000.dmp");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1 + i, 12, 0, 0, DateTimeKind.Utc));
        }

        var log = Write(dir, "2026-01-01.log");
        var other = Write(dir, "notes.dmp");

        CrashDumpWriter.Prune(dir, CrashDumpWriter.Keep);

        var left = Directory.GetFiles(dir, "crash-*.dmp").Select(f => Path.GetFileName(f)!).OrderBy(n => n).ToArray();
        Assert.Equal(["crash-20260102-120000.dmp", "crash-20260103-120000.dmp", "crash-20260104-120000.dmp"], left);
        Assert.True(File.Exists(log));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void A_crash_writes_a_real_minidump_and_keeps_three()
    {
        var dir = Path.Combine(_root, "dumps");
        string? last = null;
        for (var i = 0; i < 4; i++)
            last = CrashDumpWriter.TryWrite(dir, new DateTime(2026, 1, 1, 12, 0, i));

        Assert.NotNull(last);
        Assert.Equal(3, Directory.GetFiles(dir, "crash-*.dmp").Length);

        using var stream = File.OpenRead(last!);
        var signature = new byte[4];
        Assert.Equal(4, stream.Read(signature, 0, 4));
        Assert.Equal("MDMP", System.Text.Encoding.ASCII.GetString(signature));
    }

    [Fact]
    public void A_dump_that_cannot_be_written_returns_null_instead_of_throwing()
    {
        var blocker = Write(_root, "afile");

        Assert.Null(CrashDumpWriter.TryWrite(Path.Combine(blocker, "dumps")));
    }

    [Fact]
    public void The_crash_test_switch_exists_only_inside_a_debug_block()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory!.FullName, "src", "UltimateUpdater", "App.xaml.cs")).Replace("\r\n", "\n");

        var at = source.IndexOf("\"--crash-test\"", StringComparison.Ordinal);
        Assert.True(at > 0, "the switch is gone");
        Assert.Equal(at, source.LastIndexOf("\"--crash-test\"", StringComparison.Ordinal));

        var open = source.LastIndexOf("#if DEBUG", at, StringComparison.Ordinal);
        var closeBeforeSwitch = open < 0 ? -1 : source.IndexOf("#endif", open, StringComparison.Ordinal);
        Assert.True(open >= 0 && closeBeforeSwitch > at, "--crash-test must sit inside #if DEBUG");
    }

    // ----- delete my data, crash dialog

    [Fact]
    public void Delete_my_data_only_removes_the_exact_local_app_folder()
    {
        Assert.True(AppDataCleaner.IsDeletableLocalFolder(_local, _local + Path.DirectorySeparatorChar));
        Assert.False(AppDataCleaner.IsDeletableLocalFolder(Path.Combine(_root, "local"), _local));
        Assert.False(AppDataCleaner.IsDeletableLocalFolder(Path.Combine(_local, "logs"), _local));
        Assert.False(AppDataCleaner.IsDeletableLocalFolder(@"C:\", _local));
    }

    [Fact]
    public void The_crash_dialog_action_does_not_run_without_a_user()
    {
        var ran = false;
        var before = Dialogs.SuppressInteractiveDialogs;
        Dialogs.SuppressInteractiveDialogs = true;
        try
        {
            Dialogs.ErrorWithAction("h", "b", "details", "Open log folder", () => ran = true);
        }
        finally
        {
            Dialogs.SuppressInteractiveDialogs = before;
        }

        Assert.False(ran);
    }
}
