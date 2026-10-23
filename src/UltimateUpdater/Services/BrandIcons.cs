using System.Collections.Concurrent;
using System.Windows.Media;

namespace UltimateUpdater.Services;

/// <summary>
/// Bundled brand marks, used only when the row has no icon of its own. Package-manager rows are the
/// real reason this exists: an npm or pip package has no installed application on disk to take an
/// icon from at all, so without this they can never show anything but a letter. An icon extracted
/// from the installed program always wins over these.
/// Matching is by an explicit table rather than by similarity on purpose: the wrong company's mark on
/// a row is worse than no mark at all. Licensing: NOTICE-brand-icons.md.
/// </summary>
internal static class BrandIcons
{
    private static readonly ConcurrentDictionary<string, Geometry?> Cache = new(StringComparer.Ordinal);

    /// <summary>Names that do not already equal their slug. Keys are normalised, as lookups are.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["battlenet"] = "battledotnet",
        ["blizzardbattlenet"] = "battledotnet",
        ["chrome"] = "googlechrome",
        ["dotnettool"] = "dotnet",
        ["eaapp"] = "ea",
        ["epic"] = "epicgames",
        ["epicgameslauncher"] = "epicgames",
        ["gog"] = "gogdotcom",
        ["goggalaxy"] = "gogdotcom",
        ["golang"] = "go",
        ["intellij"] = "intellijidea",
        ["java"] = "openjdk",
        ["jdk"] = "openjdk",
        ["node"] = "nodedotjs",
        ["nodejs"] = "nodedotjs",
        ["notepadpp"] = "notepadplusplus",
        ["obs"] = "obsstudio",
        ["origin"] = "ea",
        ["postgres"] = "postgresql",
        ["python3"] = "python",
        ["riot"] = "riotgames",
        ["riotclient"] = "riotgames",
        ["rockstar"] = "rockstargames",
        ["torbrowserbundle"] = "torbrowser",
        ["ubisoftconnect"] = "ubisoft",
        ["vlc"] = "vlcmediaplayer",
        ["vue"] = "vuedotjs",
    };

    /// <summary>
    /// Only for sources where the row IS the client: a launcher row is the launcher. Package managers
    /// are deliberately absent: every Chocolatey package would otherwise wear Chocolatey's logo,
    /// which says nothing the source pill on the row does not already say, and looks like a claim
    /// about the package itself.
    /// </summary>
    private static readonly Dictionary<string, string> BySource = new(StringComparer.Ordinal)
    {
        ["battlenet"] = "battledotnet",
        ["ea"] = "ea",
        ["epic"] = "epicgames",
        ["gog"] = "gogdotcom",
        ["riot"] = "riotgames",
        ["rockstar"] = "rockstargames",
        ["steam"] = "steam",
        ["ubisoft"] = "ubisoft",
    };

    /// <summary>Words that identify the maker rather than the product.</summary>
    private static readonly string[] VendorWords =
        ["mozilla", "google", "microsoft", "oracle", "apache", "jetbrains", "valve", "gnu", "the"];

    public static Geometry? Resolve(string? name, string? sourceId, string? itemId)
    {
        foreach (var candidate in Candidates(name, sourceId, itemId))
        {
            if (Lookup(candidate) is { } geometry)
                return geometry;
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string? name, string? sourceId, string? itemId)
    {
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            // winget ids are Publisher.Product, and the product half is the recognisable one
            yield return TextNormalization.AlphanumericLower(itemId[(itemId.LastIndexOf('.') + 1)..]);
            yield return TextNormalization.AlphanumericLower(itemId);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            yield return TextNormalization.AlphanumericLower(name);

            var words = Words(name);
            yield return TextNormalization.AlphanumericLower(string.Concat(WithoutVendor(words)));
            yield return TextNormalization.AlphanumericLower(string.Concat(WithoutVersion(WithoutVendor(words))));
        }

        if (!string.IsNullOrWhiteSpace(sourceId) && BySource.TryGetValue(sourceId, out var bySource))
            yield return bySource;
    }

    private static Geometry? Lookup(string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        return Cache.GetOrAdd(key, static candidate =>
        {
            var slug = Aliases.GetValueOrDefault(candidate, candidate);
            if (!BrandIconData.Paths.TryGetValue(slug, out var data))
                return null;

            var geometry = Geometry.Parse(data);
            geometry.Freeze();   // crosses to the UI thread and is shared by every row that matches
            return geometry;
        });
    }

    private static string[] Words(string value) =>
        value.Split([' ', '\t', '-', '_'], StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<string> WithoutVendor(string[] words) =>
        words.Length > 1 && VendorWords.Contains(words[0].ToLowerInvariant())
            ? words.Skip(1)
            : words;

    /// <summary>"Firefox 129.0.1" is still Firefox; a trailing version must not defeat the match.</summary>
    private static IEnumerable<string> WithoutVersion(IEnumerable<string> words) =>
        words.Where(word => !IsVersion(word));

    private static bool IsVersion(string word) =>
        char.IsDigit(word[0]) || (word.Length > 1 && word[0] is 'v' or 'V' && char.IsDigit(word[1]));

}
