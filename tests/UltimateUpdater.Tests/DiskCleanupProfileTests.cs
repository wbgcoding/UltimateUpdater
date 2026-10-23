using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>The unattended Disk Cleanup run processes every handler that carries our flag, so the flag must
/// exist on the allow-list alone. Pure over the registry facts: nothing here touches HKLM.</summary>
public class DiskCleanupProfileTests
{
    private static readonly string[] Allowed = ["Temporary Files", "Thumbnail Cache"];

    [Fact]
    public void A_foreign_handler_carrying_the_flag_is_cleared()
    {
        var cleared = new List<string>();

        var ok = CleanupService.ClearForeignHandlerFlags(
            [("Temporary Files", true), ("DownloadsFolder", true), ("Recycle Bin", false)],
            Allowed, handler => { cleared.Add(handler); return true; }, out var failed);

        Assert.True(ok);
        Assert.Null(failed);
        Assert.Equal(["DownloadsFolder"], cleared);
    }

    [Fact]
    public void A_foreign_flag_that_cannot_be_cleared_fails_the_profile_and_names_the_handler()
    {
        var ok = CleanupService.ClearForeignHandlerFlags(
            [("DownloadsFolder", true)], Allowed, _ => false, out var failed);

        Assert.False(ok);
        Assert.Equal("DownloadsFolder", failed);
    }

    [Fact]
    public void Allowed_handlers_keep_their_flag_whatever_the_casing()
    {
        var ok = CleanupService.ClearForeignHandlerFlags(
            [("temporary files", true)], Allowed, _ => throw new InvalidOperationException("must not be touched"), out _);

        Assert.True(ok);
    }
}

/// <summary>The time limit on Disk Cleanup is a failure of its own, not the user's Stop.</summary>
public class DiskCleanupTimeLimitTests
{
    private static System.Diagnostics.Process StartNeverEndingProcess() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "-n 120 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;

    [Fact]
    public async Task Running_past_the_limit_is_a_timeout_not_a_stop()
    {
        using var process = StartNeverEndingProcess();
        try
        {
            var service = new CleanupService(new UltimateUpdater.Services.LoggingService());

            var outcome = await service.WaitForDiskCleanupAsync(process, TimeSpan.FromMilliseconds(300), CancellationToken.None);

            Assert.NotNull(outcome);
            Assert.False(outcome.Value.WasStopped);
            Assert.True(outcome.Value.TimedOut);
            Assert.True(process.WaitForExit(5000));
        }
        finally
        {
            try { process.Kill(); } catch { }
        }
    }

    [Fact]
    public async Task The_users_stop_is_still_a_stop()
    {
        using var process = StartNeverEndingProcess();
        try
        {
            var service = new CleanupService(new UltimateUpdater.Services.LoggingService());
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            var outcome = await service.WaitForDiskCleanupAsync(process, TimeSpan.FromMinutes(5), cts.Token);

            Assert.NotNull(outcome);
            Assert.True(outcome.Value.WasStopped);
            Assert.False(outcome.Value.TimedOut);
        }
        finally
        {
            try { process.Kill(); } catch { }
        }
    }

    [Fact]
    public void The_limit_is_thirty_minutes() =>
        Assert.Equal(TimeSpan.FromMinutes(30), CleanupService.DiskCleanupLimit);

    [Fact]
    public void A_timeout_travels_over_the_pipe_result()
    {
        var message = new CleanupWorkerMessage { Type = "result", Id = "disk-cleanup", TimedOut = true };

        var (state, _) = UltimateUpdater.ViewModels.CleanupViewModel.ApplySingleTargetMessage(
            UltimateUpdater.ViewModels.CleanupViewModel.SingleTargetCleanupState.Initial, message);

        Assert.True(state.Outcome!.Value.TimedOut);
        Assert.False(state.Outcome.Value.WasStopped);
    }
}
