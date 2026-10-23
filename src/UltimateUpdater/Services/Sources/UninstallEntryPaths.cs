namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Turning the path values in Windows' uninstall registry into paths that can actually be used. Both the
/// launcher lookup and the install-location lookup read the same keys and hit the same two quirks, so the
/// rules live here rather than being written out twice.
/// </summary>
internal static class UninstallEntryPaths
{
    /// <summary>Registry paths arrive quoted and padded often enough to be worth undoing.</summary>
    public static string Clean(string value) => value.Trim().Trim('"').Trim();

    /// <summary>Turns "C:\...\app.exe,0" back into a path; an icon index may be negative.</summary>
    public static string StripIconIndex(string value)
    {
        var comma = value.LastIndexOf(',');
        if (comma <= 0)
            return value;

        var index = value[(comma + 1)..].Trim().TrimStart('-');
        return index.Length > 0 && index.All(char.IsDigit) ? value[..comma] : value;
    }
}
