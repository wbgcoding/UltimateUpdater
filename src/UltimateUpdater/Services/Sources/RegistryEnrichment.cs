using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace UltimateUpdater.Services.Sources;

/// <summary>How one registry lookup ended.</summary>
internal enum RegistryFetchStatus
{
    /// <summary>The registry answered with a body.</summary>
    Ok,

    /// <summary>The registry answered that it has no such package (HTTP 404 or 410). A normal state for a
    /// package from a private index, a git install or a sideloaded extension, never a failure.</summary>
    NotFound,

    /// <summary>No usable answer: any other status (403 and 429 included), an oversized body, a connection
    /// error or a timeout.</summary>
    Failed,
}

/// <summary>
/// The outcome of one registry lookup. <see cref="Error"/> is set for <see cref="RegistryFetchStatus.Failed"/>
/// only; an internal timeout is recorded as a <see cref="TimeoutException"/>, never as an
/// <see cref="OperationCanceledException"/>, because a cancellation reads as "the user stopped this source".
/// </summary>
internal readonly record struct RegistryFetch(string? Body, RegistryFetchStatus Status, Exception? Error)
{
    /// <summary>
    /// A source that asked a registry about every installed package and got no usable answer from ANY of
    /// them has not checked anything: that is a failed source, not a machine with nothing to update. Any
    /// answer at all (a body or a plain "not found") keeps the source's normal result. A lookup that was
    /// never made is passed as null and does not count. The first connection failure is rethrown as is, so
    /// the offline detection still sees the original exception type; failing that, a timeout is rethrown;
    /// failing that, a plain failure naming the source. (A refused proxy never reaches here: it propagates out of the lookup.)
    /// </summary>
    public static void ThrowIfAllFailed(string sourceId, IEnumerable<RegistryFetch?> outcomes)
    {
        var made = outcomes.Where(outcome => outcome is not null).Select(outcome => outcome!.Value).ToList();
        if (made.Count == 0 || made.Any(outcome => outcome.Status != RegistryFetchStatus.Failed))
            return;

        var errors = made.Select(outcome => outcome.Error).Where(error => error is not null).Select(error => error!).ToList();

        var connection = errors.FirstOrDefault(error => error is HttpRequestException);
        if (connection is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(connection).Throw();

        if (errors.Any(error => error is TimeoutException))
            throw new TimeoutException($"{sourceId}: every registry lookup timed out.");

        throw new InvalidOperationException($"{sourceId}: every registry lookup failed.");
    }
}

/// <summary>
/// Shared plumbing for the read-only registry lookups the enrichment pass makes (PyPI, npm). One place
/// for the GET → parse → fail-closed contract so each source only supplies its URL and its parser.
/// Every failure (a bad status, a timeout, oversized content, a parse miss) returns null and is
/// logged, never thrown (SECURITY.md: a registry response is hostile input and must not fault a scan).
/// </summary>
internal static class RegistryEnrichment
{
    /// <summary>A packument for a popular package can be large; refuse to buffer more than this.</summary>
    private const long MaxContentBytes = 8L * 1024 * 1024;

    /// <summary>
    /// A dedicated client with redirects OFF, used only by <see cref="HeadContentLengthAsync"/>; the
    /// shared <see cref="HttpClient"/> everything else uses follows redirects on its own, which is exactly
    /// what let a request reach a private address before this method could ever check where it landed
    /// Never used for the enrichment GET, which has no SSRF exposure of its own (fixed,
    /// hard-coded registry hosts).
    /// </summary>
    internal static readonly HttpClient NoRedirectClient =
        // UseProxy = false: with a proxy, ConnectCallback would dial the proxy (refused when it sits on a
        // private address, and a public one would resolve the target itself, undoing the address check).
        new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = ConnectToPublicAddressAsync })
        {
            Timeout = ExternalCallTimeouts.Default,
        };

    /// <summary>
    /// Dials only an address that passed the public-address check, and checks it at the moment of dialling.
    /// Resolving the host for validation and then letting the stack resolve it again for the connection
    /// leaves a window in which the second answer can be a different, private address; the name is under
    /// the manifest author's control, and so is its TTL. Connecting to the very addresses that were
    /// validated closes it; the host name is untouched, so TLS still validates against the certificate.
    /// </summary>
    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var allowed = addresses.Where(IsPublicAddress).ToArray();

        if (allowed.Length == 0)
            throw new HttpRequestException($"'{context.DnsEndPoint.Host}' does not resolve to a public address.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <param name="extraHeaders">
    /// Per-request headers (e.g. GitHub's mandatory User-Agent) applied to one <see cref="HttpRequestMessage"/>
    /// rather than the shared client's <c>DefaultRequestHeaders</c>; the client is reused by every source, so
    /// mutating it there would leak one source's headers onto every other source's requests. Never used for
    /// credentials.
    /// </param>
    public static async Task<UpdateEnrichment?> FetchAsync(
        HttpClient httpClient,
        string url,
        Func<string, UpdateEnrichment> parse,
        LoggingService logging,
        string sourceId,
        string itemId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var json = await FetchRawAsync(httpClient, url, logging, sourceId, itemId, cancellationToken, extraHeaders);
        if (json is null)
            return null;

        var enrichment = parse(json);

        // Relaxed from "date or size" so a version-only or url-only result (pipx/VS Code/gh
        // extensions, whose own listing reports no target version at all) is not thrown away.
        // It still fails closed when the parser genuinely found nothing. Includes the changelog fields
        // and the publisher so a package with only one of those but no other new fact is not dropped either.
        return enrichment.PublishDate is null && enrichment.DownloadSize is null &&
               enrichment.ProjectUrl is null && enrichment.AvailableVersion is null &&
               enrichment.ChangelogBody is null && enrichment.ChangelogUrl is null &&
               enrichment.Publisher is null
            ? null
            : enrichment;
    }

    /// <summary>
    /// The GET → size-cap → body plumbing <see cref="FetchAsync"/> wraps, exposed on its own for a
    /// caller that needs more than one fact out of the SAME response (e.g. npm's packument carries both
    /// the publish date and, as a fallback, the tarball URL to HEAD for size); one request instead of
    /// parsing twice against two separate fetches. Same fail-closed contract: any failure returns null.
    /// </summary>
    public static async Task<string?> FetchRawAsync(
        HttpClient httpClient,
        string url,
        LoggingService logging,
        string sourceId,
        string itemId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        TimeSpan? timeout = null,
        long? maxContentBytes = null)
    {
        return (await FetchRawOutcomeAsync(
            httpClient, url, logging, sourceId, itemId, cancellationToken, extraHeaders, timeout, maxContentBytes)).Body;
    }

    /// <summary>
    /// The same lookup as <see cref="FetchRawAsync"/>, but says HOW it ended instead of collapsing every
    /// failure to null, so a scan can tell "the registry does not know this package" from "the registry
    /// could not be asked". <paramref name="timeout"/> bounds the whole lookup, body included, and defaults
    /// to <see cref="ExternalCallTimeouts.Long"/>: the response is read with ResponseHeadersRead, so the
    /// client's own timeout stops at the headers and a body that stalls would otherwise never end. When it
    /// fires the outcome is a failure carrying a <see cref="TimeoutException"/>. Cancellation by
    /// <paramref name="cancellationToken"/> still propagates. <paramref name="maxContentBytes"/> raises the
    /// size cap for the one caller that knowingly reads a very large document.
    /// </summary>
    public static async Task<RegistryFetch> FetchRawOutcomeAsync(
        HttpClient httpClient,
        string url,
        LoggingService logging,
        string sourceId,
        string itemId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        TimeSpan? timeout = null,
        long? maxContentBytes = null)
    {
        var outcome = await FetchCoreAsync(
            httpClient, url, logging, sourceId, itemId, cancellationToken, extraHeaders, timeout, maxContentBytes,
            (response, cap, bounded) => ReadLimitedAsync(response, cap, logging, sourceId, itemId, bounded),
            "is too large");
        return new RegistryFetch(outcome.Value, outcome.Status, outcome.Error);
    }

    /// <summary>
    /// The same GET with the same status mapping, timeout, size ceiling and fail-closed contract, but the
    /// body is handed to <paramref name="read"/> as a stream instead of being buffered into a string, for a
    /// document too large to hold (npm's full packument). <paramref name="read"/> gets the stream, the byte
    /// ceiling, a callback to log an exceeded ceiling and the token that carries the lookup's time limit; it
    /// returns null for a body it could not use. Any failure returns null, never throws (cancellation by the
    /// caller excepted).
    /// </summary>
    public static async Task<T?> FetchStreamedAsync<T>(
        HttpClient httpClient,
        string url,
        LoggingService logging,
        string sourceId,
        string itemId,
        CancellationToken cancellationToken,
        Func<Stream, long, Action, CancellationToken, Task<T?>> read,
        TimeSpan? timeout = null,
        long? maxContentBytes = null) where T : class
    {
        var outcome = await FetchCoreAsync(
            httpClient, url, logging, sourceId, itemId, cancellationToken, null, timeout, maxContentBytes,
            async (response, cap, bounded) =>
            {
                await using var stream = await response.Content.ReadAsStreamAsync(bounded);
                return await read(
                    stream, cap,
                    () => logging.Warn($"{sourceId}: enrichment response for '{itemId}' exceeded the size limit; refusing."),
                    bounded);
            },
            "could not be read");
        return outcome.Value;
    }

    private static async Task<(T? Value, RegistryFetchStatus Status, Exception? Error)> FetchCoreAsync<T>(
        HttpClient httpClient,
        string url,
        LoggingService logging,
        string sourceId,
        string itemId,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraHeaders,
        TimeSpan? timeout,
        long? maxContentBytes,
        Func<HttpResponseMessage, long, CancellationToken, Task<T?>> readBody,
        string unusableBodyText) where T : class
    {
        var cap = maxContentBytes ?? MaxContentBytes;

        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(timeout ?? ExternalCallTimeouts.Long);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (extraHeaders is not null)
            {
                foreach (var header in extraHeaders)
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return (null, RegistryFetchStatus.NotFound, null);

            if (!response.IsSuccessStatusCode)
            {
                return (null, RegistryFetchStatus.Failed,
                    new InvalidOperationException($"{sourceId}: the registry answered {(int)response.StatusCode} for '{itemId}'."));
            }

            if (response.Content.Headers.ContentLength > cap)
            {
                return (null, RegistryFetchStatus.Failed,
                    new InvalidOperationException($"{sourceId}: the registry response for '{itemId}' is too large."));
            }

            var body = await readBody(response, cap, bounded.Token);
            return body is null
                ? (null, RegistryFetchStatus.Failed,
                    new InvalidOperationException($"{sourceId}: the registry response for '{itemId}' {unusableBodyText}."))
                : (body, RegistryFetchStatus.Ok, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Not cancelled by the caller: the lookup's own time limit, or the client's. A timeout, not a cancellation.
            logging.Warn($"{sourceId}: registry lookup for '{itemId}' failed: {ex.Message}");
            return (null, RegistryFetchStatus.Failed,
                new TimeoutException($"{sourceId}: the registry did not answer for '{itemId}' in time.", ex));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            // Enrichment is best-effort, so a slow or missing registry never disturbs the scan it decorates.
            // IOException covers a connection that drops while the body is read (HttpIOException), which
            // happens after the headers arrived, so outside HttpRequestException.
            // A refused proxy (ProxyConnectionFailedException) is deliberately not caught: it names the proxy
            // and fails the caller outright.
            logging.Warn($"{sourceId}: registry lookup for '{itemId}' failed: {ex.Message}");
            return (null, RegistryFetchStatus.Failed, ex);
        }
    }

    /// <summary>
    /// Content-Length of a download URL a registry response pointed at, for the sources whose metadata
    /// API reports no size of its own (gem, composer); a HEAD costs nothing to download and answers
    /// exactly the one question asked. Same fail-closed contract as <see cref="FetchAsync"/>: any
    /// failure, a missing header, or a non-HTTPS URL returns null rather than throwing.
    /// </summary>
    public static async Task<long?> HeadContentLengthAsync(
        string? url, LoggingService logging, string sourceId, string itemId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        // Composer/RubyGems/Scoop hand this a download URL straight out of a third-party manifest;
        // unlike NVIDIA's own feed, there is no small, fixed set of hosts to allowlist (every app vendor
        // hosts its own binary wherever it likes), so a domain allowlist would just break enrichment for
        // the common case. The actual risk a HEAD request carries is SSRF: a malicious/compromised
        // manifest naming an internal address (a cloud metadata endpoint, a LAN admin panel, this
        // machine's own loopback) would have this app dutifully probe it. Resolving the host and refusing
        // anything private/loopback/link-local closes that without needing to know every legitimate host
        // in advance (a target-range check instead of a domain list is both safer and needs no host list
        // to maintain).
        if (!await IsPublicHostAsync(uri.Host, cancellationToken))
        {
            logging.Warn($"{sourceId}: refusing to query '{uri.Host}' for '{itemId}'; not a public address.");
            return null;
        }

        try
        {
            // A shared HttpClient follows redirects automatically; by the time a post-hoc check could
            // look at where the request ended up, the request itself had already been SENT to whatever
            // the redirect named, private address or not (checking after `SendAsync` returns proves
            // nothing; the SSRF already happened). Each hop is followed by hand instead, with the
            // SAME public-address check run before every single request, never only the first.
            var current = uri;
            for (var redirects = 0; redirects <= 5; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, current);
                using var response = await NoRedirectClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!IsRedirect(response.StatusCode))
                    return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength : null;

                var location = response.Headers.Location;
                if (location is null)
                    return null;

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (next.Scheme != Uri.UriSchemeHttps || !await IsPublicHostAsync(next.Host, cancellationToken))
                {
                    logging.Warn($"{sourceId}: '{itemId}' redirected to '{next.Host}'; not a public address; refusing.");
                    return null;
                }

                current = next;
            }

            logging.Warn($"{sourceId}: '{itemId}' redirected too many times; refusing.");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            logging.Warn($"{sourceId}: size lookup for '{itemId}' failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// True only when EVERY address the host resolves to is a public, routable address; a hostname that
    /// resolves to more than one address (some public, some not) is refused outright, the same "never
    /// guess, refuse instead" posture as everywhere else in this project. Resolution failure also refuses:
    /// an unresolvable host was never going to answer a HEAD request honestly anyway.
    /// </summary>
    internal static async Task<bool> IsPublicHostAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return false;
        }

        return addresses.Length > 0 && addresses.All(IsPublicAddress);
    }

    /// <summary>
    /// A denylist has to name every exception, and there are too many to trust: 100.64.0.0/10 alone
    /// (CGNAT; routable on carrier networks, not on the public internet, and exactly where a phone-scale
    /// ISP's internal admin surfaces live) plus nearly every IPv6 special-use range beyond link-local/ULA
    /// (documentation, Teredo, the NAT64 well-known prefix, 6to4's embedded-IPv4 tunnel, discard-only,
    /// IPv4-mapped ranges other than the one exact /96.NET already unwraps) are easy to miss. This
    /// ALLOWLISTS instead; IPv4 must be none of the reserved/special ranges, IPv6
    /// must fall in the Global Unicast block (2000::/3) AND not be one of the tunnelling/documentation
    /// ranges carved out of it, with 6to4/NAT64 additionally checked by unwrapping the IPv4 address they
    /// embed (a 6to4 address can smuggle a private IPv4 target the same way ::ffff: mapping can).
    /// </summary>
    internal static bool IsPublicAddress(IPAddress address)
    {
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (candidate.AddressFamily == AddressFamily.InterNetwork)
            return IsPublicIPv4(candidate.GetAddressBytes());

        if (candidate.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        if (IPAddress.IsLoopback(candidate) || candidate.IsIPv6LinkLocal || candidate.IsIPv6SiteLocal
            || candidate.IsIPv6UniqueLocal || candidate.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        var v6 = candidate.GetAddressBytes();

        // NAT64's well-known prefix (64:ff9b::/96) sits OUTSIDE the Global Unicast block entirely, so it
        // must be checked before that gate would otherwise refuse it outright. It embeds an IPv4 address
        // in the last 4 bytes; unwrap and re-check it, the same reasoning as the ::ffff: unwrap above.
        if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b
            && v6[4] == 0 && v6[5] == 0 && v6[6] == 0 && v6[7] == 0 && v6[8] == 0 && v6[9] == 0 && v6[10] == 0 && v6[11] == 0)
        {
            return IsPublicIPv4(v6[12..16]);
        }

        // Global Unicast only from here on: the top 3 bits must be 001 (2000::/3). Everything else
        // (discard-only 100::/64, the rest of the address space) is refused by default rather than
        // enumerated.
        if ((v6[0] & 0b1110_0000) != 0b0010_0000)
            return false;

        // Teredo (2001::/32) and documentation (2001:db8::/32) sit inside the Global Unicast block but
        // name no real public host.
        if (v6[0] == 0x20 && v6[1] == 0x01)
        {
            if (v6[2] == 0x00 && v6[3] == 0x00)
                return false; // Teredo, 2001:0000::/32

            if (v6[2] == 0x0d && v6[3] == 0xb8)
                return false; // documentation, 2001:db8::/32
        }

        // 6to4 (2002::/16) embeds an IPv4 address in the next 4 bytes; unwrap and re-check it, the same
        // reasoning as the ::ffff: IPv4-mapped unwrap above.
        if (v6[0] == 0x20 && v6[1] == 0x02)
            return IsPublicIPv4(v6[2..6]);

        return true;
    }

    private static bool IsPublicIPv4(ReadOnlySpan<byte> bytes) => bytes[0] switch
    {
        10 => false, // 10.0.0.0/8
        127 => false, // 127.0.0.0/8
        169 when bytes[1] == 254 => false, // 169.254.0.0/16, link-local, including cloud metadata
        172 when bytes[1] is >= 16 and <= 31 => false, // 172.16.0.0/12
        192 when bytes[1] == 168 => false, // 192.168.0.0/16
        192 when bytes[1] == 0 && bytes[2] is 0 or 2 => false, // 192.0.0.0/24 (IETF), 192.0.2.0/24 (TEST-NET-1)
        198 when bytes[1] is 18 or 19 => false, // 198.18.0.0/15; benchmarking
        198 when bytes[1] == 51 && bytes[2] == 100 => false, // 198.51.100.0/24; TEST-NET-2
        203 when bytes[1] == 0 && bytes[2] == 113 => false, // 203.0.113.0/24; TEST-NET-3
        100 when bytes[1] is >= 64 and <= 127 => false, // 100.64.0.0/10; CGNAT
        0 => false, // 0.0.0.0/8
        >= 224 => false, // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, 255.255.255.255
        _ => true,
    };

    /// <summary>
    /// Reads the body with the ceiling enforced on the STREAM, like <c>UpdateMetadata.ReadLimitedAsync</c>.
    /// The Content-Length check above is an early out only: a chunked response advertises no length at all,
    /// and that one was buffered whole; with the enrichment pass running a request per row in parallel,
    /// that is the shape of an out-of-memory. Read in chunks rather than into one MaxContentBytes buffer for
    /// the same reason: dozens of these are in flight at once.
    /// </summary>
    private static Task<string?> ReadLimitedAsync(
        HttpResponseMessage response,
        long maxContentBytes,
        LoggingService logging,
        string sourceId,
        string itemId,
        CancellationToken cancellationToken) =>
        HttpResponseReading.ReadLimitedAsync(response, maxContentBytes, cancellationToken,
            () => logging.Warn($"{sourceId}: enrichment response for '{itemId}' exceeded the size limit; refusing."));
}
