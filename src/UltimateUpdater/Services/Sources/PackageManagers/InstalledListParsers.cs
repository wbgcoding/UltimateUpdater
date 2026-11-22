using System.Text.Json;
using System.Text.RegularExpressions;
using UltimateUpdater.Services.Support;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>
/// Pure text to data parsers for the "everything installed" listings of the package managers, the counterpart of
/// <see cref="PackageManagerParsers"/> (which reads what is outdated). Tool output is untrusted: an unreadable line
/// or document is skipped, never an exception, and every id that survives is checked again before it reaches a
/// command line (<see cref="ProcessRunner.IsSafePositionalArgument"/>).
/// </summary>
public static partial class InstalledListParsers
{
    private static IEnumerable<string> Lines(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").Split('\n');

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static InstalledPackage Plain(string id, string? version, bool? requiredByOthers = null) =>
        new(id, id, NullIfEmpty(version), requiredByOthers);

    /// <summary>The installed half of a listing a source already parses for its scan (name and version only).</summary>
    public static IReadOnlyList<InstalledPackage> FromOutdated(IEnumerable<OutdatedPackage> packages) =>
        packages.Select(p => new InstalledPackage(p.Id, p.Name, NullIfEmpty(p.Installed))).ToList();

    /// <summary>`choco list --limit-output` is "id|version" per line; a banner line without the bar is skipped.</summary>
    public static IReadOnlyList<InstalledPackage> ParseChocolatey(string? stdout)
    {
        var results = new List<InstalledPackage>();
        foreach (var raw in Lines(stdout))
        {
            var parts = raw.Trim().Split('|');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0 || parts[0].Any(char.IsWhiteSpace))
                continue;

            results.Add(Plain(parts[0], parts[1]));
        }

        return results;
    }

    /// <summary>
    /// `scoop export`: a JSON document {"apps":[{"Name","Version","Source",...}]} (scoop 0.3 and later), or the older
    /// "name (version) [bucket]" lines.
    /// </summary>
    public static IReadOnlyList<InstalledPackage> ParseScoopExport(string? stdout)
    {
        var results = new List<InstalledPackage>();
        if (JsonElementHelpers.TryParseJson(stdout ?? string.Empty, out var document) && document is not null)
        {
            using (document)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("apps", out var apps)
                    && apps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var app in apps.EnumerateArray())
                    {
                        if (app.ValueKind != JsonValueKind.Object)
                            continue;

                        var name = JsonElementHelpers.GetString(app, "Name");
                        if (name.Length > 0 && !name.Any(char.IsWhiteSpace))
                            results.Add(Plain(name, JsonElementHelpers.GetString(app, "Version")));
                    }
                }
            }

            return results;
        }

        foreach (var raw in Lines(stdout))
        {
            var match = ScoopTextLine().Match(raw.Trim());
            if (match.Success)
                results.Add(Plain(match.Groups["name"].Value, match.Groups["version"].Value));
        }

        return results;
    }

    [GeneratedRegex(@"^(?<name>[^\s()\[\]]+)\s+\((?<version>[^)\s]+)\)(?:\s+\[[^\]]*\])?(?:\s+\*.*)?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ScoopTextLine();

    /// <summary>`pip list --format=json`: [{"name","version"}], every package of one interpreter.</summary>
    public static IReadOnlyList<InstalledPackage> ParsePipList(string? stdout)
    {
        var results = new List<InstalledPackage>();
        if (!JsonElementHelpers.TryParseJson(stdout ?? string.Empty, out var document) || document is null)
            return results;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var name = JsonElementHelpers.GetString(element, "name");
                if (name.Length > 0)
                    results.Add(Plain(name, JsonElementHelpers.GetString(element, "version")));
            }
        }

        return results;
    }

    /// <summary>
    /// Marks the packages of a `pip list` that another installed package needs. <paramref name="notRequired"/> is
    /// the names `pip list --not-required` printed: a package in it has no dependent, a package absent from it is
    /// required by another one. Names are compared the way pip compares them (case-insensitive, runs of '-', '_' and
    /// '.' equal). A null <paramref name="notRequired"/> (that call failed) leaves every package unknown.
    /// </summary>
    public static IReadOnlyList<InstalledPackage> MarkPipRequired(
        IReadOnlyList<InstalledPackage> all, IReadOnlyList<InstalledPackage>? notRequired)
    {
        if (notRequired is null)
            return all;

        var free = new HashSet<string>(notRequired.Select(p => NormalizePipName(p.Id)), StringComparer.Ordinal);
        return all.Select(p => p with { RequiredByOthers = !free.Contains(NormalizePipName(p.Id)) }).ToList();
    }

    internal static string NormalizePipName(string name) => PipNameSeparators().Replace(name.Trim(), "-").ToLowerInvariant();

    [GeneratedRegex("[-_.]+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PipNameSeparators();

    /// <summary>
    /// True when `npm ls -g --json` printed a report instead of a failure. npm exits 1 for a tree with a problem
    /// (a missing peer, an extraneous folder) and still prints the full list, so the code alone says nothing; an
    /// "error" member does.
    /// </summary>
    public static bool IsNpmListReport(string? stdout)
    {
        if (!JsonElementHelpers.TryParseJson(stdout ?? string.Empty, out var document) || document is null)
            return false;

        using (document)
            return document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.TryGetProperty("error", out _);
    }

    /// <summary>
    /// `npm ls -g --depth=0 --json` → {"dependencies":{"name":{"version"}}}. Everything at depth 0 of the global
    /// folder was installed on purpose, so nothing there is held by another package: RequiredByOthers is false.
    /// </summary>
    public static IReadOnlyList<InstalledPackage> ParseNpmList(string? stdout)
    {
        var results = new List<InstalledPackage>();
        if (!JsonElementHelpers.TryParseJson(stdout ?? string.Empty, out var document) || document is null)
            return results;

        using (document)
            AddDependencies(document.RootElement, results, requiredByOthers: false);

        return results;
    }

    /// <summary>`pnpm ls -g --depth=0 --json` → [{"dependencies":{"name":{"version"}}}] (one project: the global one).</summary>
    public static IReadOnlyList<InstalledPackage> ParsePnpmList(string? stdout)
    {
        var results = new List<InstalledPackage>();
        if (!JsonElementHelpers.TryParseJson(stdout ?? string.Empty, out var document) || document is null)
            return results;

        using (document)
        {
            var root = document.RootElement;
            var projects = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
            foreach (var project in projects)
                AddDependencies(project, results, requiredByOthers: null);
        }

        return results;
    }

    private static void AddDependencies(JsonElement project, List<InstalledPackage> results, bool? requiredByOthers)
    {
        if (project.ValueKind != JsonValueKind.Object
            || !project.TryGetProperty("dependencies", out var dependencies)
            || dependencies.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var dependency in dependencies.EnumerateObject())
        {
            if (dependency.Value.ValueKind != JsonValueKind.Object || dependency.Name.Length == 0)
                continue;

            if (results.Any(r => string.Equals(r.Id, dependency.Name, StringComparison.Ordinal)))
                continue;

            results.Add(Plain(dependency.Name, JsonElementHelpers.GetString(dependency.Value, "version"), requiredByOthers));
        }
    }

    /// <summary>`gh extension list` is tab separated "name, repo, version" with a leading "*" on an extension that has an update.</summary>
    public static IReadOnlyList<InstalledPackage> ParseGhExtensions(string? stdout)
    {
        var results = new List<InstalledPackage>();
        foreach (var raw in Lines(stdout))
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var fields = raw.Split('\t').Select(f => f.Trim()).Where(f => f.Length > 0 && f != "*").ToArray();
            var repo = fields.FirstOrDefault(f => f.Contains('/') && !f.Any(char.IsWhiteSpace));
            if (repo is null)
                continue;

            var version = fields.LastOrDefault(f => f != repo && !f.Contains('/') && (f[0] is 'v' or 'V' || char.IsDigit(f[0])));
            results.Add(Plain(repo, version));
        }

        return results;
    }

    /// <summary>
    /// `gem list --local` → "name (1.2.3)", "name (1.2.3, 1.1.0)" or "name (default: 3.1.1, 3.0.0)". A default gem ships
    /// with Ruby and cannot be removed, so a gem with only default versions is skipped; the version shown is the
    /// newest one that can be removed. A platform tag after the version ("1.0.0 x64-mingw-ucrt") is not part of it.
    /// </summary>
    public static IReadOnlyList<InstalledPackage> ParseGemList(string? stdout)
    {
        var results = new List<InstalledPackage>();
        foreach (var raw in Lines(stdout))
        {
            var line = raw.Trim();
            var open = line.IndexOf(" (", StringComparison.Ordinal);
            if (open <= 0 || !line.EndsWith(')') || line.StartsWith("***", StringComparison.Ordinal))
                continue;

            var name = line[..open];
            if (name.Any(char.IsWhiteSpace))
                continue;

            var versions = line[(open + 2)..^1]
                .Split(',')
                .Select(v => v.Trim())
                .Where(v => v.Length > 0 && !v.StartsWith("default:", StringComparison.OrdinalIgnoreCase))
                .Select(v => v.Split(' ')[0])
                .Where(v => char.IsDigit(v[0]))
                .ToList();

            if (versions.Count > 0)
                results.Add(Plain(name, versions[0]));
        }

        return results;
    }

    /// <summary>`composer global show --format=json` → {"installed":[{"name","version"}]}.</summary>
    public static IReadOnlyList<InstalledPackage> ParseComposerShow(string? stdout)
    {
        var results = new List<InstalledPackage>();
        if (!JsonElementHelpers.TryParseJson(stdout ?? string.Empty, out var document) || document is null)
            return results;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("installed", out var installed)
                || installed.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var element in installed.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var name = JsonElementHelpers.GetString(element, "name");
                if (name.Length > 0)
                    results.Add(Plain(name, JsonElementHelpers.GetString(element, "version")));
            }
        }

        return results;
    }

    /// <summary>`vcpkg list` → "port:triplet   version   description"; a feature line "port[feature]:triplet" is part of its port and skipped.</summary>
    public static IReadOnlyList<InstalledPackage> ParseVcpkgList(string? stdout)
    {
        var results = new List<InstalledPackage>();
        foreach (var raw in Lines(stdout))
        {
            var match = VcpkgListLine().Match(raw.Trim());
            if (!match.Success)
                continue;

            var port = match.Groups["port"].Value;
            results.Add(new InstalledPackage($"{port}:{match.Groups["triplet"].Value}", port, NullIfEmpty(match.Groups["version"].Value)));
        }

        return results;
    }

    [GeneratedRegex(@"^(?<port>[^:\s\[\]]+):(?<triplet>[^\s:]+)\s+(?<version>\S+)(?:\s.*)?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex VcpkgListLine();

    /// <summary>
    /// `mise ls --json` → {"tool":[{"version","installed","active"}]}. One row per tool: the active installed version,
    /// else the first installed one. A tool with no installed version (only requested) is skipped.
    /// </summary>
    public static IReadOnlyList<InstalledPackage> ParseMiseList(string? stdout)
    {
        var results = new List<InstalledPackage>();
        if (!JsonElementHelpers.TryParseJson(stdout ?? string.Empty, out var document) || document is null)
            return results;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return results;

            foreach (var tool in document.RootElement.EnumerateObject())
            {
                if (tool.Value.ValueKind != JsonValueKind.Array || tool.Name.Length == 0)
                    continue;

                string? version = null;
                var active = false;
                foreach (var entry in tool.Value.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object
                        || !(entry.TryGetProperty("installed", out var installed) && installed.ValueKind == JsonValueKind.True))
                    {
                        continue;
                    }

                    var candidate = JsonElementHelpers.GetString(entry, "version");
                    if (candidate.Length == 0)
                        continue;

                    var isActive = entry.TryGetProperty("active", out var flag) && flag.ValueKind == JsonValueKind.True;
                    if (version is null || (isActive && !active))
                    {
                        version = candidate;
                        active = isActive;
                    }
                }

                if (version is not null)
                    results.Add(Plain(tool.Name, version));
            }
        }

        return results;
    }

    /// <summary>
    /// `sdkmanager --list` / `android sdk list`: the "Installed packages:" section of every layout, pipe separated
    /// ("path | version | description | location") or in runs of spaces ("path   version   description"). The section
    /// ends at the next heading. Header rows and dividers are not packages.
    /// </summary>
    public static IReadOnlyList<InstalledPackage> ParseSdkManagerInstalled(string? stdout)
    {
        var results = new List<InstalledPackage>();
        var inSection = false;
        foreach (var raw in Lines(stdout))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.EndsWith(':') && !line.Contains('|'))
            {
                inSection = string.Equals(line, "Installed packages:", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
                continue;

            string path;
            string version;
            if (line.Contains('|'))
            {
                var parts = line.Split('|').Select(p => p.Trim()).ToArray();
                if (parts.Length < 2)
                    continue;

                (path, version) = (parts[0], parts[1]);
            }
            else
            {
                var match = SdkInstalledRow().Match(line);
                if (!match.Success)
                    continue;

                (path, version) = (match.Groups["path"].Value, match.Groups["version"].Value);
            }

            if (path.Length == 0 || version.Length == 0 || !char.IsDigit(version[0]) || path.Any(char.IsWhiteSpace))
                continue;

            if (results.All(r => !string.Equals(r.Id, path, StringComparison.Ordinal)))
                results.Add(Plain(path, version));
        }

        return results;
    }

    [GeneratedRegex(@"^(?<path>\S+)\s{2,}(?<version>\d\S*)(?:\s.*)?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SdkInstalledRow();

    /// <summary>`gcloud components list --format=json`: every component with a current version is installed (null = not installed).</summary>
    public static IReadOnlyList<InstalledPackage> ParseGcloudInstalled(string? stdout)
    {
        var results = new List<InstalledPackage>();
        var text = stdout ?? string.Empty;
        var start = text.IndexOf('[');
        if (start < 0 || !JsonElementHelpers.TryParseJson(text[start..], out var document) || document is null)
            return results;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var id = JsonElementHelpers.GetString(element, "id");
                var current = JsonElementHelpers.GetString(element, "current_version_string");
                if (id.Length == 0 || current.Length == 0)
                    continue;

                var name = JsonElementHelpers.GetString(element, "name");
                results.Add(new InstalledPackage(id, name.Length > 0 ? name : id, current));
            }
        }

        return results;
    }

    /// <summary>`pyenv versions`: one version per line, "* 3.11.5 (set by ...)" for the global one; "system" is no install.</summary>
    public static IReadOnlyList<string> ParsePyenvVersions(string? stdout)
    {
        var results = new List<string>();
        foreach (var raw in Lines(stdout))
        {
            var line = raw.Trim().TrimStart('*').Trim();
            var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (token is { Length: > 0 } && char.IsDigit(token[0]) && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+')
                && !results.Contains(token))
            {
                results.Add(token);
            }
        }

        return results;
    }
}
