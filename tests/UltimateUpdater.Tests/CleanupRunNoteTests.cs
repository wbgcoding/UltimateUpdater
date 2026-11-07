using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// The reason a target was skipped (admin rights missing, time limit) only showed in the live line while
/// the target was cleaning, so after the run it was never readable. It now stays on a skipped row.
/// </summary>
public class CleanupRunNoteTests
{
    private static CleanupItemViewModel Row() =>
        new(CleanupCatalog.Build().Single(t => t.Id == "component-cleanup"), () => { }, _ => { });

    [Fact]
    public void A_skipped_row_shows_its_reason_after_the_run()
    {
        var row = Row();
        row.DetailText = "Skipped: this needs admin rights.";
        row.RunState = CleanupTargetState.Skipped;

        Assert.True(row.ShowsRunNote);
    }

    [Fact]
    public void The_live_line_and_not_the_note_carries_the_text_while_cleaning()
    {
        var row = Row();
        row.RunState = CleanupTargetState.Skipped;
        row.DetailText = "DISM /StartComponentCleanup";
        row.IsCleaning = true;

        Assert.False(row.ShowsRunNote);
    }

    [Theory]
    [InlineData(CleanupTargetState.Done)]
    [InlineData(CleanupTargetState.Idle)]
    [InlineData(CleanupTargetState.Stopped)]
    public void Any_other_state_shows_no_note(CleanupTargetState state)
    {
        var row = Row();
        row.DetailText = "left over";
        row.RunState = state;

        Assert.False(row.ShowsRunNote);
    }
}
