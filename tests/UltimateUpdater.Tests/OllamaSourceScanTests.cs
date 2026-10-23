using System.Net;
using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// The Ollama scan asks the local server for its model list once (the availability probe's answer is reused
/// by the scan that follows it) and checks the registry for the models a few at a time, not all at once.
/// Puts a stand-in "ollama.exe" on PATH for the duration of each test, safe because the suite runs serially.
/// </summary>
public class OllamaSourceScanTests : IDisposable
{
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
    private readonly string _tempDir = Directory.CreateTempSubdirectory("wu-ollama-test-").FullName;

    private const string Digest = "1b226e2802dbb8a9a1c5d7e2c6f8b1d5f1a0e4a7c3b9d2e6f8a1b4c7d0e3f5a7";
    private const string Newer = "2b226e2802dbb8a9a1c5d7e2c6f8b1d5f1a0e4a7c3b9d2e6f8a1b4c7d0e3f5a7";

    private sealed class FakeServer(int modelCount, int? modelsWithNewerDigest = null) : HttpMessageHandler
    {
        private int _inFlight;
        private int _peak;
        private int _tagsCalls;

        public int TagsCalls => _tagsCalls;

        public int PeakConcurrentHeads => _peak;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref _tagsCalls);
                var models = string.Join(",", Enumerable.Range(0, modelCount).Select(index =>
                    $$"""{"name":"model{{index}}:latest","digest":"{{Digest}}","size":1000,"modified_at":"2025-01-01T00:00:00Z"}"""));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"models":[{{models}}]}""") };
            }

            var now = Interlocked.Increment(ref _inFlight);
            try
            {
                int seen;
                while (now > (seen = Volatile.Read(ref _peak)))
                {
                    if (Interlocked.CompareExchange(ref _peak, now, seen) == seen)
                        break;
                }

                await Task.Delay(40, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("ollama-content-digest", modelsWithNewerDigest is null ? Digest : Newer);
            return response;
        }
    }

    /// <summary>A local server with two library models, and a registry that answers every HEAD the same way.</summary>
    private sealed class RegistryAnswering(Func<HttpResponseMessage> head) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"models":[{"name":"a:latest","digest":"{{Digest}}"},{"name":"b:latest","digest":"{{Digest}}"}]}"""),
                });
            }

            return Task.FromResult(head());
        }
    }

    private static async Task<int> ScanWith(HttpMessageHandler handler)
    {
        return await CountRowsAsync(new OllamaSource(new LoggingService(), new HttpClient(handler)));
    }

    [Fact]
    public async Task Every_registry_check_unreachable_is_a_failed_source() =>
        await Assert.ThrowsAsync<HttpRequestException>(() => ScanWith(new RegistryAnswering(() => throw new HttpRequestException("no network"))));

    [Fact]
    public async Task Every_registry_check_refused_is_a_failed_source() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScanWith(new RegistryAnswering(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests))));

    [Fact]
    public async Task Tags_the_registry_does_not_know_are_zero_rows_and_no_failure() =>
        Assert.Equal(0, await ScanWith(new RegistryAnswering(() => new HttpResponseMessage(HttpStatusCode.NotFound))));

    private OllamaSource Source(FakeServer server)
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "ollama.exe"), [0]);
        Environment.SetEnvironmentVariable("PATH", _tempDir);
        return new OllamaSource(new LoggingService(), new HttpClient(server));
    }

    private static async Task<int> CountRowsAsync(OllamaSource source)
    {
        var rows = 0;
        await foreach (var _ in source.ScanAsync(CancellationToken.None))
            rows++;
        return rows;
    }

    [Fact]
    public async Task Registry_checks_never_exceed_the_fan_out_bound()
    {
        var server = new FakeServer(BoundedFanOut.DefaultLimit * 3, modelsWithNewerDigest: 1);
        var source = Source(server);

        var rows = await CountRowsAsync(source);

        Assert.Equal(BoundedFanOut.DefaultLimit * 3, rows);
        Assert.InRange(server.PeakConcurrentHeads, 1, BoundedFanOut.DefaultLimit);
    }

    [Fact]
    public async Task A_probe_followed_by_a_scan_reads_the_model_list_once()
    {
        var server = new FakeServer(3);
        var source = Source(server);

        Assert.True(await source.IsAvailableAsync(CancellationToken.None));
        await CountRowsAsync(source);

        Assert.Equal(1, server.TagsCalls);
    }

    [Fact]
    public async Task A_later_scan_without_a_probe_reads_the_model_list_itself()
    {
        var server = new FakeServer(3);
        var source = Source(server);

        Assert.True(await source.IsAvailableAsync(CancellationToken.None));
        await CountRowsAsync(source);
        await CountRowsAsync(source);

        Assert.Equal(2, server.TagsCalls);
    }

    [Fact]
    public async Task Without_Ollama_on_PATH_the_server_is_never_asked()
    {
        var server = new FakeServer(3);
        var source = new OllamaSource(new LoggingService(), new HttpClient(server));
        Environment.SetEnvironmentVariable("PATH", _tempDir);

        Assert.False(await source.IsAvailableAsync(CancellationToken.None));
        Assert.Equal(0, server.TagsCalls);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
