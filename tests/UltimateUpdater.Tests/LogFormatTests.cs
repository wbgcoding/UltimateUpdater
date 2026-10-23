using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// The log is meant to be read months later and to cost as little disk as that allows. These fix both
/// halves: the header of a line stays short, and a tool stuck in a retry loop cannot fill the file with
/// the same sentence a thousand times.
/// </summary>
public class LogFormatTests
{
    private static (LoggingService Log, List<string> Lines) Capture()
    {
        var lines = new List<string>();
        return (new LoggingService(lines.Add), lines);
    }

    [Fact]
    public void A_line_carries_the_time_a_level_letter_and_the_message()
    {
        var (log, lines) = Capture();

        log.Info("scan started");

        var line = Assert.Single(lines);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2} I scan started$", line);
    }

    [Fact]
    public void An_event_carries_its_category_between_the_level_and_the_message()
    {
        var (log, lines) = Capture();

        log.Event(ActivityLog.Install, "ok update winget:Firefox 3.0s");

        Assert.Matches(@"^\d{2}:\d{2}:\d{2} I inst ok update winget:Firefox 3\.0s$", Assert.Single(lines));
    }

    [Theory]
    [InlineData("W")]
    [InlineData("E")]
    public void Warnings_and_errors_are_one_letter_too(string expected)
    {
        var (log, lines) = Capture();

        if (expected == "W")
            log.Warn("something looked odd");
        else
            log.Error("something broke");

        Assert.Contains($" {expected} ", Assert.Single(lines), StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_line_over_and_over_is_counted_rather_than_repeated()
    {
        var (log, lines) = Capture();

        for (var i = 0; i < 500; i++)
            log.Warn("winget: retrying");

        log.Info("moving on");

        Assert.Equal(3, lines.Count);
        Assert.EndsWith("winget: retrying", lines[0], StringComparison.Ordinal);
        Assert.Contains("×500", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("moving on", lines[2], StringComparison.Ordinal);
    }

    /// <summary>A count still waiting when the app stops belongs in the file, not in memory.</summary>
    [Fact]
    public void A_pending_count_is_written_out_on_flush()
    {
        var (log, lines) = Capture();

        log.Info("same");
        log.Info("same");
        log.Flush();

        Assert.Equal(2, lines.Count);
        Assert.Contains("×2", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Two_different_lines_are_both_written()
    {
        var (log, lines) = Capture();

        log.Info("first");
        log.Info("second");

        Assert.Equal(2, lines.Count);
    }
}
