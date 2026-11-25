using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

public class CodePageTextTests
{
    [Fact]
    public void A_single_byte_code_page_decodes_its_own_letters()
    {
        Assert.Equal("ä", CodePageText.Decode([0x84], 437, () => "fallback"));
        Assert.Equal("ä", CodePageText.Decode([0xE4], 1252, () => "fallback"));
    }

    [Fact]
    public void A_code_page_the_system_does_not_know_gives_the_fallback()
    {
        Assert.Equal("fallback", CodePageText.Decode([0x41], 0xFFFF_FFF0u, () => "fallback"));
    }

    [Fact]
    public void Plain_ascii_is_the_same_in_every_code_page()
    {
        Assert.Equal("DISM", CodePageText.Decode("DISM"u8.ToArray(), 850, () => "fallback"));
    }
}
