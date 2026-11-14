using System.Diagnostics;
using System.IO;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// A target can name single files besides folders (the full memory dump). Each one goes through the same
/// gates as folder content: a link is never followed, a file younger than the age rule stays, and only a
/// dump file by name and extension is ever deleted this way.
/// </summary>
public class CleanupNamedFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-namedfile-test-" + Guid.NewGuid());
    private readonly CleanupService _service = new(new LoggingService());

    public CleanupNamedFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        TempTreeCleanup.Delete(_root);
    }

    private static CleanupTarget FileTarget(TimeSpan? minAge, params string[] files) => new(
        "test-dump", "x", "y", CleanupKind.Directories, DefaultEnabled: false,
        RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x")
    {
        Files = files,
        MinFileAge = minAge,
    };

    private string Dump(string name, TimeSpan age, int bytes = 4096)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetCreationTimeUtc(path, DateTime.UtcNow - age);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private static void RunCmd(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        Assert.True(process.WaitForExit(60_000), "cmd did not finish");
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task An_old_dump_file_is_counted_and_deleted()
    {
        var dump = Dump("MEMORY.DMP", TimeSpan.FromDays(2));
        var target = FileTarget(CleanupCatalog.CacheMinFileAge, dump);

        var scan = await _service.ScanAsync(target, CancellationToken.None);
        Assert.Equal(1, scan.Files);
        Assert.Equal(4096, scan.Bytes);

        var outcome = await _service.CleanAsync(target, progress: null, CancellationToken.None);

        Assert.Equal(1, outcome.DeletedFiles);
        Assert.True(outcome.FreedBytes > 0);
        Assert.False(File.Exists(dump));
    }

    [Fact]
    public async Task A_dump_younger_than_the_age_rule_stays_and_is_not_counted()
    {
        var dump = Dump("MEMORY.DMP", TimeSpan.FromMinutes(20));
        var target = FileTarget(CleanupCatalog.CacheMinFileAge, dump);

        var scan = await _service.ScanAsync(target, CancellationToken.None);
        var outcome = await _service.CleanAsync(target, progress: null, CancellationToken.None);

        Assert.Equal(0, scan.Files);
        Assert.Equal(0, outcome.DeletedFiles);
        Assert.True(File.Exists(dump));
    }

    [Fact]
    public async Task A_junction_named_like_the_dump_is_not_followed()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var inner = Path.Combine(elsewhere, "keep.dmp");
        File.WriteAllBytes(inner, new byte[100]);
        File.SetCreationTimeUtc(inner, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(inner, DateTime.UtcNow.AddDays(-3));
        var link = Path.Combine(_root, "MEMORY.DMP");
        RunCmd($"mklink /J \"{link}\" \"{elsewhere}\"");
        var target = FileTarget(CleanupCatalog.CacheMinFileAge, link);

        var scan = await _service.ScanAsync(target, CancellationToken.None);
        var outcome = await _service.CleanAsync(target, progress: null, CancellationToken.None);

        Assert.Equal(0, scan.Files);
        Assert.Equal(0, outcome.DeletedFiles);
        Assert.True(File.Exists(inner));
        Assert.True(Directory.Exists(link));
    }

    [Fact]
    public async Task A_file_symlink_named_like_the_dump_is_not_followed()
    {
        var real = Dump("real.dmp", TimeSpan.FromDays(3));
        var link = Path.Combine(_root, "MEMORY.DMP");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Creating a symlink needs developer mode or elevation; the junction test covers the directory case.
            return;
        }

        var target = FileTarget(CleanupCatalog.CacheMinFileAge, link);

        var scan = await _service.ScanAsync(target, CancellationToken.None);
        var outcome = await _service.CleanAsync(target, progress: null, CancellationToken.None);

        Assert.Equal(0, scan.Files);
        Assert.Equal(0, outcome.DeletedFiles);
        Assert.True(File.Exists(real));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public async Task A_file_that_is_not_a_dump_by_extension_is_never_deleted_this_way()
    {
        var other = Path.Combine(_root, "notes.txt");
        File.WriteAllBytes(other, new byte[100]);
        File.SetCreationTimeUtc(other, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-3));
        var target = FileTarget(CleanupCatalog.CacheMinFileAge, other);

        var scan = await _service.ScanAsync(target, CancellationToken.None);
        var outcome = await _service.CleanAsync(target, progress: null, CancellationToken.None);

        Assert.Equal(0, scan.Files);
        Assert.Equal(0, outcome.DeletedFiles);
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task A_real_folder_named_like_the_dump_is_left_untouched()
    {
        var folder = Path.Combine(_root, "MEMORY.DMP");
        Directory.CreateDirectory(folder);
        var inner = Path.Combine(folder, "a.bin");
        File.WriteAllBytes(inner, new byte[100]);
        File.SetCreationTimeUtc(inner, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(inner, DateTime.UtcNow.AddDays(-3));
        var target = FileTarget(CleanupCatalog.CacheMinFileAge, folder);

        var outcome = await _service.CleanAsync(target, progress: null, CancellationToken.None);

        Assert.Equal(0, outcome.DeletedFiles);
        Assert.True(File.Exists(inner));
    }

    [Fact]
    public async Task An_unticked_dump_row_is_left_alone()
    {
        var dump = Dump("MEMORY.DMP", TimeSpan.FromDays(2));
        var target = FileTarget(CleanupCatalog.CacheMinFileAge, dump);

        var outcome = await _service.CleanAsync(target, progress: null, CancellationToken.None, excludedEntryPaths: [dump]);

        Assert.Equal(0, outcome.DeletedFiles);
        Assert.True(File.Exists(dump));
    }

    [Fact]
    public void The_memory_dump_row_is_skipped_as_needs_admin_without_elevation()
    {
        var target = CleanupCatalog.Build().First(t => t.Id == "memory-dumps");

        Assert.True(CleanupService.SkipsWithoutAdmin(target, processElevated: false));
        Assert.False(CleanupService.SkipsWithoutAdmin(target, processElevated: true));
        Assert.False(target.DefaultEnabled);
        Assert.Equal(CleanupCatalog.CacheMinFileAge, target.MinFileAge);
    }

    [Fact]
    public void The_memory_dump_row_names_the_live_kernel_reports_folder_and_the_full_dump_file()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var target = CleanupCatalog.Build().First(t => t.Id == "memory-dumps");

        // Both exist only on a machine that has produced them, so the row lists what is there.
        if (Directory.Exists(Path.Combine(windows, "LiveKernelReports")))
            Assert.Contains(Path.Combine(windows, "LiveKernelReports"), target.Directories, StringComparer.OrdinalIgnoreCase);
        if (File.Exists(Path.Combine(windows, "MEMORY.DMP")))
            Assert.Contains(Path.Combine(windows, "MEMORY.DMP"), target.Files, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_live_kernel_reports_folder_and_the_full_dump_pass_the_safety_rules()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // The existence check is the last rule of the gate, so a folder this machine never made ("does not exist",
        // as on a fresh CI image) has passed every name and location rule before it.
        foreach (var folder in new[] { "LiveKernelReports", "Minidump" })
        {
            var safe = CleanupSafety.IsSafeToEmpty(Path.Combine(windows, folder), out var reason);
            Assert.True(safe || reason == "does not exist", $"{folder}: {reason}");
        }
    }

    [Fact]
    public void A_named_file_is_withheld_from_a_folder_target_that_contains_it()
    {
        var inner = Path.Combine(_root, "MEMORY.DMP");
        var outer = new CleanupTarget("outer", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
            RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        { Directories = [_root] };
        var dump = FileTarget(null, inner);

        var result = CleanupCatalog.WithDisjointPaths([outer, dump]);

        Assert.Contains(inner, result.First(t => t.Id == "outer").ExcludedDirectories);
        Assert.Equal([inner], result.First(t => t.Id == "test-dump").Files);
    }

    [Theory]
    [InlineData("MEMORY.DMP", true)]
    [InlineData("memory.dmp", true)]
    [InlineData("MEMORY.DMP.exe", false)]
    [InlineData("x.sys", false)]
    public void Only_dump_files_pass_the_single_file_gate(string name, bool expected)
    {
        var path = Path.Combine(_root, name);
        try
        {
            File.WriteAllBytes(path, new byte[10]);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            Assert.False(expected);
            return;
        }

        Assert.Equal(expected, CleanupSafety.IsSafeToDeleteFile(path, out _));
    }
}
