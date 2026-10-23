using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateUpdater.Services.Sources;

public readonly record struct VoltaDefaultTool(string Kind, string Name, string Version);

/// <summary>
/// Pure text→data parsers for Volta and the feeds it is compared against. Formats below are captured
/// from a real `volta 2.0.2` install on this dev box, `volta list all --format plain` has
/// no example in Volta's own docs, so guessing it was not an option (this project's own "cargo
/// install-update" lesson).
/// </summary>
public static partial class VoltaParsers
{
    // `volta list all --format plain` real captured output:
    // runtime node@18.19.0 (default)
    // runtime node@20.11.0
    // package-manager yarn@1.22.19 (default)
    // Only a "(default)" line is the version actually active outside a pinned project, the one this app
    // treats as "the installed version" a user would want kept current. A non-default line is an
    // additional side-by-side install Volta deliberately keeps around; it is never touched here.
    [GeneratedRegex(@"^(?<kind>runtime|package-manager)\s+(?<name>\S+)@(?<version>\S+)\s+\(default\)$")]
    private static partial Regex VoltaListLine();

    public static IReadOnlyList<VoltaDefaultTool> ParseDefaultTools(string? stdout)
    {
        var results = new List<VoltaDefaultTool>();
        if (string.IsNullOrWhiteSpace(stdout))
            return results;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            var match = VoltaListLine().Match(line);
            if (match.Success)
                results.Add(new VoltaDefaultTool(match.Groups["kind"].Value, match.Groups["name"].Value, match.Groups["version"].Value));
        }

        return results;
    }

    /// <summary>
    /// Node's own release index (`https://nodejs.org/dist/index.json`, real shape fetched and confirmed
    /// against a live response), a JSON array, newest release first, each entry carrying a "version" field like
    /// "v26.5.1". The leading "v" is Node's own convention, not part of the version Volta compares against.
    /// Restricted to the SAME major line as <paramref name="installedMajor"/>, the index's very first
    /// entry is the newest release overall, which silently jumps a "runtime node@20 (default)" row to
    /// whatever major Node currently ships (26 as of this writing) on one click. The pyenv-win source already
    /// restricts itself to same-line patch bumps for exactly this reason; a major Node bump is a real decision a user should make deliberately, not something
    /// this row's "Update" button does for them.
    /// </summary>
    public static string? ParseLatestNodeVersion(string? json, string installedMajor)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object ||
                    !release.TryGetProperty("version", out var version) ||
                    version.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = version.GetString();
                var stripped = text is { Length: > 1 } && text[0] is 'v' or 'V' ? text[1..] : text;
                if (stripped is null)
                    continue;

                var major = stripped.Split('.')[0];
                if (major == installedMajor)
                    return stripped;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The same release index, this time matched by the EXACT version enrichment already chose as the
    /// update target (not a major line), reads that entry's own "date" field.
    /// </summary>
    public static DateTimeOffset? ParseNodeReleaseDate(string? json, string version)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(version))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object ||
                    !release.TryGetProperty("version", out var versionProperty) ||
                    versionProperty.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = versionProperty.GetString();
                var stripped = text is { Length: > 1 } && text[0] is 'v' or 'V' ? text[1..] : text;
                if (!string.Equals(stripped, version, StringComparison.OrdinalIgnoreCase))
                    continue;

                return release.TryGetProperty("date", out var dateProperty) &&
                    dateProperty.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(
                        dateProperty.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                    ? date
                    : null;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
