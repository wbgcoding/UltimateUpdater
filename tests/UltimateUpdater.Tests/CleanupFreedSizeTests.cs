using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>Both clean routes report the same freed figure for one file: the space it occupies on disk,
/// nothing for an extra name of a hardlinked file.</summary>
public class CleanupFreedSizeTests : IDisposable
{
    private const int LogicalLength = 1024 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-freed-size-" + Guid.NewGuid());
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "wu-freed-outside-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_outside, recursive: true); } catch (IOException) { }
    }

    private static CleanupTarget TargetFor(string directory) =>
        new("test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
            RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        {
            Directories = [directory],
        };

    private static Task<CleanupOutcome> Clean(string directory, bool recycle) =>
        new CleanupService(new LoggingService()).CleanAsync(
            TargetFor(directory), progress: null, CancellationToken.None, recycleInsteadOfDelete: recycle);

    /// <summary>A file of compressible content, NTFS-compressed so its allocation is far below its length.</summary>
    private static string CompressedFile(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'a', LogicalLength).ToArray());

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        ushort format = 1;
        if (!DeviceIoControl(handle, 0x9C040, ref format, sizeof(ushort), IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_compressed_file_frees_its_size_on_disk_by_either_route(bool recycle)
    {
        var path = CompressedFile(_root, "packed.tmp");
        var onDisk = OnDiskSize.Of(path);
        Assert.InRange(onDisk, 1, LogicalLength - 1);

        var outcome = await Clean(_root, recycle);

        Assert.Equal(1, outcome.DeletedFiles);
        Assert.Equal(onDisk, outcome.FreedBytes);
    }

    [Fact]
    public async Task One_name_of_a_hard_linked_file_frees_nothing_by_the_recycle_route()
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
            Assert.True(mklink.WaitForExit(60_000), "mklink did not finish");
            Assert.Equal(0, mklink.ExitCode);
        }

        var outcome = await Clean(_root, recycle: true);

        Assert.Equal(1, outcome.DeletedFiles);
        Assert.Equal(0, outcome.FreedBytes);
        Assert.True(File.Exists(original));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, ref ushort input, int inputSize, IntPtr output, int outputSize,
        out int returned, IntPtr overlapped);
}
