using System.IO;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>When the uv cache clean may run at all (not from an elevated app), and how a cache root that the
/// tool removed is measured afterwards.</summary>
public class CleanupUvRunPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-uvpolicy-test-" + Guid.NewGuid());
    private readonly List<string> _log = [];

    public void Dispose()
    {
        ElevationService.IsElevatedForTest.Value = null;
        TempTreeCleanup.Delete(_root);
    }

    private string UvFolderWithOneFile(int bytes, out string file)
    {
        var uv = Path.Combine(_root, "Local", "uv", "cache");
        Directory.CreateDirectory(uv);
        file = Path.Combine(uv, "a.bin");
        File.WriteAllBytes(file, new byte[bytes]);
        File.SetCreationTimeUtc(file, DateTime.UtcNow - TimeSpan.FromDays(3));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - TimeSpan.FromDays(3));
        return uv;
    }

    private static CleanupTarget Target(string uv) =>
        new("test-pkg", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
            RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        {
            Directories = [uv],
            MinFileAge = CleanupCatalog.CacheMinFileAge,
            CleanCommands = new Dictionary<string, DirectoryCleanCommand>(StringComparer.OrdinalIgnoreCase)
            {
                [uv] = new DirectoryCleanCommand(
                    "uv", null, ["cache", "clean", "--cache-dir", DirectoryCleanCommand.FolderToken],
                    "uv cache clean", TimeSpan.FromMinutes(10)),
            },
        };

    private CleanupService Service(Func<string, IReadOnlyList<string>, ProcessResult> behaviour, List<string> calls)
    {
        var service = new CleanupService(new LoggingService(_log.Add));
        service.TrustedToolResolver = _ => @"C:\tools\uv.exe";
        service.ToolRunner = (exe, args, _, _) =>
        {
            calls.Add(exe);
            return Task.FromResult(behaviour(exe, args));
        };
        return service;
    }

    [Fact]
    public async Task An_app_running_elevated_never_starts_the_tool()
    {
        ElevationService.IsElevatedForTest.Value = true;
        var uv = UvFolderWithOneFile(700, out var file);
        var calls = new List<string>();

        var outcome = await Service((_, _) => new ProcessResult(0, "", ""), calls)
            .CleanAsync(Target(uv), progress: null, CancellationToken.None);

        Assert.Empty(calls);
        Assert.Equal(0, outcome.FreedBytes);
        Assert.Equal(1, outcome.SkippedFiles);
        Assert.Equal(700, outcome.SkippedBytes);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task A_cache_root_the_tool_removed_is_measured_as_empty_without_a_warning()
    {
        ElevationService.IsElevatedForTest.Value = false;
        var uv = UvFolderWithOneFile(700, out _);
        var calls = new List<string>();

        var outcome = await Service((_, args) =>
        {
            Directory.Delete(args[3], recursive: true);
            return new ProcessResult(0, "", "");
        }, calls).CleanAsync(Target(uv), progress: null, CancellationToken.None);

        Assert.Single(calls);
        Assert.Equal(700, outcome.FreedBytes);
        Assert.Equal(1, outcome.DeletedFiles);
        Assert.Equal(0, outcome.SkippedFiles);
        Assert.DoesNotContain(_log, line => line.Contains("Cleanup:", StringComparison.Ordinal) && line.Contains("enumerate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_log, line => line.Contains("stopped early", StringComparison.OrdinalIgnoreCase));
    }
}
