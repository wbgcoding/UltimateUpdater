using System.IO;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// A temp or cache target with a minimum file age keeps anything younger: the scan leaves it out of the
/// figure and the clean leaves it on disk, judged on both the creation and the write time.
/// </summary>
public class CleanupMinFileAgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-minage-test-" + Guid.NewGuid());
    private readonly LoggingService _logging = new();

    public CleanupMinFileAgeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private CleanupTarget Target(TimeSpan? minAge) => new(
        "test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
        RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
    {
        Directories = [_root],
        MinFileAge = minAge,
    };

    private string Write(string relative, TimeSpan writeAge, TimeSpan creationAge, int bytes = 100)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetCreationTimeUtc(path, DateTime.UtcNow - creationAge);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - writeAge);
        return path;
    }

    [Fact]
    public async Task A_fresh_file_survives_scan_and_clean_and_a_two_day_old_file_goes()
    {
        var fresh = Write("fresh.tmp", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        var old = Write("old.tmp", TimeSpan.FromDays(2), TimeSpan.FromDays(2));
        var service = new CleanupService(_logging);
        var target = Target(TimeSpan.FromHours(24));

        var scan = await service.ScanAsync(target, CancellationToken.None);
        Assert.Equal(1, scan.Files);
        Assert.Equal(100, scan.Bytes);

        var outcome = await service.CleanAsync(target, progress: null, CancellationToken.None);

        Assert.Equal(1, outcome.DeletedFiles);
        Assert.Equal(100, outcome.FreedBytes);
        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public async Task An_old_write_time_with_a_new_creation_time_is_kept()
    {
        // An extracted archive keeps the write times of its members but is created just now.
        var extracted = Write("payload.msi", TimeSpan.FromDays(400), TimeSpan.FromMinutes(1));
        var service = new CleanupService(_logging);
        var target = Target(TimeSpan.FromHours(24));

        var scan = await service.ScanAsync(target, CancellationToken.None);
        var outcome = await service.CleanAsync(target, progress: null, CancellationToken.None);

        Assert.Equal(0, scan.Files);
        Assert.Equal(0, outcome.DeletedFiles);
        Assert.True(File.Exists(extracted));
    }

    [Fact]
    public async Task A_new_write_time_with_an_old_creation_time_is_kept()
    {
        var written = Write("log.tmp", TimeSpan.FromMinutes(1), TimeSpan.FromDays(5));
        var service = new CleanupService(_logging);

        var outcome = await service.CleanAsync(Target(TimeSpan.FromHours(24)), progress: null, CancellationToken.None);

        Assert.Equal(0, outcome.DeletedFiles);
        Assert.True(File.Exists(written));
    }

    [Fact]
    public async Task A_file_with_a_future_time_stamp_is_kept()
    {
        var path = Write("future.tmp", TimeSpan.FromDays(2), TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(3));
        var service = new CleanupService(_logging);

        var outcome = await service.CleanAsync(Target(TimeSpan.FromHours(1)), progress: null, CancellationToken.None);

        Assert.Equal(0, outcome.DeletedFiles);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Without_a_minimum_age_a_fresh_file_is_taken_as_before()
    {
        var fresh = Write("fresh.tmp", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        var service = new CleanupService(_logging);

        var outcome = await service.CleanAsync(Target(null), progress: null, CancellationToken.None);

        Assert.Equal(1, outcome.DeletedFiles);
        Assert.False(File.Exists(fresh));
    }

    [Fact]
    public async Task A_folder_created_inside_the_window_stays_and_an_old_empty_folder_goes()
    {
        var youngFolder = Path.Combine(_root, "young");
        Directory.CreateDirectory(youngFolder);
        var oldFolder = Path.Combine(_root, "old");
        Directory.CreateDirectory(oldFolder);
        Directory.SetCreationTimeUtc(oldFolder, DateTime.UtcNow.AddDays(-3));
        var service = new CleanupService(_logging);

        await service.CleanAsync(Target(TimeSpan.FromHours(24)), progress: null, CancellationToken.None);

        Assert.True(Directory.Exists(youngFolder));
        Assert.False(Directory.Exists(oldFolder));
    }

    [Fact]
    public async Task A_folder_holding_a_fresh_file_survives_its_old_neighbour_being_deleted()
    {
        var fresh = Write(Path.Combine("installer", "payload.msi"), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        Directory.SetCreationTimeUtc(Path.GetDirectoryName(fresh)!, DateTime.UtcNow.AddDays(-9));
        var old = Write("old.tmp", TimeSpan.FromDays(2), TimeSpan.FromDays(2));
        var service = new CleanupService(_logging);

        await service.CleanAsync(Target(TimeSpan.FromHours(24)), progress: null, CancellationToken.None);

        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void The_catalogue_gives_every_temp_and_cache_target_an_age_rule()
    {
        var targets = CleanupCatalog.BuildFor(null, null).ToDictionary(t => t.Id);

        foreach (var id in new[] { "user-temp", "all-users-temp", "windows-temp", "windows-update-cache" })
            Assert.Equal(TimeSpan.FromHours(24), targets[id].MinFileAge);

        foreach (var id in new[]
                 {
                     "delivery-optimization", "thumbnail-cache", "crash-dumps", "shader-cache",
                     "package-manager-cache", "build-tool-cache", "app-cache-sweep",
                 })
        {
            Assert.Equal(TimeSpan.FromHours(1), targets[id].MinFileAge);
        }
    }
}
