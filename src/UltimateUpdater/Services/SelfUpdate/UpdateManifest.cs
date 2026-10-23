using System.Text.Json;

namespace UltimateUpdater.Services.SelfUpdate;

public sealed record ReleaseAsset(string Name, string DownloadUrl, string ApiUrl, long Size);

public sealed record ReleaseInfo(string Version, IReadOnlyList<ReleaseAsset> Assets, string ReleaseNotes);

/// <summary>
/// Parsing for the self-update inputs. Kept pure so the fail-closed behaviour is directly testable:
/// anything unparseable yields "no update", never an exception and never a partial result.
/// </summary>
public static class UpdateManifest
{
    /// <summary>
    /// Reads the GitHub releases API payload (single release object or an array of them). Drafts are
    /// skipped, and so are PRE-RELEASES unless <paramref name="allowPrereleases"/>, only the private dev
    /// channel wants those. Without that, the newest tag on the repository moved a stable install onto a
    /// beta, and the stripped suffix made it look like a plain version bump.
    /// </summary>
    public static ReleaseInfo? ParseRelease(string json, bool allowPrereleases = false) =>
        TryParseRelease(json, allowPrereleases, out var release) ? release : null;

    /// <summary>
    /// Same reading as <see cref="ParseRelease"/>, but tells "the text is not JSON" (false) apart from "readable,
    /// and no release this channel may take" (true with a null release).
    /// </summary>
    public static bool TryParseRelease(string json, bool allowPrereleases, out ReleaseInfo? release)
    {
        release = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                // releases list: take the highest valid SemVer, not feed order (GitHub sorts by creation
                // date, so a later backfill of an older version would otherwise hide the newer one). A
                // malformed entry (not an object, no tag_name, an unparseable tag) must not stop the whole
                // feed, every release after it, including genuinely valid newer ones, would otherwise be
                // hidden behind one bad element.
                (Version Core, string Prerelease)? best = null;
                foreach (var element in root.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object || IsExcluded(element, allowPrereleases))
                        continue;

                    if (ReadRelease(element) is not { } found || ParseSemver(found.Version) is not { } semver)
                        continue;

                    if (best is null || Compare(semver, best.Value) > 0)
                    {
                        best = semver;
                        release = found;
                    }
                }

                return true;
            }

            // A readable root that is neither a release list nor a release object (null, a string, a
            // number, {}) is a broken answer, not "nothing new": reporting it as up to date would hide a
            // feed that no longer works.
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
                return false;

            if (!IsExcluded(root, allowPrereleases))
                release = ReadRelease(root);

            return true;
        }
    }

    private static bool IsExcluded(JsonElement element, bool allowPrereleases) =>
        IsTrue(element, "draft") || (!allowPrereleases && IsTrue(element, "prerelease"));

    private static bool IsTrue(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private static ReleaseInfo? ReadRelease(JsonElement element)
    {
        var tag = GetString(element, "tag_name");
        if (tag.Length == 0)
            return null;

        var assets = new List<ReleaseAsset>();
        if (element.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetArray.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                    continue;

                var name = GetString(asset, "name");
                if (name.Length == 0)
                    continue;

                assets.Add(new ReleaseAsset(
                    name,
                    GetString(asset, "browser_download_url"),
                    GetString(asset, "url"),
                    asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0));
            }
        }

        return new ReleaseInfo(NormalizeVersion(tag), assets, GetString(element, "body"));
    }

    /// <summary>Parses a SHA256SUMS.txt body into filename → lowercase hex digest.</summary>
    public static IReadOnlyDictionary<string, string> ParseChecksums(string content)
    {
        var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content))
            return checksums;

        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            // "<64 hex> <filename>", with the leading "*" binary marker allowed
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                continue;

            var digest = parts[0].Trim();
            if (digest.Length != 64 || !IsHex(digest))
                continue;

            var name = parts[1].Trim().TrimStart('*');
            if (name.Length == 0)
                continue;

            checksums[name] = digest.ToLowerInvariant();
        }

        return checksums;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is strictly newer than <paramref name="current"/>, compared
    /// as semver INCLUDING the pre-release part: cutting the suffix before comparing makes
    /// `1.2.0-beta.1` and `1.2.0` look like the same version, so the beta→stable step at one numeric
    /// version would never be offered and a beta would look like a plain bump. Unparseable input fails closed.
    /// </summary>
    public static bool IsNewer(string current, string candidate)
    {
        var currentVersion = ParseSemver(current);
        var candidateVersion = ParseSemver(candidate);

        if (currentVersion is null || candidateVersion is null)
            return false;

        return Compare(candidateVersion.Value, currentVersion.Value) > 0;
    }

    /// <summary>The tag's own text, minus a leading "v". The pre-release suffix is KEPT: it is part of the version.</summary>
    private static string NormalizeVersion(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith('v') || trimmed.StartsWith('V') ? trimmed[1..] : trimmed;
    }

    /// <summary>
    /// Strict SemVer 2.0.0 split into numeric core and pre-release identifiers; null for anything else, so
    /// a malformed tag fails closed instead of being compared. Build metadata is validated and dropped.
    /// </summary>
    private static (Version Core, string Prerelease)? ParseSemver(string value)
    {
        var trimmed = NormalizeVersion(value);

        var plus = trimmed.IndexOf('+');
        if (plus >= 0)
        {
            if (!AreIdentifiers(trimmed[(plus + 1)..], numericLeadingZeroAllowed: true))
                return null;

            trimmed = trimmed[..plus];
        }

        var dash = trimmed.IndexOf('-');
        var core = dash < 0 ? trimmed : trimmed[..dash];
        var prerelease = dash < 0 ? string.Empty : trimmed[(dash + 1)..];

        if (dash >= 0 && !AreIdentifiers(prerelease, numericLeadingZeroAllowed: false))
            return null;

        var parts = core.Split('.');
        if (parts.Length != 3)
            return null;

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!IsNumericCore(parts[i]) || !int.TryParse(parts[i], out numbers[i]))
                return null;
        }

        return (new Version(numbers[0], numbers[1], numbers[2]), prerelease);
    }

    /// <summary>"0" or a digit run without a leading zero.</summary>
    private static bool IsNumericCore(string part) =>
        part.Length > 0 && part.All(char.IsAsciiDigit) && (part.Length == 1 || part[0] != '0');

    /// <summary>Dot-separated, non-empty identifiers of [0-9A-Za-z-]; a purely numeric one may not carry a
    /// leading zero in a pre-release (SemVer §9), but may in build metadata (§10).</summary>
    private static bool AreIdentifiers(string value, bool numericLeadingZeroAllowed)
    {
        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0 || !identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                return false;

            if (!numericLeadingZeroAllowed && identifier.Length > 1 && identifier[0] == '0'
                && identifier.All(char.IsAsciiDigit))
                return false;
        }

        return true;
    }

    private static int Compare((Version Core, string Prerelease) left, (Version Core, string Prerelease) right)
    {
        var core = left.Core.CompareTo(right.Core);
        if (core != 0)
            return core;

        // A release outranks every pre-release of the same core: 1.2.0 > 1.2.0-beta.1.
        if (left.Prerelease.Length == 0)
            return right.Prerelease.Length == 0 ? 0 : 1;

        if (right.Prerelease.Length == 0)
            return -1;

        return ComparePrerelease(left.Prerelease, right.Prerelease);
    }

    /// <summary>Semver §11: identifier by identifier, numeric ones below alphanumeric ones, fewer is lower.</summary>
    internal static int ComparePrerelease(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');

        for (var i = 0; i < Math.Max(leftParts.Length, rightParts.Length); i++)
        {
            if (i >= leftParts.Length)
                return -1;

            if (i >= rightParts.Length)
                return 1;

            // A pure-digit run is a numeric identifier by SemVer's own definition, however many digits,
            // int.TryParse alone silently misclassified anything past Int32.MaxValue as alphanumeric and
            // compared it as a string, which sorts wrong ("100000000000" < "99999999999" ordinally).
            var leftIsNumber = IsNumericIdentifier(leftParts[i]);
            var rightIsNumber = IsNumericIdentifier(rightParts[i]);

            var result = (leftIsNumber, rightIsNumber) switch
            {
                (true, true) => CompareNumericIdentifiers(leftParts[i], rightParts[i]),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(leftParts[i], rightParts[i]),
            };

            if (result != 0)
                return result;
        }

        return 0;
    }

    private static bool IsNumericIdentifier(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

    /// <summary>Numeric-identifier comparison (SemVer §11.4.1) without ever converting to Int32/Int64: a
    /// longer digit string (after leading zeros) is always the larger value, and same-length digit
    /// strings compare correctly ordinally, digit by digit.</summary>
    private static int CompareNumericIdentifiers(string left, string right)
    {
        var leftTrimmed = TrimLeadingZeros(left);
        var rightTrimmed = TrimLeadingZeros(right);

        return leftTrimmed.Length != rightTrimmed.Length
            ? leftTrimmed.Length.CompareTo(rightTrimmed.Length)
            : string.CompareOrdinal(leftTrimmed, rightTrimmed);
    }

    private static string TrimLeadingZeros(string digits)
    {
        var trimmed = digits.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    private static bool IsHex(string value) => value.All(Uri.IsHexDigit);

    private static string GetString(JsonElement element, string propertyName) =>
        JsonElementHelpers.GetString(element, propertyName);
}
