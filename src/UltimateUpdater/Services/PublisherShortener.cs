using System.Text.RegularExpressions;

namespace UltimateUpdater.Services;

/// <summary>
/// One shortener for every displayed publisher name (winget, Windows Update, pip): drops an embedded
/// e-mail address, strips a leading "The" and a trailing legal-entity suffix (repeatedly, so "The
/// Pallets Team" loses both "Team" and "The"), and caps the result at 24 characters. Applied only to the
/// column's own display text; every tooltip keeps the full, unshortened <c>Publisher</c> fact.
/// </summary>
public static partial class PublisherShortener
{
    private const int MaxLength = 24;

    /// <summary>An e-mail address, optionally wrapped in angle brackets or parentheses (the two forms
    /// registry metadata actually uses, "Name &lt;name@host&gt;"); matched loosely on purpose, this is
    /// display trimming, not address validation. The text comes from registry metadata (a PyPI author
    /// field, for one) and is shortened from a binding on the UI thread, so the local part excludes '@'
    /// to keep backtracking linear, the input is capped (<see cref="MaxInputLength"/>) and the match has a
    /// time limit.</summary>
    [GeneratedRegex(@"[<(]?[^\s<>()@]+@[^\s<>()]+\.[^\s<>()]+[>)]?", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex EmailPattern();

    /// <summary>Far more than any real publisher name; the result is cut to <see cref="MaxLength"/> anyway.</summary>
    private const int MaxInputLength = 512;

    /// <summary>Trailing legal-entity words/phrases this strips, already with any trailing period
    /// removed (<see cref="StripTrailingSuffix"/> trims that off the candidate text first, so "Inc." and
    /// "Inc", or "S.A." and "S.A", need only one entry each here).</summary>
    private static readonly string[] TrailingSuffixes =
    [
        "and contributors", "Corporation", "Developers", "Team", "GmbH", "S.A", "B.V", "Corp",
        "Inc", "LLC", "Ltd", "Co", "AG",
    ];

    public static string Shorten(string publisher)
    {
        var input = publisher.Length > MaxInputLength ? publisher[..MaxInputLength] : publisher;

        string withoutEmail;
        try
        {
            withoutEmail = EmailPattern().Replace(input, string.Empty).Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            withoutEmail = input;
        }

        var basis = withoutEmail.Length > 0 ? withoutEmail : input;

        var result = basis;
        bool changed;
        do
        {
            changed = false;

            if (StripLeadingThe(result) is { } withoutThe)
            {
                result = withoutThe;
                changed = true;
            }

            if (StripTrailingSuffix(result) is { } withoutSuffix)
            {
                result = withoutSuffix;
                changed = true;
            }
        } while (changed);

        if (result.Length == 0)
            result = basis;

        return result.Length > MaxLength ? result[..MaxLength].TrimEnd() : result;
    }

    private static string? StripLeadingThe(string text) =>
        text.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && text.Length > 4
            ? text[4..].TrimStart()
            : null;

    /// <summary>Never strips a name down to nothing: a candidate no longer than the suffix itself (the
    /// whole name IS that word, e.g. a publisher literally called "Team") is left alone.</summary>
    private static string? StripTrailingSuffix(string text)
    {
        var trimmed = text.TrimEnd(' ', ',', '.');

        foreach (var suffix in TrailingSuffixes)
        {
            if (trimmed.Length <= suffix.Length)
                continue;

            if (!trimmed.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            if (!char.IsWhiteSpace(trimmed[^(suffix.Length + 1)]))
                continue;

            var stripped = trimmed[..^suffix.Length].TrimEnd(' ', ',');
            return stripped.Length > 0 ? stripped : null;
        }

        return trimmed != text ? trimmed : null;
    }
}
