using System.IO;
using System.Net;
using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

/// <summary>
/// A registry that could not be asked is not a registry that knows nothing. When every lookup of a scan
/// failed the source fails (a connection error keeps its type so the offline banner still works); a plain
/// "not found" is a valid answer, and one good answer keeps the source's normal result.
/// </summary>
public class RegistryLookupFailureTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new FakeHandler(respond));

    private static HttpClient Offline() => Client(_ => throw new HttpRequestException("no network"));

    private static HttpClient Status(HttpStatusCode code) => Client(_ => new HttpResponseMessage(code));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static LoggingService Log() => new();

    private const string PsGalleryFeed = """
        <feed xmlns="http://www.w3.org/2005/Atom"
              xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"
              xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
          <entry><title>alpha</title>
            <m:properties><d:Version>2.0.0</d:Version></m:properties></entry>
        </feed>
        """;

    private static readonly IReadOnlyList<OutdatedPackage> Alpha = [new("alpha", "alpha", "1.0.0", null)];

    private static readonly IReadOnlyList<OutdatedPackage> AlphaAndBravo =
        [new("alpha", "alpha", "1.0.0", null), new("bravo", "bravo", "1.0.0", null)];

    private static readonly IReadOnlyList<OutdatedPackage> VsAlphaAndBravo =
        [new("pub.alpha", "alpha", "1.0.0", null), new("pub.bravo", "bravo", "1.0.0", null)];

    public static TheoryData<string> PerPackageSources() => new() { "pipx", "uv-tool", "vscode", "dotnet-tool", "psgallery" };

    private static string GoodBody(string source) => source switch
    {
        "vscode" => """{"versions":[{"version":"2.0.0"}]}""",
        "dotnet-tool" => """{"versions":["1.0.0","2.0.0"]}""",
        "psgallery" => PsGalleryFeed,
        _ => """{"info":{"version":"2.0.0"}}""",
    };

    private static IReadOnlyList<OutdatedPackage> Installed(string source) => source == "vscode" ? VsAlphaAndBravo : AlphaAndBravo;

    private static Task<IReadOnlyList<UpdateItem>> Run(string source, HttpClient client, IReadOnlyList<OutdatedPackage> installed) => source switch
    {
        "pipx" => new PipxSource(Log(), client).CheckAgainstPyPiAsync(installed, CancellationToken.None),
        "uv-tool" => new UvToolSource(Log(), client).CheckAgainstPyPiAsync(installed, CancellationToken.None),
        "vscode" => new VsCodeExtensionSource(Log(), client).CheckAgainstMarketplaceAsync(installed, CancellationToken.None),
        "dotnet-tool" => new DotnetToolSource(Log(), client).CheckAgainstNuGetAsync(installed, CancellationToken.None),
        "psgallery" => new PsGallerySource(Log(), client).CheckAgainstGalleryAsync(installed, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    [Theory]
    [MemberData(nameof(PerPackageSources))]
    public async Task Every_lookup_failing_with_a_connection_error_throws_that_error(string source) =>
        await Assert.ThrowsAsync<HttpRequestException>(() => Run(source, Offline(), Installed(source)));

    [Theory]
    [MemberData(nameof(PerPackageSources))]
    public async Task Every_lookup_refused_with_a_rate_limit_is_a_failed_source(string source) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(source, Status(HttpStatusCode.TooManyRequests), Installed(source)));

    [Theory]
    [MemberData(nameof(PerPackageSources))]
    public async Task Every_lookup_not_found_is_zero_rows_and_no_failure(string source) =>
        Assert.Empty(await Run(source, Status(HttpStatusCode.NotFound), Installed(source)));

    [Theory]
    [MemberData(nameof(PerPackageSources))]
    public async Task One_good_answer_among_failures_keeps_its_row(string source)
    {
        var client = Client(request => request.RequestUri!.ToString().Contains("alpha", StringComparison.Ordinal)
            ? Json(GoodBody(source))
            : throw new HttpRequestException("no network"));

        var rows = await Run(source, client, Installed(source));

        var row = Assert.Single(rows);
        Assert.Equal("2.0.0", row.AvailableVersion);
    }

    [Theory]
    [MemberData(nameof(PerPackageSources))]
    public async Task No_installed_packages_is_zero_rows_and_no_failure(string source) =>
        Assert.Empty(await Run(source, Offline(), []));

    // ---- single feed sources

    [Fact]
    public async Task Flutter_an_unreachable_feed_is_a_failed_source()
    {
        var source = new FlutterSource(Log(), Offline());

        await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchLatestAsync("stable", CancellationToken.None));
    }

    [Fact]
    public async Task Flutter_a_feed_that_refuses_is_a_failed_source()
    {
        var source = new FlutterSource(Log(), Status(HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchLatestAsync("stable", CancellationToken.None));
    }

    [Fact]
    public async Task Flutter_a_feed_that_is_gone_is_no_release_and_no_failure()
    {
        var source = new FlutterSource(Log(), Status(HttpStatusCode.NotFound));

        Assert.Null(await source.FetchLatestAsync("stable", CancellationToken.None));
    }

    [Fact]
    public async Task Volta_every_feed_unreachable_is_a_failed_source()
    {
        var source = new VoltaSource(Log(), Offline());
        VoltaDefaultTool[] tools = [new("runtime", "node", "18.19.0"), new("package-manager", "yarn", "1.22.19")];

        await Assert.ThrowsAsync<HttpRequestException>(() => source.CheckToolsAsync(tools, CancellationToken.None));
    }

    [Fact]
    public async Task Volta_feeds_that_do_not_know_the_tool_are_zero_rows()
    {
        var source = new VoltaSource(Log(), Status(HttpStatusCode.NotFound));
        VoltaDefaultTool[] tools = [new("package-manager", "yarn", "1.22.19")];

        Assert.Empty(await source.CheckToolsAsync(tools, CancellationToken.None));
    }

    [Fact]
    public async Task Volta_one_good_feed_among_failures_keeps_its_row()
    {
        var client = Client(request => request.RequestUri!.ToString().Contains("yarn", StringComparison.Ordinal)
            ? Json("""{"name":"yarn","version":"4.0.0"}""")
            : throw new HttpRequestException("no network"));
        var source = new VoltaSource(Log(), client);
        VoltaDefaultTool[] tools = [new("runtime", "node", "18.19.0"), new("package-manager", "yarn", "1.22.19")];

        var row = Assert.Single(await source.CheckToolsAsync(tools, CancellationToken.None));

        Assert.Equal("4.0.0", row.AvailableVersion);
    }

    [Fact]
    public async Task A_self_updating_tool_with_an_unreachable_GitHub_is_a_failed_source()
    {
        var source = new BunSource(Log(), Offline());

        await Assert.ThrowsAsync<HttpRequestException>(() => source.CheckLatestAsync("1.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task A_self_updating_tool_with_a_rate_limited_GitHub_is_a_failed_source()
    {
        var source = new BunSource(Log(), Status(HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.CheckLatestAsync("1.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task A_self_updating_tool_whose_repository_is_gone_is_zero_rows()
    {
        var source = new BunSource(Log(), Status(HttpStatusCode.NotFound));

        Assert.Empty(await source.CheckLatestAsync("1.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task A_self_updating_tool_with_a_newer_release_gets_its_row()
    {
        var source = new BunSource(Log(), Client(_ => Json("""{"tag_name":"v2.0.0"}""")));

        var row = Assert.Single(await source.CheckLatestAsync("1.0.0", CancellationToken.None));

        Assert.Equal("2.0.0", row.AvailableVersion);
    }

    // ---- the outcome itself

    [Fact]
    public async Task An_internal_timeout_is_a_failed_outcome_with_a_TimeoutException_not_a_cancellation()
    {
        using var client = new HttpClient(new HangingHandler());

        var fetch = await RegistryEnrichment.FetchRawOutcomeAsync(
            client, "https://example.invalid/x", Log(), "test", "item", CancellationToken.None,
            timeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(RegistryFetchStatus.Failed, fetch.Status);
        Assert.IsType<TimeoutException>(fetch.Error);
        Assert.Throws<TimeoutException>(() => RegistryFetch.ThrowIfAllFailed("test", [fetch]));
    }

    /// <summary>The headers arrive, then the body never does: the lookup's time limit must still end it.</summary>
    [Fact]
    public async Task A_body_that_stalls_after_the_headers_is_a_timeout()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });

        var fetch = await RegistryEnrichment.FetchRawOutcomeAsync(
            client, "https://example.invalid/x", Log(), "test", "item", CancellationToken.None,
            timeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(RegistryFetchStatus.Failed, fetch.Status);
        Assert.IsType<TimeoutException>(fetch.Error);
    }

    [Fact]
    public async Task A_raised_size_cap_admits_a_body_the_default_cap_refuses()
    {
        var body = new string('x', 9 * 1024 * 1024);
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

        var standard = await RegistryEnrichment.FetchRawOutcomeAsync(
            client, "https://example.invalid/x", Log(), "test", "item", CancellationToken.None);
        var raised = await RegistryEnrichment.FetchRawOutcomeAsync(
            client, "https://example.invalid/x", Log(), "test", "item", CancellationToken.None,
            maxContentBytes: 16L * 1024 * 1024);

        Assert.Equal(RegistryFetchStatus.Failed, standard.Status);
        Assert.Equal(RegistryFetchStatus.Ok, raised.Status);
    }

    /// <summary>Volta asks for the small `latest` manifest, never the full packument.</summary>
    [Fact]
    public async Task Volta_reads_the_latest_manifest_not_the_packument()
    {
        var requested = new List<string>();
        var client = Client(request =>
        {
            requested.Add(request.RequestUri!.ToString());
            return Json("""{"name":"npm","version":"11.0.0"}""");
        });
        var source = new VoltaSource(Log(), client);

        var row = Assert.Single(await source.CheckToolsAsync([new("package-manager", "npm", "10.0.0")], CancellationToken.None));

        Assert.Equal("11.0.0", row.AvailableVersion);
        Assert.Equal("https://registry.npmjs.org/npm/latest", Assert.Single(requested));
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [Fact]
    public async Task A_cancellation_by_the_caller_still_propagates()
    {
        using var client = new HttpClient(new HangingHandler());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RegistryEnrichment.FetchRawOutcomeAsync(
            client, "https://example.invalid/x", Log(), "test", "item", cancellation.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "NotFound")]
    [InlineData(HttpStatusCode.Gone, "NotFound")]
    [InlineData(HttpStatusCode.Forbidden, "Failed")]
    [InlineData(HttpStatusCode.TooManyRequests, "Failed")]
    [InlineData(HttpStatusCode.InternalServerError, "Failed")]
    [InlineData(HttpStatusCode.OK, "Ok")]
    public async Task Statuses_map_to_outcomes(HttpStatusCode code, string expected)
    {
        using var client = Client(_ => new HttpResponseMessage(code) { Content = new StringContent("{}") });

        var fetch = await RegistryEnrichment.FetchRawOutcomeAsync(
            client, "https://example.invalid/x", Log(), "test", "item", CancellationToken.None);

        Assert.Equal(expected, fetch.Status.ToString());
    }

    [Fact]
    public async Task The_best_effort_wrapper_still_returns_null_for_every_failure()
    {
        using var client = Client(_ => throw new HttpRequestException("no network"));

        Assert.Null(await RegistryEnrichment.FetchRawAsync(
            client, "https://example.invalid/x", Log(), "test", "item", CancellationToken.None));
    }
}
