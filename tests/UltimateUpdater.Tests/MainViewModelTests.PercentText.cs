using System.Globalization;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void The_overall_install_percent_text_follows_the_culture()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var row = new UpdateRowViewModel(new UpdateItem
            {
                Id = "x", SourceId = "winget", Category = UpdateCategory.Software, Name = "Example",
                InstalledVersion = "1.0", AvailableVersion = "1.1",
            });
            row.BeginInstall();
            row.ApplyInstallStatus(new InstallItemStatus("winget", "x", 50, "downloading", null, null));
            viewModel.InstallBatch.Add(row);

            var previous = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
                PercentTextTests.AssertGermanPercent("50", viewModel.InstallOverallPercentText);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
                Assert.Equal("50%", viewModel.InstallOverallPercentText);
            }
            finally
            {
                CultureInfo.CurrentUICulture = previous;
            }
        });
    }
}
