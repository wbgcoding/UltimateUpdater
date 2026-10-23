using System.IO;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

public class SelfUpdateStaleDownloadTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("wu-stale-download-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private string Create(string name, TimeSpan age)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private static string Name() => $"UltimateUpdater-{Guid.NewGuid():N}.exe";

    [Fact]
    public void An_old_download_is_deleted_and_a_recent_one_is_kept()
    {
        var old = Create(Name(), TimeSpan.FromDays(2));
        var recent = Create(Name(), TimeSpan.FromHours(1));

        SelfUpdateService.SweepStaleDownloads(_directory, TimeSpan.FromHours(24), new LoggingService());

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Only_the_exact_name_pattern_is_touched()
    {
        var guid = Guid.NewGuid().ToString("N");
        var kept = new[]
        {
            Create("UltimateUpdater-x.exe", TimeSpan.FromDays(9)),
            Create($"UltimateUpdater-{guid}.bin", TimeSpan.FromDays(9)),
            Create($"UltimateUpdater-{guid[..31]}.exe", TimeSpan.FromDays(9)),
            Create($"UltimateUpdater-{guid}0.exe", TimeSpan.FromDays(9)),
            Create($"UltimateUpdater-{new string('g', 32)}.exe", TimeSpan.FromDays(9)),
            Create($"Other-{guid}.exe", TimeSpan.FromDays(9)),
            Create($"UltimateUpdater-{Guid.NewGuid():D}.exe", TimeSpan.FromDays(9)),
            Create("report.txt", TimeSpan.FromDays(9)),
        };

        SelfUpdateService.SweepStaleDownloads(_directory, TimeSpan.FromHours(24), new LoggingService());

        Assert.All(kept, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void A_folder_with_a_matching_name_is_left_alone()
    {
        var folder = Path.Combine(_directory, Name());
        Directory.CreateDirectory(folder);
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - TimeSpan.FromDays(5));

        SelfUpdateService.SweepStaleDownloads(_directory, TimeSpan.FromHours(24), new LoggingService());

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void A_missing_directory_is_not_an_error()
    {
        SelfUpdateService.SweepStaleDownloads(
            Path.Combine(_directory, "does-not-exist"), TimeSpan.FromHours(24), new LoggingService());
    }

    private string StagingFolder() =>
        Path.Combine(_directory, SelfUpdateService.StagingFolderPrefix + Guid.NewGuid().ToString("N"));

    [Fact]
    public void An_old_staged_download_and_its_empty_folder_are_removed()
    {
        var folder = StagingFolder();
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, Name());
        File.WriteAllBytes(file, [1, 2, 3]);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - TimeSpan.FromDays(2));
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - TimeSpan.FromDays(2));

        SelfUpdateService.SweepStaleDownloads(_directory, TimeSpan.FromHours(24), new LoggingService());

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void A_recent_staged_download_is_kept()
    {
        var folder = StagingFolder();
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, Name());
        File.WriteAllBytes(file, [1, 2, 3]);

        SelfUpdateService.SweepStaleDownloads(_directory, TimeSpan.FromHours(24), new LoggingService());

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void A_staging_folder_with_other_content_keeps_that_content()
    {
        var folder = StagingFolder();
        Directory.CreateDirectory(folder);
        var other = Path.Combine(folder, "version.dll");
        File.WriteAllBytes(other, [9]);
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow - TimeSpan.FromDays(9));
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - TimeSpan.FromDays(9));

        SelfUpdateService.SweepStaleDownloads(_directory, TimeSpan.FromHours(24), new LoggingService());

        Assert.True(File.Exists(other));
    }

    [Fact]
    public void A_folder_with_a_look_alike_name_is_left_alone()
    {
        var folder = Path.Combine(_directory, SelfUpdateService.StagingFolderPrefix + "short");
        Directory.CreateDirectory(folder);
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - TimeSpan.FromDays(9));

        SelfUpdateService.SweepStaleDownloads(_directory, TimeSpan.FromHours(24), new LoggingService());

        Assert.True(Directory.Exists(folder));
    }
}
