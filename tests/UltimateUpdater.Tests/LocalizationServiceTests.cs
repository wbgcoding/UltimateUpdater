using System.ComponentModel;
using System.Globalization;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>LocalizationService.Instance is a process-wide singleton (required for XAML x:Static binding), tests run sequentially within this class by xUnit default, so mutating shared culture state is safe here.</summary>
[Collection("UiLanguage")]
public class LocalizationServiceTests
{
    [Fact]
    public void Apply_en_sets_english_culture_and_strings()
    {
        LocalizationService.Instance.Apply("en");

        Assert.Equal("en", CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        Assert.Equal("Ultimate Updater", LocalizationService.Instance["AppTitle"]);
        Assert.Equal("Language", LocalizationService.Instance["LanguageLabel"]);
    }

    [Fact]
    public void Apply_de_sets_german_culture_and_strings()
    {
        LocalizationService.Instance.Apply("de");

        Assert.Equal("de", CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        Assert.Equal("Sprache", LocalizationService.Instance["LanguageLabel"]);
        Assert.Equal("Design", LocalizationService.Instance["ThemeLabel"]);
    }

    [Theory]
    [InlineData("es-ES", "es")]
    [InlineData("es-MX", "es")]
    [InlineData("pt-BR", "pt")]
    [InlineData("ja-JP", "ja")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("de-AT", "de")]
    [InlineData("ro-RO", "ro")]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr-CA", "fr")]
    public void System_language_follows_every_shipped_windows_language(string windowsCulture, string expectedLanguage)
    {
        var resolved = LocalizationService.ResolveSystemCulture(new CultureInfo(windowsCulture));

        Assert.True(resolved.Name == expectedLanguage || resolved.Parent.Name == expectedLanguage
                || resolved.Parent.Parent.Name == expectedLanguage,
            $"{windowsCulture} resolved to {resolved.Name}");
    }

    [Theory]
    [InlineData("sv-SE")]
    [InlineData("zh-TW")]
    [InlineData("ko-KR")]
    public void System_language_falls_back_to_english_when_it_is_not_shipped(string windowsCulture) =>
        Assert.Equal("en", LocalizationService.ResolveSystemCulture(new CultureInfo(windowsCulture)).Name);

    [Fact]
    public void Unknown_key_falls_back_to_the_key_itself()
    {
        LocalizationService.Instance.Apply("en");

        Assert.Equal("NoSuchKey", LocalizationService.Instance["NoSuchKey"]);
    }

    /// <summary>
    /// A row's state word, badges and menu headers are read from the resources IN CODE, so no binding
    /// refreshes them, after a language switch the table stayed in the old language while everything
    /// bound to Loc[...] had already changed, leaving the window in two languages at once.
    /// </summary>
    [Fact]
    public void A_row_re_reads_its_computed_text_when_the_language_changes()
    {
        LocalizationService.Instance.Apply("en");

        var row = new UltimateUpdater.ViewModels.UpdateRowViewModel(new UltimateUpdater.Models.UpdateItem
        {
            Id = "Contoso.App",
            SourceId = "winget",
            Category = UltimateUpdater.Models.UpdateCategory.Software,
            Name = "Contoso App",
            InstalledVersion = "1.0",
            AvailableVersion = "1.1",
            IsCritical = true,
        })
        {
            InstallState = UltimateUpdater.ViewModels.RowInstallState.Failed,
        };

        var english = row.InstallStateText;
        var raised = new List<string?>();
        row.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        LocalizationService.Instance.Apply("de");
        row.RefreshLocalizedText();

        Assert.Contains(nameof(UltimateUpdater.ViewModels.UpdateRowViewModel.InstallStateText), raised);
        Assert.Contains(nameof(UltimateUpdater.ViewModels.UpdateRowViewModel.CriticalLabel), raised);
        Assert.NotEqual(english, row.InstallStateText);

        LocalizationService.Instance.Apply("en");
    }

    [Fact]
    public void Apply_raises_PropertyChanged_for_live_rebind()
    {
        LocalizationService.Instance.Apply("en");
        var raised = false;
        void Handler(object? s, PropertyChangedEventArgs e) => raised = true;

        LocalizationService.Instance.PropertyChanged += Handler;
        try
        {
            LocalizationService.Instance.Apply("de");
        }
        finally
        {
            LocalizationService.Instance.PropertyChanged -= Handler;
        }

        Assert.True(raised);
    }
}
