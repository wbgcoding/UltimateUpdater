using System.Net.Http;
using System.Runtime.CompilerServices;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// The Flutter SDK checked out via `flutter` on PATH. Not a <see cref="CliUpdateSource"/>:
/// unlike a package manager, `flutter` itself never says "an update exists": the only ground truth for that
/// is Google's own release feed, so the network comparison has to happen INSIDE the scan (same shape as
/// <see cref="OllamaSource"/>) rather than after it decides to show a row.
/// `flutter upgrade` fetches the whole SDK repo and can be genuinely slow; it is otherwise a plain, well-
/// documented command with no surprising side effect on the rest of the machine (unlike, say, Git for
/// Windows' own updater, which kills every running Git Bash, deliberately not built for that reason).
/// No uninstall: like the self-updating tools, the row is the SDK checkout itself, not a
/// package it manages: nothing to remove that is distinct from the checkout the developer placed by hand.
/// </summary>
public sealed class FlutterSource(LoggingService logging, HttpClient httpClient) : IUpdateSource
{
    private const string ReleasesFeedUrl = "https://storage.googleapis.com/flutter_infra_release/releases/releases_windows.json";

    /// <summary>The feed's "archive" field is a path relative to this prefix: the feed itself documents
    /// no absolute download URL.</summary>
    private const string ArchiveBaseUrl = "https://storage.googleapis.com/flutter_infra_release/releases/";

    /// <summary>Google publishes no per-version release-notes URL, only this one fixed page, still a
    /// real click-through, unlike inventing a per-version link that does not exist.</summary>
    private const string ReleaseNotesUrl = "https://docs.flutter.dev/release/release-notes";

    private static readonly TimeSpan UpgradeTimeout = TimeSpan.FromMinutes(20);

    public string Id => "flutter";

    public UpdateCategory Category => UpdateCategory.DeveloperTools;

    public SourceTier Tier => SourceTier.A;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ProcessRunner.Exists("flutter"));

    public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var installed = await ReadInstalledAsync(cancellationToken);
        if (installed is null)
            yield break;

        var latest = await FetchLatestAsync(installed.Value.Channel, cancellationToken);

        // Fail closed both ways: no answer from the feed, or a feed version that is not strictly newer than
        // the installed one (equal, or older because the checkout is ahead of the feed), both mean
        // "nothing to report". never a guessed row.
        if (latest is null || !ShouldOffer(installed.Value.Version, latest.Value.Version))
            yield break;

        yield return new UpdateItem
        {
            Id = Id,
            SourceId = Id,
            Category = Category,
            Name = "Flutter SDK",
            InstalledVersion = installed.Value.Version,
            AvailableVersion = latest.Value.Version,
            IsCritical = false,
            PublishDate = latest.Value.ReleaseDate,
            Meta = latest.Value.ArchivePath is { } archivePath
                ? new Dictionary<string, string> { ["archivePath"] = archivePath }
                : new Dictionary<string, string>(),
        };
    }

    /// <summary>True only for a feed version strictly newer than the installed one: a differing string alone
    /// would offer a downgrade to a checkout that is ahead of the feed.</summary>
    internal static bool ShouldOffer(string installed, string? latest) =>
        latest is not null && PackageManagerParsers.IsNewer(installed, latest);

    /// <summary>
    /// The release feed carries no size of its own (see <see cref="FlutterParsers.ParseLatestRelease"/>) .
    /// the archive path it DOES carry is HEAD'd here for the real download size. The release-notes page is
    /// fixed, so it is attached regardless of whether the HEAD request itself succeeds.
    /// </summary>
    public async Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        long? size = null;
        if (item.Meta.TryGetValue("archivePath", out var archivePath) && !string.IsNullOrWhiteSpace(archivePath))
        {
            size = await RegistryEnrichment.HeadContentLengthAsync(
                ArchiveBaseUrl + archivePath, logging, Id, item.Id, cancellationToken);
        }

        return new UpdateEnrichment(null, size, ChangelogUrl: ReleaseNotesUrl);
    }

    /// <summary>The plain upgrade, never forced: a checkout with local changes or a non-standard remote
    /// makes Flutter exit non-zero with its own explanation, which is reported as the failure text instead
    /// of discarding the developer's work.</summary>
    internal static readonly string[] UpgradeArguments = ["upgrade"];

    /// <summary>
    /// Runs `flutter upgrade`. A dirty or forked checkout exits non-zero with Flutter's own message, which
    /// the exit-code branch reports. No admin: the SDK checkout is writable by whoever cloned it.
    /// </summary>
    public async Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var result = await ProcessRunner.RunStreamingAsync(
            "flutter",
            UpgradeArguments,
            UpgradeTimeout,
        segment => progress.Report(new InstallProgress { PercentComplete = 50, StatusText = $"{item.Name}: {segment.Trim()}" }),
            cancellationToken);

        if (result.TimedOut)
            throw new InvalidOperationException($"{Id}: upgrading Flutter timed out.");

        if (result.ExitCode != 0)
        {
            var detail = ToolOutput.Summarize(result.StandardOutput, result.StandardError, result.ExitCode);
            throw new InvalidOperationException($"{Id}: upgrading Flutter failed. {detail}");
        }

        progress.Report(new InstallProgress { PercentComplete = 100, StatusText = item.Name });
    }

    private static async Task<FlutterInstalled?> ReadInstalledAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("flutter", ["--version", "--machine"], ExternalCallTimeouts.Long, cancellationToken);
            return ReadInstalled(result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            // The tool vanished between the availability check and the run: nothing to report, not a failure.
            return null;
        }
    }

    /// <summary>
    /// A `flutter --version --machine` that ran is either an answer or a failed scan: a timeout, a non-zero
    /// exit or output without a readable version means the tool is broken, which must show as a failed
    /// source and not as a machine with nothing to update.
    /// </summary>
    internal static FlutterInstalled ReadInstalled(ProcessResult result)
    {
        if (result.TimedOut)
            throw new TimeoutException("flutter: reading the installed version timed out.");

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"flutter: 'flutter --version' exited with code {result.ExitCode}.");

        return FlutterParsers.ParseInstalled(result.StandardOutput)
            ?? throw new InvalidOperationException("flutter: 'flutter --version' printed no readable version.");
    }

    /// <summary>
    /// The newest release on the channel, from Google's feed. A feed that could not be read (not reachable,
    /// refusing, too slow) is a failed source, not "nothing to report": an unanswered question is not an
    /// answer. A feed that answers with no release for the channel is a valid, empty answer.
    /// </summary>
    internal async Task<FlutterRelease?> FetchLatestAsync(string channel, CancellationToken cancellationToken)
    {
        var fetch = await RegistryEnrichment.FetchRawOutcomeAsync(
            httpClient, ReleasesFeedUrl, logging, Id, "release feed", cancellationToken, timeout: ExternalCallTimeouts.Default);

        RegistryFetch.ThrowIfAllFailed(Id, [fetch]);

        return fetch.Body is null ? null : FlutterParsers.ParseLatestRelease(fetch.Body, channel);
    }
}
