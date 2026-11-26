using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>One decoded program start of Windows' own usage log: the program's full path and when it was last run (UTC).</summary>
public sealed record UserAssistRecord(string Path, DateTime LastUsedUtc);

/// <summary>
/// When a program was last used, from what Windows already records on this PC: the per-user "UserAssist" start log (every program
/// the user started from Explorer, the Start menu or a shortcut) and, for Steam games, the "LastPlayed" stamp of the game's manifest.
/// Local reads only, nothing leaves the PC. A program with no entry has no usage data (it is never called unused).
/// The decoding and the matching are pure and tested; <see cref="Read"/> is the only part that touches the registry and the disk.
/// </summary>
public static partial class ProgramUsageReader
{
    private const string UserAssistKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";

    /// <summary>The two UserAssist groups: shortcut starts and executable starts.</summary>
    private static readonly string[] UserAssistGroups =
    [
        "{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}",
        "{F4E57C4B-2036-496C-A9B8-B9E3F11F96F2}",
    ];

    /// <summary>Size of a UserAssist value on Windows 7 and later; the last-run FILETIME sits at offset 60.</summary>
    private const int FileTimeOffset = 60;

    /// <summary>The last use (UTC) per entry key, for every entry with usage data. Blocking: call it off the UI thread.</summary>
    public static IReadOnlyDictionary<string, DateTime> Read(IReadOnlyList<InstalledProgramEntry> entries, CancellationToken cancellationToken = default)
    {
        var records = ReadUserAssist(cancellationToken);
        var result = new Dictionary<string, DateTime>(Match(entries, records), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SteamManifestPath(entry) is not { } manifest)
                continue;

            try
            {
                if (ParseSteamLastPlayed(File.ReadAllText(manifest)) is { } played
                    && (!result.TryGetValue(entry.Key, out var known) || played > known))
                {
                    result[entry.Key] = played;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // no manifest or not readable: no usage data from Steam for this game
            }
        }

        return result;
    }

    private static List<UserAssistRecord> ReadUserAssist(CancellationToken cancellationToken)
    {
        var records = new List<UserAssistRecord>();
        foreach (var group in UserAssistGroups)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{UserAssistKey}\{group}\Count");
                if (key is null)
                    continue;

                foreach (var name in key.GetValueNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (key.GetValue(name) is byte[] data && DecodeUserAssist(name, data) is { } record)
                        records.Add(record);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                // the log is unreadable: no usage data, so nothing is called unused
            }
        }

        return records;
    }

    /// <summary>
    /// Decodes one UserAssist value: the name is ROT13 text, usually "{known folder id}\relative path" or a full path; the data carries the
    /// last run as a FILETIME at offset 60. Null for a value that is not a program start (the session counter, too short, no time).
    /// </summary>
    /// <param name="knownFolder">Resolves a known-folder id (without braces, upper case) to its path; defaults to this PC's folders.</param>
    public static UserAssistRecord? DecodeUserAssist(string rot13Name, byte[] data, Func<string, string?>? knownFolder = null)
    {
        if (data.Length < FileTimeOffset + sizeof(long) || string.IsNullOrWhiteSpace(rot13Name))
            return null;

        var fileTime = BitConverter.ToInt64(data, FileTimeOffset);
        if (fileTime <= 0 || fileTime > DateTime.MaxValue.ToFileTimeUtc())
            return null;

        var path = ExpandKnownFolder(Rot13(rot13Name), knownFolder ?? KnownFolderPath);
        if (path.StartsWith("UEME_", StringComparison.Ordinal))
            return null;

        return new UserAssistRecord(path, DateTime.FromFileTimeUtc(fileTime));
    }

    /// <summary>ROT13 over the Latin letters; every other character stays.</summary>
    public static string Rot13(string text) =>
        string.Create(text.Length, text, static (buffer, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var ch = source[i];
                buffer[i] = ch switch
                {
                    >= 'a' and <= 'z' => (char)('a' + ((ch - 'a' + 13) % 26)),
                    >= 'A' and <= 'Z' => (char)('A' + ((ch - 'A' + 13) % 26)),
                    _ => ch,
                };
            }
        });

    /// <summary>Replaces a leading "{known folder id}" with that folder's path; any other text (and an unknown id) stays as it is.</summary>
    private static string ExpandKnownFolder(string name, Func<string, string?> knownFolder)
    {
        if (name.Length < 40 || name[0] != '{' || name[37] != '}' || name[38] != '\\')
            return name;

        var folder = knownFolder(name.Substring(1, 36).ToUpperInvariant());
        return string.IsNullOrEmpty(folder) ? name : Path.Combine(folder, name[39..]);
    }

    private static string? KnownFolderPath(string id) => id switch
    {
        "6D809377-6AF0-444B-8957-A3773F02200E" => Environment.GetEnvironmentVariable("ProgramW6432") ?? Folder(Environment.SpecialFolder.ProgramFiles),
        "7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E" => Folder(Environment.SpecialFolder.ProgramFilesX86),
        "905E63B6-C1BF-494E-B29C-65B732D3D21A" => Folder(Environment.SpecialFolder.ProgramFiles),
        "F38BF404-1D43-42F2-9305-67DE0B28FC23" => Folder(Environment.SpecialFolder.Windows),
        "1AC14E77-02E7-4E5D-B744-2EB1AE5198B7" => Folder(Environment.SpecialFolder.System),
        "D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27" => Folder(Environment.SpecialFolder.SystemX86),
        "F1B32785-6FBA-4FCF-9D55-7B8E7F157091" => Folder(Environment.SpecialFolder.LocalApplicationData),
        "3EB685DB-65F9-4CF6-A03A-E3EF65729F3D" => Folder(Environment.SpecialFolder.ApplicationData),
        "5E6C858F-0E22-4760-9AFE-EA3317B67173" => Folder(Environment.SpecialFolder.UserProfile),
        _ => null,
    };

    private static string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

    /// <summary>
    /// Matches each program to its newest start: every record whose path lies under the program's install folder counts, the latest wins.
    /// A program without an install folder, or without a record, is not in the result.
    /// </summary>
    public static IReadOnlyDictionary<string, DateTime> Match(IReadOnlyList<InstalledProgramEntry> entries, IReadOnlyList<UserAssistRecord> records)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Kind != UninstallEntryKind.Program || string.IsNullOrWhiteSpace(entry.InstallLocation))
                continue;

            var folder = entry.InstallLocation.Trim().Trim('"').TrimEnd('\\', '/') + "\\";
            DateTime? latest = null;
            foreach (var record in records)
            {
                if (record.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && (latest is null || record.LastUsedUtc > latest))
                    latest = record.LastUsedUtc;
            }

            if (latest is { } found)
                result[entry.Key] = found;
        }

        return result;
    }

    /// <summary>The manifest of a Steam game next to the library's "common" folder, or null for any other entry.</summary>
    public static string? SteamManifestPath(InstalledProgramEntry entry)
    {
        if (UninstallEntryOrigin.SteamAppId(entry) is not { Length: > 0 } appId || string.IsNullOrWhiteSpace(entry.InstallLocation))
            return null;

        var common = Path.GetDirectoryName(entry.InstallLocation.Trim().TrimEnd('\\', '/'));
        var steamapps = common is null ? null : Path.GetDirectoryName(common);
        if (steamapps is null
            || !string.Equals(Path.GetFileName(common), "common", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(steamapps), "steamapps", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.Combine(steamapps, $"appmanifest_{appId}.acf");
    }

    /// <summary>The "LastPlayed" stamp (Unix seconds) of a Steam manifest as a UTC time; null when it is missing or 0 (never played).</summary>
    public static DateTime? ParseSteamLastPlayed(string manifest)
    {
        var match = LastPlayedPattern().Match(manifest);
        if (!match.Success
            || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds <= 0
            || seconds > 253_402_300_799)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }

    [GeneratedRegex("\"LastPlayed\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LastPlayedPattern();
}
