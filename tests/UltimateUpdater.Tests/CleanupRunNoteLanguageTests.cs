using UltimateUpdater.Services.Cleanup;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>The note on a skipped target is resolved when the run ends; a language switch must say it again in the new language.</summary>
[Collection("UiLanguage")]
public class CleanupRunNoteLanguageTests
{
    [Fact]
    public void A_skipped_row_keeps_its_reason_in_the_language_in_use()
    {
        try
        {
            LocalizationService.Instance.Apply("de");
            var row = new CleanupItemViewModel(CleanupCatalog.Build().Single(t => t.Id == "component-cleanup"), () => { }, _ => { });
            row.SkipReason = new CleanupSkipReason("CleanupNeedsAdminSkipped");
            row.DetailText = row.SkipReason.Text;
            row.RunState = CleanupTargetState.Skipped;
            var german = row.DetailText;

            LocalizationService.Instance.Apply("en");
            row.RefreshLocalizedText();

            Assert.NotEqual(german, row.DetailText);
            Assert.Equal(LocalizationService.Instance["CleanupNeedsAdminSkipped"], row.DetailText);
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }
}
