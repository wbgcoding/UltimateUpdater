using System.Globalization;
using System.Text.Json;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Reads the <c>engines.node</c> range of an npm manifest and tests an installed Node version against it,
/// for the shapes npm itself publishes: <c>||</c> alternatives, space-separated comparators, and the
/// operators <c>^ ~ &gt;= &gt; &lt;= &lt; =</c> (or a bare version). Anything it cannot read is "unknown",
/// never "unsatisfied": a range this code does not understand must not hide an update.
/// </summary>
public static class NodeEngineRange
{
    /// <summary>The <c>engines.node</c> string of a manifest, or null when there is none.</summary>
    public static string? ParseNodeEngine(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("engines", out var engines)
                && engines.ValueKind == JsonValueKind.Object
                && engines.TryGetProperty("node", out var node)
                && node.ValueKind == JsonValueKind.String
                && node.GetString() is { Length: > 0 } range
                ? range
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>False only when both sides were read and the version provably falls outside every
    /// alternative of the range; null when either side could not be understood.</summary>
    public static bool? Satisfies(string? range, string? nodeVersion)
    {
        if (string.IsNullOrWhiteSpace(range) || !TryParseVersion(nodeVersion, out var version))
            return null;

        var anyUnderstood = false;
        foreach (var alternative in range.Split("||", StringSplitOptions.TrimEntries))
        {
            var result = SatisfiesAll(alternative, version);
            if (result is null)
                return null;

            anyUnderstood = true;
            if (result == true)
                return true;
        }

        return anyUnderstood ? false : null;
    }

    private static bool? SatisfiesAll(string alternative, Version version)
    {
        var comparators = alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (comparators.Length == 0)
            return null;

        foreach (var comparator in comparators)
        {
            var result = SatisfiesOne(comparator, version);
            if (result is null)
                return null;

            if (result == false)
                return false;
        }

        return true;
    }

    private static bool? SatisfiesOne(string comparator, Version version)
    {
        var op = comparator.StartsWith(">=", StringComparison.Ordinal) || comparator.StartsWith("<=", StringComparison.Ordinal)
            ? comparator[..2]
            : comparator[0] is '>' or '<' or '^' or '~' or '='
                ? comparator[..1]
                : string.Empty;

        if (!TryParseVersion(comparator[op.Length..], out var bound))
            return null;

        return op switch
        {
            ">=" => version >= bound,
            ">" => version > bound,
            "<=" => version <= bound,
            "<" => version < bound,
            "^" => version >= bound && version < CaretCeiling(bound),
            "~" => version >= bound && version < new Version(bound.Major, bound.Minor + 1, 0),
            _ => version == bound,
        };
    }

    private static Version CaretCeiling(Version bound) =>
        bound.Major > 0 ? new Version(bound.Major + 1, 0, 0)
        : bound.Minor > 0 ? new Version(0, bound.Minor + 1, 0)
        : new Version(0, 0, bound.Build + 1);

    private static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().TrimStart('v', 'V');
        var dash = trimmed.IndexOfAny(['-', '+']);
        if (dash >= 0)
            trimmed = trimmed[..dash];

        var parts = trimmed.Split('.');
        if (parts.Length is 0 or > 3)
            return false;

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }
}
