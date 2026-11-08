using System.Text.Json;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// A cleanup tool that fails, never starts or runs out of time must not read as a finished target: the
/// outcome says so and the row shows why. Real tiny batch files stand in for the tools.
/// </summary>
public class CleanupToolFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-tool-failure-" + Guid.NewGuid());
    private readonly LoggingService _logging = new();

    private string Script(string name, string body)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "@echo off\r\n" + body + "\r\n");
        return path;
    }

    private static CleanupTarget RepairTarget(params RepairStep[] steps) => new(
        "test-repair", "x", "y", CleanupKind.SystemRepair, DefaultEnabled: true,
        RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x", NeedsElevation: true)
    {
        RepairSteps = steps,
    };

    private static CleanupTarget DefragTarget() => new(
        "test-defrag", "x", "y", CleanupKind.Defragmentation, DefaultEnabled: false,
        RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x", NeedsElevation: true)
    {
        DriveLetter = "C",
    };

    private static CleanupTarget DiskCleanupTarget() => new(
        "test-diskcleanup", "x", "y", CleanupKind.DiskCleanup, DefaultEnabled: false,
        RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x", NeedsElevation: true);

    [Fact]
    public async Task A_repair_step_with_a_failing_exit_code_is_a_failed_outcome_with_that_code()
    {
        var script = Script("fail.cmd", "exit /b 7");

        var outcome = await new CleanupService(_logging)
            .CleanAsync(RepairTarget(new RepairStep(script, [], "step")), null, CancellationToken.None);

        Assert.True(outcome.Failed);
        Assert.Equal(7, outcome.ExitCode);
        Assert.False(outcome.TimedOut);
    }

    [Fact]
    public async Task A_repair_step_that_ends_with_restart_required_is_a_success()
    {
        var script = Script("restart.cmd", "exit /b 3010");

        var outcome = await new CleanupService(_logging)
            .CleanAsync(RepairTarget(new RepairStep(script, [], "step")), null, CancellationToken.None);

        Assert.False(outcome.Failed);
        Assert.Null(outcome.ExitCode);
    }

    [Fact]
    public async Task A_repair_step_with_exit_code_zero_is_not_failed()
    {
        var script = Script("ok.cmd", "exit /b 0");

        var outcome = await new CleanupService(_logging)
            .CleanAsync(RepairTarget(new RepairStep(script, [], "step")), null, CancellationToken.None);

        Assert.False(outcome.Failed);
    }

    [Fact]
    public async Task A_repair_step_whose_program_is_missing_is_failed_without_an_exit_code()
    {
        var missing = Path.Combine(_root, "not-there.exe");

        var outcome = await new CleanupService(_logging)
            .CleanAsync(RepairTarget(new RepairStep(missing, [], "step")), null, CancellationToken.None);

        Assert.True(outcome.Failed);
        Assert.Null(outcome.ExitCode);
    }

    [Fact]
    public async Task A_later_failing_step_fails_the_target_even_after_an_earlier_good_one()
    {
        var ok = Script("ok.cmd", "exit /b 0");
        var fail = Script("fail.cmd", "exit /b 5");

        var outcome = await new CleanupService(_logging).CleanAsync(
            RepairTarget(new RepairStep(ok, [], "one"), new RepairStep(fail, [], "two")), null, CancellationToken.None);

        Assert.True(outcome.Failed);
        Assert.Equal(5, outcome.ExitCode);
    }

    [Fact]
    public async Task A_repair_step_past_its_time_limit_is_timed_out_and_not_failed()
    {
        var slow = Script("slow.cmd", "ping -n 30 127.0.0.1 >nul");

        var outcome = await new CleanupService(_logging) { RepairTimeout = TimeSpan.FromMilliseconds(800) }
            .CleanAsync(RepairTarget(new RepairStep(slow, [], "step")), null, CancellationToken.None);

        Assert.True(outcome.TimedOut);
        Assert.False(outcome.Failed);
    }

    [Fact]
    public async Task Defrag_with_a_failing_exit_code_is_a_failed_outcome_with_that_code()
    {
        var script = Script("defrag-fail.cmd", "exit /b 2");

        var outcome = await new CleanupService(_logging) { DefragExecutable = script }
            .CleanAsync(DefragTarget(), null, CancellationToken.None);

        Assert.True(outcome.Failed);
        Assert.Equal(2, outcome.ExitCode);
    }

    [Fact]
    public async Task Defrag_with_exit_code_zero_is_not_failed()
    {
        var script = Script("defrag-ok.cmd", "exit /b 0");

        var outcome = await new CleanupService(_logging) { DefragExecutable = script }
            .CleanAsync(DefragTarget(), null, CancellationToken.None);

        Assert.False(outcome.Failed);
        Assert.False(outcome.TimedOut);
    }

    [Fact]
    public async Task Defrag_past_its_time_limit_is_timed_out()
    {
        var script = Script("defrag-slow.cmd", "ping -n 30 127.0.0.1 >nul");

        var outcome = await new CleanupService(_logging)
        {
            DefragExecutable = script,
            DefragTimeout = TimeSpan.FromMilliseconds(800),
        }.CleanAsync(DefragTarget(), null, CancellationToken.None);

        Assert.True(outcome.TimedOut);
        Assert.False(outcome.Failed);
    }

    [Fact]
    public async Task Defrag_with_a_missing_program_is_failed_without_an_exit_code()
    {
        var outcome = await new CleanupService(_logging) { DefragExecutable = Path.Combine(_root, "none.exe") }
            .CleanAsync(DefragTarget(), null, CancellationToken.None);

        Assert.True(outcome.Failed);
        Assert.Null(outcome.ExitCode);
    }

    [Fact]
    public async Task Disk_cleanup_with_a_missing_program_is_failed()
    {
        var outcome = await new CleanupService(_logging) { CleanmgrExecutable = Path.Combine(_root, "none.exe") }
            .CleanAsync(DiskCleanupTarget(), null, CancellationToken.None);

        Assert.True(outcome.Failed);
    }

    [Fact]
    public async Task Disk_cleanup_whose_profile_cannot_be_written_is_failed_and_starts_nothing()
    {
        var marker = Path.Combine(_root, "started.txt");
        var script = Script("cleanmgr.cmd", $"echo started > \"{marker}\"");

        var outcome = await new CleanupService(_logging)
        {
            CleanmgrExecutable = script,
            SagesetProfileWriter = () => false,
        }.CleanAsync(DiskCleanupTarget(), null, CancellationToken.None);

        Assert.True(outcome.Failed);
        await Task.Delay(300);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void The_failure_travels_in_the_worker_result_message()
    {
        var message = new CleanupWorkerMessage { Type = "result", Id = "x", Failed = true, ExitCode = 9 };
        var round = JsonSerializer.Deserialize<CleanupWorkerMessage>(JsonSerializer.Serialize(message))!;

        var (state, _) = CleanupViewModel.ApplySingleTargetMessage(CleanupViewModel.SingleTargetCleanupState.Initial, round);

        Assert.True(state.Outcome!.Value.Failed);
        Assert.Equal(9, state.Outcome!.Value.ExitCode);
    }

    [Fact]
    public void A_message_without_a_failure_stays_a_normal_outcome()
    {
        var message = new CleanupWorkerMessage { Type = "result", Id = "x" };

        var (state, _) = CleanupViewModel.ApplySingleTargetMessage(CleanupViewModel.SingleTargetCleanupState.Initial, message);

        Assert.False(state.Outcome!.Value.Failed);
        Assert.Null(state.Outcome!.Value.ExitCode);
    }

    [Fact]
    public void The_failed_text_names_the_exit_code_or_that_the_tool_did_not_run()
    {
        var loc = LocalizationService.Instance;

        Assert.Equal(string.Format(loc["CleanupToolFailed"], "7"), CleanupViewModel.FailedText(CleanupOutcome.Failure(7)));
        Assert.Equal(string.Format(loc["CleanupToolFailed"], "0x800F0806"), CleanupViewModel.FailedText(CleanupOutcome.Failure(unchecked((int)0x800F0806))));
        Assert.Equal(loc["CleanupToolNotRun"], CleanupViewModel.FailedText(CleanupOutcome.Failure()));
    }

    [Fact]
    public void The_timed_out_text_names_the_limit_of_the_kind()
    {
        var loc = LocalizationService.Instance;

        Assert.Equal(string.Format(loc["CleanupToolTimedOut"], 240), CleanupViewModel.TimedOutText(CleanupKind.SystemRepair));
        Assert.Equal(string.Format(loc["CleanupToolTimedOut"], 360), CleanupViewModel.TimedOutText(CleanupKind.Defragmentation));
        Assert.Equal(
            string.Format(loc["CleanupDiskCleanupTimedOut"], (int)CleanupService.DiskCleanupLimit.TotalMinutes),
            CleanupViewModel.TimedOutText(CleanupKind.DiskCleanup));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
