using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>One model Ollama has on disk, as its own local API reports it.</summary>
public readonly record struct OllamaModel(string Name, string Digest, long? Size, DateTimeOffset? Modified);

/// <summary>
/// Locally pulled Ollama models.
/// The hard part is knowing when a model is actually STALE. A model tag carries no version, and
/// `ollama pull` on a current tag is a no-op; so listing every model as an "update" would be exactly the
/// phantom row this project keeps out of the table. There is a real signal, and it was verified end to end on
/// a clean VM: the local API reports each model's manifest digest, and the registry returns the digest of the
/// tag it serves in an `ollama-content-digest` header. For `all-minilm:latest` both were
/// `1b226e2802db…75ef`, bit for bit. Equal digests mean there is nothing to do; different digests mean the
/// tag has genuinely moved.
/// The scan speaks HTTP, not the CLI: `ollama list` needs the same server anyway, and the API answers with
/// the digest, the byte size and the pull date in one clean document. If the server is not running the source
/// simply reports itself unavailable; starting a server the user did not ask for is not this app's business.
/// </summary>
public sealed class OllamaSource(LoggingService logging, HttpClient httpClient) : IUpdateSource
{
    private const string LocalApi = "http://127.0.0.1:11434/api/tags";
    private const string Registry = "https://registry.ollama.ai/v2/library";
    private const string DigestHeader = "ollama-content-digest";

    /// <summary>Short: a running Ollama answers at once, and a scan must not wait on one that is not there.</summary>
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan RegistryTimeout = ExternalCallTimeouts.Default;

    public string Id => "ollama";

    public UpdateCategory Category => UpdateCategory.DeveloperTools;

    public SourceTier Tier => SourceTier.A;

    /// <summary>
    /// The executable is checked FIRST because it is free: without Ollama installed there is certainly no
    /// server, and the HTTP attempt cost two seconds of every scan on machines that will never have one
    /// (measured by the source probe). Only when Ollama is present is the server actually asked.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (!ProcessRunner.Exists("ollama"))
            return false;

        var models = await ReadLocalModelsAsync(cancellationToken);
        if (models is null)
            return false;

        // The scan that follows a probe would otherwise ask the same server for the same list a moment later.
        lock (_probeLock)
            _probe = (models, Stopwatch.GetTimestamp());

        return true;
    }

    /// <summary>How long a probe's model list may stand in for the scan's own read.</summary>
    private static readonly TimeSpan ProbeFreshness = TimeSpan.FromSeconds(30);

    private readonly object _probeLock = new();
    private (IReadOnlyList<OllamaModel> Models, long Taken)? _probe;

    /// <summary>
    /// The list the availability probe just read, handed out once and only while it is fresh. A scan that
    /// comes later (the probe is skipped for a source already seen) reads the server itself.
    /// </summary>
    private IReadOnlyList<OllamaModel>? TakeFreshProbe()
    {
        lock (_probeLock)
        {
            var probe = _probe;
            _probe = null;

            return probe is { } taken && Stopwatch.GetElapsedTime(taken.Taken) <= ProbeFreshness ? taken.Models : null;
        }
    }

    public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var models = TakeFreshProbe() ?? await ReadLocalModelsAsync(cancellationToken);
        if (models is null)
            yield break;

        // One HEAD per model, together rather than one after another, but never all at once.
        var checks = await BoundedFanOut.SelectAsync(models, async (model, token) =>
        {
            var (published, fetch) = await RegistryDigestAsync(model.Name, token);
            return (model, published, fetch);
        }, cancellationToken);

        // Every registry check failing (offline, refused) means nothing was checked: a failed source, not
        // a set of models that are all current.
        RegistryFetch.ThrowIfAllFailed(Id, checks.Select(check => check.fetch));

        foreach (var (model, published, _) in checks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Fail closed: no answer from the registry means we do not know of a newer manifest.
            if (published is null || string.Equals(published, model.Digest, StringComparison.OrdinalIgnoreCase))
                continue;

            yield return new UpdateItem
            {
                Id = model.Name,
                SourceId = Id,
                Category = Category,
                Name = AiModelNames.Friendly(model.Name),
                InstalledVersion = Short(model.Digest),
                AvailableVersion = Short(published),
                IsCritical = false,
                DownloadSize = model.Size,
                PublishDate = model.Modified,
            };
        }
    }

    /// <summary>
    /// `ollama pull` re-fetches the tag, which is what an update IS here. Through the CLI rather than the
    /// API's own pull endpoint, because the CLI prints progress this app already knows how to read.
    /// </summary>
    public async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (!ProcessRunner.IsSafePositionalArgument(item.Id))
            throw new InvalidOperationException($"{Id}: refusing to pull '{item.Id}'; that is not a plain model name.");

        progress.Report(new Models.InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var result = await ProcessRunner.RunStreamingAsync(
            "ollama",
            ["pull", item.Id],
            TimeSpan.FromHours(2),
            segment =>
            {
                if (ProgressParser.ToInstallProgress(segment, item.Name) is { } update)
                    progress.Report(update);
            },
            cancellationToken);

        if (result.TimedOut)
            throw new InvalidOperationException($"{Id}: pulling '{item.Id}' timed out.");

        if (result.ExitCode != 0)
        {
            var detail = ToolOutput.Summarize(result.StandardOutput, result.StandardError, result.ExitCode);
            throw new InvalidOperationException($"{Id}: pulling '{item.Id}' failed. {detail}");
        }

        progress.Report(new Models.InstallProgress { PercentComplete = 100, StatusText = item.Name });
    }

    /// <summary>
    /// What the local server has. Null, not an empty list, when there is no server to ask, which is what
    /// tells availability apart from "running, with no models".
    /// </summary>
    private async Task<IReadOnlyList<OllamaModel>?> ReadLocalModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LocalTimeout);

            using var response = await httpClient.GetAsync(LocalApi, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            return OllamaParsers.ParseLocalModels(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The user's Stop, not a missing server: let the scan report it as cancelled.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
                                       or System.Text.Json.JsonException)
        {
            // No server, or one that answered with something unusable. Either way there is nothing to show.
            return null;
        }
    }

    /// <summary>
    /// The digest the registry currently serves for this tag, and how the check ended (null when no check
    /// was made). A HEAD is enough; the digest travels in a header, and the manifest body is not needed.
    /// </summary>
    private async Task<(string? Digest, RegistryFetch? Fetch)> RegistryDigestAsync(string model, CancellationToken cancellationToken)
    {
        if (!OllamaParsers.TrySplitLibraryModel(model, out var name, out var tag))
        {
            // A model from a user namespace or a private registry lives somewhere this app cannot guess, and
            // building a URL out of its name is exactly how a name turns into a request to another host.
            logging.Info($"ollama: '{model}' is not a library model; no update check for it.");
            return (null, null);
        }

        var url = $"{Registry}/{Uri.EscapeDataString(name)}/manifests/{Uri.EscapeDataString(tag)}";

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RegistryTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await httpClient.SendAsync(request, timeout.Token);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return (null, new RegistryFetch(null, RegistryFetchStatus.NotFound, null));

            if (!response.IsSuccessStatusCode)
            {
                logging.Info($"ollama: the registry answered {(int)response.StatusCode} for '{model}'.");
                return (null, new RegistryFetch(null, RegistryFetchStatus.Failed,
                    new InvalidOperationException($"{Id}: the registry answered {(int)response.StatusCode} for '{model}'.")));
            }

            var digest = response.Headers.TryGetValues(DigestHeader, out var values)
                ? OllamaParsers.NormalizeDigest(values.FirstOrDefault())
                : null;
            return (digest, new RegistryFetch(null, RegistryFetchStatus.Ok, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            logging.Info($"ollama: the registry did not answer for '{model}': {ex.Message}");
            return (null, new RegistryFetch(null, RegistryFetchStatus.Failed,
                new TimeoutException($"{Id}: the registry did not answer for '{model}' in time.", ex)));
        }
        catch (HttpRequestException ex)
        {
            logging.Info($"ollama: the registry did not answer for '{model}': {ex.Message}");
            return (null, new RegistryFetch(null, RegistryFetchStatus.Failed, ex));
        }
    }

    public bool CanUninstall(UpdateItem item) => true;

    /// <summary>`ollama rm` removes the pulled model from local disk; nothing here reaches the registry.</summary>
    public async Task UninstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (!ProcessRunner.IsSafePositionalArgument(item.Id))
            throw new InvalidOperationException($"{Id}: refusing to remove '{item.Id}'; that is not a plain model name.");

        progress.Report(new Models.InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var result = await ProcessRunner.RunAsync("ollama", ["rm", item.Id], ExternalCallTimeouts.Long, cancellationToken);

        if (result.TimedOut)
            throw new InvalidOperationException($"{Id}: removing '{item.Id}' timed out.");

        if (result.ExitCode != 0)
        {
            var detail = ToolOutput.Summarize(result.StandardOutput, result.StandardError, result.ExitCode);
            throw new InvalidOperationException($"{Id}: removing '{item.Id}' failed. {detail}");
        }

        progress.Report(new Models.InstallProgress { PercentComplete = 100, StatusText = item.Name });
    }

    /// <summary>The 12 characters Ollama itself shows as a model's id.</summary>
    private static string Short(string digest) => digest.Length <= 12 ? digest : digest[..12];
}
