using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>The figures under the history total: last check results, averages and the history file's handling
/// of the repair result.</summary>
public class CleanupHistoryStatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static CleanupHistoryEntry Run(DateTimeOffset when, long freed, params CleanupHistoryArea[] areas) => new()
    {
        Timestamp = when,
        FreedBytes = freed,
        Areas = [.. areas],
    };

    private static CleanupHistoryArea Check(string id, RepairResult? result) => new()
    {
        Id = id,
        TitleKey = "CleanupSystemRepairSfc",
        RepairResult = result,
    };

    [Fact]
    public void The_last_check_is_the_newest_run_with_a_known_result()
    {
        var entries = new[]
        {
            Run(Now.AddDays(-10), 0, Check("system-repair-sfc", RepairResult.ProblemsFound)),
            Run(Now.AddDays(-5), 0, Check("system-repair-sfc", RepairResult.Repaired)),
            Run(Now.AddDays(-1), 0, Check("system-repair-sfc", null)),
            Run(Now.AddDays(-2), 0, Check("system-repair-dism", RepairResult.Ok)),
        };

        var sfc = CleanupHistoryStats.LastCheck(entries, "system-repair-sfc");
        var dism = CleanupHistoryStats.LastCheck(entries, "system-repair-dism");

        Assert.Equal((Now.AddDays(-5), RepairResult.Repaired), sfc);
        Assert.Equal((Now.AddDays(-2), RepairResult.Ok), dism);
        Assert.Null(CleanupHistoryStats.LastCheck([], "system-repair-sfc"));
    }

    [Fact]
    public void Average_and_recent_totals_follow_the_runs()
    {
        var entries = new[]
        {
            Run(Now.AddDays(-40), 600),
            Run(Now.AddDays(-29), 300),
            Run(Now.AddDays(-1), 100),
        };

        Assert.Equal(333, CleanupHistoryStats.AverageFreedPerRun(entries));
        Assert.Equal(400, CleanupHistoryStats.FreedInRecentDays(entries, Now));
        Assert.Null(CleanupHistoryStats.AverageFreedPerRun([]));
        Assert.Null(CleanupHistoryStats.FreedInRecentDays([Run(Now.AddDays(-60), 5)], Now));
    }

    [Fact]
    public void The_two_checks_show_never_and_the_other_lines_stay_hidden_without_history()
    {
        LocalizationService.Instance.Apply("en");

        var lines = CleanupHistoryStatViewModel.Build([], Now, null);

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal("never", l.Value));
    }

    [Fact]
    public void Every_line_shows_once_there_is_history_and_a_drive()
    {
        LocalizationService.Instance.Apply("en");
        var entries = new[] { Run(Now.AddDays(-1), 2048, Check("system-repair-sfc", RepairResult.ProblemsFound)) };

        var lines = CleanupHistoryStatViewModel.Build(entries, Now, ("C:", 1L << 30));

        Assert.Equal(6, lines.Count);
        Assert.EndsWith("problems found", lines[0].Value);
        Assert.Equal("never", lines[1].Value);
        Assert.Equal("Free on C:", lines[5].Label);
    }

    [Fact]
    public void A_repair_result_survives_the_history_file_and_an_old_file_without_one_still_loads()
    {
        var folder = Directory.CreateTempSubdirectory("wu-cleanup-history-").FullName;
        var path = Path.Combine(folder, "history.json");
        var service = new CleanupHistoryService(new LoggingService(), path);
        service.Append(Run(Now, 0, Check("system-repair-sfc", RepairResult.Repaired), Check("other", null)));

        var reloaded = new CleanupHistoryService(new LoggingService(), path).Load();

        Assert.Equal(RepairResult.Repaired, reloaded[0].Areas[0].RepairResult);
        Assert.Null(reloaded[0].Areas[1].RepairResult);

        File.WriteAllText(path, """[{"timestamp":"2026-08-01T10:00:00+00:00","freedBytes":5,"areas":[{"id":"a","titleKey":"CleanupUserTemp","freedBytes":5}]}]""");
        var old = new CleanupHistoryService(new LoggingService(), path).Load();
        Assert.Single(old);
        Assert.Null(old[0].Areas[0].RepairResult);
    }

    [Fact]
    public void The_repair_result_travels_in_the_worker_result_message()
    {
        var message = new CleanupWorkerMessage { Type = "result", Id = "system-repair-sfc", RepairResult = RepairResult.ProblemsFound };

        var (state, _) = CleanupViewModel.ApplySingleTargetMessage(CleanupViewModel.SingleTargetCleanupState.Initial, message);

        Assert.Equal(RepairResult.ProblemsFound, state.Outcome!.Value.RepairResult);
    }
}
