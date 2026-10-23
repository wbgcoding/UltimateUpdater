using System.Diagnostics;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="RegistryBackupService.WrittenFileLandedInExpectedFolder"/> is the verification that
/// runs right after the backup file is created, closing the gap between the last text-based junction check
/// and the write that actually lands on disk. Proven directly on a real junction, not by timing the race
/// itself, which the item explicitly rules out as an acceptance criterion.
/// </summary>
public class RegistryBackupHandleVerificationTests
{
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

    /// <summary>
    /// A file created through a junction path physically lands under the junction's REAL target, not under
    /// the text the caller believed it wrote to. The verification must catch exactly this, the one thing a
    /// path-only check (proven where the text points, never where it actually leads) cannot.
    /// </summary>
    [Fact]
    public void WrittenFileLandedInExpectedFolder_rejects_a_file_written_through_a_junction()
    {
        var root = Path.Combine(Path.GetTempPath(), "wu-regbackup-handle-" + Guid.NewGuid());
        var target = Path.Combine(Path.GetTempPath(), "wu-regbackup-handle-target-" + Guid.NewGuid());
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);

        try
        {
            CreateJunction(link, target);

            var filePath = Path.Combine(link, "backup.reg");
            using var file = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

            // The believed destination is "link" (what BackupBeforeWriteAsync's own backupFolder variable
            // would hold); the bytes actually landed under "target".
            Assert.False(RegistryBackupService.WrittenFileLandedInExpectedFolder(file.SafeFileHandle, link));
        }
        finally
        {
            try { File.Delete(Path.Combine(target, "backup.reg")); } catch { }
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(target, recursive: true); } catch { }
        }
    }

    /// <summary>The ordinary case: a plain folder with no reparse point anywhere is accepted.</summary>
    [Fact]
    public void WrittenFileLandedInExpectedFolder_accepts_a_plain_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wu-regbackup-handle-plain-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);

        try
        {
            var filePath = Path.Combine(folder, "backup.reg");
            using var file = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

            Assert.True(RegistryBackupService.WrittenFileLandedInExpectedFolder(file.SafeFileHandle, folder));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A junction planted one level ABOVE the backup folder itself (rather than at the backup folder's own
    /// leaf) redirects the whole subtree under it just as effectively; the handle-based check must catch
    /// this shape too, not just a junction that is itself the expected folder.
    /// </summary>
    [Fact]
    public void WrittenFileLandedInExpectedFolder_rejects_a_junction_higher_up_the_chain()
    {
        var root = Path.Combine(Path.GetTempPath(), "wu-regbackup-handle-deep-" + Guid.NewGuid());
        var target = Path.Combine(Path.GetTempPath(), "wu-regbackup-handle-deep-target-" + Guid.NewGuid());
        var link = Path.Combine(root, "link");
        var expectedFolder = Path.Combine(link, "sub");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(target, "sub"));

        try
        {
            CreateJunction(link, target);

            var filePath = Path.Combine(expectedFolder, "backup.reg");
            using var file = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

            Assert.False(RegistryBackupService.WrittenFileLandedInExpectedFolder(file.SafeFileHandle, expectedFolder));
        }
        finally
        {
            try { File.Delete(Path.Combine(target, "sub", "backup.reg")); } catch { }
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(target, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// End to end: a backup folder that is itself a plain directory still backs up successfully once a
    /// real key is named, proving the handle check does not reject the ordinary case it sits in front of.
    /// The export itself is now a direct, read-only registry walk (no reg.exe, no temp file), so
    /// this no longer depends on whether the test process happens to run elevated - reading HKCU needs no
    /// elevation either way.
    /// </summary>
    [Fact]
    public async Task BackupBeforeWriteAsync_still_succeeds_for_a_plain_folder()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "wu-regbackup-handle-e2e-" + Guid.NewGuid());

        try
        {
            var result = await RegistryBackupService.BackupBeforeWriteAsync(
                ["HKCU\\Control Panel\\Desktop"], folder, 5,
                new UltimateUpdater.Services.LoggingService(),
                CancellationToken.None);

            Assert.NotNull(result);
            Assert.True(File.Exists(result));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }
    /// <summary>
    /// The elevated prune of old backups must never delete through a folder that resolves somewhere else:
    /// a same-user junction swapped in between enumeration and delete would otherwise aim an admin delete
    /// at any file the link names. A file reached through the junction survives; one in the real folder goes.
    /// </summary>
    [Fact]
    public void TryDeleteInsideFolder_refuses_a_file_reached_through_a_junction()
    {
        var root = Path.Combine(Path.GetTempPath(), "wu-regbackup-prune-" + Guid.NewGuid());
        var target = Path.Combine(Path.GetTempPath(), "wu-regbackup-prune-target-" + Guid.NewGuid());
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);

        try
        {
            CreateJunction(link, target);
            var victim = Path.Combine(target, "keep.reg");
            File.WriteAllText(victim, "x");

            Assert.False(RegistryBackupService.TryDeleteInsideFolder(Path.Combine(link, "keep.reg"), link));
            Assert.True(File.Exists(victim));

            Assert.True(RegistryBackupService.TryDeleteInsideFolder(victim, target));
            Assert.False(File.Exists(victim));
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(target, recursive: true); } catch { }
        }
    }
}
