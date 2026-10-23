using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>
/// <c>pip list --outdated</c> only ever compares an installed version against the newest one
/// on PyPI; it has no idea a DIFFERENT installed package pins the very version it is about to offer.
/// This reads <c>pip inspect</c>'s own JSON (every installed distribution's <c>requires_dist</c>,
/// straight from its metadata) and drops an outdated candidate whose offered version would break some
/// other installed package's own requirement on it - exactly the case pip's own resolver would refuse.
/// Real, measured pairs: pydantic pins
/// pydantic-core==2.46.5, semgrep pins opentelemetry-api~=1.37.0, datasets pins
/// huggingface-hub&lt;2.0,&gt;=0.25.0.
/// </summary>
public static class PipDependencyGuard
{
    /// <summary>One constraint read off an installed distribution's own metadata: <paramref
    /// name="Dependent"/> (the package that has this requirement) needs <paramref name="DependencyName"/>
    /// to satisfy <paramref name="SpecifierSet"/>.</summary>
    public readonly record struct Constraint(string Dependent, string DependencyName, string SpecifierSet);

    /// <summary>What a parsed <c>pip inspect</c> report carries forward: the environment's Python
    /// version (for marker evaluation) and every constraint found among installed distributions with a
    /// non-empty specifier. A distribution with no version constraint on its dependency (a bare
    /// "requests", no operator at all) contributes nothing - there is nothing to violate.</summary>
    public sealed record Report(string? PythonVersion, IReadOnlyList<Constraint> Constraints);

    /// <summary>
    /// Parses <c>pip inspect --disable-pip-version-check</c>'s JSON. Returns null for anything that is
    /// not that JSON at all (older pip, before pip 22.2, prints no such command and this is called with
    /// whatever its stderr was) so the caller can skip the filter for one scan and log it once, rather
    /// than throwing the whole pip scan away over a missing feature.
    /// </summary>
    public static Report? Parse(string pipInspectJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(pipInspectJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (!document.RootElement.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.Array)
                return null;

            // A non-object "environment" is an empty environment: markers that need it then cannot be
            // decided, and an undecidable marker keeps its constraint.
            var environmentValues = new Dictionary<string, string>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("environment", out var environment)
                && environment.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in environment.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } text)
                        environmentValues[property.Name] = text;
                }
            }

            // Nothing here ever requests an optional extra, so "extra" is the empty string: a requirement
            // gated by `extra == "docs"` evaluates to false and is dropped.
            environmentValues["extra"] = string.Empty;
            var pythonVersion = environmentValues.GetValueOrDefault("python_version");

            var constraints = new List<Constraint>();

            foreach (var entry in installed.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("metadata", out var metadata)
                    || metadata.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!metadata.TryGetProperty("name", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String
                    || nameElement.GetString() is not { Length: > 0 } dependentName)
                {
                    continue;
                }

                if (!metadata.TryGetProperty("requires_dist", out var requiresDist) || requiresDist.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var requirementElement in requiresDist.EnumerateArray())
                {
                    if (requirementElement.ValueKind != JsonValueKind.String)
                        continue;

                    var requirementText = requirementElement.GetString();
                    if (string.IsNullOrWhiteSpace(requirementText))
                        continue;

                    var parsed = ParseRequirement(requirementText);
                    if (parsed is null)
                        continue;

                    if (!MarkerApplies(parsed.Value.Marker, environmentValues))
                        continue;

                    if (parsed.Value.SpecifierSet is { Length: > 0 } specifierSet)
                        constraints.Add(new Constraint(dependentName, parsed.Value.DependencyName, specifierSet));
                }
            }

            return new Report(pythonVersion, constraints);
        }
    }

    private static readonly Regex RequirementPattern = new(
        @"^\s*(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)\s*(?:\[[^\]]*\])?\s*(?<specifier>[^;]*?)\s*(?:;\s*(?<marker>.*))?$",
        RegexOptions.Compiled);

    /// <summary>Splits one PEP 508 requirement string into its distribution name, its (possibly empty)
    /// version specifier, and its (possibly absent) environment marker. Extras (the "[http]" part) are
    /// dropped: they select optional sub-dependencies of the requirement itself, not a version
    /// constraint.</summary>
    private static (string DependencyName, string? SpecifierSet, string? Marker)? ParseRequirement(string requirement)
    {
        var match = RequirementPattern.Match(requirement);
        if (!match.Success)
            return null;

        var name = match.Groups["name"].Value;
        var specifier = match.Groups["specifier"].Value.Trim();
        // Older wheel metadata (PEP 345 style, still written by e.g. python-dateutil 2.8.2) wraps the
        // specifier in parentheses: "six (>=1.5)". Pep440 would fail closed on the "(" and hold back a
        // perfectly allowed upgrade.
        if (specifier.Length >= 2 && specifier[0] == '(' && specifier[^1] == ')')
            specifier = specifier[1..^1].Trim();
        // A PEP 508 direct reference ("name @ https://...") names a location, not a version range.
        if (specifier.StartsWith('@'))
            specifier = string.Empty;
        var marker = match.Groups["marker"].Success ? match.Groups["marker"].Value.Trim() : null;

        return (name, specifier.Length > 0 ? specifier : null, marker);
    }

    /// <summary>
    /// True when a requirement's marker applies to the environment <c>pip inspect</c> reported for the
    /// scanned interpreter (never this app's own: pip's interpreter is what matters). The marker is
    /// evaluated in full (<see cref="PipMarkerEvaluator"/>). Anything it cannot decide, a variable the
    /// environment lacks, an ordering operator on non-version text, a marker that does not parse, counts as
    /// APPLYING: a kept constraint at worst holds back an upgrade, a dropped one could offer an upgrade that
    /// breaks an installed dependent.
    /// </summary>
    internal static bool MarkerApplies(string? marker, IReadOnlyDictionary<string, string> environment) =>
        marker is null || (PipMarkerEvaluator.Evaluate(marker, environment) ?? true);

    /// <summary>One outdated candidate a scan is about to offer: its normalized name and the version
    /// pip would upgrade it to.</summary>
    public readonly record struct Candidate(string Id, string AvailableVersion);

    /// <summary>A candidate <see cref="PipDependencyGuard.Candidate.Id"/> pip's outdated list is dropping,
    /// and the one line explaining why (see <see cref="PipSource"/>'s log format).</summary>
    public readonly record struct HeldBack(string Id, string LogLine);

    /// <summary>
    /// Every candidate whose offered version violates at least one installed distribution's own
    /// requirement on it. Matching is by PEP 503 normalized name, both sides, so a Report built from
    /// "Foo_Bar" and a candidate spelled "foo-bar" still match.
    /// </summary>
    public static IReadOnlyList<HeldBack> FindHeldBack(IReadOnlyList<Candidate> candidates, Report report)
    {
        var byDependencyName = report.Constraints
            .GroupBy(c => Pep440.NormalizeName(c.DependencyName))
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<HeldBack>();

        foreach (var candidate in candidates)
        {
            if (!byDependencyName.TryGetValue(Pep440.NormalizeName(candidate.Id), out var constraints))
                continue;

            foreach (var constraint in constraints)
            {
                if (Pep440.Satisfies(candidate.AvailableVersion, constraint.SpecifierSet))
                    continue;

                result.Add(new HeldBack(
                    candidate.Id,
                    $"pip: '{candidate.Id}' {candidate.AvailableVersion} held back by {constraint.Dependent} (requires {candidate.Id}{constraint.SpecifierSet})"));
                break;
            }
        }

        return result;
    }
}
