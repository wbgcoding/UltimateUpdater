using UltimateUpdater.Services.Sources.Windows;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>One row of `winget list`: the package id behind a name and version.</summary>
public sealed record WingetListRow(string Name, string Id, string Version);

/// <summary>
/// Reads one full `winget list` run and ties its ids to registry programs. A program gets an id only when a
/// row has exactly its name and exactly its version and names one id; a near match is no match, because the
/// id later goes onto a removal command line.
/// </summary>
public static class WingetListParser
{
    /// <summary>The rows of the table; a bad row is skipped, nothing escapes on malformed text.</summary>
    public static IReadOnlyList<WingetListRow> Parse(string stdout)
    {
        try
        {
            return WingetUpgradeParser.ParseListRows(stdout)
                .Select(row => new WingetListRow(row.Name, row.Id, row.Version))
                .ToList();
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// The programs, with <see cref="InstalledProgramEntry.WingetId"/> set where exactly one id has an exact
    /// name and version match. Store apps and package entries are left as they are.
    /// </summary>
    public static IReadOnlyList<InstalledProgramEntry> AttachIds(
        IReadOnlyList<InstalledProgramEntry> programs, IReadOnlyList<WingetListRow> rows)
    {
        if (rows.Count == 0)
            return programs;

        var byNameAndVersion = rows
            .GroupBy(row => (Name: row.Name.Trim(), Version: row.Version.Trim()), NameVersionComparer.Instance)
            .ToDictionary(group => group.Key,
                group => group.Select(row => row.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                NameVersionComparer.Instance);

        var result = new List<InstalledProgramEntry>(programs.Count);
        foreach (var program in programs)
        {
            if (program.Kind == UninstallEntryKind.Program
                && program.Version.Length > 0
                && byNameAndVersion.TryGetValue((program.Name.Trim(), program.Version.Trim()), out var ids)
                && ids.Count == 1)
                result.Add(program with { WingetId = ids[0] });
            else
                result.Add(program);
        }

        return result;
    }

    private sealed class NameVersionComparer : IEqualityComparer<(string Name, string Version)>
    {
        public static readonly NameVersionComparer Instance = new();

        public bool Equals((string Name, string Version) x, (string Name, string Version) y) =>
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Version, y.Version, StringComparison.Ordinal);

        public int GetHashCode((string Name, string Version) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name), value.Version.GetHashCode());
    }
}
