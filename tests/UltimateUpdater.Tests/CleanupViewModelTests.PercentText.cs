using System.Globalization;

namespace UltimateUpdater.Tests;

public partial class CleanupViewModelTests
{
    [Fact]
    public void The_overall_clean_percent_text_follows_the_culture()
    {
        var viewModel = Create(CreateSettings());
        viewModel.CleanPercent = 50;

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            PercentTextTests.AssertGermanPercent("50", viewModel.CleanPercentText);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("50%", viewModel.CleanPercentText);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
