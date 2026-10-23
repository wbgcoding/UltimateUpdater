using Raffinert.FuzzySharp;

namespace UltimateUpdater.Services;

/// <summary>
/// Pure filtering/ranking logic for the update table, kept out of the ViewModel so it can be tested
/// directly. A row matches when the query is a substring (cheap, always correct for exact typing) or
/// when the fuzzy score clears the threshold.
/// </summary>
public static class UpdateFilter
{
    /// <summary>Chosen so obvious typos still match while unrelated names do not.</summary>
    public const int FuzzyScoreThreshold = 60;

    /// <summary>
    /// Below this length the fuzzy pass is skipped and the substring check alone decides. It is the
    /// expensive half of the filter and it runs per row and per field, on every keystroke, and it cannot
    /// change the answer here anyway: a one- or two-character query needs every character to line up to
    /// reach the threshold at all, which is exactly what the substring check already tested.
    /// </summary>
    public const int MinFuzzyQueryLength = 3;

    /// <summary>
    /// <paramref name="source"/> (the display name, e.g. "Windows Update") and
    /// <paramref name="publisher"/> (<see cref="Sources.PublisherFromId"/>, null where the id carries
    /// none) widen the match too, both optional so every existing caller keeps compiling unchanged.
    /// Substring only, deliberately NOT run through the fuzzy score the way name/id are: both fields are
    /// short, generic words ("winget", "npm", "Mozilla"), and short strings collide under
    /// FuzzySharp's PartialRatio far more easily than a real product name does (found: the query "eslint"
    /// fuzzy-matched the source "winget" above the threshold): a false positive there would put an
    /// unrelated row into a search result, which is worse than a real typo in a source name not being
    /// forgiven.
    /// </summary>
    public static bool MatchesQuery(string query, string name, string id, string? source = null, string? publisher = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var trimmed = query.Trim();

        if (Contains(name, trimmed) || Contains(id, trimmed) || Contains(source, trimmed) || Contains(publisher, trimmed))
            return true;

        if (trimmed.Length < MinFuzzyQueryLength)
            return false;

        return Score(trimmed, name) >= FuzzyScoreThreshold ||
               Score(trimmed, id) >= FuzzyScoreThreshold;
    }

    public static int Score(string query, string candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
            return 0;

        // partial ratio: a short query should score well against a long product name
        return Fuzz.PartialRatio(query.ToLowerInvariant(), candidate.ToLowerInvariant());
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
