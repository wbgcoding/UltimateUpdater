using System.IO;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// Where a program comes from when it came through a game launcher, so two entries with the same name (the same game
/// installed through Steam and through the EA app) can be told apart in the list. The launcher is recognised from the
/// entry's key and its own uninstall command or folder, by fixed patterns, never by the name. An entry that matches none
/// has no origin. Reading only: nothing here removes or runs anything.
/// </summary>
public static class UninstallEntryOrigin
{
    public const string Steam = "Steam";
    public const string EaApp = "EA app";
    public const string Epic = "Epic Games";
    public const string Gog = "GOG";
    public const string Ubisoft = "Ubisoft Connect";
    public const string BattleNet = "Battle.net";

    private const int ShortFolderLength = 36;

    /// <summary>The launcher label of an entry, or null. Only a classic program has one.</summary>
    public static string? Label(InstalledProgramEntry entry)
    {
        if (entry.Kind != UninstallEntryKind.Program)
            return null;

        var label = LabelOf(entry);

        // The launcher's own entry is no game from that launcher.
        return label is not null && LauncherClients.Contains(entry.Name) ? null : label;
    }

    /// <summary>The Steam app id of a game entry (its key is named "Steam App &lt;id&gt;"); null for every other entry.</summary>
    public static string? SteamAppId(InstalledProgramEntry entry)
    {
        var leaf = KeyLeaf(entry.Key);
        const string prefix = "Steam App ";
        return leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && leaf.Length > prefix.Length && leaf[prefix.Length..].All(char.IsAsciiDigit)
            ? leaf[prefix.Length..]
            : null;
    }

    private static readonly HashSet<string> LauncherClients = new(StringComparer.OrdinalIgnoreCase)
    {
        "Steam", "Epic Games Launcher", "GOG GALAXY", "Ubisoft Connect", "Ubisoft Game Launcher", "Battle.net", "EA app", "EA Desktop", "Origin",
    };

    private static string? LabelOf(InstalledProgramEntry entry)
    {
        var leaf = KeyLeaf(entry.Key);

        // Steam writes one key per game, named after the app id.
        if (leaf.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)
            && leaf.Length > "Steam App ".Length
            && leaf["Steam App ".Length..].All(char.IsAsciiDigit))
        {
            return Steam;
        }

        // Ubisoft Connect: "Uplay Install <id>".
        if (leaf.StartsWith("Uplay Install ", StringComparison.OrdinalIgnoreCase))
            return Ubisoft;

        var commands = string.Join('\n', entry.UninstallString, entry.QuietUninstallString, entry.DisplayIcon).ToLowerInvariant();
        var folder = entry.InstallLocation.ToLowerInvariant();

        if (commands.Contains("eainstaller") || commands.Contains(@"\ea desktop\") || InFolder(folder, "ea games"))
            return EaApp;

        if (commands.Contains("epicgameslauncher") || commands.Contains("com.epicgames.launcher:") || InFolder(folder, "epic games"))
            return Epic;

        if (commands.Contains(@"\gog galaxy\") || commands.Contains(@"\gog games\") || InFolder(folder, "gog games") || InFolder(folder, "gog galaxy")
            || entry.Publisher.Equals("GOG.com", StringComparison.OrdinalIgnoreCase))
        {
            return Gog;
        }

        if (commands.Contains(@"\ubisoft game launcher\") || commands.Contains(@"\ubisoft connect\") || InFolder(folder, "ubisoft game launcher"))
            return Ubisoft;

        if (commands.Contains(@"battle.net\agent") || (commands.Contains("--game=") && commands.Contains("battle.net")))
            return BattleNet;

        return null;
    }

    /// <summary>Whether a folder path has the named folder as one of its own parts ("D:\EA Games\Apex" has "ea games").</summary>
    private static bool InFolder(string lowerPath, string folderName) =>
        lowerPath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).Any(part => part == folderName);

    private static string KeyLeaf(string key)
    {
        var at = key.LastIndexOf('\\');
        return at < 0 ? key : key[(at + 1)..];
    }

    /// <summary>The install folder as the row shows it: short paths whole, long ones as the last two folders behind an ellipsis.
    /// Empty when the entry has no folder or is not a classic program.</summary>
    public static string ShortFolder(InstalledProgramEntry entry)
    {
        if (entry.Kind != UninstallEntryKind.Program)
            return string.Empty;

        var path = entry.InstallLocation.Trim().TrimEnd('\\', '/');
        if (path.Length <= ShortFolderLength)
            return path;

        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? path : "\u2026\\" + string.Join('\\', parts[^2..]);
    }

    /// <summary>The Steam entries whose install folder exists but holds no file: the game is most likely not installed any more.
    /// Each such entry comes back with <see cref="InstalledProgramEntry.InstallFolderEmpty"/> set; the others come back as they are.
    /// The folder test runs here, in the read, never when the row is drawn.</summary>
    public static IReadOnlyList<InstalledProgramEntry> MarkEmptyFolders(
        IReadOnlyList<InstalledProgramEntry> entries, Func<string, bool> isEmptyFolder)
    {
        var result = new List<InstalledProgramEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var empty = Label(entry) == Steam && entry.InstallLocation.Length > 0 && SafeIsEmpty(isEmptyFolder, entry.InstallLocation);
            result.Add(empty == entry.InstallFolderEmpty ? entry : entry with { InstallFolderEmpty = empty });
        }

        return result;
    }

    private static bool SafeIsEmpty(Func<string, bool> isEmptyFolder, string path)
    {
        try
        {
            return isEmptyFolder(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>True when the folder exists and no file lies anywhere under it. A folder that cannot be read counts as not empty.</summary>
    public static bool IsEmptyFolder(string path)
    {
        try
        {
            return Directory.Exists(path)
                   && !Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false })
                       .Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
