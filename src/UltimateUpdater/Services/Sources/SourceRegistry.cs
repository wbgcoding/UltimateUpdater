using System.Net;
using System.Net.Http;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// The shared scan client tried the system proxy and it refused the connection outright (407) or could
/// not be reached at all, and the direct retry <see cref="SourceRegistry"/> falls back to also failed.
/// Distinct from an ordinary <see cref="HttpRequestException"/> so a source's own catch (which normally
/// lands on <see cref="ScanOrchestrator"/>'s generic error text) can instead surface a clear line naming
/// the proxy, rather than whatever bare socket error the failed retry itself produced.
/// </summary>
public sealed class ProxyConnectionFailedException(string proxyAddress, Exception innerException)
    : Exception($"Proxy at '{proxyAddress}' refused the connection.", innerException)
{
    public string ProxyAddress { get; } = proxyAddress;
}

/// <summary>
/// Single place where the provider set is defined, so the app and the elevated worker always agree
/// on which sources exist and how they are constructed.
/// </summary>
public static class SourceRegistry
{
    /// <summary>
    /// Every source id <see cref="CreateAll"/> can produce, without constructing any of the roughly
    /// forty source objects themselves (a shared HTTP client, a registry view, a launcher catalogue walk):
    /// settings loading, which also runs inside the elevated worker process, must be able to check a key
    /// against the known set without paying for any of that. Pinned against <see cref="CreateAll"/>'s
    /// actual output by a dedicated test so the two can never silently drift apart.
    /// </summary>
    public static IReadOnlyCollection<string> KnownIds { get; } = BuildKnownIds();

    private static IReadOnlyCollection<string> BuildKnownIds()
    {
        HashSet<string> ids =
        [
            "winget", "windowsupdate", "nvidia-driver", "chocolatey", "scoop", "pip", "npm", "pnpm",
            "dotnet-tool", "psgallery", "gem", "composer", "rustup", "vcpkg", "mise", "android-sdk",
            "android-studio", "gh-extension", "pipx", "vscode-extension", "wsl", "deno", "bun", "uv",
            "uv-tool", "gcloud", "ollama", "flutter", "volta", "pyenv-win", "steam", "msstore",
            "github-release", "epic", "gog",
        ];

        // The per-launcher Tier C rows are data-driven (LauncherCatalog.Known(), no construction), so
        // their ids are added the same way ClientLauncherSource.CreateKnown builds the sources themselves.
        foreach (var launcher in LauncherCatalog.Known())
            ids.Add(launcher.Id);

        return ids;
    }

    /// <summary>
    /// Which sources only ever read THIS machine and never need a working internet connection to say
    /// anything useful: an explicit table, matched exactly, never by similarity or by tier (a wrong
    /// "does not need network" would silently scan a source that can only fail against a dead
    /// connection). Everything not listed here is assumed to need network, the same fail-safe direction
    /// <see cref="IUpdateSource"/>'s own default-true members already lean.
    /// <list type="bullet">
    /// <item>wsl: detection only, never reports an update (see WslSource).</item>
    /// <item>android-studio: reads the local jdk.table.xml only, surfaces only when sdkmanager is absent.</item>
    /// <item>msstore, epic, gog and every LauncherCatalog entry: Tier C, detect-and-open only; each one
    /// only checks whether the launcher's own executable/registry entry is present.</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> LocalOnlySourceIds = BuildLocalOnlySourceIds();

    private static HashSet<string> BuildLocalOnlySourceIds()
    {
        HashSet<string> ids = ["wsl", "android-studio", "msstore", "epic", "gog"];

        foreach (var launcher in LauncherCatalog.Known())
            ids.Add(launcher.Id);

        return ids;
    }

    /// <summary>True unless the source is explicitly known to only ever read the local machine: the
    /// safe default for a source this table has never heard of is to assume it needs a connection,
    /// never the other way round.</summary>
    public static bool NeedsNetwork(string sourceId) => !LocalOnlySourceIds.Contains(sourceId);

    // one shared client: HttpClient is designed to be reused, and per-call instances exhaust sockets.
    // Two full handler chains, each with its own TrafficLoggingHandler (so both log and both follow
    // redirects by hand, exactly like every other caller of this class): the normal path honours
    // whatever proxy Windows itself is configured with, made explicit and testable rather than merely
    // relying on the .NET default (a corporate network's own proxy, most commonly); the direct path
    // never uses one at all, and exists ONLY as ProxyFallbackHandler's one-time retry target when the
    // proxy itself refuses the connection. This changes nothing about TLS or certificate validation on
    // either path, and does not touch the separate client self-update uses. The NVIDIA source shares this
    // client and adds its own host allowlist and final-URL check on top.
    private static readonly Security.TrafficLoggingHandler ProxyTrafficHandler = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = true,
        Proxy = System.Net.Http.HttpClient.DefaultProxy,
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
    });

    private static readonly Security.TrafficLoggingHandler DirectTrafficHandler = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
    });

    // The buffered ceiling for every source that reads a registry response whole (GetStringAsync /
    // ReadAsStringAsync): several of those run per row and in parallel during enrichment, so one hostile
    // or broken endpoint could otherwise answer a scan with as much memory as it likes. 8 MB is the same
    // ceiling RegistryEnrichment already enforces by hand, and no registry answer here comes near it.
    // Streaming callers (ResponseHeadersRead + ReadAsStreamAsync: the NVIDIA installer, the Android SDK
    // manifest, enrichment itself) are untouched by this and keep their own explicit byte limits.
    private const int MaxBufferedResponseBytes = 8 * 1024 * 1024;

    private static readonly HttpClient HttpClient = new(new ProxyFallbackHandler(ProxyTrafficHandler, DirectTrafficHandler))
    {
        Timeout = ExternalCallTimeouts.Long,
        MaxResponseContentBufferSize = MaxBufferedResponseBytes,
    };

    /// <summary>
    /// Sits in front of <paramref name="proxyHandler"/>: a request that comes back as 407 (proxy
    /// authentication required), never reaches the proxy at all, or gets no answer from it within
    /// <see cref="ExternalCallTimeouts.ProxyAttempt"/> is retried EXACTLY ONCE, through
    /// <paramref name="directHandler"/> instead. A second failure on that direct retry is final and
    /// surfaces as <see cref="ProxyConnectionFailedException"/> rather than whatever bare
    /// <see cref="HttpRequestException"/> the retry itself produced, so a source's own catch block
    /// (which otherwise lands on the generic scan-failed text) can name the proxy specifically.
    /// </summary>
    internal sealed class ProxyFallbackHandler(
        HttpMessageHandler proxyHandler, HttpMessageHandler directHandler, TimeSpan? proxyAttemptTimeout = null)
        : DelegatingHandler(proxyHandler)
    {
        private readonly TimeSpan _proxyAttemptTimeout = proxyAttemptTimeout ?? ExternalCallTimeouts.ProxyAttempt;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Only take the "proxy failed, retry direct" path when a proxy genuinely applies to THIS
            // request: Windows reporting no proxy at all (the common case off a corporate network) still
            // sends every request through here, and without this check its first, only attempt failing
            // was blamed on "the proxy" and cost a second, redundant attempt for nothing.
            var hasRealProxy = HasRealProxyFor(request.RequestUri);
            HttpResponseMessage? response = null;

            try
            {
                response = hasRealProxy
                    ? await SendThroughProxyAsync(request, cancellationToken)
                    : await base.SendAsync(request, cancellationToken);
                if (response.StatusCode != HttpStatusCode.ProxyAuthenticationRequired || !hasRealProxy)
                    return response;
            }
            catch (HttpRequestException) when (hasRealProxy)
            {
                // Could not even reach the proxy; fall through to the direct retry below exactly the
                // same way a 407 does. When no real proxy applies the `when` clause is false, so the
                // exception is never caught here at all and propagates unchanged instead.
            }
            catch (OperationCanceledException) when (hasRealProxy && !cancellationToken.IsCancellationRequested)
            {
                // The proxy accepted nothing and answered nothing within the attempt budget (only this
                // handler's own timer can cancel here, the caller's token is untouched): same retry as
                // an unreachable proxy. A cancellation the caller asked for fails the `when` and propagates.
            }

            response?.Dispose();

            var proxyAddress = DescribeProxy(request.RequestUri);
            LoggingFor(InnerHandler)?.Warn($"Proxy at '{proxyAddress}' refused the connection; retrying '{request.RequestUri?.Host}' directly.");

            try
            {
                using var direct = new HttpMessageInvoker(directHandler, disposeHandler: false);
                var retried = await direct.SendAsync(CloneWithoutBody(request), cancellationToken);
                LoggingFor(directHandler)?.Info($"Connected to '{request.RequestUri?.Host}' directly after the proxy failed.");
                return retried;
            }
            catch (HttpRequestException ex)
            {
                throw new ProxyConnectionFailedException(proxyAddress, ex);
            }
        }

        /// <summary>The proxy attempt alone is bounded: the handler returns at the response headers, so this
        /// covers connecting and waiting for the headers, never the body that follows.</summary>
        private async Task<HttpResponseMessage> SendThroughProxyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(_proxyAttemptTimeout);
            return await base.SendAsync(request, attempt.Token);
        }

        private static LoggingService? LoggingFor(HttpMessageHandler? handler) =>
            (handler as Security.TrafficLoggingHandler)?.Logging;

        /// <summary>Every call through this shared client is a GET with no body, so a plain copy of
        /// method/URI/headers is enough; an HttpRequestMessage cannot be sent twice regardless.</summary>
        private static HttpRequestMessage CloneWithoutBody(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            return clone;
        }

        /// <summary>True only when a proxy genuinely stands between this app and <paramref name="requestUri"/>:
        /// <c>GetProxy</c> names an address other than the destination itself AND that destination is not
        /// on the proxy's own bypass list. Windows reporting no proxy at all for a request answers
        /// <c>GetProxy</c> with the destination URI unchanged and <c>IsBypassed</c> true, which is exactly
        /// the case this must say no to.</summary>
        private static bool HasRealProxyFor(Uri? requestUri)
        {
            if (requestUri is null)
                return false;

            try
            {
                var proxy = System.Net.Http.HttpClient.DefaultProxy;
                var proxyUri = proxy.GetProxy(requestUri);
                return proxyUri is not null && proxyUri != requestUri && !proxy.IsBypassed(requestUri);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException)
            {
                return false;
            }
        }

        private static string DescribeProxy(Uri? requestUri)
        {
            try
            {
                var proxyUri = requestUri is not null
                    ? System.Net.Http.HttpClient.DefaultProxy.GetProxy(requestUri)
                    : null;
                return proxyUri?.ToString() ?? "system proxy";
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException)
            {
                return "system proxy";
            }
        }
    }

    public static IReadOnlyList<IUpdateSource> CreateAll(LoggingService logging)
    {
        ProxyTrafficHandler.Logging = logging;
        DirectTrafficHandler.Logging = logging;

        // The self-updating runtimes show a row ONLY when no package manager owns their
        // executable: see SelfUpdatingToolSource for the rule and for why Git for Windows is absent.
        // Named locally (not inline below) so GitHubReleaseSource can be told which GitHub repos they
        // already own. Otherwise it does not know astral-sh/uv is "uv", and both this source AND the
        // generic fallback produced a row for it.
        var deno = new DenoSource(logging, HttpClient);
        var bun = new BunSource(logging, HttpClient);
        var uvSelf = new UvSelfSource(logging, HttpClient);
        SelfUpdatingToolSource[] selfUpdating = [deno, bun, uvSelf];
        var ownedRepos = selfUpdating.Select(s => s.CoversGitHubRepo).ToList();

        return [
            // Tier A: list and install
            new WingetSource(logging),
            new WindowsUpdateSource(logging),
            // NVIDIA is Tier A where its lookup succeeds (download + silent install), Tier C otherwise
            new NvidiaDriverSource(logging, HttpClient),
            new ChocolateySource(logging, HttpClient),
            new ScoopSource(logging),
            new PipSource(logging, HttpClient),
            new NpmSource(logging, HttpClient),
            new PnpmSource(logging, HttpClient),
            new DotnetToolSource(logging, HttpClient),
            new PsGallerySource(logging, HttpClient),
            new GemSource(logging, HttpClient),
            new ComposerSource(logging, HttpClient),
            new RustupSource(logging, HttpClient),
            new VcpkgSource(logging),
            new MiseSource(logging),
            // Android SDK, GitHub CLI extensions, pipx, VS Code extensions,
            // WSL (detection only: see WslSource for why it never reports an update).
            new AndroidSdkSource(logging, HttpClient),

            // Only surfaces when sdkmanager is absent: see AndroidStudioSource.
            new AndroidStudioSource(logging),
            new GhExtensionSource(logging, HttpClient),
            new PipxSource(logging, HttpClient),
            new VsCodeExtensionSource(logging, HttpClient),
            new WslSource(logging),

            deno,
            bun,
            uvSelf,
            new UvToolSource(logging, HttpClient),
            new GcloudComponentSource(logging),
            new OllamaSource(logging, HttpClient),
            new FlutterSource(logging, HttpClient),
            new VoltaSource(logging, HttpClient),
            new PyenvWinSource(logging),

            // Tier B: real status, install handed to the vendor
            new SteamSource(logging, httpClient: HttpClient),

            // Tier C: detect and open only
            new MicrosoftStoreSource(logging),
            // Any installed program whose own uninstall entry names a GitHub repo: a setup.exe
            // downloaded directly, not through any package manager. See class remarks for why this stays
            // Tier C rather than silently guessing an unknown installer's silent-switch. The ownedRepos set
            // keeps it out of the way of the dedicated self-updating sources above.
            new GitHubReleaseSource(logging, HttpClient, WindowsRegistryView.Instance, ownedRepos),
            new EpicGamesSource(logging),
            new GogGalaxySource(logging),
            .. ClientLauncherSource.CreateKnown(logging),
        ];
    }
}
