using System.Text.RegularExpressions;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>
/// A small, pure PEP 440 version comparator and specifier matcher, and the PEP 503 name normalisation
/// that goes with it. Pip's own dependency resolver already understands all of this; this
/// exists only so <see cref="PipDependencyGuard"/> can ask, off the exact same data pip printed,
/// "would upgrading to X actually violate what some OTHER installed package requires" without shelling
/// out to pip again for every candidate.
/// </summary>
public static class Pep440
{
    /// <summary>PEP 503: lower-case, and any run of "-", "_" or "." collapses to a single "-". Two
    /// distribution names that differ only by case or by which of these three characters they used are
    /// the SAME package on PyPI.</summary>
    public static string NormalizeName(string name) =>
        Regex.Replace(name.ToLowerInvariant(), "[-_.]+", "-");

    /// <summary>
    /// One parsed release: an epoch, numeric release segments, an optional pre-release (a/b/rc), an
    /// optional post-release and an optional dev-release. Every number is kept as a digit string without
    /// leading zeros and compared by length, then ordinally, so no value can overflow. Local versions (the
    /// "+..." suffix) are accepted but dropped: pip's own outdated/available versions never carry one, and
    /// PEP 440 says a local version is only ever compared to another local version of the very same
    /// public version anyway.
    /// </summary>
    private readonly record struct Version(
        string Epoch, IReadOnlyList<string> Release, (string Phase, string N)? Pre, string? Post, string? Dev)
        : IComparable<Version>
    {
        private static int PhaseRank(string phase) => phase switch
        {
            "a" => 0,
            "b" => 1,
            _ => 2,
        };

        /// <summary>
        /// The ordering PEP 440 itself describes (mirrored from the reference <c>packaging</c>
        /// implementation): a version with no explicit pre-release sorts AFTER every real pre-release of
        /// the same release (so "1.0" beats "1.0a1"), UNLESS it is a bare dev-release with no pre and no
        /// post at all, which instead sorts BEFORE every real pre-release (so "1.0.dev1" is older than
        /// "1.0a1"). A version with no post-release sorts before one that has one; a version with no
        /// dev-release sorts after one that has one, for the same release/pre/post triple.
        /// </summary>
        public int CompareTo(Version other)
        {
            var result = CompareNumbers(Epoch, other.Epoch);
            if (result != 0)
                return result;

            result = CompareRelease(Release, other.Release);
            if (result != 0)
                return result;

            result = PreCategory.CompareTo(other.PreCategory);
            if (result != 0)
                return result;

            if (Pre is { } pre && other.Pre is { } otherPre)
            {
                result = PhaseRank(pre.Phase).CompareTo(PhaseRank(otherPre.Phase));
                if (result != 0)
                    return result;

                result = CompareNumbers(pre.N, otherPre.N);
                if (result != 0)
                    return result;
            }

            result = ComparePresent(Post, other.Post, absentIsLower: true);
            return result != 0 ? result : ComparePresent(Dev, other.Dev, absentIsLower: false);
        }

        private int PreCategory => Pre is not null ? 0 : Post is null && Dev is not null ? -1 : 1;

        private static int ComparePresent(string? left, string? right, bool absentIsLower)
        {
            if (left is null && right is null)
                return 0;
            if (left is null)
                return absentIsLower ? -1 : 1;
            if (right is null)
                return absentIsLower ? 1 : -1;

            return CompareNumbers(left, right);
        }

        private static int CompareRelease(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            var length = Math.Max(a.Count, b.Count);
            for (var i = 0; i < length; i++)
            {
                var result = CompareNumbers(i < a.Count ? a[i] : "0", i < b.Count ? b[i] : "0");
                if (result != 0)
                    return result;
            }

            return 0;
        }
    }

    /// <summary>Digit strings without leading zeros: the longer one is the larger, equal lengths compare
    /// digit by digit.</summary>
    private static int CompareNumbers(string left, string right) =>
        left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);

    private static string TrimZeros(string digits)
    {
        var trimmed = digits.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    // PEP 440 Appendix B, anchored: trailing garbage ("2.0garbage") must not parse as "2.0".
    private static readonly Regex VersionPattern = new(
        @"^\s*v?(?:(?<epoch>[0-9]+)!)?(?<release>[0-9]+(?:\.[0-9]+)*)"
        + @"(?:[-_.]?(?<prephase>alpha|a|beta|b|preview|pre|c|rc)[-_.]?(?<pren>[0-9]+)?)?"
        + @"(?:-(?<post1>[0-9]+)|[-_.]?(?<postkw>post|rev|r)[-_.]?(?<post2>[0-9]+)?)?"
        + @"(?:[-_.]?(?<devkw>dev)[-_.]?(?<dev>[0-9]+)?)?"
        + @"(?:\+[a-z0-9]+(?:[-_.][a-z0-9]+)*)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static bool TryParse(string text, out Version version)
    {
        version = default;

        Match match;
        try
        {
            match = VersionPattern.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        if (!match.Success)
            return false;

        var epoch = match.Groups["epoch"].Success ? TrimZeros(match.Groups["epoch"].Value) : "0";
        var release = match.Groups["release"].Value.Split('.').Select(TrimZeros).ToList();

        (string, string)? pre = null;
        if (match.Groups["prephase"].Success)
        {
            var phase = match.Groups["prephase"].Value.ToLowerInvariant() switch
            {
                "alpha" or "a" => "a",
                "beta" or "b" => "b",
                _ => "rc",
            };
            pre = (phase, match.Groups["pren"].Success ? TrimZeros(match.Groups["pren"].Value) : "0");
        }

        // A post keyword with no number is post-release 0; the "-N" form always carries its number.
        string? post = null;
        if (match.Groups["post1"].Success)
            post = TrimZeros(match.Groups["post1"].Value);
        else if (match.Groups["postkw"].Success)
            post = match.Groups["post2"].Success ? TrimZeros(match.Groups["post2"].Value) : "0";

        string? dev = null;
        if (match.Groups["devkw"].Success)
            dev = match.Groups["dev"].Success ? TrimZeros(match.Groups["dev"].Value) : "0";

        version = new Version(epoch, release, pre, post, dev);
        return true;
    }

    /// <summary>True when the text is a complete, valid PEP 440 version.</summary>
    internal static bool IsVersion(string text) => TryParse(text, out _);

    /// <summary>
    /// Whether <paramref name="available"/> is a newer Python package version than
    /// <paramref name="installed"/>, ordered by PEP 440 when both parse (so "24.1a1" is newer than
    /// "23.12.1", and "23.12.1" is not newer than "24.1a1"); anything else falls back to the generic
    /// <see cref="PackageManagerParsers.IsNewer"/>.
    /// </summary>
    internal static bool IsNewer(string? installed, string? available) =>
        installed is not null && available is not null
        && TryParse(installed, out var installedVersion) && TryParse(available, out var availableVersion)
            ? availableVersion.CompareTo(installedVersion) > 0
            : PackageManagerParsers.IsNewer(installed, available);

    /// <summary>
    /// Whether <paramref name="version"/> satisfies every comma-separated clause of
    /// <paramref name="specifierSet"/> (PEP 440 §"Version specifiers": a comma is AND, never OR).
    /// An unparseable version or an unparseable clause fails closed (does not satisfy) rather than
    /// silently letting a candidate through a constraint nobody could actually check.
    /// </summary>
    public static bool Satisfies(string version, string specifierSet)
    {
        if (!TryParse(version, out var parsedVersion))
            return false;

        foreach (var clause in specifierSet.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!SatisfiesClause(parsedVersion, clause))
                return false;
        }

        return true;
    }

    private static readonly Regex ClausePattern = new(
        @"^(?<op>==|!=|<=|>=|~=|<|>)\s*(?<value>[^\s]+)$", RegexOptions.Compiled);

    private static bool SatisfiesClause(Version version, string clause)
    {
        var match = ClausePattern.Match(clause);
        if (!match.Success)
            return false;

        var op = match.Groups["op"].Value;
        var value = match.Groups["value"].Value;

        // "==X.*" / "!=X.*": a prefix match on the release segments, wildcard aside; pre/post/dev on
        // the candidate do not matter for this form. Segment-aware ONLY: a raw string prefix check here
        // would let "==2.1.*" wrongly accept "2.10.0", because "2.1"
        // is a literal string prefix of "2.10.0" even though its second release segment (10) is not 1.
        // "2.*" still matches "2" itself, and still must not match "20.1": HasReleasePrefix compares
        // parsed release segments element by element, never the text.
        if (value.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = value[..^2];
            var matchesPrefix = TryParse(prefix, out var prefixVersion) && HasReleasePrefix(version, prefixVersion);

            return op switch
            {
                "==" => matchesPrefix,
                "!=" => !matchesPrefix,
                _ => false,
            };
        }

        if (!TryParse(value, out var bound))
            return false;

        var comparison = version.CompareTo(bound);

        return op switch
        {
            "==" => comparison == 0,
            "!=" => comparison != 0,
            "<=" => comparison <= 0,
            ">=" => comparison >= 0,
            "<" => comparison < 0,
            ">" => comparison > 0,
            // "~=X.Y" means ">=X.Y, ==X.*" (compatible release): at least the given version, but never a
            // different next-to-last segment.
            "~=" => comparison >= 0 && HasCompatiblePrefix(version, bound),
            _ => false,
        };
    }

    private static bool HasReleasePrefix(Version version, Version prefix)
    {
        if (prefix.Epoch != version.Epoch)
            return false;

        // A release shorter than the prefix is zero-padded, as pip does: "2" matches "==2.0.*".
        for (var i = 0; i < prefix.Release.Count; i++)
        {
            var segment = i < version.Release.Count ? version.Release[i] : "0";
            if (segment != prefix.Release[i])
                return false;
        }

        return true;
    }

    private static bool HasCompatiblePrefix(Version version, Version bound)
    {
        // ~=2.2 means >=2.2, ==2.*; ~=2.2.3 means >=2.2.3, ==2.2.* (PEP 440 §Compatible release):
        // the LAST release segment of the bound is the one allowed to vary.
        if (version.Epoch != bound.Epoch)
            return false;

        var prefixLength = Math.Max(bound.Release.Count - 1, 0);
        for (var i = 0; i < prefixLength; i++)
        {
            var left = i < version.Release.Count ? version.Release[i] : "0";
            if (left != bound.Release[i])
                return false;
        }

        return true;
    }
}
