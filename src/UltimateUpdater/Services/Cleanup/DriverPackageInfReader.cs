using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>One [Version] section's worth of facts read out of an oem*.inf file.</summary>
public sealed record DriverInfVersionInfo(string Provider, string ClassName, string? DriverVersion, DateTimeOffset? DriverDate);

/// <summary>
/// Reads the plain facts out of a third-party driver package's .inf file: no SetupAPI call needed for this
/// part, it is a text file. Pure over the decoded text (<see cref="Parse"/>), so a unit test drives every
/// branch (UTF-16, ANSI, %string% tokens, a missing field) from a literal string; only <see
/// cref="DecodeInfBytes"/> touches the OS (the machine's own ANSI code page for the non-Unicode case, same
/// P/Invoke shape as <see cref="OemTextRecovery"/>).
/// </summary>
public static class DriverPackageInfReader
{
    /// <summary>The app's own 11 supported UI cultures (see <see cref="LocalizationService.Apply"/>), mapped
    /// to the hex LANGID a driver INF's own <c>[Strings.xxxx]</c> section uses for that same language. Used
    /// only as the SECOND priority behind the neutral <c>[Strings]</c> section, and only for the app's own
    /// current language - never any other locale's section, so a %token% never surfaces text in a language
    /// the user never chose.</summary>
    private static readonly IReadOnlyDictionary<string, string> AppLanguageToInfLangId =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "0409",
            ["de"] = "0407",
            ["es"] = "040a",
            ["it"] = "0410",
            ["pt"] = "0416",
            ["pl"] = "0415",
            ["zh-Hans"] = "0804",
            ["ja"] = "0411",
            ["nl"] = "0413",
            ["tr"] = "041f",
            ["ro"] = "0418",
        };

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int MultiByteToWideChar(
        uint codePage, uint flags, byte[] bytes, int byteCount, char[]? chars, int charCount);

    private static readonly Regex SectionHeader = new(@"^\s*\[(?<name>[^\]]+)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex KeyValue = new(@"^\s*(?<key>[^;=\s][^=]*?)\s*=\s*(?<value>.*?)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Decodes a whole .inf file's raw bytes into text. An INF is either Unicode (a leading UTF-16LE BOM,
    /// occasionally UTF-8) or the machine's own single-byte ANSI code page; guessing wrong on a driver
    /// vendor's own provider name is how a broken accent ends up on screen, never a crash, so this never
    /// throws.
    /// </summary>
    public static string DecodeInfBytes(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        var codePage = GetACP();
        var charCount = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, null, 0);
        if (charCount <= 0)
            return Encoding.Latin1.GetString(bytes);

        var chars = new char[charCount];
        var written = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, chars, charCount);
        return written <= 0 ? Encoding.Latin1.GetString(bytes) : new string(chars, 0, written);
    }

    /// <summary>
    /// Reads Provider/Class/DriverVer out of already-decoded INF text. <c>%token%</c> values are resolved
    /// against <see cref="BuildResolutionStrings"/>'s merged table (case-insensitive, quotes stripped): the
    /// neutral [Strings] section first, then the app's own UI-culture section, then [Strings.0409], never
    /// another locale's; an unresolvable token is returned as-is rather than blanked, so a display still
    /// shows something rather than nothing. Returns null when there is no [Version] section at all, an INF
    /// this malformed is not one this category should ever have found in the driver store in the first place.
    /// </summary>
    public static DriverInfVersionInfo? Parse(string infText)
    {
        var sections = SplitSections(infText);
        if (!sections.TryGetValue("version", out var versionLines))
            return null;

        var values = ReadKeyValues(versionLines);
        var strings = BuildResolutionStrings(sections);

        var provider = Resolve(values.GetValueOrDefault("Provider"), strings);
        var className = Resolve(values.GetValueOrDefault("Class"), strings);

        var driverVerRaw = values.GetValueOrDefault("DriverVer");
        var (date, version) = ParseDriverVer(driverVerRaw);

        return new DriverInfVersionInfo(provider ?? string.Empty, className ?? string.Empty, version, date);
    }

    private static (DateTimeOffset? Date, string? Version) ParseDriverVer(string? driverVer)
    {
        if (string.IsNullOrWhiteSpace(driverVer))
            return (null, null);

        // An inline comment ("DriverVer=01/18/2023,1.2.3.4 ; note") would otherwise end up in the version.
        var comment = driverVer.IndexOf(';');
        if (comment >= 0)
            driverVer = driverVer[..comment];

        var parts = driverVer.Split(',', 2, StringSplitOptions.TrimEntries);
        DateTimeOffset? date = null;
        // "M/d/yyyy" accepts both "6/21/2006" and "06/21/2006"; INF files use either.
        if (parts.Length > 0
            && DateTime.TryParseExact(parts[0], "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            date = new DateTimeOffset(parsed, TimeSpan.Zero);

        var version = parts.Length > 1 ? parts[1] : null;
        return (date, version);
    }

    /// <summary>
    /// The string table a %token% resolves against, built with a strict priority so a foreign-language
    /// driver INF never surfaces text in a language the user never picked: the neutral <c>[Strings]</c>
    /// section always wins, then the app's own current UI-culture section (never any other locale's), then
    /// <c>[Strings.0409]</c> (en-US) as a last resort so a token still resolves to SOME language rather
    /// than a raw <c>%token%</c>. Each later section only fills in keys the earlier ones left unresolved.
    /// </summary>
    private static Dictionary<string, string> BuildResolutionStrings(Dictionary<string, List<string>> sections)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void MergeSection(string sectionName)
        {
            if (!sections.TryGetValue(sectionName, out var lines))
                return;

            foreach (var pair in ReadKeyValues(lines))
                result.TryAdd(pair.Key, pair.Value);
        }

        MergeSection("Strings");

        // The system-language default keeps the specific culture ("de-DE"), so its parent is tried too.
        var uiCulture = CultureInfo.CurrentUICulture;
        if (AppLanguageToInfLangId.TryGetValue(uiCulture.Name, out var appLangId)
            || AppLanguageToInfLangId.TryGetValue(uiCulture.Parent.Name, out appLangId))
            MergeSection($"Strings.{appLangId}");

        MergeSection("Strings.0409");

        return result;
    }

    private static string? Resolve(string? rawValue, IReadOnlyDictionary<string, string> strings)
    {
        if (rawValue is null)
            return null;

        var trimmed = Strip(rawValue);
        if (trimmed.StartsWith('%') && trimmed.EndsWith('%') && trimmed.Length > 2)
        {
            var token = trimmed[1..^1];
            return strings.TryGetValue(token, out var resolved) ? Strip(resolved) : trimmed;
        }

        return trimmed;
    }

    private static string Strip(string value) => value.Trim().Trim('"');

    private static Dictionary<string, List<string>> SplitSections(string text)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var headerMatch = SectionHeader.Match(line);
            if (headerMatch.Success)
            {
                current = new List<string>();
                sections[headerMatch.Groups["name"].Value.Trim()] = current;
                continue;
            }

            current?.Add(line);
        }

        return sections;
    }

    private static Dictionary<string, string> ReadKeyValues(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith(';'))
                continue;

            var match = KeyValue.Match(line);
            if (match.Success)
                values[match.Groups["key"].Value.Trim()] = match.Groups["value"].Value;
        }

        return values;
    }
}
