using System.Globalization;
using System.Text.Json;

namespace UltimateUpdater.Services.Sources;

/// <summary>What the installed Flutter SDK reports about itself.</summary>
public readonly record struct FlutterInstalled(string Version, string Channel);

/// <summary>What the public release feed says about the newest build on one channel.</summary>
public readonly record struct FlutterRelease(string Version, DateTimeOffset? ReleaseDate, string? ArchivePath);

/// <summary>
/// Pure parsing for <see cref="FlutterSource"/>. Both shapes below were captured against a real machine
/// this session, not written from documentation alone.
/// </summary>
public static class FlutterParsers
{
    /// <summary>
    /// `flutter --version --machine`, captured verbatim:
    /// { "frameworkVersion": "3.44.1", "channel": "stable",
    /// "repositoryUrl": "https://github.com/flutter/flutter.git",
    /// "frameworkRevision": "924134a4…", "frameworkCommitDate": "2026-05-29 12:13:22 -0400",
    ///..., "dartSdkVersion": "3.12.1", "flutterVersion": "3.44.1", "flutterRoot": "C:\\flutter" }
    /// </summary>
    public static FlutterInstalled? ParseInstalled(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        // A first run prints tool bootstrap lines ("Building flutter tool...", download progress) before the
        // JSON, so the object starts at the first brace and not at the start of the output.
        var start = json.IndexOf('{');
        if (start < 0)
            return null;

        try
        {
            using var document = JsonDocument.Parse(json.AsMemory(start));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var version = JsonElementHelpers.GetString(root, "frameworkVersion");
            var channel = JsonElementHelpers.GetString(root, "channel");
            return version.Length > 0 && channel.Length > 0 ? new FlutterInstalled(version, channel) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// `https://storage.googleapis.com/flutter_infra_release/releases/releases_windows.json`, fetched live, looks like this:
    /// { "current_release": { "stable": "058e0af2…", "beta": "7c7929ad…", "dev": "13a2fb10…" },
    /// "releases": [ { "hash": "7c7929ad…", "channel": "beta", "version": "3.47.0-0.3.pre",
    /// "release_date": "2026-07-30T20:57:55.349209Z",
    /// "archive": "beta/windows/flutter_windows_3.47.0-0.3.pre-beta.zip",
    /// "sha256": "e4d645b8…" },... ] }
    /// `current_release[channel]` names the commit hash of that channel's newest build; the matching entry
    /// in `releases` (same hash AND same channel, a hash is only unique per channel, not globally) carries
    /// the actual version string. No size field on this feed; the archive
    /// path is carried along in the item's Meta (archivePath).
    /// </summary>
    public static FlutterRelease? ParseLatestRelease(string json, string channel)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(channel))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (!root.TryGetProperty("current_release", out var current) || current.ValueKind != JsonValueKind.Object)
                return null;

            var targetHash = JsonElementHelpers.GetString(current, channel);
            if (targetHash.Length == 0)
                return null;

            if (!root.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var release in releases.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object)
                    continue;

                if (!string.Equals(JsonElementHelpers.GetString(release, "hash"), targetHash, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.Equals(JsonElementHelpers.GetString(release, "channel"), channel, StringComparison.OrdinalIgnoreCase))
                    continue;

                var version = JsonElementHelpers.GetString(release, "version");
                if (version.Length == 0)
                    continue;

                var date = DateTimeOffset.TryParse(
                    JsonElementHelpers.GetString(release, "release_date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : (DateTimeOffset?)null;

                var archive = JsonElementHelpers.GetString(release, "archive");
                return new FlutterRelease(version, date, archive.Length > 0 ? archive : null);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
