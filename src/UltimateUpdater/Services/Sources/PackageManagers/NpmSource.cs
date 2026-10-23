using System.Net.Http;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources.PackageManagers;

public sealed class NpmSource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    public override string Id => "npm";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "npm";

    /// <summary>
    /// `npm outdated` reports no publish date, so it is read afterwards from the public npm registry
    /// packument (<see cref="NpmEnrichment"/>). Fails closed.
    /// </summary>
    public override Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken) =>
        NpmEnrichment.FetchAsync(httpClient, Logging, Id, item.Id, item.AvailableVersion, item.Id, cancellationToken);

    /// <summary>`npm outdated` exits 1 precisely WHEN it finds outdated packages; its success case.</summary>
    protected override bool IsAcceptedScanExitCode(int exitCode) => exitCode is 0 or 1;

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["outdated", "-g", "--json"], cancellationToken);

        // The prefix is only needed to judge a failure, so the extra npm call stays off the normal path.
        string? prefix = null;
        if (!result.TimedOut && !IsAcceptedScanExitCode(result.ExitCode) && MentionsEnoent(result))
            prefix = await ReadGlobalPrefixAsync(cancellationToken);

        return ScanFromResult(result, prefix);
    }

    /// <summary>The scan after `npm outdated` ran, split out so a test can hand in the tool's result and
    /// the global prefix.</summary>
    internal IReadOnlyList<UpdateItem> ScanFromResult(ProcessResult outdated, string? globalPrefix)
    {
        if (!outdated.TimedOut && !IsAcceptedScanExitCode(outdated.ExitCode) && IsMissingGlobalPrefix(outdated, globalPrefix))
        {
            Logging.Info($"{Id}: the global npm folder does not exist yet, nothing is installed globally.");
            return [];
        }

        // `npm outdated` exits 1 precisely WHEN it finds outdated packages: that is the success case
        // here, so only stdout decides. Any OTHER code is a broken npm and now says so.
        var result = EnsureScanCompleted(outdated);

        return PackageManagerParsers.ParseNpmJson(result.StandardOutput)
            .Select(p => CreateItem(p.Id, p.Name, p.Installed, p.Available))
            .ToList();
    }

    private async Task<string?> ReadGlobalPrefixAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(Executable, ["config", "get", "prefix"], PrefixLookupTimeout, cancellationToken);
        if (result.TimedOut || result.ExitCode != 0)
            return null;

        return result.StandardOutput.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
    }

    private static readonly TimeSpan PrefixLookupTimeout = TimeSpan.FromSeconds(30);

    private static bool MentionsEnoent(ProcessResult result) =>
        result.StandardError.Contains("ENOENT", StringComparison.Ordinal)
        || result.StandardOutput.Contains("ENOENT", StringComparison.Ordinal);

    /// <summary>
    /// `npm outdated -g` fails with ENOENT on the global prefix folder itself while that folder does not
    /// exist (a fresh Node install has none until the first global install). Both halves must hold: the
    /// error code is ENOENT, and the failing path is the global prefix or its node_modules. ENOENT on any
    /// other path (a broken package, a project file) is a real failure, and so is an unknown prefix.
    /// </summary>
    internal static bool IsMissingGlobalPrefix(ProcessResult result, string? globalPrefix)
    {
        if (string.IsNullOrWhiteSpace(globalPrefix))
            return false;

        string? code = null;
        string? path = null;
        foreach (var line in (result.StandardError + "\n" + result.StandardOutput).Split('\n').Select(l => l.Trim()))
        {
            if (TryField(line, "code") is { } c)
                code ??= c;
            else if (TryField(line, "path") is { } p)
                path ??= p;
        }

        if (!string.Equals(code, "ENOENT", StringComparison.Ordinal) || path is null)
            return false;

        try
        {
            var failing = Normalize(path);
            var prefix = Normalize(globalPrefix);
            return string.Equals(failing, prefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(failing, prefix + System.IO.Path.DirectorySeparatorChar + "node_modules", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return false;
        }
    }

    private static string Normalize(string path) =>
        System.IO.Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/');

    /// <summary>The value of an `npm error FIELD value` line (older npm prints `npm ERR!`).</summary>
    private static string? TryField(string line, string field)
    {
        foreach (var lead in new[] { "npm error ", "npm ERR! " })
        {
            if (line.StartsWith(lead + field + " ", StringComparison.Ordinal))
                return line[(lead.Length + field.Length + 1)..].Trim();
        }

        return null;
    }

    /// <summary>The re-check: a local package list, no registry call.</summary>
    protected override IReadOnlyList<string>? VersionCheckArguments(string id) => ["list", "-g", id, "--depth=0", "--json"];

    protected override string? ParseInstalledVersion(string stdout, string id) =>
        PackageManagerParsers.ParseNpmListVersion(stdout, id);

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => UpgradeArguments(item.Id, item.AvailableVersion);

    /// <summary>
    /// `npm update -g` treats every global package as a caret range and stops at `wanted`, so a new major
    /// version the scan offers (it shows `latest`) would never be applied. Installing the exact version
    /// does apply it. A version that is not a plain release number keeps the in-range update.
    /// </summary>
    internal static IReadOnlyList<string> UpgradeArguments(string packageId, string? version) =>
        IsPlainVersion(version) ? ["install", "-g", $"{packageId}@{version}"] : ["update", "-g", packageId];

    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) => ["uninstall", "-g", item.Id];
}
