using System.Text;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>Pure parsing over already-decoded INF text, plus the two byte-level decode shapes an
/// oem*.inf can actually arrive in. The locale-priority tests mutate
/// <see cref="LocalizationService"/>'s process-wide current culture (see LocalizationServiceTests' own
/// note); xUnit runs tests within one class sequentially, and each test restores "en" itself.</summary>
[Collection("UiLanguage")]
public class DriverPackageInfReaderTests
{
    private const string SampleInf = """
        [Version]
        Signature="$WINDOWS NT$"
        Class=Sound
        Provider=%ProviderName%
        DriverVer=03/15/2024,2.5.1.0

        [Strings]
        ProviderName="Acme Corp"
        """;

    [Fact]
    public void Parse_reads_provider_class_version_and_date_and_resolves_a_string_token()
    {
        var info = DriverPackageInfReader.Parse(SampleInf);

        Assert.NotNull(info);
        Assert.Equal("Acme Corp", info!.Provider);
        Assert.Equal("Sound", info.ClassName);
        Assert.Equal("2.5.1.0", info.DriverVersion);
        Assert.Equal(new DateTimeOffset(2024, 3, 15, 0, 0, 0, TimeSpan.Zero), info.DriverDate);
    }

    [Fact]
    public void A_literal_quoted_provider_needs_no_string_table_lookup()
    {
        const string inf = """
            [Version]
            Class=Net
            Provider="Literal Vendor"
            """;

        var info = DriverPackageInfReader.Parse(inf);

        Assert.Equal("Literal Vendor", info!.Provider);
    }

    [Fact]
    public void An_unresolvable_token_is_returned_as_is_rather_than_blanked()
    {
        const string inf = """
            [Version]
            Class=Net
            Provider=%MissingToken%
            """;

        var info = DriverPackageInfReader.Parse(inf);

        Assert.Equal("%MissingToken%", info!.Provider);
    }

    [Fact]
    public void No_Version_section_at_all_returns_null()
    {
        const string inf = "[Strings]\nFoo=Bar";

        Assert.Null(DriverPackageInfReader.Parse(inf));
    }

    [Fact]
    public void A_missing_DriverVer_leaves_version_and_date_null()
    {
        const string inf = """
            [Version]
            Class=Net
            Provider=Acme
            """;

        var info = DriverPackageInfReader.Parse(inf);

        Assert.Null(info!.DriverVersion);
        Assert.Null(info.DriverDate);
    }

    [Fact]
    public void Comment_lines_inside_a_section_are_ignored()
    {
        const string inf = """
            [Version]
            ; this is a comment, not a key
            Class=Net
            Provider=Acme
            """;

        var info = DriverPackageInfReader.Parse(inf);

        Assert.Equal("Net", info!.ClassName);
    }

    [Fact]
    public void DecodeInfBytes_reads_a_utf16_le_bom_file()
    {
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("[Version]\r\nClass=Net\r\n")).ToArray();

        var text = DriverPackageInfReader.DecodeInfBytes(bytes);

        Assert.Contains("Class=Net", text);
    }

    [Fact]
    public void DecodeInfBytes_falls_back_to_ansi_for_a_plain_byte_file()
    {
        var bytes = Encoding.ASCII.GetBytes("[Version]\r\nClass=Net\r\n");

        var text = DriverPackageInfReader.DecodeInfBytes(bytes);

        Assert.Contains("Class=Net", text);
    }

    private const string LocalizedSampleInf = """
        [Version]
        Class=Net
        Provider=%ProviderName%

        [Strings]
        ProviderName="Neutral Vendor"

        [Strings.0804]
        ProviderName="供应商"

        [Strings.0407]
        ProviderName="Deutscher Hersteller"
        """;

    [Fact]
    public void The_neutral_Strings_section_wins_even_when_a_locale_section_also_defines_the_token()
    {
        LocalizationService.Instance.Apply("de");
        try
        {
            var info = DriverPackageInfReader.Parse(LocalizedSampleInf);

            Assert.Equal("Neutral Vendor", info!.Provider);
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public void Falls_back_to_the_apps_own_ui_culture_section_when_the_neutral_section_lacks_the_token()
    {
        const string inf = """
            [Version]
            Class=Net
            Provider=%ProviderName%

            [Strings.0804]
            ProviderName="供应商"

            [Strings.0407]
            ProviderName="Deutscher Hersteller"
            """;

        LocalizationService.Instance.Apply("de");
        try
        {
            var info = DriverPackageInfReader.Parse(inf);

            Assert.Equal("Deutscher Hersteller", info!.Provider);
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public void Falls_back_to_0409_when_neither_the_neutral_nor_the_apps_own_culture_section_has_it()
    {
        const string inf = """
            [Version]
            Class=Net
            Provider=%ProviderName%

            [Strings.0804]
            ProviderName="供应商"

            [Strings.0409]
            ProviderName="English Vendor"
            """;

        // Turkish has no section of its own in this INF, so this proves the LAST resort, not the
        // app-culture branch (that is covered by the German test above).
        LocalizationService.Instance.Apply("tr");
        try
        {
            var info = DriverPackageInfReader.Parse(inf);

            Assert.Equal("English Vendor", info!.Provider);
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public void Never_resolves_against_a_locale_section_that_is_neither_the_apps_own_nor_0409()
    {
        const string inf = """
            [Version]
            Class=Net
            Provider=%ProviderName%

            [Strings.0804]
            ProviderName="供应商"
            """;

        LocalizationService.Instance.Apply("de");
        try
        {
            var info = DriverPackageInfReader.Parse(inf);

            // Neither [Strings], [Strings.0407] (German) nor [Strings.0409] exist in this file: the
            // token must stay unresolved rather than pick up the Chinese section that IS present.
            Assert.Equal("%ProviderName%", info!.Provider);
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }
}
