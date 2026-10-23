namespace UltimateUpdater.Services.Sources;

public sealed record SteamApp(
    string AppId, string Name, string? BuildId, string? TargetBuildId, bool UpdateRequired,
    DateTimeOffset? LastUpdated = null, string? InstallDir = null, string? InstallPath = null,
    long? DownloadSize = null);

/// <summary>
/// Minimal reader for Valve's KeyValues text format (appmanifest_*.acf, libraryfolders.vdf).
/// Only flat top-level keys are needed, so this stays a small tolerant scanner rather than a full
/// VDF parser. As with every external file, malformed input yields no result instead of throwing.
/// </summary>
public static class SteamAcfParser
{
    /// <summary>StateFlags bit 1 (value 2) = "update required" (documented in the Steam/Lutris ecosystem).</summary>
    private const int StateFlagUpdateRequired = 2;

    /// <summary>Largest Unix second <see cref="DateTimeOffset.FromUnixTimeSeconds"/> accepts.</summary>
    private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    public static SteamApp? ParseAppManifest(string content)
    {
        var values = ReadKeyValues(content);

        if (!values.TryGetValue("appid", out var appId) || appId.Length == 0)
            return null;

        values.TryGetValue("name", out var name);
        values.TryGetValue("buildid", out var buildId);
        values.TryGetValue("targetbuildid", out var targetBuildId);
        values.TryGetValue("installdir", out var installDir);

        var updateRequired = values.TryGetValue("stateflags", out var stateFlagsText) &&
                             int.TryParse(stateFlagsText, out var stateFlags) &&
                             (stateFlags & StateFlagUpdateRequired) != 0;

        // Steam writes "0" for TargetBuildID when it has no pending target, so that is "unknown",
        // not a version. Reporting it would put a fabricated "-> 0" in the table.
        var target = NormalizeBuildId(targetBuildId);

        // a genuinely different target build is the other signal that Steam knows an update is due
        if (!updateRequired && target is not null && target != buildId)
            updateRequired = true;

        // Unix seconds; the time the game last finished updating: the closest real date Steam records
        // for a title (it does not expose the pending update's own release date).
        // The range check is not cosmetic: FromUnixTimeSeconds THROWS outside it, and a single corrupt
        // manifest with an absurd number would have taken the whole Steam source down with it.
        DateTimeOffset? lastUpdated = null;
        if (values.TryGetValue("lastupdated", out var lastUpdatedText)
            && long.TryParse(lastUpdatedText, out var epoch)
            && epoch > 0 && epoch <= MaxUnixSeconds)
        {
            lastUpdated = DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        // Only meaningful while a download is actually pending/in progress. Steam leaves the field at 0
        // for an up-to-date game, and 0 would otherwise show as a fabricated "0 B" download size.
        long? downloadSize = null;
        if (values.TryGetValue("bytestodownload", out var bytesToDownloadText)
            && long.TryParse(bytesToDownloadText, out var bytesToDownload) && bytesToDownload > 0)
        {
            downloadSize = bytesToDownload;
        }

        return new SteamApp(
            appId,
            string.IsNullOrWhiteSpace(name) ? $"Steam app {appId}" : name!,
            NormalizeBuildId(buildId),
            target,
            updateRequired,
            lastUpdated,
            string.IsNullOrWhiteSpace(installDir) ? null : installDir,
            DownloadSize: downloadSize);
    }

    /// <summary>Library roots from libraryfolders.vdf ("path" entries).</summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string content)
    {
        var paths = new List<string>();

        foreach (var (key, value) in ReadAllPairs(content))
        {
            if (key == "path" && value.Length > 0)
                paths.Add(value);
        }

        return paths;
    }

    private static Dictionary<string, string> ReadKeyValues(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in ReadAllPairs(content))
            values.TryAdd(key, value);

        return values;
    }

    /// <summary>Reads the quoted token whose opening quote sits at <paramref name="open"/>. An escaped quote and an
    /// escaped backslash (two backslashes) are escapes, so a quote after a backslash does not end the value; any other backslash stays literal.</summary>
    private static bool TryReadQuoted(string line, int open, out string text, out int close)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = open + 1; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                text = builder.ToString();
                close = i;
                return true;
            }

            if (c == '\\' && i + 1 < line.Length && line[i + 1] is '"' or '\\')
            {
                builder.Append(line[++i]);
                continue;
            }

            builder.Append(c);
        }

        text = string.Empty;
        close = -1;
        return false;
    }

    /// <summary>Yields every `"key" "value"` pair, ignoring nesting and comments.</summary>
    private static IEnumerable<(string Key, string Value)> ReadAllPairs(string content)
    {
        if (string.IsNullOrEmpty(content))
            yield break;

        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '"')
                continue;

            if (!TryReadQuoted(line, 0, out var key, out var keyEnd) || key.Length == 0)
                continue;

            var secondOpen = line.IndexOf('"', keyEnd + 1);
            if (secondOpen < 0 || !TryReadQuoted(line, secondOpen, out var value, out _))
                continue;

            yield return (key.ToLowerInvariant(), value);
        }
    }


    /// <summary>Empty or "0" means Steam does not know a build here: report nothing rather than a fake version.</summary>
    private static string? NormalizeBuildId(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "0" ? null : value;
}
