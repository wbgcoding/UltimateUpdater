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

    /// <summary>
    /// Zero only. `npm outdated` also exits 1 WHEN it finds outdated packages, but npm uses 1 for registry and
    /// network errors too, so that code is judged by its output (<see cref="IsOutdatedReport"/>).
    /// </summary>
    protected override bool IsAcceptedScanExitCode(int exitCode) => exitCode == 0;

    /// <summary>Exit 1 is the "found outdated packages" success only when stdout is a plain JSON object of
    /// packages; npm's own failures print an object with a top level `error` member (or nothing parsable).</summary>
    private static bool IsOutdatedReport(ProcessResult result) =>
        result.ExitCode == 1 && PnpmSource.IsOutdatedObject(result.StandardOutput);

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["outdated", "-g", "--json"], cancellationToken);

        // A broken cache fails every scan until it is repaired; one repair and one more try, never a loop.
        if (!result.TimedOut && !IsOutdatedReport(result) && IsBrokenCache(result) && await RepairCacheAsync(cancellationToken))
            result = await RunAsync(["outdated", "-g", "--json"], cancellationToken);

        // The prefix is only needed to judge a failure, so the extra npm call stays off the normal path.
        string? prefix = null;
        if (!result.TimedOut && !IsAcceptedScanExitCode(result.ExitCode) && !IsOutdatedReport(result) && MentionsEnoent(result))
            prefix = await ReadGlobalPrefixAsync(cancellationToken);

        return ScanFromResult(result, prefix);
    }

    /// <summary>The scan after `npm outdated` ran, split out so a test can hand in the tool's result and
    /// the global prefix.</summary>
    internal IReadOnlyList<UpdateItem> ScanFromResult(ProcessResult outdated, string? globalPrefix)
    {
        if (!outdated.TimedOut && !IsAcceptedScanExitCode(outdated.ExitCode) && !IsOutdatedReport(outdated) && IsMissingGlobalPrefix(outdated, globalPrefix))
        {
            Logging.Info($"{Id}: the global npm folder does not exist yet, nothing is installed globally.");
            return [];
        }

        // `npm outdated` exits 1 precisely WHEN it finds outdated packages, but also on a registry or
        // network error, so exit 1 counts only with an error-free JSON object. Anything else fails the scan.
        var result = IsOutdatedReport(outdated) ? outdated : FailWithNpmError(outdated);

        return PackageManagerParsers.ParseNpmJson(result.StandardOutput)
            .Select(p => CreateItem(p.Id, p.Name, p.Installed, p.Available))
            .ToList();
    }

    /// <summary>Fails the scan; npm's own `error.summary` (or `error.code`) leads the message when stdout carries one.</summary>
    private ProcessResult FailWithNpmError(ProcessResult result)
    {
        if (!result.TimedOut && result.ExitCode == 1 && NpmErrorSummary(result.StandardOutput) is { Length: > 0 } summary)
            throw new InvalidOperationException($"{Id}: scan failed; '{Executable}' exited with code 1. {LoggingService.Redact(summary)}");

        return EnsureScanCompleted(result);
    }

    private static string? NpmErrorSummary(string stdout)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(stdout);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;

            foreach (var name in new[] { "summary", "code" })
            {
                if (error.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                    && value.GetString() is { Length: > 0 } text)
                    return text.Length <= 300 ? text : text[..300];
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return null;
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

        if (EnoentPath(result) is not { } path)
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

    /// <summary>
    /// A cache whose index still names content files that are gone (half emptied by hand or by an older
    /// cleanup) makes npm fail with ENOENT on a file inside its own <c>_cacache</c> folder on every call that
    /// reads that entry; npm never repairs it by itself. Only that exact shape counts: code ENOENT and the
    /// failing path inside a <c>_cacache</c> folder.
    /// </summary>
    internal static bool IsBrokenCache(ProcessResult result)
    {
        if (EnoentPath(result) is not { } path)
            return false;

        var segments = path.Trim().Trim('"').Split('\\', '/');
        return segments.Length > 1
            && segments[..^1].Any(s => string.Equals(s, "_cacache", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The failing path of an `npm error code ENOENT` report, null for any other error.</summary>
    private static string? EnoentPath(ProcessResult result)
    {
        string? code = null;
        string? path = null;
        foreach (var line in (result.StandardError + "\n" + result.StandardOutput).Split('\n').Select(l => l.Trim()))
        {
            if (TryField(line, "code") is { } c)
                code ??= c;
            else if (TryField(line, "path") is { } p)
                path ??= p;
        }

        return string.Equals(code, "ENOENT", StringComparison.Ordinal) ? path : null;
    }

    /// <summary>
    /// Lets npm repair its own cache: `npm cache verify` drops index entries whose content is gone, keeps the
    /// rest, and fetches nothing. True when it succeeded, so the scan is worth running again.
    /// </summary>
    private async Task<bool> RepairCacheAsync(CancellationToken cancellationToken)
    {
        Logging.Warn($"{Id}: the npm cache names files that are gone; running 'npm cache verify' to repair it.");
        var result = await RunAsync(["cache", "verify", "--no-update-notifier"], cancellationToken);
        if (result.TimedOut || result.ExitCode != 0)
        {
            Logging.Warn($"{Id}: 'npm cache verify' did not succeed (exit code {result.ExitCode}).");
            return false;
        }

        return true;
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
