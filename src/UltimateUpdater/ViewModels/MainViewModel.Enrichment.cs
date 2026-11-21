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
    /// <summary>A small HttpClient of its own for the changelog re-fetch, never the shared source client: it must not follow a redirect (a compromised or renamed repo could send it anywhere) and is used only from a deliberate click. Static so the process keeps one socket pool for it.</summary>
    private static readonly Services.Security.TrafficLoggingHandler _changelogTrafficHandler =
        new(new HttpClientHandler { AllowAutoRedirect = false });

    private static readonly HttpClient _changelogHttpClient =
        new(_changelogTrafficHandler) { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Read cap for a changelog API response; past this many bytes the response is abandoned rather than read in full.</summary>
    private const int MaxChangelogResponseBytes = 256 * 1024;

    /// <summary>Only reached when the row's own text is empty and its link is a GitHub or GitLab release page. Checks the shared enrichment cache first, including a cached empty result, so a repeat click never refetches or spends more of GitHub's per-IP hourly quota. The entry is merged, not replaced, so the date, size and project URL learned earlier are kept.</summary>
    private async Task TryEnrichChangelogFromLinkAsync(UpdateRowViewModel row, string releaseUrl)
    {
        var isGitHub = ChangelogUrlParsers.TryParseGitHubReleaseUrl(releaseUrl) is not null;
        var isGitLab = !isGitHub && ChangelogUrlParsers.TryParseGitLabReleaseUrl(releaseUrl) is not null;
        if (!isGitHub && !isGitLab)
            return;

        await _enrichmentCache.LoadAsync();
        var cacheKey = EnrichmentCache.Key(row.Item.SourceId, row.Item.Id, row.Item.AvailableVersion);
        var hasCached = _enrichmentCache.TryGet(cacheKey, out var cached);

        // An earlier click already produced a definitive answer (real text, or "" for "tried, found nothing"); reuse it.
        string? body = hasCached && cached.ChangelogBody is not null ? cached.ChangelogBody : null;

        if (body is null)
        {
            body = await _changelogFetcher(releaseUrl, CancellationToken.None);

            // Only a definitive answer is remembered. A null is a failure (offline, timeout, rate limit) and must not suppress retries for the cache's seven days.
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

    /// <summary>The network call. Every failure (network error, timeout, a non-2xx answer such as GitHub's 403/429, an oversized response) degrades to null, which is not cached; a release with no text is "", which is. Never throws into the caller.</summary>
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
            or OperationCanceledException or IOException or ArgumentException)
        {
            _logging.Warn($"Changelog fetch failed for '{releaseUrl}': {ex.Message}");
            return null;
        }
    }

    /// <summary>5 s per request, response capped at <see cref="MaxChangelogResponseBytes"/>; null on anything but a clean small 2xx body.</summary>
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

    /// <summary>The installed Store (MSIX) packages: names the app to start and the folder whose running processes block an update. Shared with the pre-install check.</summary>
    private readonly PackagedApps _packagedApps = new(Services.Sources.WindowsRegistryView.Instance);

    /// <summary>Sources already enriched this scan, so one lingering in its Done state across several timer ticks is not enriched every tick.</summary>
    private readonly HashSet<string> _enrichedSourcesThisScan = [];

    /// <summary>Fires the moment this source's own scan drains, so a slow source does not hold back enrichment for the ones already finished. Every row goes to its source's EnrichAsync, which fills in a release date and download size; sources without one no-op.</summary>
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

    /// <summary>The install directory (for "open folder") and the program's own executable (for "Start"), for every row. Registry and disk only, no network, and sequential on purpose: resolving a launch exe probes up to three directories, so all rows at once would be a burst of disk I/O. Each row is published as soon as it is known. A row that resolves to nothing offers neither action.</summary>
    private async Task ResolveInstallLocationsAsync(
        IReadOnlyList<UpdateRowViewModel> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            string? folder;
            string? launchExe;
            var packagedApp = false;

            try
            {
                folder = _installLocations.Resolve(row.Item);

                // The folder is handed on rather than resolved again; the launch lookup falls back to searching it.
                launchExe = _installLocations.ResolveLaunchExe(row.Item, folder);

                // A Store app installed through winget has no Uninstall entry to find an exe by; its package names the application to start.
                if (row.Item.SourceId == "winget" && _packagedApps.FindForWingetRow(row.Item.Id, row.Item.Name) is { } package)
                {
                    packagedApp = true;
                    launchExe ??= _packagedApps.StartTargetOf(package);
                }
            }
            catch (Exception ex)
            {
                // One row's lookup failing must not stop the rest; it keeps neither folder nor launch target.
                _logging.Warn($"Install-location lookup failed for '{row.Item.SourceId}:{row.Item.Id}': {ex.Message}");
                continue;
            }

            if (folder is null && launchExe is null && !packagedApp)
                continue;

            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (folder is not null)
                        row.InstallLocationPath = folder;
                    if (launchExe is not null)
                        row.LaunchTargetPath = launchExe;
                    if (packagedApp)
                        row.IsPackagedApp = true;
                },
                DispatcherPriority.Background);
        }
    }

    /// <summary>Enrichment is part of the scan, just started from here, so its tool processes also yield to the user's own programs.</summary>
    private static async Task<UpdateEnrichment?> EnrichAtScanPriorityAsync(
        IUpdateSource source, UpdateItem item, CancellationToken cancellationToken)
    {
        using var scanScope = ScanScope.Enter();
        return await source.EnrichAsync(item, cancellationToken);
    }

    private async Task EnrichAllAsync(IReadOnlyList<UpdateRowViewModel> rows, CancellationToken cancellationToken)
    {
        // A handful at a time: fills the table quickly without opening dozens of sockets to the registries at once.
        using var gate = new SemaphoreSlim(6);

        await _enrichmentCache.LoadAsync();

        // Runs alongside the network lookups, not before them: one is disk-bound and one network-bound, so neither should queue behind the other.
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
                if (await EnrichAtScanPriorityAsync(source, row.Item, cancellationToken) is { } enrichment)
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
                // One row's failing lookup must not fault the Task.WhenAll below and stop enrichment for the rest.
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
            // Safety net for the method itself: it only runs through a fire-and-forget Task.Run, so anything escaping here would reach only the unobserved-task handler.
            _logging.Warn($"Enrichment pass failed: {ex.Message}");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                // Persist what was learned so the next scan starts warm.
                _enrichmentCache.Save();

                // Only a table sorted by a column enrichment just filled in needs re-ordering; once at the end of the pass, not per row.
                if (SortColumn is "date" or "size" or "version")
                    await _dispatcher.InvokeAsync(() => RefreshViewSafely(UpdatesView, nameof(UpdatesView)), DispatcherPriority.Background);
            }
        }
    }
}
