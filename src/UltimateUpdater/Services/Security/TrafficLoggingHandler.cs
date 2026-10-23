using System.Net;
using System.Net.Http;

namespace UltimateUpdater.Services.Security;

/// <summary>
/// Logs the host of every outbound HTTP request to the existing rolling log. It gives visibility into
/// what this app actually talks to over the network, not a firewall. Wraps the app's shared HttpClient
/// instances. <see cref="Logging"/> is settable rather than a constructor argument because the handler
/// is built at static-field-init time, before a <see cref="LoggingService"/> instance exists.
/// </summary>
public sealed class TrafficLoggingHandler : DelegatingHandler
{
    private const int MaxRedirects = 5;

    public LoggingService? Logging { get; set; }

    /// <summary>
    /// The inner handler never follows redirects itself: see <see cref="SendAsync"/> for why. A caller
    /// that needs a fully custom inner handler passes its own; every other caller keeps the plain default.
    /// </summary>
    public TrafficLoggingHandler(HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var startedOnHttps = request.RequestUri?.Scheme == Uri.UriSchemeHttps;
        var current = request;

        // Every source sharing this handler's HttpClient (SourceRegistry) has no per-call host allowlist
        // the way self-update/NVIDIA do. Checking the FINAL url only after the inner handler already
        // followed a redirect proves nothing: by then the clear-text request had already been sent to
        // wherever the redirect named (same lesson as RegistryEnrichment's manual hop-by-hop loop). Each
        // hop is therefore followed by hand, with the scheme checked BEFORE that hop is sent, not after.
        for (var hop = 0; ; hop++)
        {
            // Host only, never the full URL: a query string can carry the very data a call is looking up
            // (package names, tokens in principle), and SECURITY.md rules that out of the log.
            if (current.RequestUri is { } uri)
                Logging?.Info($"HTTP -> {uri.Host}");

            var response = await base.SendAsync(current, cancellationToken);

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
                return response;

            if (hop >= MaxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException("Refused: too many redirects.");
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);

            // A request that started on HTTPS must never end up trusting a plain-HTTP response, whatever
            // redirect chain got it there. A request that was HTTP from the start (the local Ollama API
            // on loopback) is untouched: this only refuses a DOWNGRADE.
            if (startedOnHttps && next.Scheme != Uri.UriSchemeHttps)
            {
                Logging?.Warn($"HTTP: refusing a redirect that left HTTPS, to '{next.Host}'.");
                response.Dispose();
                throw new HttpRequestException("Refused: redirect left HTTPS.");
            }

            var redirected = new HttpRequestMessage(current.Method, next);
            foreach (var header in current.Headers)
            {
                // Authorization is never carried across a redirect to a different host. None of today's
                // callers set it on this shared client, but a future one might, and stripping it here
                // costs nothing.
                if (header.Key == "Authorization" && !string.Equals(next.Host, current.RequestUri?.Host, StringComparison.OrdinalIgnoreCase))
                    continue;
                redirected.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Dispose();
            current = redirected;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
