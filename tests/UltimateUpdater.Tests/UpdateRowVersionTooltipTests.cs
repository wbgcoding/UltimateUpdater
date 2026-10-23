using UltimateUpdater.Models;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>One tooltip for the whole version lane, covering both shapes a row can be in.</summary>
public class UpdateRowVersionTooltipTests
{
    private static UpdateRowViewModel Row(string? installedVersion, string? availableVersion) => new(new UpdateItem
    {
        Id = "x",
        SourceId = "winget",
        Category = UpdateCategory.Software,
        Name = "Example",
        InstalledVersion = installedVersion,
        AvailableVersion = availableVersion,
    });

    [Fact]
    public void Both_versions_known_shows_installed_arrow_available()
    {
        var row = Row("1.0.0", "1.1.0");

        Assert.Equal("1.0.0 → 1.1.0", row.VersionChangeTooltip);
    }

    [Fact]
    public void Only_the_available_version_is_known_shows_that_alone()
    {
        var row = Row(installedVersion: null, availableVersion: "1.1.0");

        Assert.Equal("1.1.0", row.VersionChangeTooltip);
    }
}
