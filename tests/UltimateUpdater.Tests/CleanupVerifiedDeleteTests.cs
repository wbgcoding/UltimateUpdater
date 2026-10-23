using System.Diagnostics;
using System.IO;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

public class CleanupVerifiedDeleteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-verified-delete-" + Guid.NewGuid());
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "wu-verified-outside-" + Guid.NewGuid());

    public void Dispose()
    {
        CleanupService.BeforeFileDeleteForTest.Value = null;
        CleanupService.EnumerateFilesHookForTest.Value = null;
        foreach (var junction in Directory.Exists(_root)
                     ? Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                         .Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList()
                     : [])
        {
            try { Directory.Delete(junction); } catch (IOException) { }
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_outside, recursive: true); } catch (IOException) { }
    }

    private static void CreateJunction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit(5000);
        Assert.Equal(0, mklink.ExitCode);
    }

    private CleanupTarget TargetFor(string directory) =>
        new("test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
            RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        {
            Directories = [directory],
        };

    private Task<CleanupOutcome> Clean(string directory) =>
        new CleanupService(new LoggingService()).CleanAsync(TargetFor(directory), progress: null, CancellationToken.None);

    /// <summary>Deleting one name of a hard-linked file frees nothing: the data stays reachable through the
    /// other name, so the freed figure must not count it.</summary>
    [Fact]
    public async Task Deleting_one_name_of_a_hard_linked_file_frees_nothing()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        var original = Path.Combine(_outside, "original.dat");
        File.WriteAllBytes(original, new byte[4096]);
        var link = Path.Combine(_root, "link.tmp");
        using (var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /H \"{link}\" \"{original}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            mklink.WaitForExit(5000);
            Assert.Equal(0, mklink.ExitCode);
        }

        var outcome = await Clean(_root);

        Assert.Equal(1, outcome.DeletedFiles);
        Assert.Equal(0, outcome.FreedBytes);
        Assert.False(File.Exists(link));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public async Task A_folder_swapped_for_a_junction_after_listing_never_deletes_outside_the_root()
    {
        var b = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(b, "victim.tmp"), "inside");
        Directory.CreateDirectory(_outside);
        var outsideVictim = Path.Combine(_outside, "victim.tmp");
        File.WriteAllText(outsideVictim, "outside");

        // The swap happens two levels below the root: the root lock cannot stop a rename down there.
        CleanupService.BeforeFileDeleteForTest.Value = file =>
        {
            if (!file.EndsWith("victim.tmp", StringComparison.OrdinalIgnoreCase) || new DirectoryInfo(b).LinkTarget is not null)
                return;

            Directory.Delete(b, recursive: true);
            CreateJunction(b, _outside);
        };

        var outcome = await Clean(_root);

        Assert.True(File.Exists(outsideVictim), "a file outside the cleaned root was deleted through a swapped junction");
        Assert.Equal(0, outcome.DeletedFiles);
    }

    [Fact]
    public async Task A_root_swapped_for_a_junction_before_it_is_locked_deletes_nothing_outside()
    {
        var root = Path.Combine(_root, "root");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "victim.tmp"), "inside");
        Directory.CreateDirectory(_outside);
        var outsideVictim = Path.Combine(_outside, "victim.tmp");
        File.WriteAllText(outsideVictim, "outside");

        CleanupSafety.BeforeLockOpenForTest.Value = path =>
        {
            if (new DirectoryInfo(path).LinkTarget is not null)
                return;

            Directory.Delete(path, recursive: true);
            CreateJunction(path, _outside);
        };

        try
        {
            var outcome = await Clean(root);

            Assert.True(File.Exists(outsideVictim), "a file outside the root was deleted through a swapped root");
            Assert.Equal(0, outcome.DeletedFiles);
        }
        finally
        {
            CleanupSafety.BeforeLockOpenForTest.Value = null;
        }
    }

    [Fact]
    public async Task A_read_only_file_is_deleted()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "locked.tmp");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var outcome = await Clean(_root);

        Assert.False(File.Exists(file));
        Assert.Equal(1, outcome.DeletedFiles);
    }

    [Fact]
    public async Task A_file_another_process_holds_open_with_full_sharing_is_still_deleted()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "open.tmp");
        File.WriteAllText(file, "x");
        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var outcome = await Clean(_root);

        Assert.Equal(1, outcome.DeletedFiles);
    }

    [Fact]
    public async Task An_empty_folder_swapped_for_a_junction_after_the_file_pass_is_not_removed_outside_the_root()
    {
        var sub = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(Path.Combine(_outside, "emptyfolder"));
        File.WriteAllText(Path.Combine(_root, "a", "anchor.tmp"), "x");

        // The anchor file is the last file; swap "b" once the file pass is done.
        CleanupService.BeforeFileDeleteForTest.Value = file =>
        {
            if (!file.EndsWith("anchor.tmp", StringComparison.OrdinalIgnoreCase))
                return;

            Directory.Delete(sub);
            CreateJunction(sub, Path.Combine(_outside, "emptyfolder"));
        };

        await Clean(_root);

        Assert.True(Directory.Exists(Path.Combine(_outside, "emptyfolder")));
    }

    [Fact]
    public void Only_the_old_style_disposition_unavailable_errors_trigger_the_fallback()
    {
        Assert.True(VerifiedDelete.ShouldFallBack(87));
        Assert.True(VerifiedDelete.ShouldFallBack(50));
        Assert.True(VerifiedDelete.ShouldFallBack(1));
        Assert.False(VerifiedDelete.ShouldFallBack(5));
        Assert.False(VerifiedDelete.ShouldFallBack(32));
    }

    [Fact]
    public void Inside_root_requires_a_separator_boundary()
    {
        Assert.True(VerifiedDelete.IsInsideRoot(@"\?\C:\Temp\x", @"\?\C:\Temp\x\a\b.tmp"));
        Assert.True(VerifiedDelete.IsInsideRoot(@"\?\C:\Temp\x\", @"\?\c:\temp\X\a.tmp"));
        Assert.False(VerifiedDelete.IsInsideRoot(@"\?\C:\Temp\x", @"\?\C:\Temp\xother\a.tmp"));
        Assert.False(VerifiedDelete.IsInsideRoot(@"\?\C:\Temp\x", @"\?\C:\Temp\x"));
    }
}
