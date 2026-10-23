using System.Text.Json;

namespace UltimateUpdater.Services.Sources;

/// <param name="BeyondConstraint">True when the tool says the available version lies outside the
/// installed package's own version constraint (Composer's "update-possible"), so a plain update of
/// the package cannot reach it.</param>
public sealed record OutdatedPackage(string Id, string Name, string? Installed, string? Available, bool BeyondConstraint = false);

/// <summary>
/// Pure text→data parsers for the CLI package managers. No process launching here, so every one of
/// these is directly unit-testable. All of them must tolerate garbage: skip the bad row, never throw.
/// </summary>
public static class PackageManagerParsers
{
    /// <summary>`choco outdated -r` → `name|current|available|pinned` per line. Pinned packages are excluded.</summary>
    public static IReadOnlyList<OutdatedPackage> ParseChocolatey(string stdout)
    {
        var results = new List<OutdatedPackage>();
        if (string.IsNullOrWhiteSpace(stdout))
            return results;

        foreach (var rawLine in SplitLines(stdout))
        {
            var parts = rawLine.Split('|');
            if (parts.Length < 3)
                continue;

            var name = parts[0].Trim();
            var current = parts[1].Trim();
            var available = parts[2].Trim();
            var pinned = parts.Length > 3 && bool.TryParse(parts[3].Trim(), out var isPinned) && isPinned;

            // choco prints a package it cannot find on any source as "name|ver|ver|false" (installed version
            // as the "available" one): not an update, and offered it never went away.
            if (name.Length == 0 || available.Length == 0 || pinned
                || string.Equals(current, available, StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(new OutdatedPackage(name, name, current, available));
        }

        return results;
    }

    /// <summary>Start offsets of each run of dashes in a table's rule line.</summary>
    private static int[] DashRunStarts(string rule)
    {
        var starts = new List<int>();
        for (var i = 0; i < rule.Length; i++)
        {
            if (rule[i] == '-' && (i == 0 || rule[i - 1] != '-'))
                starts.Add(i);
        }

        return starts.ToArray();
    }

    private static string SliceColumn(string line, int[] starts, int index)
    {
        var start = starts[index];
        if (start >= line.Length)
            return string.Empty;

        var end = index + 1 < starts.Length ? Math.Min(starts[index + 1], line.Length) : line.Length;
        return line[start..end].Trim();
    }

    /// <summary>
    /// `scoop status` table. Column layout varies between scoop versions, so rows are split on runs
    /// of whitespace and only rows whose 2nd and 3rd fields look like versions are accepted.
    /// </summary>
    public static IReadOnlyList<OutdatedPackage> ParseScoopStatus(string stdout)
    {
        var results = new List<OutdatedPackage>();
        if (string.IsNullOrWhiteSpace(stdout))
            return results;

        // scoop status also lists apps that are NOT outdated (missing dependencies, failed, deprecated): their
        // "Latest Version" cell is empty and an Info column follows ("foo  1.0    7zip"). Split on whitespace,
        // the Info text was read as the available version. With the dashed rule known, columns are read by
        // position instead.
        int[]? columnStarts = null;

        foreach (var rawLine in SplitLines(stdout))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("---", StringComparison.Ordinal))
            {
                columnStarts = DashRunStarts(rawLine.TrimEnd());
                continue;
            }

            // A held app is listed as outdated, but `scoop update` skips it with a warning and exits 0: the row
            // would come back after every "successful" update.
            if (line.Contains("Held package", StringComparison.OrdinalIgnoreCase))
                continue;

            string name, installed, available;
            if (columnStarts is { Length: >= 3 })
            {
                name = SliceColumn(rawLine, columnStarts, 0);
                installed = SliceColumn(rawLine, columnStarts, 1);
                available = SliceColumn(rawLine, columnStarts, 2);
                if (name.Length == 0 || installed.Length == 0 || available.Length == 0)
                    continue;
            }
            else
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 3)
                    continue;

                name = fields[0];
                installed = fields[1];
                available = fields[2];
            }

            // The header row's own column name, not a package; rejected EXPLICITLY.
            if (string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase))
                continue;

            // demanding a LEADING digit rejected genuinely valid scoop versions that are not
            // numeric at all; "nightly", "current", a bare "v1.2". Widened to "contains a digit
            // somewhere, or is one of scoop's own non-numeric version words"; still narrow enough to
            // reject ordinary prose (a status line with no table to parse at all).
            if (!LooksLikeScoopVersion(installed) || !LooksLikeScoopVersion(available))
                continue;

            if (string.Equals(installed, available, StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(new OutdatedPackage(name, name, installed, available));
        }

        return results;
    }

    /// <summary>`pip list --outdated --format=json` → array of {name, version, latest_version}.</summary>
    public static IReadOnlyList<OutdatedPackage> ParsePipJson(string stdout)
    {
        var results = new List<OutdatedPackage>();
        if (!JsonElementHelpers.TryParseJson(stdout, out var document) || document is null)
            return results;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var name = GetString(element, "name");
                var version = GetString(element, "version");
                var latest = GetString(element, "latest_version");

                if (name.Length == 0 || latest.Length == 0)
                    continue;

                results.Add(new OutdatedPackage(name, name, NullIfEmpty(version), latest));
            }
        }

        return results;
    }

    /// <summary>`npm outdated -g --json` → object keyed by package name, each {current, latest}.</summary>
    public static IReadOnlyList<OutdatedPackage> ParseNpmJson(string stdout)
    {
        var results = new List<OutdatedPackage>();
        if (!JsonElementHelpers.TryParseJson(stdout, out var document) || document is null)
            return results;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return results;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var current = GetString(property.Value, "current");
                var latest = GetString(property.Value, "latest");

                if (latest.Length == 0 || string.Equals(current, latest, StringComparison.Ordinal))
                    continue;

                // npm also lists a package installed AHEAD of its `latest` tag (an @next/@beta install),
                // because installed != wanted; offering `latest` there would be a downgrade.
                if (current.Length > 0 && !IsNewer(current, latest))
                    continue;

                results.Add(new OutdatedPackage(property.Name, property.Name, NullIfEmpty(current), latest));
            }
        }

        return results;
    }

    /// <summary>
    /// The re-check for Chocolatey: `choco list &lt;id&gt; --exact --limit-output` -> the same
    /// `name|version` shape as <see cref="ParseChocolatey"/>, just one row. Matched by id (choco lower-cases
    /// it in this output, --exact already narrowed the search) rather than trusted positionally, so a
    /// warning banner Chocolatey prints on its own line can never be misread as the package row.
    /// </summary>
    public static string? ParseChocolateyExactVersion(string stdout, string id)
    {
        foreach (var rawLine in SplitLines(stdout))
        {
            var parts = rawLine.Split('|');
            if (parts.Length < 2)
                continue;

            var name = parts[0].Trim();
            if (string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
                return NullIfEmpty(parts[1].Trim());
        }

        return null;
    }

    /// <summary>
    /// The re-check for npm: `npm list -g &lt;id&gt; --depth=0 --json` -> {"dependencies":{"&lt;id&gt;":
    /// {"version":"x.y.z"}}}. A package that is not installed simply has no entry, not a
    /// parse failure.
    /// </summary>
    public static string? ParseNpmListVersion(string stdout, string id)
    {
        if (!JsonElementHelpers.TryParseJson(stdout, out var document) || document is null)
            return null;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var property in dependencies.EnumerateObject())
            {
                if (!string.Equals(property.Name, id, StringComparison.Ordinal))
                    continue;

                var version = GetString(property.Value, "version");
                return NullIfEmpty(version);
            }
        }

        return null;
    }

    /// <summary>
    /// The re-check for pip: `pip show &lt;id&gt;` -> a plain "Label: value" listing, one label per
    /// line; "Version:" is the one this needs. Not JSON (pip's `show` command has no `--format` option),
    /// so this reads the label the same way winget's own label:value output already is
    /// (<see cref="WingetUpgradeParser.ParseShowReleaseDate"/>).
    /// </summary>
    public static string? ParsePipShowVersion(string stdout)
    {
        const string label = "Version:";

        foreach (var rawLine in SplitLines(stdout))
        {
            var line = rawLine.Trim();
            if (line.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                return NullIfEmpty(line[label.Length..].Trim());
        }

        return null;
    }

    /// <summary>
    /// `dotnet tool list --global` table: PackageId / Version / Commands, separated by a dashed rule.
    /// Only the installed set is returned here; latest versions come from NuGet separately.
    /// </summary>
    public static IReadOnlyList<OutdatedPackage> ParseDotnetToolList(string stdout)
    {
        var results = new List<OutdatedPackage>();
        if (string.IsNullOrWhiteSpace(stdout))
            return results;

        var pastHeader = false;

        foreach (var rawLine in SplitLines(stdout))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("---", StringComparison.Ordinal))
            {
                pastHeader = true;
                continue;
            }

            if (!pastHeader)
                continue;

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !LooksLikeVersion(fields[1]))
                continue;

            results.Add(new OutdatedPackage(fields[0], fields[0], fields[1], null));
        }

        return results;
    }

    /// <summary>NuGet flat-container index → newest STABLE version, ignoring pre-releases.</summary>
    public static string? ParseNuGetLatestStable(string json)
    {
        if (!JsonElementHelpers.TryParseJson(json, out var document) || document is null)
            return null;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? latest = null;
            foreach (var element in versions.EnumerateArray())
            {
                // GetString() throws on anything that is not a JSON string, and this parser's caller
                // does not catch that; one odd entry in the feed would have made every .NET tool
                // update disappear behind a provider error instead of being skipped.
                if (element.ValueKind != JsonValueKind.String)
                    continue;

                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value) || value.Contains('-'))
                    continue;
                latest = value;
            }

            // the feed is ordered oldest→newest, so the last stable entry is the newest one
            return latest;
        }
    }

    /// <summary>
    /// A NuGet-shaped OData v2 feed (the PowerShell Gallery's, and Chocolatey's) → the Version of the
    /// single entry it returned. Read namespace-agnostically, and only when the feed really did narrow to
    /// one package: more than one entry means the query matched something other than what was asked for,
    /// and picking one of them would attach a foreign package's version to this row.
    /// </summary>
    public static string? ParseODataLatestVersion(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        try
        {
            var root = System.Xml.Linq.XDocument.Parse(xml).Root;
            if (root is null)
                return null;

            var entries = root.Elements()
                .Where(e => e.Name.LocalName.Equals("entry", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (entries.Count != 1)
                return null;

            var version = entries[0].Descendants()
                .FirstOrDefault(e => e.Name.LocalName.Equals("Version", StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim();

            return string.IsNullOrEmpty(version) ? null : version;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Our PowerShell helper emits [{Name, Installed}] for the user's own PSGallery modules.</summary>
    public static IReadOnlyList<OutdatedPackage> ParsePsGalleryInstalledJson(string stdout)
    {
        var results = new List<OutdatedPackage>();
        if (!JsonElementHelpers.TryParseJson(stdout, out var document) || document is null)
            return results;

        using (document)
        {
            // A single module comes back as a bare object, several as an array; ConvertTo-Json does not
            // wrap one element in a list.
            var elements = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.EnumerateArray().ToList(),
                JsonValueKind.Object => [document.RootElement],
                _ => new List<JsonElement>(),
            };

            foreach (var element in elements)
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var name = GetString(element, "Name");
                var installed = GetString(element, "Installed");

                if (name.Length == 0 || installed.Length == 0)
                    continue;

                results.Add(new OutdatedPackage(name, name, installed, null));
            }
        }

        return results;
    }

    /// <summary>`gem outdated` → "name (installed &lt; available)" per line.</summary>
    public static IReadOnlyList<OutdatedPackage> ParseGemOutdated(string stdout)
    {
        var results = new List<OutdatedPackage>();
        if (string.IsNullOrWhiteSpace(stdout))
            return results;

        foreach (var rawLine in SplitLines(stdout))
        {
            var line = rawLine.Trim();

            var open = line.IndexOf('(');
            var less = line.IndexOf('<', open + 1);
            var close = line.IndexOf(')', less + 1);
            if (open <= 0 || less < 0 || close < 0)
                continue;

            var name = line[..open].Trim();
            var installed = line[(open + 1)..less].Trim();
            var available = line[(less + 1)..close].Trim();

            if (name.Length == 0 || available.Length == 0)
                continue;

            results.Add(new OutdatedPackage(name, name, NullIfEmpty(installed), available));
        }

        return results;
    }

    /// <summary>`composer global outdated --format=json` → {"installed":[{name,version,latest}]}.</summary>
    public static IReadOnlyList<OutdatedPackage> ParseComposerJson(string stdout)
    {
        var results = new List<OutdatedPackage>();
        if (!JsonElementHelpers.TryParseJson(stdout, out var document) || document is null)
            return results;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("installed", out var installed) ||
                installed.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var element in installed.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var name = GetString(element, "name");
                var version = GetString(element, "version");
                var latest = GetString(element, "latest");

                if (name.Length == 0 || latest.Length == 0 || string.Equals(version, latest, StringComparison.OrdinalIgnoreCase))
                    continue;

                // "semver-safe-update" is reachable with `composer update`; "update-possible" is a new
                // major the caret constraint does not reach, so the constraint has to change first. Only
                // a direct dependency gets that treatment: requiring a transitive one would make it a
                // root requirement of the global project as a side effect.
                var beyondConstraint =
                    string.Equals(GetString(element, "latest-status"), "update-possible", StringComparison.Ordinal)
                    && !(element.TryGetProperty("direct-dependency", out var direct) && direct.ValueKind == JsonValueKind.False);
                results.Add(new OutdatedPackage(name, name, NullIfEmpty(version), latest, beyondConstraint));
            }
        }

        return results;
    }

    /// <summary>`rustup check` → "&lt;name&gt; - Update available : X -&gt; Y" for each updatable component.</summary>
    public static IReadOnlyList<OutdatedPackage> ParseRustupCheck(string stdout)
    {
        var results = new List<OutdatedPackage>();
        if (string.IsNullOrWhiteSpace(stdout))
            return results;

        foreach (var rawLine in SplitLines(stdout))
        {
            var line = rawLine.Trim();

            var marker = line.IndexOf("Update available", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                continue;

            var arrow = line.IndexOf("->", marker, StringComparison.Ordinal);
            if (arrow < 0)
                continue;

            var dash = line.LastIndexOf(" - ", marker, StringComparison.Ordinal);
            var name = (dash > 0 ? line[..dash] : line[..marker]).Trim();
            if (name.Length == 0)
                continue;

            var colon = line.IndexOf(':', marker);
            var installed = colon > 0 && colon < arrow ? line[(colon + 1)..arrow].Trim() : string.Empty;
            var available = line[(arrow + 2)..].Trim();
            if (available.Length == 0)
                continue;

            // Real output carries a commit hash + date in parens after each version
            // ("1.97.1 (8bab26f4f 2026-07-14)"). The 62px version column has room
            // for neither, so drop everything from " (" on.
            results.Add(new OutdatedPackage(name, name, NullIfEmpty(TrimVersionSuffix(installed)), TrimVersionSuffix(available)));
        }

        return results;
    }

    private static string TrimVersionSuffix(string value)
    {
        var parenIndex = value.IndexOf(" (", StringComparison.Ordinal);
        if (parenIndex <= 0)
            return value;

        var trimmed = value[..parenIndex].Trim();
        return trimmed.Length > 0 ? trimmed : value;
    }

    /// <summary>
    /// `vcpkg update` (classic mode). Shape taken from Microsoft's own vcpkg command reference
    /// (learn.microsoft.com/en-us/vcpkg/commands/update); not run against a live vcpkg install:
    /// The following packages differ from their port versions:
    /// corrade:x64-windows 2020.06#4 -> 2020.06#5
    /// magnum:x64-windows 2020.06#11 -> 2020.06#12
    /// To update these packages and all dependencies, run
    /// .\vcpkg upgrade
    /// The id carries the triplet: the same port can be installed for more than one triplet
    /// (x64-windows and x86-windows side by side) and each is its own independent upgrade.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex VcpkgUpdateLine = new(
        @"^\s*(?<port>[^:\s]+):(?<triplet>\S+)\s+(?<old>\S+)\s*->\s*(?<new>\S+)\s*$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<OutdatedPackage> ParseVcpkgUpdate(string stdout)
    {
        var results = new List<OutdatedPackage>();
        if (string.IsNullOrWhiteSpace(stdout))
            return results;

        foreach (var rawLine in SplitLines(stdout))
        {
            var match = VcpkgUpdateLine.Match(rawLine);
            if (!match.Success)
                continue;

            var port = match.Groups["port"].Value;
            var triplet = match.Groups["triplet"].Value;
            var id = $"{port}:{triplet}";

            results.Add(new OutdatedPackage(id, port, match.Groups["old"].Value, match.Groups["new"].Value));
        }

        return results;
    }

    /// <summary>
    /// `sdkmanager --list` has two layouts. The CLASSIC one prints an "Available Updates:" table further
    /// down the same output as the installed-packages table, columns pipe-separated. The NEWER one (no
    /// updates table at all) prints an "Installed packages:" section and an "Available packages:" section,
    /// columns separated by runs of whitespace; an available version can carry a trailing "(+n)" meaning
    /// further versions exist beyond the one shown. Overload without <paramref name="recognized"/> is for
    /// callers (tests, mostly) that do not need to tell "genuinely nothing to report" apart from
    /// "could not read this at all".
    /// </summary>
    public static IReadOnlyList<OutdatedPackage> ParseSdkManagerList(string stdout) =>
        ParseSdkManagerList(stdout, out _);

    /// <param name="recognized">
    /// True once either layout's own header was seen, even when that layout then yields zero rows: an
    /// empty result from a RECOGNIZED layout is a genuine "no updates" answer, not a parse failure.
    /// </param>
    public static IReadOnlyList<OutdatedPackage> ParseSdkManagerList(string stdout, out bool recognized)
    {
        recognized = false;
        var results = new List<OutdatedPackage>();
        if (string.IsNullOrWhiteSpace(stdout))
            return results;

        var lines = SplitLines(stdout).ToList();

        // The classic layout ALSO prints an "Installed packages:" section ahead of its "Available
        // Updates:" table, so that header alone cannot tell the two apart; "Available packages:" only
        // ever appears in the newer layout, which has no updates table at all.
        if (lines.Any(l => string.Equals(l.Trim(), "Available packages:", StringComparison.OrdinalIgnoreCase)))
        {
            recognized = true;
            return ParseSdkManagerNewLayout(lines);
        }

        // The newest command-line tools list everything in one "Installed packages:" section and mark a
        // package that has an update inline ("37.1.11  ->  37.2.12"); there is no updates table.
        if (!lines.Any(l => l.Contains("Available Updates", StringComparison.OrdinalIgnoreCase))
            && TryParseSdkManagerInlineLayout(lines, out var inlineRows))
        {
            recognized = true;
            return inlineRows;
        }

        var pastHeader = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (!pastHeader)
            {
                if (line.Contains("Available Updates", StringComparison.OrdinalIgnoreCase))
                {
                    pastHeader = true;
                    recognized = true;
                }
                continue;
            }

            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            if (parts.Length != 3 || parts.Any(p => p.Length == 0))
                continue;

            // the column header row and the "-------|-----|-----" divider both split into 3 non-empty
            // parts too, so they need their own rejection rather than falling out of the length check
            if (string.Equals(parts[0], "ID", StringComparison.OrdinalIgnoreCase) || parts[0].All(c => c == '-'))
                continue;

            results.Add(new OutdatedPackage(parts[0], parts[0], parts[1], parts[2]));
        }

        return results;
    }

    /// <summary>
    /// Reads the "Installed packages:" and "Available packages:" sections and emits one row per path
    /// present in both where the available version is strictly newer (a "(+n)" suffix on the available
    /// column means further versions exist and is stripped before comparing, never treated as part of the
    /// version itself). A path that only exists in "available" is NOT an update, it was never installed.
    /// </summary>
    private static IReadOnlyList<OutdatedPackage> ParseSdkManagerNewLayout(IEnumerable<string> lines)
    {
        var installed = new Dictionary<string, string>(StringComparer.Ordinal);
        var available = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = 0; // 0 = none yet, 1 = Installed packages, 2 = Available packages

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.Equals(trimmed, "Installed packages:", StringComparison.OrdinalIgnoreCase))
            {
                section = 1;
                continue;
            }

            if (string.Equals(trimmed, "Available packages:", StringComparison.OrdinalIgnoreCase))
            {
                section = 2;
                continue;
            }

            if (section == 0 || trimmed.Length == 0)
                continue;

            var parts = SdkManagerColumns.Split(trimmed);
            if (parts.Length < 2)
                continue;

            var path = parts[0].Trim();
            var version = StripUpdateCountSuffix(parts[1].Trim());

            // Also rejects the section's own column header ("Path  Version  Description") and any
            // "-------  -------" divider row, neither of which starts with a digit.
            if (path.Length == 0 || !LooksLikeVersion(version))
                continue;

            var target = section == 1 ? installed : available;
            target.TryAdd(path, version);
        }

        var results = new List<OutdatedPackage>();
        foreach (var (path, installedVersion) in installed)
        {
            if (available.TryGetValue(path, out var availableVersion) && IsNewer(installedVersion, availableVersion))
                results.Add(new OutdatedPackage(path, path, installedVersion, availableVersion));
        }

        return results;
    }

    private static readonly System.Text.RegularExpressions.Regex SdkManagerInlineRow = new(
        @"^(?<path>\S+)\s{2,}(?<installed>\d\S*)(?:\s+->\s+(?<available>\d\S*))?(?:\s|$)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The inline layout: a whitespace-column "Installed packages:" section, no pipes. Recognised as soon as
    /// the section holds one package row, with or without an update marker, because the tool prints the
    /// same listing when everything is current (and then exits non-zero). An offered version that is not
    /// newer (a downgrade) is dropped like in the other layouts.
    /// </summary>
    private static bool TryParseSdkManagerInlineLayout(IEnumerable<string> lines, out IReadOnlyList<OutdatedPackage> rows)
    {
        var results = new List<OutdatedPackage>();
        var inSection = false;
        var sawRow = false;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            if (string.Equals(trimmed, "Installed packages:", StringComparison.OrdinalIgnoreCase))
            {
                inSection = true;
                continue;
            }

            if (!inSection || trimmed.Length == 0 || trimmed.Contains('|'))
                continue;

            var match = SdkManagerInlineRow.Match(trimmed);
            if (!match.Success)
                continue;

            sawRow = true;
            var installed = match.Groups["installed"].Value;
            if (match.Groups["available"] is { Success: true } available && IsNewer(installed, available.Value))
                results.Add(new OutdatedPackage(match.Groups["path"].Value, match.Groups["path"].Value, installed, available.Value));
        }

        rows = results;
        return sawRow;
    }

    /// <summary>Splits a trimmed sdkmanager table row on runs of two or more spaces, its column
    /// separator in the new whitespace-column layout (a single space inside "3.0.0 (+1)" must survive).</summary>
    private static readonly System.Text.RegularExpressions.Regex SdkManagerColumns = new(
        @"\s{2,}", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Strips a trailing "(+n)" marker the new layout adds to an available version when further
    /// versions exist beyond the one shown; it is a count, never part of the version itself.</summary>
    private static string StripUpdateCountSuffix(string version)
    {
        var index = version.IndexOf(" (+", StringComparison.Ordinal);
        return index < 0 ? version : version[..index].TrimEnd();
    }

    /// <summary>
    /// `gh extension list` is tab-separated; a leading "*" column is gh's own "update available" marker,
    /// and only those rows are returned; the rest are already current and have nothing to offer.
    /// </summary>
    public static IReadOnlyList<OutdatedPackage> ParseGhExtensionList(string stdout)
    {
        var results = new List<OutdatedPackage>();
        if (string.IsNullOrWhiteSpace(stdout))
            return results;

        foreach (var rawLine in SplitLines(stdout))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;

            var fields = rawLine.Split('\t').Select(f => f.Trim()).ToArray();
            if (fields.Length == 0 || fields[0] != "*")
                continue;

            var rest = fields[1..];
            if (rest.Length < 2)
                continue;

            var repo = rest.FirstOrDefault(f => f.Contains('/'));
            var version = rest.LastOrDefault(f => f.Length > 0 && (f[0] == 'v' || char.IsDigit(f[0])));
            if (repo is null || version is null)
                continue;

            results.Add(new OutdatedPackage(repo, repo, version, null));
        }

        return results;
    }

    /// <summary>
    /// `pipx list --json` → {"venvs": {name: {"metadata": {"main_package": {"package", "package_version"}}}}}.
    /// pipx has no "outdated" mode, so this is every installed package, not a confirmed pending update;
    /// PipxSource then checks each one against PyPI and keeps only those genuinely behind.
    /// </summary>
    public static IReadOnlyList<OutdatedPackage> ParsePipxJson(string json)
    {
        var results = new List<OutdatedPackage>();
        if (!JsonElementHelpers.TryParseJson(json, out var document) || document is null)
            return results;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("venvs", out var venvs) ||
                venvs.ValueKind != JsonValueKind.Object)
            {
                return results;
            }

            foreach (var venv in venvs.EnumerateObject())
            {
                if (venv.Value.ValueKind != JsonValueKind.Object ||
                    !venv.Value.TryGetProperty("metadata", out var metadata) ||
                    metadata.ValueKind != JsonValueKind.Object ||
                    !metadata.TryGetProperty("main_package", out var mainPackage) ||
                    mainPackage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var package = GetString(mainPackage, "package");
                var version = GetString(mainPackage, "package_version");
                if (package.Length == 0)
                    continue;

                results.Add(new OutdatedPackage(package, package, NullIfEmpty(version), null));
            }
        }

        return results;
    }

    /// <summary>
    /// `code --list-extensions --show-versions` → "publisher.name@1.2.3" per line. `code` reports every
    /// installed extension, not just outdated ones, so the result is only the installed list; the
    /// Marketplace check in VsCodeExtensionSource decides what is behind, and its install always asks for the latest.
    /// </summary>
    public static IReadOnlyList<OutdatedPackage> ParseVsCodeExtensions(string stdout)
    {
        var results = new List<OutdatedPackage>();
        if (string.IsNullOrWhiteSpace(stdout))
            return results;

        foreach (var rawLine in SplitLines(stdout))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var at = line.LastIndexOf('@');
            if (at <= 0 || at == line.Length - 1)
                continue;

            var id = line[..at];
            var version = line[(at + 1)..];
            if (id.Length == 0 || version.Length == 0)
                continue;

            results.Add(new OutdatedPackage(id, id, version, null));
        }

        return results;
    }

    /// <summary>
    /// One outdated gcloud component, with the download size gcloud itself reports.
    /// </summary>
    public readonly record struct GcloudComponent(string Id, string Name, string Installed, string Available, long? Size);

    /// <summary>
    /// `gcloud components list --format=json`; an array of every component, installed or not. Verified
    /// against the real output on a clean VM:
    /// { "id": "bq", "name": "BQ Command Line Tool", "current_version_string": "2.1.35",
    /// "latest_version_string": "2.1.35", "size": 1973488, "state": { "name": "Installed" } }
    /// A null <c>current_version_string</c> means "not installed"; those are offers to ADD a component, not
    /// updates, and are skipped. The decision compares the two versions rather than reading
    /// <c>state.name</c>: that is gcloud's own wording, and only "Installed" and "Not Installed" have been
    /// seen, so relying on a third value would be a guess. The "your version is…"
    /// chatter gcloud prints goes to stderr, and any leading noise on stdout is skipped by starting at the array.
    /// </summary>
    public static IReadOnlyList<GcloudComponent> ParseGcloudComponents(string output)
    {
        var results = new List<GcloudComponent>();

        if (string.IsNullOrWhiteSpace(output))
            return results;

        var start = output.IndexOf('[');
        if (start < 0 || !JsonElementHelpers.TryParseJson(output[start..], out var document) || document is null)
            return results;

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var id = GetString(element, "id");
                var installed = GetString(element, "current_version_string");
                var latest = GetString(element, "latest_version_string");

                if (id.Length == 0 || installed.Length == 0 || latest.Length == 0)
                    continue;

                if (!IsNewer(installed, latest))
                    continue;

                var name = GetString(element, "name");
                long? size = element.TryGetProperty("size", out var sizeValue)
                             && sizeValue.ValueKind == JsonValueKind.Number
                             && sizeValue.TryGetInt64(out var bytes) && bytes > 0
                    ? bytes
                    : null;

                results.Add(new GcloudComponent(id, name.Length > 0 ? name : id, installed, latest, size));
            }
        }

        return results;
    }

    /// <summary>
    /// `uv tool list`; one line per tool, then one indented "- name" line per executable it provides:
    /// graphifyy v0.9.26
    /// - graphify
    /// - graphify-mcp
    /// ruff v0.15.20
    /// - ruff
    /// Only the tool lines are wanted. The executable lines are skipped by their leading dash, which is also
    /// what a message line would never start with. No available version: uv only knows what is installed, so
    /// the source asks PyPI per tool (verified against the real output).
    /// </summary>
    public static IReadOnlyList<OutdatedPackage> ParseUvToolList(string output)
    {
        var results = new List<OutdatedPackage>();

        foreach (var raw in SplitLines(output))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '-')
                continue;

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || fields[1].Length < 2 || fields[1][0] != 'v' || !char.IsDigit(fields[1][1]))
                continue;

            results.Add(new OutdatedPackage(fields[0], fields[0], fields[1][1..], null));
        }

        return results;
    }

    /// <summary>
    /// The first version-shaped token in a tool's own version output. `--version` has no agreed format;
    /// "1.3.14", "deno 2.9.4 (stable, …)", "uv 0.9.7 (abc1234 2026-01-01)" are all real; but they all put a
    /// dotted number somewhere in the first line or two, and the first one is always the tool's own.
    /// Returns null rather than guessing when there is no such token.
    /// </summary>
    public static string? FirstVersionToken(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var match = VersionToken.Match(output);
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// Two or three dotted numbers, optionally with a pre-release or build suffix. Bounded on purpose
    /// (ReDoS-safe: no nesting, every part has a fixed upper length); this runs on tool output.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex VersionToken = new(
        @"\d{1,9}\.\d{1,9}(?:\.\d{1,9}){0,2}(?:[-+][0-9A-Za-z.]{1,32})?",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// True when a newer version is available. Build metadata (after a "+") takes no part. Equal numeric
    /// cores are ordered by SemVer pre-release precedence when both suffixes are SemVer shaped, and are
    /// NOT offered when they are not (no confident order, so no row). Only an unparseable numeric core
    /// falls back to string inequality.
    /// </summary>
    public static bool IsNewer(string? installed, string? available)
    {
        if (string.IsNullOrWhiteSpace(available))
            return false;
        if (string.IsNullOrWhiteSpace(installed))
            return true;

        var (installedCore, installedSuffix) = SplitSuffix(WithoutVPrefix(installed));
        var (availableCore, availableSuffix) = SplitSuffix(WithoutVPrefix(available));

        if (Version.TryParse(WithMinor(installedCore), out var installedVersion) &&
            Version.TryParse(WithMinor(availableCore), out var availableVersion))
        {
            installedVersion = Padded(installedVersion);
            availableVersion = Padded(availableVersion);

            if (availableVersion != installedVersion)
                return availableVersion > installedVersion;

            // Same numeric core: comparing the numeric core ONLY would read
            // "1.0.0-beta" and "1.0.0" as equal, so the stable release would never be offered once a
            // pre-release build was installed. A release carrying no suffix outranks one that does;
            // semver's own rule. Two suffixes on the same core are ordered by semver precedence, but only
            // when both really are semver pre-releases: anything else has no order we can trust, and
            // offering a possible downgrade is worse than missing an update.
            if (installedSuffix.Length > 0 && availableSuffix.Length == 0)
                return true;
            if (installedSuffix.Length == 0)
                return false;

            return IsSemVerPrerelease(installedSuffix)
                && IsSemVerPrerelease(availableSuffix)
                && SelfUpdate.UpdateManifest.ComparePrerelease(installedSuffix, availableSuffix) < 0;
        }

        return !string.Equals(installed, available, StringComparison.OrdinalIgnoreCase);

        // Version.TryParse rejects a lone number, so "10" would otherwise fall to string inequality
        // and read "2" as an update over it.
        static string WithMinor(string core) => core.Length > 0 && core.All(char.IsAsciiDigit) ? core + ".0" : core;

        // Version treats a missing part as lower than zero, so "1.2" would read as older than "1.2.0" and
        // the same release would be offered as an update over itself.
        static Version Padded(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

        // Tags such as "v2.0.0" are numeric versions with a decoration; left in, Version.TryParse rejects
        // them and a downgrade would read as an update through the inequality fallback below.
        static string WithoutVPrefix(string value)
        {
            var trimmed = value.Trim();
            return trimmed.Length > 1 && (trimmed[0] is 'v' or 'V') && char.IsAsciiDigit(trimmed[1])
                ? trimmed[1..]
                : value;
        }

        // Version.Parse rejects pre-release suffixes, so the numeric core is compared separately from it.
        // Build metadata is dropped first so "1.0.0+abc" is the plain release it names.
        static (string Core, string Suffix) SplitSuffix(string value)
        {
            var plus = value.IndexOf('+');
            if (plus >= 0)
                value = value[..plus];

            var cut = value.IndexOf('-');
            return cut < 0 ? (value, string.Empty) : (value[..cut], value[(cut + 1)..]);
        }

        // Dot separated identifiers, each non-empty and made of [0-9A-Za-z-] only.
        static bool IsSemVerPrerelease(string suffix) =>
            suffix.Split('.').All(identifier => identifier.Length > 0
                && identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n');

    private static bool LooksLikeVersion(string value) => value.Length > 0 && char.IsDigit(value[0]);

    /// <summary>Scoop-specific, wider than <see cref="LooksLikeVersion"/>: a digit
    /// anywhere, not only leading, plus scoop's own recognised non-numeric version words.</summary>
    private static readonly HashSet<string> ScoopVersionKeywords =
        new(StringComparer.OrdinalIgnoreCase) { "nightly", "current", "latest", "unknown", "head", "master", "main" };

    private static bool LooksLikeScoopVersion(string value) =>
        value.Length > 0 && (value.Any(char.IsDigit) || ScoopVersionKeywords.Contains(value));

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return string.Empty;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            _ => string.Empty,
        };
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
