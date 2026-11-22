using UltimateUpdater.Services.Formatting;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// How the leftover scans compare names. A name matches another only when both are EXACTLY equal after the
/// normalisation (case, spaces and punctuation gone), never when one merely contains the other: "Notepad"
/// must not claim "NotepadPlusPlus" and "Chrome" must not claim "ChromeRemoteDesktop". Names that are too
/// short or too common to identify a program never match at all.
/// </summary>
internal static class LeftoverNames
{
    /// <summary>The shortest normalised name that may identify a program.</summary>
    internal const int MinimumLength = 3;

    /// <summary>
    /// Names (normalised) that belong to Windows or are a generic container word, so they say nothing about
    /// whose folder it is. A folder, key or publisher with one of these names is never matched and never
    /// offered, whatever program was removed. Cloud sync folders hold the user's own files and are listed
    /// for the same reason.
    /// </summary>
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "microsoft", "windows", "windowsapps", "packages", "package", "packagecache", "temp", "tmp", "programs",
        "program", "programfiles", "commonfiles", "common", "shared", "data", "appdata", "apps", "app", "setup",
        "install", "installer", "installers", "update", "updates", "updater", "cache", "caches", "logs", "log",
        "config", "settings", "default", "system", "systemdata", "users", "user", "public", "classes", "policies",
        "software", "wow6432node", "desktop", "documents", "downloads", "plugins", "plugin", "tools", "tool",
        "runtime", "runtimes", "library", "libraries", "resources", "assets", "content", "storage", "services",
        "service", "drivers", "driver", "crashdumps", "crashreports", "diagnostics", "history", "local",
        "locallow", "roaming", "onedrive", "dropbox", "googledrive", "icloud", "sync", "backup", "backups",
        // This app's own folders (the data folder may sit anywhere), then folders Windows keeps beside the programs.
        "ultimateupdater", "windowsupdater",
        "windowsdefender", "windowsmail", "windowsmediaplayer", "windowsnt", "windowsphotoviewer",
        "windowspowershell", "windowssidebar", "windowsportabledevices", "windowsmultimediaplatform",
        "internetexplorer", "modifiablewindowsapps", "uninstallinformation", "msbuild", "referenceassemblies",
        "dotnet", "microsoftedge", "microsoftedgecore", "microsoftedgewebview", "edge", "edgeupdate",
    };

    private static readonly string[] CompanySuffixes =
    [
        "inc", "incorporated", "corp", "corporation", "ltd", "limited", "llc", "gmbh", "co", "company", "ag", "sa",
        "bv", "srl", "plc", "pty", "oy", "ab", "kg", "ug", "sarl", "spa", "nv", "as", "aps", "pvt", "lp", "llp",
    ];

    internal static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : TextNormalization.AlphanumericLower(value);

    /// <summary>True when the normalised name may identify a program at all.</summary>
    internal static bool IsUsable(string normalized) =>
        normalized.Length >= MinimumLength && !Generic.Contains(normalized);

    internal static bool IsGeneric(string normalized) => Generic.Contains(normalized);

    /// <summary>
    /// The normalised spellings a publisher folder may carry: the full name and the name without trailing
    /// company words ("Contoso Software GmbH" and "Contoso"). Unusable (generic) forms are left out, so
    /// "Microsoft Corporation" yields nothing and its folders are never touched.
    /// </summary>
    internal static IReadOnlyList<string> PublisherForms(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
            return [];

        var forms = new List<string>();
        var full = Normalize(publisher);
        if (IsUsable(full))
            forms.Add(full);

        var words = publisher
            .Split([' ', '\t', ',', '.', '-', '_', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (words.Count > 0 && string.Equals(words[0], "the", StringComparison.OrdinalIgnoreCase))
            words.RemoveAt(0);

        while (words.Count > 1 && CompanySuffixes.Contains(Normalize(words[^1]), StringComparer.Ordinal))
            words.RemoveAt(words.Count - 1);

        var stripped = Normalize(string.Concat(words));

        // A publisher that is only generic once its company words are gone ("Microsoft Corporation") offers
        // no form at all, not even the full spelling.
        if (!IsUsable(stripped))
            return [];

        if (!forms.Contains(stripped))
            forms.Add(stripped);

        return forms;
    }
}
