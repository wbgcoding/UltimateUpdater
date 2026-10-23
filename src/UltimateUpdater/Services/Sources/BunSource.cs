using System.Net.Http;

namespace UltimateUpdater.Services.Sources;

/// <summary>The Bun runtime. Usually installed by its own script, so usually ours to update.</summary>
public sealed class BunSource(LoggingService logging, HttpClient httpClient)
    : SelfUpdatingToolSource(logging, httpClient)
{
    public override string Id => "bun";

    protected override string Executable => "bun";

    protected override string GitHubRepo => "oven-sh/bun";

    protected override string DisplayName => "Bun";

    protected override IReadOnlyList<string> UpgradeArguments => ["upgrade"];
}
