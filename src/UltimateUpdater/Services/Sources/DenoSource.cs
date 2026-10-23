using System.Net.Http;

namespace UltimateUpdater.Services.Sources;

/// <summary>The Deno runtime, which replaces its own executable on `deno upgrade`.</summary>
public sealed class DenoSource(LoggingService logging, HttpClient httpClient)
    : SelfUpdatingToolSource(logging, httpClient)
{
    public override string Id => "deno";

    protected override string Executable => "deno";

    protected override string GitHubRepo => "denoland/deno";

    protected override string DisplayName => "Deno";

    protected override IReadOnlyList<string> UpgradeArguments => ["upgrade"];
}
