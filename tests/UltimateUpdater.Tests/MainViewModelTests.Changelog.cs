using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>
    /// A link-only row whose release-page fetch comes back empty must fall through to the small "open
    /// the vendor's page" confirmation, never the big changelog window, and the fetch's "no text"
    /// result must not be mistaken for real text on the row.
    /// </summary>
    [Fact]
    public void ShowChangelogAsync_with_a_fetch_that_finds_nothing_never_opens_the_changelog_window()
    {
        WpfHarness.OnUiThread(() =>
        {
            const string id = "test-owner/no-text-repo";
            var viewModel = Create(changelogFetcher: (_, _) => Task.FromResult<string?>(null));
            var row = LinkOnlyChangelogRow(id);

            try
            {
                RunPumped(viewModel.ShowChangelogAsync(row));

                Assert.DoesNotContain(System.Windows.Application.Current!.Windows.OfType<Views.ChangelogWindow>(), _ => true);
                Assert.True(string.IsNullOrWhiteSpace(row.ChangelogText));
            }
            finally
            {
                RemovePersistedEnrichmentCacheEntry(EnrichmentCache.Key("github-release", id, "2.0"));
            }
        });
    }

    /// <summary>The counterpart: a fetch that DOES find text opens the real window, and the fetched body
    /// lands on the row via ApplyEnrichment so the window has something to show.</summary>
    [Fact]
    public void ShowChangelogAsync_with_a_fetch_that_finds_text_opens_the_changelog_window()
    {
        WpfHarness.OnUiThread(() =>
        {
            const string id = "test-owner/real-text-repo";
            var viewModel = Create(changelogFetcher: (_, _) => Task.FromResult<string?>("### Fixed\n- a bug"));
            var row = LinkOnlyChangelogRow(id);

            try
            {
                RunPumped(viewModel.ShowChangelogAsync(row));

                var opened = Assert.Single(System.Windows.Application.Current!.Windows.OfType<Views.ChangelogWindow>());
                opened.Close();
                Assert.Equal("### Fixed\n- a bug", row.ChangelogText);
            }
            finally
            {
                RemovePersistedEnrichmentCacheEntry(EnrichmentCache.Key("github-release", id, "2.0"));
            }
        });
    }

    /// <summary>
    /// The self-update banner's "what is new" button opens the release notes the check already resolved
    /// and never reaches the install path: installing stays a separate, deliberate click.
    /// </summary>
    [Fact]
    public void ShowSelfUpdateChangelogCommand_opens_the_release_notes_without_installing()
    {
        WpfHarness.OnUiThread(() =>
        {
            const string releasesJson = """
                {"tag_name":"v999.0.0","draft":false,"body":"### Fixed\n- a bug","assets":[
                  {"name":"Ultimate Updater.exe","browser_download_url":"https://github.com/x/y/releases/download/v999.0.0/a.exe","url":"https://api.github.com/repos/x/y/releases/assets/1","size":10},
                  {"name":"Setup-UltimateUpdater-999.0.0.exe","browser_download_url":"https://github.com/x/y/releases/download/v999.0.0/b.exe","url":"https://api.github.com/repos/x/y/releases/assets/2","size":10},
                  {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/x/y/releases/download/v999.0.0/SHA256SUMS.txt","url":"https://api.github.com/repos/x/y/releases/assets/3","size":10}
                ]}
                """;
            var logging = new LoggingService();
            var selfUpdate = new SelfUpdateService(new HttpClient(), logging, new FakeSelfUpdateChannel(releasesJson));
            var viewModel = Create(selfUpdate: selfUpdate);

            var found = RunPumped(viewModel.CheckForUpdatesInteractiveAsync());
            Assert.Equal(SelfUpdateCheckOutcome.Found, found);

            viewModel.ShowSelfUpdateChangelogCommand.Execute(null);

            var opened = Assert.Single(System.Windows.Application.Current!.Windows.OfType<Views.ChangelogWindow>());
            try
            {
                var changelogViewModel = Assert.IsType<ChangelogViewModel>(opened.DataContext);
                Assert.Equal("999.0.0", changelogViewModel.VersionText);
                Assert.Contains("Fixed", changelogViewModel.Text);
            }
            finally
            {
                opened.Close();
            }

            Assert.False(viewModel.SelfUpdateInProgress);
        });
    }

    /// <summary>
    /// A later check that comes back empty (a timed-out request, a channel briefly unreachable) must
    /// never wipe an update the FIRST check already found and offered: _pendingSelfUpdate used to be
    /// overwritten with null unconditionally, leaving SelfUpdateAvailable true with nothing left for
    /// Install or the changelog link to act on.
    /// </summary>
    [Fact]
    public void A_later_empty_check_never_wipes_an_update_already_offered()
    {
        WpfHarness.OnUiThread(() =>
        {
            const string releasesJson = """
                {"tag_name":"v999.0.0","draft":false,"body":"### Fixed\n- a bug","assets":[
                  {"name":"Ultimate Updater.exe","browser_download_url":"https://github.com/x/y/releases/download/v999.0.0/a.exe","url":"https://api.github.com/repos/x/y/releases/assets/1","size":10},
                  {"name":"Setup-UltimateUpdater-999.0.0.exe","browser_download_url":"https://github.com/x/y/releases/download/v999.0.0/b.exe","url":"https://api.github.com/repos/x/y/releases/assets/2","size":10},
                  {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/x/y/releases/download/v999.0.0/SHA256SUMS.txt","url":"https://api.github.com/repos/x/y/releases/assets/3","size":10}
                ]}
                """;
            var logging = new LoggingService();
            var channel = new SequencedSelfUpdateChannel(releasesJson, null);
            var selfUpdate = new SelfUpdateService(new HttpClient(), logging, channel);
            var viewModel = Create(selfUpdate: selfUpdate);

            Assert.Equal(SelfUpdateCheckOutcome.Found, RunPumped(viewModel.CheckForUpdatesInteractiveAsync()));
            Assert.True(viewModel.SelfUpdateAvailable);

            // The second check's own GetStringAsync answers null; RunSelfUpdateCheckAsync must leave
            // the first result in place rather than clearing it.
            Assert.Equal(SelfUpdateCheckOutcome.Found, RunPumped(viewModel.CheckForUpdatesInteractiveAsync()));
            Assert.True(viewModel.SelfUpdateAvailable);

            viewModel.ShowSelfUpdateChangelogCommand.Execute(null);
            var opened = Assert.Single(System.Windows.Application.Current!.Windows.OfType<Views.ChangelogWindow>());
            opened.Close();
        });
    }

    /// <summary>Answers a fixed sequence of releases payloads, one per call, holding the last one for any
    /// call past the end: lets a test simulate a check that finds something followed by one that does
    /// not (e.g. a transient network failure), against the real <see cref="SelfUpdateService"/>.</summary>
    private sealed class SequencedSelfUpdateChannel(params string?[] responses) : IUpdateChannel
    {
        private int _index;

        public bool IsAvailable => true;

        public Task<string?> GetStringAsync(HttpClient client, string url, CancellationToken cancellationToken) =>
            Task.FromResult(_index < responses.Length ? responses[_index++] : responses[^1]);

        public Task<bool> DownloadAsync(
            HttpClient client, string url, string targetPath, long maxBytes, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    /// <summary>
    /// This fix's npm half: unlike every other source, an npm row's button lights up with NEITHER text nor
    /// a link known (UpdateRowViewModel.CanFetchChangelogOnClick), so the click itself must resolve a
    /// GitHub repository into that repo's release page and hand it to the SAME release-link enrichment
    /// GitHub/GitLab rows already use, ending up with real fetched text rather than just a link.
    /// </summary>
    [Fact]
    public void ShowChangelogAsync_for_an_npm_row_with_a_github_repository_fetches_real_release_text()
    {
        WpfHarness.OnUiThread(() =>
        {
            const string id = "npm-github-repo-package";
            var viewModel = Create(
                npmChangelogLookup: (_, _) => Task.FromResult(
                    new Services.Sources.NpmChangelogLookup.NpmChangelogSource("git+https://github.com/some-owner/some-repo.git", "https://example.com")),
                changelogFetcher: (_, _) => Task.FromResult<string?>("### Fixed\n- a bug"));
            var row = new UpdateRowViewModel(Item(id, "Some npm tool", "npm"));

            try
            {
                Assert.True(row.HasChangelog);

                RunPumped(viewModel.ShowChangelogAsync(row));

                var opened = Assert.Single(System.Windows.Application.Current!.Windows.OfType<Views.ChangelogWindow>());
                opened.Close();
                Assert.Equal("### Fixed\n- a bug", row.ChangelogText);
            }
            finally
            {
                RemovePersistedEnrichmentCacheEntry(EnrichmentCache.Key("npm", id, "2.0"));
            }
        });
    }

    /// <summary>
    /// The npm counterpart to the GitHub/GitLab "fetch finds nothing" case: a lookup with neither a
    /// GitHub repository nor a usable homepage must not leave the click's loading spinner just stopping
    /// with nothing to show; it opens the changelog window with the explicit "no release notes" copy.
    /// </summary>
    [Fact]
    public void ShowChangelogAsync_for_an_npm_row_with_nothing_found_shows_the_no_release_notes_text()
    {
        WpfHarness.OnUiThread(() =>
        {
            const string id = "npm-nothing-found-package";
            var viewModel = Create(
                npmChangelogLookup: (_, _) => Task.FromResult(new Services.Sources.NpmChangelogLookup.NpmChangelogSource(null, null)));
            var row = new UpdateRowViewModel(Item(id, "Some npm tool", "npm"));

            try
            {
                RunPumped(viewModel.ShowChangelogAsync(row));

                var opened = Assert.Single(System.Windows.Application.Current!.Windows.OfType<Views.ChangelogWindow>());
                opened.Close();
                Assert.True(string.IsNullOrWhiteSpace(row.ChangelogUrl));
            }
            finally
            {
                RemovePersistedEnrichmentCacheEntry(EnrichmentCache.Key("npm", id, "2.0"));
            }
        });
    }

    /// <summary>
    /// Both the scanning rules and this fix's own acceptance require the npm changelog lookup to run
    /// ONLY on a deliberate click, never as part of a scan or the automatic post-scan enrichment pass
    /// that fires for every row. A full scan plus its enrichment pass over an npm row must complete
    /// without ever invoking the lookup delegate.
    /// </summary>
    [Fact]
    public void A_scan_never_invokes_the_npm_changelog_lookup()
    {
        WpfHarness.OnUiThread(() =>
        {
            var invoked = false;
            var npmSource = new OneShotSource("npm", Item("eslint", "eslint", "npm"));
            var viewModel = Create(
                sources: [npmSource],
                npmChangelogLookup: (_, _) =>
                {
                    invoked = true;
                    return Task.FromResult(new Services.Sources.NpmChangelogLookup.NpmChangelogSource(null, null));
                });

            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            var row = Assert.Single(viewModel.Updates);
            Assert.Equal("npm", row.SourceId);

            Assert.False(invoked);
        });
    }
}
