using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>A file held open without delete sharing fails at the open, before its size is read. The skipped
/// figure still has to carry the listed length, or the summary reports files left behind with no bytes.</summary>
public class CleanupSkippedBytesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-skipped-bytes-" + Guid.NewGuid());
    private readonly CleanupService _service = new(new LoggingService());

    public CleanupSkippedBytesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => TempTreeCleanup.Delete(_root);

    [Fact]
    public async Task A_file_held_open_in_a_folder_counts_its_length_as_skipped_bytes()
    {
        var path = Path.Combine(_root, "held.tmp");
        File.WriteAllBytes(path, new byte[5000]);
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var target = new CleanupTarget("test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
            RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        {
            Directories = [_root],
        };

        var outcome = await _service.CleanAsync(target, progress: null, CancellationToken.None);

        Assert.Equal(1, outcome.SkippedFiles);
        Assert.Equal(5000, outcome.SkippedBytes);
        Assert.Equal(0, outcome.FreedBytes);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task A_named_file_held_open_counts_its_length_as_skipped_bytes()
    {
        var path = Path.Combine(_root, "MEMORY.DMP");
        File.WriteAllBytes(path, new byte[4096]);
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var target = new CleanupTarget("test-dump", "x", "y", CleanupKind.Directories, DefaultEnabled: false,
            RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x")
        {
            Files = [path],
        };

        var outcome = await _service.CleanAsync(target, progress: null, CancellationToken.None);

        Assert.Equal(1, outcome.SkippedFiles);
        Assert.Equal(4096, outcome.SkippedBytes);
        Assert.True(File.Exists(path));
    }
}
