using System.Globalization;
using System.Text;
using System.Text.Json;

namespace UltimateUpdater.Services.Sources;

public sealed record WingetUpgradeEntry(string Id, string Name, string InstalledVersion, string AvailableVersion);

/// <summary>
/// Parses the machine-facing winget outputs this app uses. Everything here is hostile input:
/// no exception may escape on malformed data, a bad row is skipped, not fatal.
/// </summary>
public static class WingetUpgradeParser
{
    /// <summary>
    /// Parses `Get-WinGetPackage | ConvertTo-Json` (official Microsoft.WinGet.Client module).
    /// Structured and locale-independent, the preferred path.
    /// </summary>
    public static IReadOnlyList<WingetUpgradeEntry> ParseModuleJson(string json)
    {
        var results = new List<WingetUpgradeEntry>();
        if (string.IsNullOrWhiteSpace(json))
            return results;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return results;
        }

        using (document)
        {
            // ConvertTo-Json emits a bare object when the result set has exactly one element
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray())
                    AddModuleEntry(results, element);
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                AddModuleEntry(results, document.RootElement);
            }
        }

        return results;
    }

    private static void AddModuleEntry(List<WingetUpgradeEntry> results, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        var id = GetString(element, "Id");
        if (string.IsNullOrWhiteSpace(id))
            return;

        var name = GetString(element, "Name");
        var installed = GetString(element, "InstalledVersion");
        var available = GetString(element, "AvailableVersion");

        results.Add(new WingetUpgradeEntry(
            id,
            string.IsNullOrWhiteSpace(name) ? id : name,
            installed,
            available));
    }

    private static string GetString(JsonElement element, string propertyName) =>
        JsonElementHelpers.GetString(element, propertyName);

    /// <summary>
    /// Fallback: parses the `winget upgrade` table. The header row is LOCALIZED, so column
    /// positions are derived from the ASCII separator line ("-----") rather than from header text,
    /// and long names may be truncated by winget itself, the Id column is the trustworthy key.
    /// Rows after a second separator ("explicit targeting required") are still real upgrades and
    /// are included.
    /// </summary>
    public static IReadOnlyList<WingetUpgradeEntry> ParseCliTable(string stdout, Action<string>? onTruncatedId = null)
    {
        var results = new List<WingetUpgradeEntry>();
        if (string.IsNullOrWhiteSpace(stdout))
            return results;

        var lines = stdout.Replace("\r\n", "\n").Split('\n');
        int[]? columnStarts = null;
        string? previousLine = null;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd();
            if (line.Length == 0)
            {
                previousLine = null;
                continue;
            }

            if (IsSeparator(line))
            {
                // winget draws one continuous dashed rule, so the separator itself carries no
                // column positions, they come from the header line above it. Only the POSITIONS
                // are used; the header words themselves are localized and never interpreted.
                columnStarts = previousLine is null ? null : FindColumnStarts(previousLine, BlockRows(lines, index + 1));
                previousLine = line;
                continue;
            }

            previousLine = line;

            if (columnStarts is null || columnStarts.Length < 4)
                continue;

            var name = Slice(line, columnStarts, 0);
            var id = Slice(line, columnStarts, 1);
            var installed = Slice(line, columnStarts, 2);
            var available = Slice(line, columnStarts, 3);

            // A real Id is one word: Publisher.Package, or a Microsoft Store product id with no dot at all.
            // This also filters the trailing summary lines.
            if (!IsPackageId(id) || available.Length == 0)
                continue;

            // winget shortens a column that does not fit with '…', the Id too; a shortened Id names no
            // package, and installing by it would fail or match something else.
            if (id.Contains('…'))
            {
                onTruncatedId?.Invoke(id);
                continue;
            }

            results.Add(new WingetUpgradeEntry(id, name.Length == 0 ? id : name, installed, available));
        }

        return results;
    }

    /// <summary>
    /// The re-check: `winget list --id &lt;id&gt; --exact` -> Name/Id/Version/Source, no "Available"
    /// column at all once the package is fully up to date, so this cannot reuse <see cref="ParseCliTable"/>
    /// (which requires one). Same column-start technique, id column matched against
    /// <paramref name="expectedId"/> rather than trusted positionally: --exact already narrowed the
    /// search to at most one package, this only guards against a column-alignment guess going wrong.
    /// </summary>
    public static string? ParseListVersion(string stdout, string expectedId)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        var lines = stdout.Replace("\r\n", "\n").Split('\n');
        int[]? columnStarts = null;
        string? previousLine = null;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd();
            if (line.Length == 0)
            {
                previousLine = null;
                continue;
            }

            if (IsSeparator(line))
            {
                columnStarts = previousLine is null ? null : FindColumnStarts(previousLine, BlockRows(lines, index + 1));
                previousLine = line;
                continue;
            }

            previousLine = line;

            if (columnStarts is null || columnStarts.Length < 3)
                continue;

            var id = Slice(line, columnStarts, 1);
            if (!string.Equals(id, expectedId, StringComparison.OrdinalIgnoreCase))
                continue;

            var version = Slice(line, columnStarts, 2);
            return version.Length == 0 ? null : version;
        }

        return null;
    }

    private static bool IsPackageId(string id) =>
        id.Length > 0
        && !id.Any(char.IsWhiteSpace)
        && (id.Contains('.') || StoreProductId.IsMatch(id));

    /// <summary>A Microsoft Store product id: 12 upper-case letters or digits (9NKSQGP7F2NH), or "XP" and 12 more.</summary>
    private static readonly System.Text.RegularExpressions.Regex StoreProductId =
        new("^(?:[0-9A-Z]{12}|XP[0-9A-Z]{12})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsSeparator(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 4 && trimmed.All(c => c is '-' or ' ');
    }

    /// <summary>
    /// Column start offsets = the start of each run of non-space characters in the header line, counted in
    /// console CELLS, not UTF-16 units: winget aligns by cell width, so a CJK or full-width character
    /// (two cells, one or two chars) would otherwise shift every column behind it.
    /// Position-only, so it works regardless of the header's language.
    /// winget pads a column to its widest value plus ONE space, so a header label exactly as wide as its
    /// column ("Name Id      Version Source" when every name is shorter than "Name") is followed by a single
    /// space. A single space is also a gap inside a multi-word label (Korean "사용 가능"), so it only starts a
    /// new column when every row of the table under it has a blank cell there too. Only rows reaching the
    /// header's last label count as table rows: the "2 upgrades available." summary winget prints right
    /// under the table is not one, and must neither allow nor block a split.
    /// </summary>
    private static int[] FindColumnStarts(string header, IReadOnlyList<string> rows)
    {
        var lastLabelStart = LastLabelStart(header);
        rows = rows.Where(row => CellLength(row) > lastLabelStart).ToList();

        var starts = new List<int>();
        var spaceRun = 0;
        var cell = 0;
        var first = true; // true only while the header's very first character is still ahead

        foreach (var rune in header.EnumerateRunes())
        {
            var width = CellWidth(rune);

            if (Rune.IsWhiteSpace(rune))
            {
                spaceRun += width;
                cell += width;
                first = false;
                continue;
            }

            // Columns are padded apart by at least two spaces; a single space is just a gap inside
            // a multi-word header label (which some locales have), not a new column.
            if (first || spaceRun >= 2
                || (spaceRun == 1 && rows.Count > 0 && rows.All(row => IsBlankCell(row, cell - 1))))
                starts.Add(cell);

            first = false;
            spaceRun = 0;
            cell += width;
        }

        return starts.ToArray();
    }

    /// <summary>The data rows of the table whose separator is just before <paramref name="from"/>: up to a blank
    /// line, the next separator, or the header line just above the next separator.</summary>
    private static List<string> BlockRows(string[] lines, int from)
    {
        var rows = new List<string>();
        for (var i = from; i < lines.Length; i++)
        {
            var row = lines[i].TrimEnd();
            if (row.Length == 0 || IsSeparator(row) || (i + 1 < lines.Length && IsSeparator(lines[i + 1].TrimEnd())))
                break;
            rows.Add(row);
        }

        return rows;
    }

    private static int CellLength(string text)
    {
        var cells = 0;
        foreach (var rune in text.EnumerateRunes())
            cells += CellWidth(rune);
        return cells;
    }

    /// <summary>The console cell where the header's last non-space run starts.</summary>
    private static int LastLabelStart(string header)
    {
        var cell = 0;
        var last = 0;
        var previousWasSpace = true;
        foreach (var rune in header.EnumerateRunes())
        {
            var isSpace = Rune.IsWhiteSpace(rune);
            if (!isSpace && previousWasSpace)
                last = cell;
            previousWasSpace = isSpace;
            cell += CellWidth(rune);
        }

        return last;
    }

    /// <summary>True when the row has white space, or nothing at all, at console cell <paramref name="target"/>.</summary>
    private static bool IsBlankCell(string row, int target)
    {
        var cell = 0;
        foreach (var rune in row.EnumerateRunes())
        {
            var width = CellWidth(rune);
            if (cell + width > target)
                return Rune.IsWhiteSpace(rune);
            cell += width;
        }

        return true;
    }

    /// <summary>Console cell width of one character: 0 for combining and format marks, 2 for the East Asian
    /// wide and full-width blocks, 1 for everything else (the ellipsis winget appends included).</summary>
    private static int CellWidth(Rune rune)
    {
        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.Format:
                return 0;
        }

        var value = rune.Value;
        return value is (>= 0x1100 and <= 0x115F)
            or (>= 0x2E80 and <= 0x303E)
            or (>= 0x3041 and <= 0x33FF)
            or (>= 0x3400 and <= 0x4DBF)
            or (>= 0x4E00 and <= 0x9FFF)
            or (>= 0xA000 and <= 0xA4CF)
            or (>= 0xAC00 and <= 0xD7A3)
            or (>= 0xF900 and <= 0xFAFF)
            or (>= 0xFE30 and <= 0xFE4F)
            or (>= 0xFF00 and <= 0xFF60)
            or (>= 0xFFE0 and <= 0xFFE6)
            or (>= 0x20000 and <= 0x3FFFD)
            ? 2
            : 1;
    }

    /// <summary>
    /// The text of one column. The row is walked by characters and cut at the first one that STARTS at or
    /// past the next column's cell offset, so a wide character straddling a boundary belongs to the column
    /// it starts in.
    /// </summary>
    private static string Slice(string line, int[] starts, int index)
    {
        if (index >= starts.Length)
            return string.Empty;

        var start = starts[index];
        var end = index + 1 < starts.Length ? starts[index + 1] : int.MaxValue;
        var result = new StringBuilder();
        var cell = 0;

        foreach (var rune in line.EnumerateRunes())
        {
            if (cell >= end)
                break;

            if (cell >= start)
                result.Append(rune.ToString());

            cell += CellWidth(rune);
        }

        return result.ToString().Trim();
    }

    /// <summary>
    /// `winget show --id &lt;id&gt; --exact` → the release date, when the manifest has one. Unlike
    /// <see cref="ParseCliTable"/> this is a plain label:value list, not a table, there is no position
    /// trick to fall back on, and winget prints the labels in the OS display language. Only labels
    /// actually verified are matched: "Release Date:" (English, winget's own documented convention) and
    /// "Freigabedatum:" (German, captured from a real winget install). Every other
    /// language yields null, never guessed. No installer-size field exists anywhere in this output
    /// (checked live against several real packages the same session): `winget show` simply does not
    /// expose one, so enrichment from this source is date-only.
    /// </summary>
    public static DateTimeOffset? ParseShowReleaseDate(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        foreach (var rawLine in stdout.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();

            foreach (var label in ReleaseDateLabels)
            {
                if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = line[label.Length..].Trim();
                if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                    return date;
            }
        }

        return null;
    }

    private static readonly string[] ReleaseDateLabels = ["Release Date:", "Freigabedatum:"];

    private static readonly string[] ReleaseNotesLabels = ["Release Notes:", "Versionshinweise:"];
    private static readonly string[] ReleaseNotesUrlLabels = ["Release Notes Url:", "URL der Versionshinweise:"];

    /// <summary>
    /// `winget show`'s optional "Release Notes" field, an unindented "Label:" line, its value on the
    /// indented lines below (real captured shape, both English and German: `Beschreibung`/
    /// `Versionshinweise` follow the exact same two-space-indented-continuation convention). Most
    /// packages carry neither field at all, since the manifest author has to fill it in, that is a real
    /// "nothing to show" answer, not a parse failure.
    /// </summary>
    public static (string? Body, string? Url) ParseShowReleaseNotes(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return (null, null);

        var lines = stdout.Replace("\r\n", "\n").Split('\n');
        string? url = null;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.Trim();
            foreach (var label in ReleaseNotesUrlLabels)
            {
                if (trimmed.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                {
                    url = trimmed[label.Length..].Trim();
                    break;
                }
            }
        }

        var bodyLines = new List<string>();
        var collecting = false;

        foreach (var rawLine in lines)
        {
            if (collecting)
            {
                // An unindented line ends the field, either a new "Label:" or the output simply moving
                // on. A blank line inside the notes themselves is real formatting and is kept.
                if (rawLine.Length > 0 && !char.IsWhiteSpace(rawLine[0]))
                    break;

                bodyLines.Add(rawLine.Length >= 2 ? rawLine[2..] : rawLine.TrimStart());
                continue;
            }

            var trimmed = rawLine.Trim();
            if (ReleaseNotesLabels.Any(label => trimmed.Equals(label, StringComparison.OrdinalIgnoreCase)))
                collecting = true;
        }

        var body = bodyLines.Count > 0 ? string.Join('\n', bodyLines).Trim() : null;
        return (string.IsNullOrEmpty(body) ? null : body, string.IsNullOrEmpty(url) ? null : url);
    }

    /// <summary>
    /// `winget show --id &lt;id&gt; --exact --versions` → every version winget knows about, newest first
    /// (real captured shape: a "Found …" line, a one-column header, a dashed separator, then
    /// one version per line). Locale-independent like <see cref="ParseCliTable"/>: only the separator
    /// line's POSITION is used, everything below it, one version per line, until a blank line or the
    /// output ends. The (localized) header text above the separator is never read.
    /// </summary>
    public static IReadOnlyList<string> ParseVersionsList(string stdout)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(stdout))
            return results;

        var pastSeparator = false;

        foreach (var rawLine in stdout.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();

            if (!pastSeparator)
            {
                if (IsSeparator(line))
                    pastSeparator = true;
                continue;
            }

            if (line.Length == 0)
                break;

            results.Add(line);
        }

        return results;
    }
}
