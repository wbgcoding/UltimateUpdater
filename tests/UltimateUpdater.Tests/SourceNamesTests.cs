using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// A source name that is an ordinary noun around a product ("Ollama models") follows the UI language;
/// a product name ("Windows Update", "winget") stays as the product spells it in every language.
/// </summary>
[Collection("UiLanguage")]
public class SourceNamesTests
{
    [Fact]
    public void Generic_source_names_follow_the_ui_language()
    {
        try
        {
            LocalizationService.Instance.Apply("de");

            Assert.Equal("Ollama-Modelle", SourceNames.Display("ollama"));
            Assert.Equal("VS-Code-Erweiterungen", SourceNames.Display("vscode-extension"));
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }

        Assert.Equal("Ollama models", SourceNames.Display("ollama"));
    }

    [Fact]
    public void Product_names_are_never_translated()
    {
        try
        {
            LocalizationService.Instance.Apply("de");

            Assert.Equal("Windows Update", SourceNames.Display("windowsupdate"));
            Assert.Equal("winget", SourceNames.Display("winget"));
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public void Translated_sources_are_still_known_ids()
    {
        Assert.True(SourceNames.IsKnownId("ollama"));
        Assert.True(SourceNames.IsKnownId("github-release"));
    }
}
