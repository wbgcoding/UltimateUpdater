using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Google publishes no per-version release-notes URL, only one fixed page, FlutterSource.EnrichAsync
/// always attaches it, even when there is no archive path to HEAD for a size (no network call in that
/// case, so this runs safely without a live connection).
/// </summary>
public class FlutterSourceChangelogTests
{
    [Fact]
    public async Task EnrichAsync_attaches_the_fixed_release_notes_page_even_with_no_archive_path()
    {
        var source = new FlutterSource(new LoggingService(), new HttpClient());
        var item = new UpdateItem
        {
            Id = "flutter", SourceId = "flutter", Category = UpdateCategory.DeveloperTools, Name = "Flutter SDK",
        };

        var enrichment = await source.EnrichAsync(item, CancellationToken.None);

        Assert.NotNull(enrichment);
        Assert.Equal("https://docs.flutter.dev/release/release-notes", enrichment!.Value.ChangelogUrl);
        Assert.Null(enrichment.Value.DownloadSize);
    }
}
