using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// A repo already covered by a dedicated <see cref="SelfUpdatingToolSource"/> (uv, deno, bun) must never
/// ALSO get a Tier C manual row here, a Tier C row's <see cref="SourceRank.DuplicateKey"/> is always
/// null, so it can never merge away with the real row and just sits there duplicated.
/// </summary>
public class GitHubReleaseSourceTests
{
    [Fact]
    public async Task An_owned_repo_never_produces_a_row_or_reaches_the_network()
    {
        var registry = new FakeRegistry()
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\uv", "DisplayName", "uv")
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\uv", "DisplayVersion", "0.5.0")
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\uv", "URLInfoAbout", "https://github.com/astral-sh/uv");

        var handler = new ThrowingHandler();
        using var httpClient = new HttpClient(handler);
        var source = new GitHubReleaseSource(new LoggingService(), httpClient, registry, ["astral-sh/uv"]);

        var items = new List<UpdateItem>();
        await foreach (var item in source.ScanAsync(CancellationToken.None))
            items.Add(item);

        Assert.Empty(items);
        Assert.False(handler.WasCalled, "an owned repo must be filtered out before any HTTP call is made");
    }

    /// <summary>An UN-owned repo is the control case: it must still be found and looked up as before.</summary>
    [Fact]
    public async Task An_unowned_repo_still_reaches_the_network()
    {
        var registry = new FakeRegistry()
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\thing", "DisplayName", "Thing")
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\thing", "DisplayVersion", "1.0")
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\thing", "URLInfoAbout", "https://github.com/someone/thing");

        var handler = new ThrowingHandler();
        using var httpClient = new HttpClient(handler);
        var source = new GitHubReleaseSource(new LoggingService(), httpClient, registry, ["astral-sh/uv"]);

        // ThrowingHandler always throws, and with that one lookup failing the source has checked nothing,
        // so the scan fails with the connection error. handler.WasCalled proves the candidate was found
        // and an HTTP call was attempted.
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in source.ScanAsync(CancellationToken.None))
            {
            }
        });

        Assert.True(handler.WasCalled);
    }

    /// <summary>A rate limit on every lookup is a failed source, never a green "nothing found".</summary>
    [Fact]
    public async Task Every_lookup_refused_is_a_failed_source()
    {
        using var httpClient = new HttpClient(new StatusHandler(System.Net.HttpStatusCode.Forbidden));
        var source = new GitHubReleaseSource(new LoggingService(), httpClient, ThingRegistry(), []);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in source.ScanAsync(CancellationToken.None))
            {
            }
        });
    }

    /// <summary>A repository with no release (404) is an answer: zero rows, no failure.</summary>
    [Fact]
    public async Task A_repository_without_a_release_is_zero_rows_and_no_failure()
    {
        using var httpClient = new HttpClient(new StatusHandler(System.Net.HttpStatusCode.NotFound));
        var source = new GitHubReleaseSource(new LoggingService(), httpClient, ThingRegistry(), []);

        var items = new List<UpdateItem>();
        await foreach (var item in source.ScanAsync(CancellationToken.None))
            items.Add(item);

        Assert.Empty(items);
    }

    private static FakeRegistry ThingRegistry() => new FakeRegistry()
        .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\thing", "DisplayName", "Thing")
        .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\thing", "DisplayVersion", "1.0")
        .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\thing", "URLInfoAbout", "https://github.com/someone/thing");

    private sealed class StatusHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    /// <summary>
    /// mise-en-place is winget-installed (so `winget upgrade` already tracks it), but its OWN uninstall
    /// entry also names its GitHub repo, a repo list limited to a few hardcoded names (deno/bun/uv) would
    /// let this source produce a SECOND, duplicate row with a different "available" version than the real
    /// winget row. A candidate whose InstallLocation sits inside a package manager's own store must be
    /// excluded the same way, not just the hardcoded repos.
    /// </summary>
    [Fact]
    public async Task A_repo_whose_install_location_a_package_manager_already_owns_never_produces_a_row()
    {
        var wingetPackages = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages", "jdx.mise_x64");

        var registry = new FakeRegistry()
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\mise", "DisplayName", "mise-en-place")
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\mise", "DisplayVersion", "2026.7.12")
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\mise", "URLInfoAbout", "https://github.com/jdx/mise")
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\mise", "InstallLocation", wingetPackages);

        var handler = new ThrowingHandler();
        using var httpClient = new HttpClient(handler);
        var source = new GitHubReleaseSource(new LoggingService(), httpClient, registry, []);

        var items = new List<UpdateItem>();
        await foreach (var item in source.ScanAsync(CancellationToken.None))
            items.Add(item);

        Assert.Empty(items);
        Assert.False(handler.WasCalled, "a winget-owned install location must be filtered out before any HTTP call is made");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new HttpRequestException("must not be reached for an owned repo");
        }
    }

    /// <summary>A registry made of nothing but the keys a test puts in it.</summary>
    private sealed class FakeRegistry : IRegistryView
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public FakeRegistry Set(string keyPath, string valueName, string value)
        {
            if (!_keys.TryGetValue(keyPath, out var values))
                _keys[keyPath] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            values[valueName] = value;
            return this;
        }

        public IReadOnlyList<string> SubKeyNames(string keyPath) =>
            _keys.Keys
                .Where(key => key.StartsWith(keyPath + @"\", StringComparison.OrdinalIgnoreCase))
                .Select(key => key[(keyPath.Length + 1)..].Split('\\')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        public string? Value(string keyPath, string valueName) =>
            _keys.TryGetValue(keyPath, out var values) ? values.GetValueOrDefault(valueName) : null;
    }
}
