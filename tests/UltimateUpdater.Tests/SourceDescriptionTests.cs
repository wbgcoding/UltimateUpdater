using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

[Collection("UiLanguage")]
public class SourceDescriptionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_id_has_no_name_and_no_description(string? id)
    {
        Assert.Equal(string.Empty, SourceNames.Display(id));
        Assert.Equal(string.Empty, SourceNames.Description(id));
    }

    [Fact]
    public void Every_registered_source_is_a_known_name_with_a_readable_sentence_in_english_and_german()
    {
        try
        {
            foreach (var language in new[] { "en", "de" })
            {
                LocalizationService.Instance.Apply(language);
                foreach (var id in SourceRegistry.KnownIds)
                {
                    Assert.True(SourceNames.IsKnownId(id), $"{id} has no entry in the name table");

                    var display = SourceNames.Display(id);
                    var description = SourceNames.Description(id);

                    Assert.False(string.IsNullOrWhiteSpace(display), $"{language}: no name for {id}");
                    Assert.DoesNotContain("SourceName", display);
                    Assert.Contains(' ', description);
                    Assert.DoesNotContain("SourceDesc", description);
                    Assert.DoesNotContain("{0}", description);
                }
            }
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public void A_launcher_without_its_own_text_shares_the_template_with_its_own_name()
    {
        LocalizationService.Instance.Apply("en");

        var text = SourceNames.Description("epic");

        Assert.StartsWith("Opens the Epic Games client.", text);
    }

    [Fact]
    public void An_unknown_id_falls_back_to_the_launcher_template_with_the_raw_id()
    {
        LocalizationService.Instance.Apply("en");

        var text = SourceNames.Description("some-new-launcher");

        Assert.Contains("some-new-launcher", text);
        Assert.False(SourceNames.IsKnownId("some-new-launcher"));
    }

    [Fact]
    public void Known_ids_are_matched_ignoring_case()
    {
        Assert.True(SourceNames.IsKnownId("WinGet"));
        Assert.Equal(SourceNames.Display("winget"), SourceNames.Display("WINGET"));
    }
}
