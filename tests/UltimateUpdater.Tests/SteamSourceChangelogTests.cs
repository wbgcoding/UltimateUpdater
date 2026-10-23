using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// SteamNewsParser is pure (tested here against a trimmed, realistic sample of Steam's own
/// `ISteamNews/GetNewsForApp/v2` response shape, never live network, same convention as every other
/// parser in this project). SteamSource.EnrichAsync itself is only exercised where it needs no network
/// call at all (no client injected, or a non-numeric id), the same fail-closed paths WingetSource's own
/// EnrichAsync tests cover for its non-network branches.
/// </summary>
public class SteamSourceChangelogTests
{
    private const string RealShapedResponse = """
        {
          "appnews": {
            "appid": 440,
            "newsitems": [
              {
                "gid": "5023028931415624937",
                "title": "Team Fortress 2 Update Released",
                "url": "https://store.steampowered.com/news/app/440/view/5023028931415624937",
                "is_external_url": true,
                "author": "Valve",
                "contents": "[b]Team Fortress 2 Update Released[/b]\nAn update has been released for Team Fortress 2.\n[list]\n[*]Fixed a bug\n[*]Balanced a weapon\n[/list]\nSee the <a href=\"https://example.com\">forum thread</a> for details.",
                "feedlabel": "Team Fortress 2 Updates",
                "date": 1735689600,
                "feedname": "steam_community_announcements",
                "feed_type": 0,
                "appid": 440
              }
            ],
            "count": 1
          }
        }
        """;

    private const string EmptyNewsItemsResponse = """
        { "appnews": { "appid": 440, "newsitems": [], "count": 0 } }
        """;

    [Fact]
    public void ParseFirstNewsItem_reads_the_first_item_s_contents_and_url()
    {
        var news = SteamNewsParser.ParseFirstNewsItem(RealShapedResponse);

        Assert.NotNull(news);
        Assert.Equal("https://store.steampowered.com/news/app/440/view/5023028931415624937", news!.Value.Url);
        Assert.Contains("Team Fortress 2 Update Released", news.Value.Contents);
    }

    [Fact]
    public void ParseFirstNewsItem_returns_null_for_an_empty_newsitems_array() =>
        Assert.Null(SteamNewsParser.ParseFirstNewsItem(EmptyNewsItemsResponse));

    [Fact]
    public void ParseFirstNewsItem_refuses_a_non_http_url_but_keeps_the_contents()
    {
        const string json = """
            { "appnews": { "appid": 440, "newsitems": [
              { "contents": "text", "url": "file:///etc/passwd" }
            ], "count": 1 } }
            """;

        var news = SteamNewsParser.ParseFirstNewsItem(json);

        Assert.NotNull(news);
        Assert.Null(news!.Value.Url);
        Assert.Equal("text", news.Value.Contents);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"appnews\": {} }")]
    [InlineData("{ \"somethingElse\": 1 }")]
    public void ParseFirstNewsItem_never_throws_on_malformed_input(string? json) =>
        Assert.Null(SteamNewsParser.ParseFirstNewsItem(json));

    [Fact]
    public void StripMarkup_removes_bbcode_and_html_tags_but_keeps_the_words()
    {
        var stripped = SteamNewsParser.StripMarkup(
            "[b]Update[/b] fixed a bug. See the <a href=\"https://example.com\">forum thread</a>.");

        Assert.DoesNotContain("[b]", stripped);
        Assert.DoesNotContain("<a", stripped);
        Assert.Contains("Update", stripped);
        Assert.Contains("fixed a bug", stripped);
        Assert.Contains("forum thread", stripped);
    }

    [Fact]
    public void StripMarkup_is_safe_on_null_and_whitespace()
    {
        Assert.Equal(string.Empty, SteamNewsParser.StripMarkup(null));
        Assert.Equal(string.Empty, SteamNewsParser.StripMarkup("   "));
    }

    [Fact]
    public async Task EnrichAsync_reports_nothing_when_no_http_client_was_injected()
    {
        var source = new SteamSource(new LoggingService());
        var item = new UpdateItem { Id = "440", SourceId = "steam", Category = UpdateCategory.Games, Name = "Team Fortress 2" };

        Assert.Null(await source.EnrichAsync(item, CancellationToken.None));
    }

    [Fact]
    public async Task EnrichAsync_ignores_the_parent_Steam_row_whose_id_is_not_numeric()
    {
        var source = new SteamSource(new LoggingService(), httpClient: new HttpClient());
        var item = new UpdateItem { Id = "steam", SourceId = "steam", Category = UpdateCategory.Games, Name = "Steam" };

        Assert.Null(await source.EnrichAsync(item, CancellationToken.None));
    }
}
