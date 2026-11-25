using UltimateUpdater.Services.Cleanup;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>Label and value lines built in code follow each language's own punctuation, like the labels in the resource files.</summary>
[Collection("UiLanguage")]
public class LabelValueTests
{
    [Theory]
    [InlineData("en", ": ")]
    [InlineData("fr", " : ")]
    [InlineData("zh-Hans", "：")]
    [InlineData("ja", "：")]
    public void The_risk_tooltip_joins_level_and_consequence_in_the_language_s_style(string language, string expectedJoin)
    {
        try
        {
            LocalizationService.Instance.Apply(language);
            var item = new CleanupItemViewModel(CleanupCatalog.Build().First(), () => { });

            Assert.Contains(expectedJoin, item.RiskTooltip);
            if (language != "en")
                Assert.DoesNotContain(": ", item.RiskTooltip.Replace(" : ", string.Empty));
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public void The_cleanup_shares_are_joined_with_the_list_separator_of_the_language()
    {
        try
        {
            LocalizationService.Instance.Apply("ja");

            var text = CleanupItemViewModel.FormatSkippedShares("{0}", 2L * 1024 * 1024, 1024 * 1024);

            Assert.Contains("、", text);
            Assert.DoesNotContain(", ", text);
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }
}
