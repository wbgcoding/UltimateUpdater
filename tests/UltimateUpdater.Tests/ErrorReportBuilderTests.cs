using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// The one-click error report. <see cref="ErrorReportBuilder.Build"/> is pure text formatting
/// (report shape, the 200-line cap, redaction), <see cref="ErrorReportBuilder.Prune"/> is the last-ten
/// disk housekeeping; both are exercised directly rather than through the clipboard/UI.
/// </summary>
public class ErrorReportBuilderTests
{
    [Fact]
    public void The_report_names_the_package_and_its_version_change()
    {
        var report = ErrorReportBuilder.Build("7-Zip", "winget", "23.0 → 23.1", "boom");

        Assert.Contains("7-Zip", report);
        Assert.Contains("winget", report);
        Assert.Contains("23.0 → 23.1", report);
    }

    [Fact]
    public void The_report_carries_machine_context_even_with_no_tool_output()
    {
        var report = ErrorReportBuilder.Build("7-Zip", "winget", null, null);

        Assert.Contains("App version:", report);
        Assert.Contains("Windows:", report);
        Assert.Contains("Architecture:", report);
    }

    [Fact]
    public void The_exit_code_is_read_the_same_way_the_explainer_itself_reads_it()
    {
        var report = ErrorReportBuilder.Build(
            "7-Zip", "winget", null, "Installer failed with exit code: 1603");

        Assert.Contains("Exit code: 1603", report);
    }

    [Fact]
    public void Output_beyond_two_hundred_lines_is_capped()
    {
        var output = string.Join('\n', Enumerable.Range(1, 500).Select(i => $"line {i}"));

        var report = ErrorReportBuilder.Build("7-Zip", "winget", null, output);

        Assert.Contains("line 200", report);
        Assert.DoesNotContain("line 201", report);
    }

    [Fact]
    public void A_user_profile_path_in_the_output_is_redacted()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var output = $"failed to write {profile}\\AppData\\thing.tmp";

        var report = ErrorReportBuilder.Build("7-Zip", "winget", null, output);

        Assert.DoesNotContain(profile, report);
        Assert.Contains("%USERPROFILE%", report);
    }

    /// <summary>File names are zero-padded so plain ordinal text sort is also chronological order,
    /// exactly like <see cref="ErrorReportBuilder.Save"/>'s own timestamp-named files.</summary>
    private static string ReportFileName(int index) => $"error-report-2026-{index:D4}.txt";

    [Fact]
    public void Pruning_keeps_only_the_ten_newest_reports()
    {
        var dir = Directory.CreateTempSubdirectory("wu-reports-test-").FullName;
        try
        {
            for (var i = 0; i < 15; i++)
                File.WriteAllText(Path.Combine(dir, ReportFileName(i)), "x");

            ErrorReportBuilder.Prune(dir);

            var remaining = Directory.GetFiles(dir, "error-report-*.txt").Select(Path.GetFileName).ToList();
            Assert.Equal(10, remaining.Count);
            // the ten HIGHEST-numbered (newest) file names survive, the five oldest are gone
            for (var i = 0; i < 5; i++)
                Assert.DoesNotContain(ReportFileName(i), remaining);
            for (var i = 5; i < 15; i++)
                Assert.Contains(ReportFileName(i), remaining);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Saving_a_report_writes_a_file_and_prunes_in_one_step()
    {
        var dir = Directory.CreateTempSubdirectory("wu-reports-test-").FullName;
        try
        {
            for (var i = 0; i < 10; i++)
                File.WriteAllText(Path.Combine(dir, ReportFileName(i)), "x");

            ErrorReportBuilder.Save("a fresh report", new LoggingService(), dir);

            Assert.Equal(10, Directory.GetFiles(dir, "error-report-*.txt").Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
