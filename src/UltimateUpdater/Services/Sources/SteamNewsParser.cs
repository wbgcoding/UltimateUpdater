using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateUpdater.Services.Sources;

/// <summary>The most recent post Steam's news API reports for one app.</summary>
public readonly record struct SteamNewsItem(string? Contents, string? Url);

/// <summary>
/// Pure parsing for `ISteamNews/GetNewsForApp/v2` (no API key), consumed by
/// <see cref="SteamSource.EnrichAsync"/>. Malformed or empty input yields null rather than throwing,
/// the same convention as every other parser in this project.
/// </summary>
public static class SteamNewsParser
{
    private static readonly Regex HtmlTag = new(@"<[^>]*>", RegexOptions.Compiled);

    // Steam news posts are written in BBCode ([b]…[/b], [url=…]…[/url], [img]…[/img]); this only strips
    // the bracket markup itself, same "lose the punctuation, keep the words" approach as ChangelogFormatter.
    private static readonly Regex BbCodeTag = new(@"\[/?[a-zA-Z0-9_*]{1,16}(?:=[^\]\r\n]{0,2048})?\]", RegexOptions.Compiled);

    public static SteamNewsItem? ParseFirstNewsItem(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("appnews", out var appNews))
                return null;

            if (appNews.ValueKind != JsonValueKind.Object || !appNews.TryGetProperty("newsitems", out var newsItems)
                || newsItems.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var first = newsItems.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object)
                return null;

            var contents = TextOrNull(first, "contents");
            var url = UrlSafety.SafeHttpUrl(TextOrNull(first, "url"));
            return contents is null && url is null ? null : new SteamNewsItem(contents, url);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Strips HTML tags and BBCode markup down to plain text. Not a renderer: like
    /// <see cref="ChangelogFormatter"/>, formatting is simply dropped, nothing is reflowed.
    /// </summary>
    public static string StripMarkup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var result = HtmlTag.Replace(text, string.Empty);
        result = BbCodeTag.Replace(result, string.Empty);
        return result.Trim();
    }

    private static string? TextOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
