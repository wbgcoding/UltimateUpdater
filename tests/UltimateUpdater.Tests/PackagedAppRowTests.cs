using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// A Store app updated through winget is registered at once but applied by Windows only after the app
/// has been closed, so the version often looks unchanged right after. That row says so instead of
/// calling it a failure, and it never counts toward the "ignore this update" tally.
/// </summary>
public class PackagedAppRowTests
{
    private static UpdateRowViewModel Row(bool packagedApp)
    {
        var row = new UpdateRowViewModel(new UpdateItem
        {
            Id = "Zq.Chat",
            SourceId = "winget",
            Category = UpdateCategory.Software,
            Name = "Zq Chat",
            InstalledVersion = "1.0",
            AvailableVersion = "1.1",
        })
        {
            IsPackagedApp = packagedApp,
        };
        row.VersionUnchanged = true;
        return row;
    }

    [Fact]
    public void A_store_app_with_an_unchanged_version_says_windows_installs_it_after_closing_the_app()
    {
        var row = Row(packagedApp: true);
        var expected = string.Format(LocalizationService.Instance["InstallMsixDeferred"], "Zq Chat");

        Assert.Equal(expected, row.VersionUnchangedText);
        Assert.Equal(expected, row.VersionUnchangedTooltip);
        Assert.Contains("Zq Chat", row.VersionUnchangedText);
    }

    [Fact]
    public void Any_other_row_keeps_the_general_unchanged_text()
    {
        var row = Row(packagedApp: false);

        Assert.Equal(LocalizationService.Instance["InstallVersionUnchanged"], row.VersionUnchangedText);
        Assert.NotEqual(string.Format(LocalizationService.Instance["InstallMsixDeferred"], "Zq Chat"), row.VersionUnchangedTooltip);
    }

    [Fact]
    public void Marking_the_row_as_a_store_app_refreshes_the_text_in_place()
    {
        var row = Row(packagedApp: false);
        var raised = new List<string?>();
        row.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        row.IsPackagedApp = true;

        Assert.Contains(nameof(UpdateRowViewModel.VersionUnchangedText), raised);
        Assert.Contains(nameof(UpdateRowViewModel.VersionUnchangedTooltip), raised);
    }
}
