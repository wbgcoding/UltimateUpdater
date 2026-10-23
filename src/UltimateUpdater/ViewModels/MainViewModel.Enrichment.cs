using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.ViewModels;

// Background enrichment: install locations, sizes and dates, and the changelog fetched from a release link.
public partial class MainViewModel
{
    /// <summary>
    /// A dedicated, small HttpClient for the changelog re-fetch; never the shared source
    /// client: this one must never follow a redirect (a compromised or renamed repo could otherwise send
    /// it anywhere) and is used from nowhere but a deliberate click. Static so the process keeps one
    /// socket pool for it; <see cref="_changelogTrafficHandler"/>'s Logging is wired in the constructor,
    /// same idiom as SourceRegistry's own shared client.
    /// </summary>
    private static readonly Services.Security.TrafficLoggingHandler _changelogTrafficHandler =
        new(new HttpClientHandler { AllowAutoRedirect = false });

    private static readonly HttpClient _changelogHttpClient =
        new(_changelogTrafficHandler) { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Read cap for a changelog API response; the same idea as RecycleBinScanner's MaxIndexFileBytes,
    /// applied to a network response instead of a file: past this many bytes the response is abandoned rather
    /// than read in full.</summary>
    private const int MaxChangelogResponseBytes = 256 * 1024;

    /// <summary>
    /// Only reached when the row's own text is empty and its link is a GitHub/GitLab release page. Checks
    /// the shared EnrichmentCache under the same key the background enrichment pass already uses before
    /// touching the network at all; including a cached EMPTY result, so a repeat click on the same
    /// version never refetches and never spends more of GitHub's per-IP hourly quota. The cache entry is
    /// merged, not replaced: whatever date/size/project-url the earlier background pass already learned
    /// for this key is preserved.
    /// </summary>
    private async Task TryEnrichChangelogFromLinkAsync(UpdateRowViewModel row, string releaseUrl)
    {
        var isGitHub = ChangelogUrlParsers.TryParseGitHubReleaseUrl(releaseUrl) is not null;
        var isGitLab = !isGitHub && ChangelogUrlParsers.TryParseGitLabReleaseUrl(releaseUrl) is not null;
        if (!isGitHub && !isGitLab)
            return;

        await _enrichmentCache.LoadAsync();
        var cacheKey = EnrichmentCache.Key(row.Item.SourceId, row.Item.Id, row.Item.AvailableVersion);
        var hasCached = _enrichmentCache.TryGet(cacheKey, out var cached);

        // A previous click already produced a definitive answer for this exact key (real text, or the
        // deliberate "" meaning "tried, found nothing"); reuse it rather than asking again.
        string? body = hasCached && cached.ChangelogBody is not null ? cached.ChangelogBody : null;

        if (body is null)
        {
            body = await _changelogFetcher(releaseUrl, CancellationToken.None);

            // Only a definitive answer is remembered (text, or "" for "this release has none"). A null is a
            // failure (offline, timeout, GitHub's rate limit): cached as "", it suppressed every retry of
            // this version for the cache's full seven days.
            if (body is not null)
            {
                _enrichmentCache.Set(cacheKey, new UpdateEnrichment(
                    hasCached ? cached.PublishDate : null,
                    hasCached ? cached.DownloadSize : null,
                    hasCached ? cached.ProjectUrl : null,
                    hasCached ? cached.AvailableVersion : null,
                    body,
                    releaseUrl,
                    hasCached ? cached.Publisher : null));
                await Task.Run(_enrichmentCache.Save); // the whole cache, atomically: not on the UI thread
            }
        }

        if (!string.IsNullOrWhiteSpace(body))
            row.ApplyEnrichment(null, null, changelogBody: body);
    }

    /// <summary>
    /// The actual network call. Every failure; network error, timeout, non-2xx (403/429 from GitHub's
    /// unauthenticated rate limit included), an oversized response; degrades to null, which is not cached.
    /// A release that was read but has no text is "", which is. Never throws into the caller.
    /// </summary>
    private async Task<string?> FetchReleaseBodyAsync(string releaseUrl, CancellationToken cancellationToken)
    {
        try
        {
            if (ChangelogUrlParsers.TryParseGitHubReleaseUrl(releaseUrl) is { } gitHub)
            {
                var ownerAndRepo = $"{gitHub.Owner}/{gitHub.Repo}";
                var apiUrl = gitHub.Tag is null
                    ? Services.Sources.GitHubReleases.LatestUrl(ownerAndRepo)
                    : Services.Sources.GitHubReleases.TagUrl(ownerAndRepo, gitHub.Tag);

                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                foreach (var header in Services.Sources.GitHubReleases.Headers)
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);

                var json = await SendCappedAsync(request, cancellationToken);
                return json is null ? null : GitHubReleaseParsers.ParseLatestRelease(json).Body ?? string.Empty;
            }

            if (ChangelogUrlParsers.TryParseGitLabReleaseUrl(releaseUrl) is { } gitLab)
            {
                var apiUrl = Services.Sources.GitLabReleases.ReleaseUrl(gitLab.OwnerAndRepo, gitLab.Tag);
                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);

                var json = await SendCappedAsync(request, cancellationToken);
                return json is null ? null : GitLabReleaseParsers.ParseReleaseDescription(json) ?? string.Empty;
            }

            return string.Empty; // not a release page this app can read: a definitive "no text"
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or OperationCanceledException or IOException)
        {
            _logging.Warn($"Changelog fetch failed for '{releaseUrl}': {ex.Message}");
            return null;
        }
    }

    /// <summary>5 s per request, response capped at <see cref="MaxChangelogResponseBytes"/>; null on anything
    /// but a clean small 2xx body.</summary>
    private static async Task<string?> SendCappedAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        using var response = await _changelogHttpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
            return null;

        if (response.Content.Headers.ContentLength is > MaxChangelogResponseBytes)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeoutCts.Token)) > 0)
        {
            if (buffer.Length + read > MaxChangelogResponseBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private CancellationTokenSource? _enrichCts;

    /// <summary>Sources already handed to <see cref="EnrichAllAsync"/> this scan, so a source lingering in
    /// its Done state across several timer ticks is not re-enriched every tick.</summary>
    private readonly HashSet<string> _enrichedSourcesThisScan = [];

    /// <summary>
    /// Fires the moment THIS source's own scan drains; not batched behind the whole scan finishing (a slow
    /// source, e.g. the Android SDK, held back enrichment for sources that had already finished scanning
    /// minutes earlier). Every row is handed to its source's EnrichAsync, which fills in
    /// a release date and download size the outdated listing never carried. Sources without an enrichment
    /// simply no-op. Called from <see cref="FlushProviderStatuses"/> the first time a source reports Done.
    /// </summary>
    private void EnrichSource(string sourceId)
    {
        var cts = _enrichCts;
        if (cts is null)
            return;

        var rows = Updates
            .Where(row => row.Item.SourceId == sourceId)
            .SelectMany(row => new[] { row }.Concat(row.Children))
            .ToList();
        if (rows.Count == 0)
            return;

        _ = Task.Run(() => EnrichAllAsync(rows, cts.Token), cts.Token);
    }

    /// <summary>
    /// The install directory (for "open folder") and the program's own executable (for "Start"), for every
    /// row. Registry and disk only, no network; and sequential on purpose: resolving a launch exe probes
    /// up to three directories, so firing every row at once turned a quiet background pass into a burst of
    /// disk I/O. Fail-closed: a row that resolves to nothing simply offers neither action.
    /// Each row is published the moment it is known, rather than all of them at the end. Collecting them
    /// first is what made the table sit still and then change everywhere at once.
    /// </summary>
    private async Task ResolveInstallLocationsAsync(
        IReadOnlyList<UpdateRowViewModel> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            string? folder;
            string? launchExe;

            try
            {
                folder = _installLocations.Resolve(row.Item);

                // The folder is handed on rather than resolved a second time; the launch lookup falls
                // back to searching it, and repeating the registry and existence checks per row was pure
                // duplicate work.
                launchExe = _installLocations.ResolveLaunchExe(row.Item, folder);
            }
            catch (Exception ex)
            {
                // One row's lookup failing must not stop the rest of the table from being resolved; it
                // simply keeps neither the folder nor the launch target, per the fail-closed contract above.
                _logging.Warn($"Install-location lookup failed for '{row.Item.SourceId}:{row.Item.Id}': {ex.Message}");
                continue;
            }

            if (folder is null && launchExe is null)
                continue;

            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (folder is not null)
                        row.InstallLocationPath = folder;
                    if (launchExe is not null)
                        row.LaunchTargetPath = launchExe;
                },
                DispatcherPriority.Background);
        }
    }

    private async Task EnrichAllAsync(IReadOnlyList<UpdateRowViewModel> rows, CancellationToken cancellationToken)
    {
        // A handful at a time: enough to fill the table quickly without opening dozens of sockets to
        // the registries at once.
        using var gate = new SemaphoreSlim(6);

        await _enrichmentCache.LoadAsync();

        // Alongside the network lookups, not before them. Awaiting the whole registry-and-disk pass first
        // meant nothing at all appeared until it had walked every row, and then the network results; which
        // had not even been started; arrived together a moment later. One is disk-bound and one is
        // network-bound, so they have no reason to queue behind each other.
        var locations = ResolveInstallLocationsAsync(rows, cancellationToken);

        var tasks = rows.Select(async row =>
        {
            var source = SourceFor(row.Item);
            if (source is null)
                return;

            var cacheKey = EnrichmentCache.Key(row.Item.SourceId, row.Item.Id, row.Item.AvailableVersion);

            // A cache hit needs no network and no concurrency slot.
            if (_enrichmentCache.TryGet(cacheKey, out var cached))
            {
                await _dispatcher.InvokeAsync(
                    () => row.ApplyEnrichment(
                        cached.PublishDate, cached.DownloadSize, cached.ProjectUrl, cached.AvailableVersion,
                        cached.ChangelogBody, cached.ChangelogUrl, cached.Publisher),
                    DispatcherPriority.Background);
                return;
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                if (await source.EnrichAsync(row.Item, cancellationToken) is { } enrichment)
                {
                    _enrichmentCache.Set(cacheKey, enrichment);
                    await _dispatcher.InvokeAsync(
                        () => row.ApplyEnrichment(
                            enrichment.PublishDate, enrichment.DownloadSize, enrichment.ProjectUrl,
                            enrichment.AvailableVersion, enrichment.ChangelogBody, enrichment.ChangelogUrl,
                            enrichment.Publisher),
                        DispatcherPriority.Background);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // One row's network lookup failing must not fault the Task.WhenAll below and take down
                // enrichment for every other row still in flight.
                _logging.Warn($"Enrichment failed for '{row.Item.SourceId}:{row.Item.Id}': {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks.Append(locations));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Every per-row task above already catches its own failure; this is a defence-in-
            // depth net for the method itself, called only through EnrichSource's fire-and-forget
            // Task.Run, so anything escaping Task.WhenAll here would only reach the
            // process-wide unobserved-task handler.
            _logging.Warn($"Enrichment pass failed: {ex.Message}");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                // Persist whatever was learned this pass, so the next scan starts warm.
                _enrichmentCache.Save();

                // Only when the table is actually sorted by a column enrichment just filled in does it
                // need re-ordering; a one-off refresh at the end of the pass, not per row.
                if (SortColumn is "date" or "size")
                    await _dispatcher.InvokeAsync(() => RefreshViewSafely(UpdatesView, nameof(UpdatesView)), DispatcherPriority.Background);
            }
        }
    }
}
