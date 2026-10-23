using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>A second changelog click while the first window is still open shows the NEW content, not
    /// the old window raised to the front.</summary>
    [Fact]
    public void A_second_changelog_replaces_the_one_that_is_open()
    {
        WpfHarness.OnUiThread(() =>
        {
            const string firstId = "test-owner/first-changelog-repo";
            const string secondId = "test-owner/second-changelog-repo";
            var viewModel = Create(changelogFetcher: (url, _) => Task.FromResult<string?>(
                url.Contains("second-changelog-repo", StringComparison.Ordinal) ? "second text" : "first text"));

            try
            {
                RunPumped(viewModel.ShowChangelogAsync(LinkOnlyChangelogRow(firstId)));
                RunPumped(viewModel.ShowChangelogAsync(LinkOnlyChangelogRow(secondId)));

                var opened = Assert.Single(System.Windows.Application.Current!.Windows.OfType<Views.ChangelogWindow>());
                try
                {
                    Assert.Equal("second text", Assert.IsType<ChangelogViewModel>(opened.DataContext).Text);
                }
                finally
                {
                    opened.Close();
                }
            }
            finally
            {
                RemovePersistedEnrichmentCacheEntry(EnrichmentCache.Key("github-release", firstId, "2.0"));
                RemovePersistedEnrichmentCacheEntry(EnrichmentCache.Key("github-release", secondId, "2.0"));
            }
        });
    }
}
