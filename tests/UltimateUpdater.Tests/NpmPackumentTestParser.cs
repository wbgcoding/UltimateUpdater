using System.Text;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>Test entry point for the string form of a packument: the same scan and mapping the streaming
/// reader feeds in production, applied to a JSON text.</summary>
internal static class NpmPackumentTestParser
{
    public static (UpdateEnrichment Enrichment, string? TarballUrl) Parse(string json, string version)
    {
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(json))
            return default;

        var facts = NpmPackumentReader.Scan(Encoding.UTF8.GetBytes(json), version);
        return facts is null ? default : PackageMetadataParsers.FromNpmPackument(facts);
    }
}
