using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// Pins the search behaviour the table relies on. The fuzzy score comes from a library, so these
/// also catch a scoring change slipping in with a dependency update.
/// </summary>
public class UpdateFilterTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_query_matches_everything(string query)
    {
        Assert.True(UpdateFilter.MatchesQuery(query, "Mozilla Firefox", "Mozilla.Firefox"));
    }

    [Fact]
    public void Substring_of_the_name_matches_regardless_of_case()
    {
        Assert.True(UpdateFilter.MatchesQuery("firefox", "Mozilla Firefox", "Mozilla.Firefox"));
        Assert.True(UpdateFilter.MatchesQuery("FIREFOX", "Mozilla Firefox", "Mozilla.Firefox"));
    }

    [Fact]
    public void Substring_of_the_id_matches_when_the_name_does_not()
    {
        Assert.True(UpdateFilter.MatchesQuery("7zip", "Seven Zip", "7zip.7zip"));
    }

    [Fact]
    public void Surrounding_whitespace_is_ignored()
    {
        Assert.True(UpdateFilter.MatchesQuery("  firefox  ", "Mozilla Firefox", "Mozilla.Firefox"));
    }

    [Fact]
    public void A_typo_still_finds_the_package()
    {
        Assert.True(UpdateFilter.MatchesQuery("firefx", "Mozilla Firefox", "Mozilla.Firefox"));
    }

    [Fact]
    public void An_unrelated_query_does_not_match()
    {
        Assert.False(UpdateFilter.MatchesQuery("photoshop", "Mozilla Firefox", "Mozilla.Firefox"));
    }

    /// <summary>Below <see cref="UpdateFilter.MinFuzzyQueryLength"/> the fuzzy fallback is skipped entirely
    ///, a one- or two-character query needs every character to line up to reach the threshold anyway,
    /// which the substring check above already tested.</summary>
    [Fact]
    public void A_query_shorter_than_the_fuzzy_minimum_only_matches_as_a_substring()
    {
        Assert.False(UpdateFilter.MatchesQuery("fx", "Mozilla Firefox", "Mozilla.Firefox"));
        Assert.True(UpdateFilter.MatchesQuery("fi", "Mozilla Firefox", "Mozilla.Firefox"));
    }

    [Fact]
    public void Scoring_an_empty_candidate_is_zero_rather_than_a_throw()
    {
        Assert.Equal(0, UpdateFilter.Score("firefox", ""));
        Assert.Equal(0, UpdateFilter.Score("", "Mozilla Firefox"));
    }

    // --- Source and publisher widen the match too ------------------------------------------

    [Fact]
    public void A_query_matching_only_the_source_finds_the_row()
    {
        Assert.True(UpdateFilter.MatchesQuery(
            "windows update", "KB5041585", "KB5041585", source: "Windows Update", publisher: null));
    }

    [Fact]
    public void A_query_matching_only_the_publisher_finds_the_row()
    {
        Assert.True(UpdateFilter.MatchesQuery(
            "mozilla", "Firefox", "Mozilla.Firefox", source: "winget", publisher: "Mozilla"));
    }

    [Fact]
    public void A_null_source_or_publisher_is_simply_not_a_match_source_never_throws()
    {
        Assert.False(UpdateFilter.MatchesQuery(
            "photoshop", "eslint", "eslint", source: "npm", publisher: null));
    }

    [Fact]
    public void Omitting_source_and_publisher_behaves_exactly_as_before()
    {
        Assert.True(UpdateFilter.MatchesQuery("firefox", "Mozilla Firefox", "Mozilla.Firefox"));
        Assert.False(UpdateFilter.MatchesQuery("photoshop", "Mozilla Firefox", "Mozilla.Firefox"));
    }
}
