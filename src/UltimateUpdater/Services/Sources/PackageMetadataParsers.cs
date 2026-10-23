using System.Globalization;
using System.Text.Json;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Pure text→data parsers for the public package-registry responses used by the background enrichment
/// pass (release date and download size for an already-found update). No I/O here, so every one is
/// directly unit-testable, and all of them tolerate garbage: a malformed or missing field yields an
/// empty result, never an exception (SECURITY.md, registry responses are hostile input).
/// </summary>
public static class PackageMetadataParsers
{
    /// <summary>
    /// PyPI `GET /pypi/{name}/{version}/json` → the "urls" array of distribution files. The publish
    /// date is the earliest upload among them; the size is the largest file (the wheel, typically),
    /// which is what actually comes down on an upgrade.
    /// </summary>
    public static UpdateEnrichment ParsePyPiRelease(string json)
    {
        if (!JsonElementHelpers.TryParseJson(json, out var document) || document is null)
            return default;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("urls", out var urls) ||
                urls.ValueKind != JsonValueKind.Array)
            {
                return default;
            }

            DateTimeOffset? earliest = null;

            foreach (var file in urls.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object)
                    continue;

                var uploaded = ReadDate(file, "upload_time_iso_8601") ?? ReadDate(file, "upload_time");
                if (uploaded is { } date && (earliest is null || date < earliest))
                    earliest = date;
            }

            return new UpdateEnrichment(
                earliest, PickPyPiSize(urls.EnumerateArray()), PyPiProjectUrl(document.RootElement),
                ChangelogUrl: PyPiChangelogUrl(document.RootElement),
                Publisher: PyPiPublisher(document.RootElement));
        }
    }

    /// <summary>
    /// The row's publisher for a pip package: PyPI's own "info.author", else "info.maintainer", else the
    /// owner of a github.com/gitlab.com link among its project URLs (the closest thing to a vendor name a
    /// registry package reliably has). Modern packaging metadata (a pyproject.toml "authors" table) often
    /// leaves the classic author/maintainer fields blank and folds the name into "author_email"/
    /// "maintainer_email" instead, as RFC 5322's "Name &lt;email&gt;" form, so both are tried; a bare
    /// email with no name gives nothing usable and falls through to the next source rather than showing
    /// an address as though it were a vendor. No caller-visible shortening happens here, that is a
    /// display concern (<see cref="PublisherShortener"/>), this returns the fact as PyPI states it.
    /// </summary>
    private static string? PyPiPublisher(JsonElement root)
    {
        if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
            return null;

        return NameOrEmailSender(info, "author", "author_email")
            ?? NameOrEmailSender(info, "maintainer", "maintainer_email")
            ?? ProjectUrlOwner(info);
    }

    /// <summary>A plain name field if it carries one, else the name portion of its "…_email" sibling
    /// when that one is in "Name &lt;email&gt;" form; a sibling holding only a bare address returns null.</summary>
    private static string? NameOrEmailSender(JsonElement info, string nameField, string emailField)
    {
        if (GetStringOrNull(info, nameField) is { Length: > 0 } name)
            return name;

        if (GetStringOrNull(info, emailField) is not { Length: > 0 } combined)
            return null;

        var angle = combined.IndexOf('<');
        if (angle < 0)
            return null; // a bare address with no name ahead of it

        var candidate = combined[..angle].Trim(' ', '"');
        return candidate.Length > 0 ? candidate : null;
    }

    /// <summary>The owner segment of a github.com/gitlab.com URL among the project's declared links
    /// (project_urls, falling back to home_page), tried in that order and stopping at the first match.</summary>
    private static string? ProjectUrlOwner(JsonElement info)
    {
        if (info.TryGetProperty("project_urls", out var urls) && urls.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in urls.EnumerateObject())
            {
                if (RepoOwner(GetStringOrNull(property.Value)) is { } owner)
                    return owner;
            }
        }

        return RepoOwner(GetStringOrNull(info, "home_page"));
    }

    /// <summary>The first path segment of a github.com or gitlab.com repository URL, whatever segment
    /// count follows it (gitlab.com allows nested groups; github.com never has more than owner/repo, but a
    /// stricter check here would just mean fewer packages get a publisher for no real benefit).
    /// Internal rather than private: <see cref="PackageManagers.PipDistInfoMetadata"/> reuses
    /// this same owner-extraction rule for the offline, dist-info-METADATA fallback, so a package looks
    /// the same whether its publisher came from PyPI's JSON or from disk.</summary>
    internal static string? RepoOwner(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return null;

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Host, "gitlab.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var owner = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(owner) ? null : owner;
    }

    /// <summary>
    /// npm `GET /{name}` (full packument) → the publish date for a specific version from the "time"
    /// map, and its unpacked size from "versions[version].dist.unpackedSize" when present.
    /// </summary>
    public static UpdateEnrichment ParseNpmPackument(string json, string version) =>
        ParseNpmPackumentAndTarball(json, version).Enrichment;

    /// <summary>
    /// npm `GET /{name}` (full packument) → the exact version's `dist.tarball` URL, from the SAME response
    /// <see cref="ParseNpmPackument"/> already reads. Used when that call's `unpackedSize` came back empty
    /// (npm does not always report it), the tarball is what actually comes down on an install, HEAD'd
    /// separately by the caller for its real size.
    /// </summary>
    public static string? ParseNpmTarballUrl(string json, string version) =>
        ParseNpmPackumentAndTarball(json, version).TarballUrl;

    /// <summary>
    /// Both facts above out of ONE parse: a packument for a popular package runs to tens of megabytes, too
    /// much to parse twice per row.
    /// </summary>
    public static (UpdateEnrichment Enrichment, string? TarballUrl) ParseNpmPackumentAndTarball(string json, string version)
    {
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(json))
            return default;

        var facts = NpmPackumentReader.Scan(System.Text.Encoding.UTF8.GetBytes(json), version);
        return facts is null ? default : FromNpmPackument(facts);
    }

    /// <summary>
    /// The enrichment and tarball URL out of the raw facts the streaming reader collected: the date is
    /// parsed leniently, every URL passes the http(s) gate, an unusable field simply stays unknown.
    /// </summary>
    internal static (UpdateEnrichment Enrichment, string? TarballUrl) FromNpmPackument(NpmPackumentFacts facts) =>
        (new UpdateEnrichment(ParseDate(facts.PublishedText), facts.UnpackedSize, NpmProjectUrl(facts.Homepage, facts.RepositoryUrl)),
            UrlSafety.SafeHttpUrl(facts.Tarball));

    /// <summary>
    /// npm `GET /{name}/latest` (the one version manifest the `latest` tag points at) → its `version`. A
    /// few kilobytes, where the full packument of npm itself is tens of megabytes. Used where nothing
    /// already knows a target version to compare against (Volta's package-manager tools, which, unlike
    /// npm/pnpm's own CLI, never report "outdated" themselves).
    /// </summary>
    public static string? ParseNpmManifestVersion(string json)
    {
        if (!JsonElementHelpers.TryParseJson(json, out var document) || document is null)
            return null;

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String &&
                version.GetString() is { Length: > 0 } value
                ? value
                : null;
        }
    }

    /// <summary>
    /// The best "learn more" link from a PyPI response: a changelog/release-notes URL if the project
    /// declares one, else its homepage. Only http(s) links are returned, the row hands this to the
    /// shell, so anything else (a file:/mailto: URL) is refused (SECURITY.md).
    /// </summary>
    private static string? PyPiProjectUrl(JsonElement root)
    {
        if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
            return null;

        if (info.TryGetProperty("project_urls", out var urls) && urls.ValueKind == JsonValueKind.Object)
        {
            // prefer the most useful label, then any homepage-ish one
            foreach (var wanted in new[] { "changelog", "changes", "release", "documentation", "homepage", "home" })
            {
                foreach (var property in urls.EnumerateObject())
                {
                    if (property.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase) &&
                        UrlSafety.SafeHttpUrl(GetStringOrNull(property.Value)) is { } url)
                    {
                        return url;
                    }
                }
            }
        }

        return UrlSafety.SafeHttpUrl(info.TryGetProperty("home_page", out var home) ? GetStringOrNull(home) : null)
            ?? UrlSafety.SafeHttpUrl(info.TryGetProperty("package_url", out var pkg) ? GetStringOrNull(pkg) : null);
    }

    /// <summary>
    /// A real changelog/release-notes link only, unlike <see cref="PyPiProjectUrl"/>, this does NOT fall
    /// back to a homepage: that fallback already drives the row's separate "info link" button
    /// (<c>UpdateRowViewModel.EnrichedProjectUrl</c>), and pointing the changelog button at the exact same
    /// URL would be a second button for the same destination. Null when the project declares no
    /// changelog-specific link, which is common and not an error.
    /// </summary>
    private static string? PyPiChangelogUrl(JsonElement root)
    {
        if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object ||
            !info.TryGetProperty("project_urls", out var urls) || urls.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var wanted in new[] { "changelog", "changes", "release notes", "whatsnew", "news" })
        {
            foreach (var property in urls.EnumerateObject())
            {
                if (property.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase) &&
                    UrlSafety.SafeHttpUrl(GetStringOrNull(property.Value)) is { } url)
                {
                    return url;
                }
            }
        }

        return null;
    }

    /// <summary>npm homepage, or the repository URL, normalised to an http(s) link.</summary>
    private static string? NpmProjectUrl(string? homepageText, string? repositoryUrlText)
    {
        if (UrlSafety.SafeHttpUrl(homepageText) is { } homepage)
            return homepage;

        // repository URLs are often "git+https://…​.git", trim the git prefix/suffix
        var raw = repositoryUrlText?.Replace("git+", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (raw is not null && raw.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^4];
        return UrlSafety.SafeHttpUrl(raw);
    }

    /// <summary>
    /// A NuGet registration leaf (`registration5-semver1/{id}/{version}.json`) → the package's publish
    /// date. NuGet marks an unlisted package with the sentinel year 1900, which is treated as "unknown".
    /// </summary>
    public static UpdateEnrichment ParseNuGetPublished(string json)
    {
        if (!JsonElementHelpers.TryParseJson(json, out var document) || document is null)
            return default;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return default;

            // the leaf inlines a catalogEntry object carrying "published"; fall back to a top-level one
            var hasEntry = document.RootElement.TryGetProperty("catalogEntry", out var entry)
                && entry.ValueKind == JsonValueKind.Object;
            var published = hasEntry ? ReadDate(entry, "published") : ReadDate(document.RootElement, "published");
            var releaseNotes = hasEntry ? GetStringOrNull(entry, "releaseNotes") : null;

            return new UpdateEnrichment(
                published is { Year: > 1901 } ? published : null, null,
                ChangelogBody: string.IsNullOrWhiteSpace(releaseNotes) ? null : releaseNotes);
        }
    }

    /// <summary>
    /// A Chocolatey community-feed OData entry (`.../api/v2/Packages(Id='x',Version='y')`) → publish date
    /// and package size, read namespace-agnostically from the Atom XML so a feed namespace change does
    /// not break it. Any malformed field simply stays unknown.
    /// </summary>
    public static UpdateEnrichment ParseChocolateyODataMeta(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return default;

        try
        {
            var root = System.Xml.Linq.XDocument.Parse(xml).Root;
            if (root is null)
                return default;

            DateTimeOffset? published = null;
            long? size = null;
            string? releaseNotes = null;

            foreach (var element in root.Descendants())
            {
                if (element.Name.LocalName.Equals("Published", StringComparison.OrdinalIgnoreCase)
                    && ParseDate(element.Value) is { Year: > 1901 } date)
                {
                    published = date;
                }
                else if (element.Name.LocalName.Equals("PackageSize", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(element.Value, out var bytes) && bytes > 0)
                {
                    size = bytes;
                }
                else if (element.Name.LocalName.Equals("ReleaseNotes", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(element.Value))
                {
                    releaseNotes = element.Value;
                }
            }

            return new UpdateEnrichment(published, size, ChangelogBody: releaseNotes);
        }
        catch (System.Xml.XmlException)
        {
            return default;
        }
    }

    /// <summary>
    /// Google's Android SDK repository manifest (`dl.google.com/android/repository/repository2-3.xml`)
    /// → the Windows archive size for one package, matched by its exact `path` attribute (the same
    /// string `sdkmanager --list` uses as the package id, e.g. "build-tools;35.0.1", verified live).
    /// No date: the manifest carries a revision number, not a publish date, matching what
    /// `sdkmanager` itself already shows. A package with no Windows archive (rare) yields nothing rather
    /// than a foreign platform's size.
    /// </summary>
    public static long? ParseAndroidSdkPackageSize(string xml, string packagePath)
    {
        if (string.IsNullOrWhiteSpace(xml) || string.IsNullOrWhiteSpace(packagePath))
            return null;

        try
        {
            var root = System.Xml.Linq.XDocument.Parse(xml).Root;
            if (root is null)
                return null;

            var package = root.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "remotePackage" &&
                    (string?)e.Attribute("path") == packagePath);
            if (package is null)
                return null;

            var archives = package.Elements().FirstOrDefault(e => e.Name.LocalName == "archives");
            if (archives is null)
                return null;

            foreach (var archive in archives.Elements().Where(e => e.Name.LocalName == "archive"))
            {
                var hostOs = archive.Elements().FirstOrDefault(e => e.Name.LocalName == "host-os")?.Value;
                if (hostOs is not null && !string.Equals(hostOs, "windows", StringComparison.OrdinalIgnoreCase))
                    continue;

                var complete = archive.Elements().FirstOrDefault(e => e.Name.LocalName == "complete");
                var sizeText = complete?.Elements().FirstOrDefault(e => e.Name.LocalName == "size")?.Value;
                if (long.TryParse(sizeText, out var bytes) && bytes > 0)
                    return bytes;
            }

            return null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The download size that belongs to what this machine would actually install: the largest wheel built
    /// for this machine's Windows platform tag, else the largest platform independent ("any") wheel, else
    /// the source distribution, else nothing. A release lists one file per platform and Python version, so
    /// "the largest file" or "the last wheel" can be a Linux or macOS build. The platform tag is the last
    /// dash separated part of the wheel file name (compressed tags are dot separated).
    /// </summary>
    private static long? PickPyPiSize(IEnumerable<JsonElement> files)
    {
        var wantedTag = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64
            ? "win_arm64"
            : "win_amd64";

        long matching = 0;
        long universal = 0;
        long sdist = 0;

        foreach (var file in files)
        {
            if (file.ValueKind != JsonValueKind.Object ||
                !file.TryGetProperty("size", out var sizeProperty) ||
                sizeProperty.ValueKind != JsonValueKind.Number ||
                !sizeProperty.TryGetInt64(out var bytes) || bytes <= 0)
            {
                continue;
            }

            var packageType = GetStringOrNull(file, "packagetype");
            if (string.Equals(packageType, "sdist", StringComparison.OrdinalIgnoreCase))
            {
                sdist = Math.Max(sdist, bytes);
                continue;
            }

            if (!string.Equals(packageType, "bdist_wheel", StringComparison.OrdinalIgnoreCase) ||
                GetStringOrNull(file, "filename") is not { } filename ||
                !filename.EndsWith(".whl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var stem = filename[..^".whl".Length];
            var tags = stem[(stem.LastIndexOf('-') + 1)..].Split('.');

            if (tags.Contains(wantedTag, StringComparer.OrdinalIgnoreCase))
                matching = Math.Max(matching, bytes);
            else if (tags.Contains("any", StringComparer.OrdinalIgnoreCase))
                universal = Math.Max(universal, bytes);
        }

        return matching > 0 ? matching : universal > 0 ? universal : sdist > 0 ? sdist : null;
    }

    /// <summary>
    /// PyPI `GET /pypi/{name}/json` (no version in the path), used for pipx, whose own listing never
    /// reports a target version at all. <c>info.version</c> is the newest release; the publish date is
    /// the LATEST upload among that version's files (unlike <see cref="ParsePyPiRelease"/>, which reads
    /// the earliest for an already-known version, here there is only one version to look at, and a
    /// wheel/sdist pair can differ by seconds, so the later one is "when this release actually finished
    /// shipping"). Size follows <see cref="PickPyPiSize"/>: the wheel this machine would install.
    /// </summary>
    public static UpdateEnrichment ParsePyPiLatest(string json)
    {
        if (!JsonElementHelpers.TryParseJson(json, out var document) || document is null)
            return default;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("info", out var info) ||
                info.ValueKind != JsonValueKind.Object ||
                !info.TryGetProperty("version", out var versionProperty) ||
                versionProperty.ValueKind != JsonValueKind.String)
            {
                return default;
            }

            var version = versionProperty.GetString();
            if (string.IsNullOrWhiteSpace(version))
                return default;

            DateTimeOffset? latestUpload = null;
            long? size = null;

            if (document.RootElement.TryGetProperty("releases", out var releases) &&
                releases.ValueKind == JsonValueKind.Object &&
                releases.TryGetProperty(version, out var files) &&
                files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    if (file.ValueKind != JsonValueKind.Object)
                        continue;

                    var uploaded = ReadDate(file, "upload_time_iso_8601") ?? ReadDate(file, "upload_time");
                    if (uploaded is { } date && (latestUpload is null || date > latestUpload))
                        latestUpload = date;
                }

                size = PickPyPiSize(files.EnumerateArray());
            }

            return new UpdateEnrichment(
                latestUpload, size, PyPiProjectUrl(document.RootElement), version,
                ChangelogUrl: PyPiChangelogUrl(document.RootElement));
        }
    }

    /// <summary>
    /// A VS Code Marketplace extension lookup (`.../publishers/{publisher}/extensions/{extension}`) →
    /// the newest published version, its last-update time, and (when the response happens to carry one,
    /// the Marketplace does not reliably expose a package size per asset) a download size. The project
    /// link is read from the extension's own "properties" bag (Marketplace convention for repository /
    /// getting-started links), where one links out.
    /// </summary>
    public static UpdateEnrichment ParseVsMarketplaceExtension(string json)
    {
        if (!JsonElementHelpers.TryParseJson(json, out var document) || document is null)
            return default;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() == 0)
            {
                return default;
            }

            // The list carries pre-releases too (newest first), but `code --install-extension --force`
            // installs the newest STABLE version, so a pre-release here would be offered on every scan and
            // never go away. Only when every listed version is a pre-release is the first one used.
            var latest = versions.EnumerateArray()
                .FirstOrDefault(v => v.ValueKind == JsonValueKind.Object && !IsVsMarketplacePreRelease(v));
            if (latest.ValueKind != JsonValueKind.Object)
                latest = versions[0];
            if (latest.ValueKind != JsonValueKind.Object)
                return default;

            var version = latest.TryGetProperty("version", out var versionProperty) &&
                          versionProperty.ValueKind == JsonValueKind.String
                ? versionProperty.GetString()
                : null;

            var published = ReadDate(latest, "lastUpdated") ?? ReadDate(document.RootElement, "lastUpdated");

            long? size = null;
            if (latest.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    if (file.ValueKind == JsonValueKind.Object &&
                        file.TryGetProperty("size", out var sizeProperty) &&
                        sizeProperty.ValueKind == JsonValueKind.Number &&
                        sizeProperty.TryGetInt64(out var bytes) && bytes > (size ?? 0))
                    {
                        size = bytes;
                    }
                }
            }

            var projectUrl = VsMarketplaceProjectUrl(latest) ?? VsMarketplaceProjectUrl(document.RootElement);

            return string.IsNullOrWhiteSpace(version) && published is null && size is null && projectUrl is null
                ? default
                : new UpdateEnrichment(published, size, projectUrl, string.IsNullOrWhiteSpace(version) ? null : version);
        }
    }

    /// <summary>Repository/getting-started link from a Marketplace "properties" bag ([{key, value},...]).</summary>
    private static bool IsVsMarketplacePreRelease(JsonElement version) =>
        version.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Array
        && properties.EnumerateArray().Any(property =>
            property.ValueKind == JsonValueKind.Object
            && property.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
            && key.GetString() == "Microsoft.VisualStudio.Code.PreRelease"
            && property.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase));

    private static string? VsMarketplaceProjectUrl(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var wanted in new[] { "source", "repository", "getstarted", "learn" })
        {
            foreach (var property in properties.EnumerateArray())
            {
                if (property.ValueKind != JsonValueKind.Object ||
                    !property.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String ||
                    !property.TryGetProperty("value", out var value))
                {
                    continue;
                }

                if (key.GetString()?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true &&
                    UrlSafety.SafeHttpUrl(GetStringOrNull(value)) is { } url)
                {
                    return url;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// GitHub `GET /repos/{owner}/{repo}/releases/latest` → the tag (leading "v" trimmed, GitHub's own
    /// convention), when it was published, the largest release asset's size (there is no single
    /// "package size", the source only reports one asset was bigger, which is the best available
    /// proxy), and the release's own page as the project link.
    /// </summary>
    public static UpdateEnrichment ParseGitHubLatestRelease(string json)
    {
        var release = GitHubReleaseParsers.ParseLatestRelease(json);

        // An unparseable body yields a default release, whose asset list is null.
        long? size = (release.Assets ?? []).Where(asset => asset.Size > 0).Select(asset => asset.Size).Max();
        var projectUrl = UrlSafety.SafeHttpUrl(release.HtmlUrl);
        var version = string.IsNullOrWhiteSpace(release.Version) ? null : release.Version;

        return version is null && release.PublishDate is null && size is null && projectUrl is null
            ? default
            : new UpdateEnrichment(release.PublishDate, size, projectUrl, version);
    }

    /// <summary>
    /// RubyGems `GET /api/v1/versions/{gem}.json` → the array of published versions; matched by
    /// <c>number</c>. The API reports no size for a version, verified live, so the
    /// caller HEADs rubygems.org's own well-known download URL separately for that.
    /// </summary>
    public static DateTimeOffset? ParseRubyGemsVersionDate(string json, string version)
    {
        if (string.IsNullOrWhiteSpace(version) || !JsonElementHelpers.TryParseJson(json, out var document) || document is null)
            return null;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                var number = GetStringOrNull(entry, "number");
                if (string.Equals(number, version, StringComparison.OrdinalIgnoreCase))
                    return ReadDate(entry, "created_at");
            }

            return null;
        }
    }

    /// <summary>
    /// Packagist `GET /p2/{vendor}/{package}.json` → the package's version list; matched by version
    /// with a leading "v" trimmed on both sides (a common git-tag convention Composer's own version
    /// strings do not always carry). Publish time and the distributable's own URL, HEAD'd separately
    /// for size, since this endpoint reports none, come from the same matched entry.
    /// </summary>
    public static (DateTimeOffset? PublishDate, string? DownloadUrl) ParsePackagistVersion(
        string json, string packageName, string version)
    {
        if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(version) ||
            !JsonElementHelpers.TryParseJson(json, out var document) || document is null)
        {
            return (null, null);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("packages", out var packages) ||
                packages.ValueKind != JsonValueKind.Object ||
                !packages.TryGetProperty(packageName, out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
            {
                return (null, null);
            }

            var trimmedTarget = version.TrimStart('v', 'V');

            foreach (var entry in versions.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                var entryVersion = GetStringOrNull(entry, "version")?.TrimStart('v', 'V');
                var normalized = GetStringOrNull(entry, "version_normalized");

                // The normalized form is compared against the target padded the same way, NOT by prefix:
                // a bare StartsWith let the target "1.2" match "1.20.0.0", and the first match wins, so
                // another release's date and download URL could be attached to the row.
                var matches = string.Equals(entryVersion, trimmedTarget, StringComparison.OrdinalIgnoreCase)
                    || MatchesNormalizedComposerVersion(normalized, trimmedTarget);

                if (!matches)
                    continue;

                var publishDate = ReadDate(entry, "time");
                var downloadUrl = entry.TryGetProperty("dist", out var dist) && dist.ValueKind == JsonValueKind.Object
                    ? UrlSafety.SafeHttpUrl(GetStringOrNull(dist, "url"))
                    : null;

                return (publishDate, downloadUrl);
            }

            return (null, null);
        }
    }

    /// <summary>
    /// Whether Packagist's <c>version_normalized</c> is this exact version. Composer normalizes to four
    /// numeric components, so "1.2" is stored as "1.2.0.0", the target is padded the same way and then
    /// compared whole, with an optional stability suffix ("-beta") allowed after it.
    /// </summary>
    private static bool MatchesNormalizedComposerVersion(string? normalized, string target)
    {
        if (string.IsNullOrEmpty(normalized) || string.IsNullOrEmpty(target))
            return false;

        var suffixStart = target.IndexOf('-');
        var core = suffixStart < 0 ? target : target[..suffixStart];
        var suffix = suffixStart < 0 ? string.Empty : target[suffixStart..];

        var parts = core.Split('.');
        if (parts.Length > 4)
            return false;

        var padded = string.Join('.', parts.Concat(Enumerable.Repeat("0", 4 - parts.Length))) + suffix;

        return string.Equals(normalized, padded, StringComparison.OrdinalIgnoreCase)
            || (suffix.Length == 0 && normalized.StartsWith(padded + "-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scoop's manifest for an installed app (`scoop cat {id}`), plain JSON, never locale-dependent
    /// text. Picks the download URL for the running architecture, falling back through the others; a
    /// simple manifest with no per-architecture block keeps one top-level "url" instead, read the same
    /// way. A manifest whose "url" is a multi-file array (rare) is left alone rather than guessed at.
    /// </summary>
    public static string? ParseScoopManifestDownloadUrl(string json, bool preferArm64)
    {
        if (!JsonElementHelpers.TryParseJson(json, out var document) || document is null)
            return null;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (document.RootElement.TryGetProperty("architecture", out var architecture) &&
                architecture.ValueKind == JsonValueKind.Object)
            {
                var order = preferArm64
                    ? new[] { "arm64", "64bit", "32bit" }
                    : new[] { "64bit", "32bit", "arm64" };

                foreach (var key in order)
                {
                    if (architecture.TryGetProperty(key, out var arch) && arch.ValueKind == JsonValueKind.Object &&
                        UrlSafety.SafeHttpUrl(GetStringOrNull(arch, "url")) is { } archUrl)
                    {
                        return archUrl;
                    }
                }
            }

            return UrlSafety.SafeHttpUrl(GetStringOrNull(document.RootElement, "url"));
        }
    }

    /// <summary>
    /// Scoop's manifest for an installed app (`scoop cat {id}`), the "homepage" field, when the manifest
    /// declares one (most do). Scoop itself has no per-version changelog concept, so this is the best
    /// "what is this / what changed" link available for a Scoop row.
    /// </summary>
    public static string? ParseScoopManifestHomepage(string json)
    {
        if (!JsonElementHelpers.TryParseJson(json, out var document) || document is null)
            return null;

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? UrlSafety.SafeHttpUrl(GetStringOrNull(document.RootElement, "homepage"))
                : null;
        }
    }

    /// <summary>
    /// rustup's channel manifest (`https://static.rust-lang.org/dist/channel-rust-{channel}.toml`), only
    /// the top-level <c>date = "YYYY-MM-DD"</c> line, matched before any <c>[section]</c> header so a
    /// same-named field nested under a package/target table is never mistaken for it. No TOML parser for
    /// this one scalar. Per-target archive URLs need the installed host triple to pick the right one and
    /// are not attempted: a wrong guess would be worse than no size at all.
    /// </summary>
    public static DateTimeOffset? ParseRustupChannelDate(string toml)
    {
        if (string.IsNullOrWhiteSpace(toml))
            return null;

        foreach (var rawLine in toml.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            // TOML sections start the per-package/per-target tables; the manifest-wide date only ever
            // appears before the first one.
            if (line.StartsWith('['))
                break;

            if (!line.StartsWith("date", StringComparison.Ordinal))
                continue;

            var equals = line.IndexOf('=');
            if (equals < 0 || line[..equals].Trim() != "date")
                continue;

            var value = line[(equals + 1)..].Trim().Trim('"');
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                return date;
        }

        return null;
    }

    private static string? GetStringOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? GetStringOrNull(value) : null;

    // A registry response is hostile input: a field documented as a string can arrive as null, a number,
    // or an object (real-world PyPI/npm inconsistencies). JsonElement.GetString() throws on anything but
    // String/Null, so every call site routes through here instead of calling it directly.
    private static string? GetStringOrNull(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? ReadDate(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? ParseDate(value.GetString())
            : null;

    private static DateTimeOffset? ParseDate(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
}
