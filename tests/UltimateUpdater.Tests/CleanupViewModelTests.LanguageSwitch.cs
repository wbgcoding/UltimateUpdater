using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

public partial class CleanupViewModelTests
{
    [Fact]
    public void A_language_switch_refreshes_every_text_the_window_builds_in_code()
    {
        var viewModel = Create(CreateSettings());
        var raised = new HashSet<string>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        LocalizationService.Instance.Apply("de");
        try
        {
            viewModel.RefreshLocalizedText();
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }

        string[] expected =
        [
            nameof(viewModel.ShowAllCategoriesText), nameof(viewModel.DriveCaptionText),
            nameof(viewModel.ComponentStoreSizeText), nameof(viewModel.ComponentStoreRecommendedText),
            nameof(viewModel.LastDoneText), nameof(viewModel.AreaOfTotalText), nameof(viewModel.FreedText),
            nameof(viewModel.LiveFreedText),
        ];
        var missing = expected.Where(name => !raised.Contains(name)).ToList();
        Assert.True(missing.Count == 0, "not refreshed: " + string.Join(", ", missing));
    }
}
