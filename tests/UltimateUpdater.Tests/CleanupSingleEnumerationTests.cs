using System.IO;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

public class CleanupSingleEnumerationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-cleanup-once-" + Guid.NewGuid());

    public void Dispose()
    {
        CleanupService.EnumerateFilesHookForTest.Value = null;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Every_directory_of_a_target_is_walked_once_and_all_its_files_are_removed()
    {
        var dirA = Path.Combine(_root, "A");
        var dirB = Path.Combine(_root, "B");
        Directory.CreateDirectory(Path.Combine(dirA, "sub"));
        Directory.CreateDirectory(dirB);
        File.WriteAllText(Path.Combine(dirA, "one.tmp"), "1");
        File.WriteAllText(Path.Combine(dirA, "sub", "two.tmp"), "2");
        File.WriteAllText(Path.Combine(dirB, "three.tmp"), "3");

        var walked = new List<string>();
        CleanupService.EnumerateFilesHookForTest.Value = walked.Add;

        var target = new CleanupTarget(
            "test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
            RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        {
            Directories = [dirA, dirB],
        };

        var outcome = await new CleanupService(new LoggingService()).CleanAsync(
            target, progress: null, CancellationToken.None);

        Assert.Equal(3, outcome.DeletedFiles);
        Assert.Equal([dirA, dirB], walked.OrderBy(d => d, StringComparer.Ordinal));
    }
}
