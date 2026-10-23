using UltimateUpdater.Models;
using UltimateUpdater.Services.Install;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="InstallPreflightLineViewModel.FromBlockedGroups"/> folds rows blocked by the SAME
/// program into one line, never per row, exactly like the dialog it replaced
/// (<see cref="Services.Install.InstallBlockers.GroupBlockedRows{TRow}"/>) already did.
/// </summary>
public class InstallPreflightLineViewModelTests
{
    private static UpdateRowViewModel Row(string id, string name) => new(new UpdateItem
    {
        Id = id,
        SourceId = "winget",
        Category = UpdateCategory.Software,
        Name = name,
    });

    /// <summary>The list item's UI Automation name comes from ToString; the default would be the type
    /// name, which a screen reader then reads out.</summary>
    [Fact]
    public void A_line_reads_as_its_own_text_not_as_its_type_name()
    {
        var blocked = FromOneRow("Discord", new BlockedInstall(null!, "Discord", ["Discord"]));
        Assert.Equal(blocked.Text, blocked.ToString());
        Assert.DoesNotContain("InstallPreflightLineViewModel", blocked.ToString());

        var plain = new InstallPreflightLineViewModel("Low disk space");
        Assert.Equal("Low disk space", plain.ToString());
    }

    private static InstallPreflightLineViewModel FromOneRow(string name, BlockedInstall blocker) =>
        Assert.Single(InstallPreflightLineViewModel.FromBlockedGroups([(Row(name + ".id", name), blocker)]));

    [Fact]
    public void Two_rows_blocked_by_the_same_program_and_one_by_another_produce_two_lines()
    {
        var visualStudio = new BlockedInstall(null!, "Visual Studio", ["devenv"]);
        var discord = new BlockedInstall(null!, "Discord", ["Discord"]);

        var rowA = Row("Microsoft.VisualStudio.A", "VS Component A");
        var rowB = Row("Microsoft.VisualStudio.B", "VS Component B");
        var rowC = Row("Discord.Discord", "Discord");

        var lines = InstallPreflightLineViewModel.FromBlockedGroups(
        [
            (rowA, visualStudio),
            (rowB, visualStudio),
            (rowC, discord),
        ]);

        Assert.Equal(2, lines.Count);

        var vsLine = Assert.Single(lines, l => l.Text.Contains("Visual Studio"));
        Assert.Contains("VS Component A", vsLine.Text);
        Assert.Contains("VS Component B", vsLine.Text);
        Assert.True(vsLine.IsBlocker);
        Assert.Equal([rowA, rowB], vsLine.BlockedRows);

        var discordLine = Assert.Single(lines, l => l.Text.Contains("Discord"));
        Assert.Contains("Discord", discordLine.Text);
        Assert.Equal([rowC], discordLine.BlockedRows);
    }

    [Fact]
    public void A_blocker_line_carries_no_buttons_of_its_own()
    {
        var blocker = new BlockedInstall(null!, "Visual Studio", ["devenv"]);
        var row = Row("Microsoft.VisualStudio.A", "VS Component A");

        var line = Assert.Single(InstallPreflightLineViewModel.FromBlockedGroups([(row, blocker)]));

        Assert.True(line.IsBlocker);
        Assert.False(line.HasAction);
    }
}
