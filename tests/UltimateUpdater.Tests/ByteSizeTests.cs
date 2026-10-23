using System.Globalization;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="ByteSize.Format"/> follows the chosen UI language on purpose (a deliberate design decision,
/// not a default), de shows a comma decimal separator, en a period. <see cref="ByteSize.FormatInvariant"/>
/// is the separate path <c>ActivityLog</c> uses for its log lines, which must read the same regardless of
/// the reading machine's Windows language.
/// </summary>
[Collection("UiLanguage")]
public class ByteSizeTests
{
    [Fact]
    public void Format_uses_a_comma_under_german_ui_culture()
    {
        try
        {
            LocalizationService.Instance.Apply("de");

            Assert.Equal("1,5 KB", ByteSize.Format(1536));
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public void Format_uses_a_period_under_english_ui_culture()
    {
        LocalizationService.Instance.Apply("en");

        Assert.Equal("1.5 KB", ByteSize.Format(1536));
    }

    [Fact]
    public void FormatInvariant_always_uses_a_period_regardless_of_ui_culture()
    {
        try
        {
            LocalizationService.Instance.Apply("de");

            Assert.Equal("1.5 KB", ByteSize.FormatInvariant(1536));
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }

        Assert.Equal("1.5 KB", ByteSize.FormatInvariant(1536));
    }

    [Fact]
    public void Format_climbs_units_up_to_terabytes()
    {
        LocalizationService.Instance.Apply("en");

        Assert.Equal("0.0 B", ByteSize.Format(0));
        Assert.Equal("512.0 B", ByteSize.Format(512));
        Assert.Equal("1.0 GB", ByteSize.Format(1024L * 1024 * 1024));
        Assert.Equal("1.0 TB", ByteSize.Format(1024L * 1024 * 1024 * 1024));
    }
}
