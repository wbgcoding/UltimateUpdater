using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="ElevationService.IsElevatedWorkerProcess"/> must stay distinct from
/// <see cref="ElevationService.IsElevated"/>: the latter is also true for an ordinary unelevated main
/// process whenever UAC is off or the account is the built-in Administrator.
/// </summary>
public class ElevationServiceTests
{
    [Fact]
    public void The_test_process_is_never_the_elevated_worker() =>
        // No test run ever passes --elevated-worker on its command line, so this must be false here,
        // the same way it is false for an ordinary unelevated OR ambiently-admin-token main process.
        Assert.False(ElevationService.IsElevatedWorkerProcess);

    /// <summary>
    /// The test assembly's module initializer sets <see
    /// cref="ElevationService.BlockRealLaunchForTests"/> before any test runs, so a real UAC prompt is
    /// structurally impossible here, not merely avoided by every caller behaving. This proves the flag is
    /// actually on, not just that the code compiles.
    /// </summary>
    [Fact]
    public void The_test_process_refuses_to_start_a_real_elevated_worker()
    {
        Assert.True(ElevationService.BlockRealLaunchForTests);

        Assert.Throws<InvalidOperationException>(
            () => ElevationService.StartWorker("test-pipe-" + Guid.NewGuid(), new LoggingService()));
    }

    /// <summary>
    /// Locking the resolved image blocks the rename-swap attack. The lock is opened against a
    /// throwaway file here, never the real test host exe.
    /// </summary>
    [Fact]
    public void Locking_an_image_blocks_renaming_it_until_released()
    {
        var path = Path.Combine(Path.GetTempPath(), "wu-selfimage-test-" + Guid.NewGuid() + ".exe");
        File.WriteAllBytes(path, [0]);
        var renamed = path + ".renamed";

        try
        {
            ElevationService.LockSelfImage(new LoggingService(), path);

            Assert.ThrowsAny<IOException>(() => File.Move(path, renamed));

            ElevationService.ReleaseSelfImageLock();

            File.Move(path, renamed);
            Assert.True(File.Exists(renamed));
        }
        finally
        {
            ElevationService.ReleaseSelfImageLock();
            try { File.Delete(path); } catch { }
            try { File.Delete(renamed); } catch { }
        }
    }

    [Theory]
    [InlineData("--elevated-worker", "pipe", "123")]
    [InlineData("--elevated-worker")]
    public void The_worker_flag_in_first_position_is_worker_mode(params string[] args) =>
        Assert.True(ElevationService.IsWorkerInvocation(args));

    [Theory]
    [InlineData("--tray", "--elevated-worker")]
    [InlineData("pipe", "--elevated-worker", "123")]
    [InlineData("--Elevated-Worker")]
    [InlineData("")]
    public void The_worker_flag_anywhere_but_first_is_not_worker_mode(params string[] args) =>
        Assert.False(ElevationService.IsWorkerInvocation(args));

    [Fact]
    public void No_arguments_is_not_worker_mode() =>
        Assert.False(ElevationService.IsWorkerInvocation([]));

    [Fact]
    public void The_executable_path_is_never_taken_for_the_flag()
    {
        // GetCommandLineArgs() leads with the executable; the shared check works on what follows it.
        string[] commandLine = ["--elevated-worker", "--tray"];

        Assert.False(ElevationService.IsWorkerInvocation(commandLine.Skip(1)));
    }
}
