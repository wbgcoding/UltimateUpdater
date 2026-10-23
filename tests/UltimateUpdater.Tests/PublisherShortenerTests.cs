using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>The one shortener every displayed publisher name goes through (winget, Windows Update, pip):
/// strips a trailing legal-entity suffix and a leading "The", drops an embedded e-mail address, and caps
/// the result at 24 characters. The full name is untouched elsewhere (tooltips).</summary>
public class PublisherShortenerTests
{
    [Theory]
    [InlineData("Google LLC", "Google")]
    [InlineData("Microsoft Corporation", "Microsoft")]
    [InlineData("The Pallets Team", "Pallets")]
    [InlineData("Pallets", "Pallets")]
    [InlineData("Acme Inc.", "Acme")]
    [InlineData("Acme Inc", "Acme")]
    [InlineData("Acme, Inc.", "Acme")]
    [InlineData("Acme Ltd.", "Acme")]
    [InlineData("Acme Ltd", "Acme")]
    [InlineData("Acme GmbH", "Acme")]
    [InlineData("Acme AG", "Acme")]
    [InlineData("Acme Corp", "Acme")]
    [InlineData("Acme Co.", "Acme")]
    [InlineData("Acme S.A.", "Acme")]
    [InlineData("Acme B.V.", "Acme")]
    [InlineData("Werkzeug and contributors", "Werkzeug")]
    [InlineData("NumPy Developers", "NumPy")]
    public void Known_shapes_shorten_to_their_expected_form(string raw, string expected) =>
        Assert.Equal(expected, PublisherShortener.Shorten(raw));

    [Fact]
    public void An_embedded_email_is_dropped()
    {
        Assert.Equal("Jane Doe", PublisherShortener.Shorten("Jane Doe <jane@example.com>"));
        Assert.Equal("Jane Doe", PublisherShortener.Shorten("Jane Doe (jane@example.com)"));
    }

    [Fact]
    public void A_name_that_needs_no_shortening_is_returned_unchanged() =>
        Assert.Equal("Mozilla", PublisherShortener.Shorten("Mozilla"));

    [Fact]
    public void A_long_name_is_capped_at_24_characters()
    {
        var result = PublisherShortener.Shorten("A Genuinely Very Long Publisher Name With No Suffix At All");

        Assert.True(result.Length <= 24, $"'{result}' is {result.Length} characters, expected at most 24");
    }

    /// <summary>
    /// Hostile metadata: a huge run of address-like characters with no dot after the '@' used to make the
    /// e-mail pattern backtrack over the whole text; it now returns at once with a capped result.
    /// </summary>
    [Theory]
    [InlineData('a')]
    [InlineData('@')]
    public void Pathological_author_text_returns_quickly(char filler)
    {
        var hostile = new string(filler, 200_000) + "@" + new string('a', 200_000);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = PublisherShortener.Shorten(hostile);
        watch.Stop();

        Assert.True(result.Length <= 24);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    /// <summary>A suffix word IS the whole name: stripping it would leave nothing to show, so it is left
    /// alone rather than blanked out.</summary>
    [Fact]
    public void A_name_that_is_only_a_suffix_word_is_left_alone() =>
        Assert.Equal("Team", PublisherShortener.Shorten("Team"));
}
