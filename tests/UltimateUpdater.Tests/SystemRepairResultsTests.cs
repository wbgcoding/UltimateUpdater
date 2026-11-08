using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

public class SystemRepairResultsTests
{
    private static readonly string[] CleanLog =
    [
        "2026-11-07 10:00:01, Info                  CSI    00000001 [SR] Beginning Verify and Repair transaction",
        "2026-11-07 10:03:40, Info                  CSI    00000002 [SR] Verify complete",
        "2026-11-07 10:03:41, Info                  CSI    00000003 [SR] Repair complete",
    ];

    private static readonly string[] RepairedLog =
    [
        "2026-11-07 10:00:01, Info                  CSI    00000001 [SR] Beginning Verify and Repair transaction",
        "2026-11-07 10:02:10, Info                  CSI    00000002 [SR] Repairing corrupted file \\??\\C:\\Windows\\System32\\x.dll from store",
        "2026-11-07 10:02:11, Info                  CSI    00000003 [SR] Repaired file \\??\\C:\\Windows\\System32\\x.dll by copying from backup",
        "2026-11-07 10:03:41, Info                  CSI    00000004 [SR] Repair complete",
    ];

    private static readonly string[] ProblemLog =
    [
        "2026-11-07 10:02:10, Info                  CSI    00000002 [SR] Repairing corrupted file \\??\\C:\\Windows\\a.dll from store",
        "2026-11-07 10:02:12, Info                  CSI    00000003 [SR] Cannot repair member file [l:10]\\\"b.dll\\\" of Microsoft-Windows-X, Version = 10.0.1, pA = PROCESSOR_ARCHITECTURE_AMD64 (9)",
        "2026-11-07 10:03:41, Info                  CSI    00000004 [SR] Repair complete",
    ];

    [Fact]
    public void A_clean_scan_log_is_ok() =>
        Assert.Equal(RepairResult.Ok, SystemRepairResults.ClassifyCbsLines(CleanLog));

    [Fact]
    public void A_log_with_a_fixed_file_is_repaired() =>
        Assert.Equal(RepairResult.Repaired, SystemRepairResults.ClassifyCbsLines(RepairedLog));

    [Fact]
    public void A_file_that_could_not_be_fixed_is_a_problem_even_next_to_fixed_ones() =>
        Assert.Equal(RepairResult.ProblemsFound, SystemRepairResults.ClassifyCbsLines(ProblemLog));

    [Fact]
    public void No_lines_is_ok() =>
        Assert.Equal(RepairResult.Ok, SystemRepairResults.ClassifyCbsLines([]));

    [Theory]
    [InlineData(0, 0, 0, RepairResult.Ok)]
    [InlineData(0, 1, 0, RepairResult.Repaired)]
    [InlineData(1, 1, 0, RepairResult.Repaired)]
    [InlineData(0, 0, 1, RepairResult.ProblemsFound)]
    [InlineData(0, 1, 1, RepairResult.ProblemsFound)]
    [InlineData(1, 0, 0, RepairResult.ProblemsFound)]
    [InlineData(740, 740, 740, RepairResult.ProblemsFound)]
    public void The_image_check_follows_the_exit_codes(int check, int scan, int restore, RepairResult expected) =>
        Assert.Equal(expected, SystemRepairResults.ClassifyDism(check, scan, restore));

    [Fact]
    public void A_step_that_did_not_run_leaves_the_image_result_open()
    {
        Assert.Null(SystemRepairResults.ClassifyDism(0, null, 0));
        Assert.Null(SystemRepairResults.ClassifyDism(null, null, null));
    }

    [Fact]
    public void Only_the_lines_added_after_the_start_offset_are_read()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-cbs-").FullName, "CBS.log");
        File.WriteAllText(path, "old [SR] Cannot repair member file\r\n");
        var start = SystemRepairResults.CbsLength(path);
        Assert.NotNull(start);
        File.AppendAllText(path, "new [SR] Repaired file x\r\nnext line\r\n");

        var lines = SystemRepairResults.ReadCbsLinesSince(path, start!.Value);

        Assert.Equal(["new [SR] Repaired file x", "next line"], lines);
        Assert.Equal(RepairResult.Repaired, SystemRepairResults.ClassifyCbsLines(lines!));
    }

    [Fact]
    public void A_log_rotated_during_the_run_is_read_from_the_top()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-cbs-").FullName, "CBS.log");
        File.WriteAllText(path, "short\r\n");

        var lines = SystemRepairResults.ReadCbsLinesSince(path, 100_000);

        Assert.Equal(["short"], lines);
    }

    [Fact]
    public void A_missing_log_is_unknown_not_clean()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wu-cbs-missing-{Guid.NewGuid():N}.log");

        Assert.Null(SystemRepairResults.CbsLength(path));
        Assert.Null(SystemRepairResults.ReadCbsLinesSince(path, 0));
    }
}
