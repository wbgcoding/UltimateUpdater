using System.Globalization;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;
using Wpf.Ui.Controls;

namespace UltimateUpdater.Tests;

[Collection("UiLanguage")]
public class CategoryIconConverterTests
{
    private static UpdateRowViewModel ManualRow() => new(new UpdateItem
    {
        Id = "steam",
        SourceId = "steam",
        Category = UpdateCategory.Games,
        Name = "Steam",
        OpenTarget = "steam://open/downloads",
        Tier = SourceTier.C,
    });

    private static object Icon(string? key) =>
        new CategoryIconConverter().Convert(key, typeof(SymbolRegular), null!, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("en", "Open yourself")]
    [InlineData("de", "Selbst öffnen")]
    public void The_manual_group_is_named_and_iconned_in_both_languages(string language, string expectedName)
    {
        LocalizationService.Instance.Apply(language);
        try
        {
            var row = ManualRow();

            Assert.Equal(expectedName, row.CategoryDisplay);
            // The icon follows the language-independent key, not the translated name.
            Assert.Equal("ManualUpdate", row.CategoryKey);
            Assert.Equal(SymbolRegular.WindowArrowUp20, Icon(row.CategoryKey));
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public void Every_category_key_has_its_own_icon_and_an_unknown_one_falls_back_to_a_box()
    {
        Assert.Equal(SymbolRegular.Apps24, Icon(nameof(UpdateCategory.Software)));
        Assert.Equal(SymbolRegular.Code24, Icon(nameof(UpdateCategory.DeveloperTools)));
        Assert.Equal(SymbolRegular.Desktop24, Icon(nameof(UpdateCategory.SystemAndDrivers)));
        Assert.Equal(SymbolRegular.Games24, Icon(nameof(UpdateCategory.Games)));
        Assert.Equal(SymbolRegular.StoreMicrosoft24, Icon(nameof(UpdateCategory.Store)));
        Assert.Equal(SymbolRegular.WindowArrowUp20, Icon("GitHubManual"));
        Assert.Equal(SymbolRegular.Box24, Icon("nonsense"));
        Assert.Equal(SymbolRegular.Box24, Icon(null));
    }
}
