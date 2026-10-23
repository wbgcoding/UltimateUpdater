using System.IO;
using Microsoft.Win32;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// "Delete my data" has to take everything the app wrote about the user: the unreadable copies it set
/// aside, the crash note in the temp folder and the autostart entry, not only the known file names.
/// </summary>
public class AppDataCleanerLeftoverTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wu-leftover-test-").FullName;
    private readonly string _temp = Directory.CreateTempSubdirectory("wu-leftover-temp-").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_temp, recursive: true);
    }

    private static string Touch(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Unreadable_copies_the_app_set_aside_are_deleted_and_look_alikes_are_kept()
    {
        var history = Touch(_dir, "cleanup-history.corrupt-20260102-030405.json");
        var settings = Touch(_dir, "settings.json.corrupt-20260102-030405");
        var settingsKept = Touch(_dir, "settings.json");
        var stranger = Touch(_dir, "cleanup-history.corrupt-notatime.json");
        var other = Touch(_dir, "notes.corrupt-20260102-030405");

        var failed = AppDataCleaner.DeleteOwnedContent(_dir, new LoggingService());

        Assert.False(failed);
        Assert.False(File.Exists(history));
        Assert.False(File.Exists(settings));
        Assert.True(File.Exists(settingsKept));
        Assert.True(File.Exists(stranger));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void The_crash_note_in_the_temp_folder_is_deleted_and_other_temp_files_are_kept()
    {
        var note = Touch(_temp, AppDataCleaner.CrashNoteName);
        var other = Touch(_temp, "unrelated.log");

        var failed = AppDataCleaner.DeleteOwnedContent(_dir, new LoggingService(), _temp);

        Assert.False(failed);
        Assert.False(File.Exists(note));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void The_autostart_entry_is_removed()
    {
        var keyPath = $@"Software\UltimateUpdaterTest-{Guid.NewGuid():N}";
        var logging = new LoggingService();
        var autostart = new AutostartService(logging, keyPath);
        try
        {
            Assert.True(autostart.TrySet(true));
            Assert.True(autostart.IsEnabled());

            AppDataCleaner.DeleteOwnedContent(_dir, logging, autostart: autostart);

            Assert.False(autostart.IsEnabled());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }
}
