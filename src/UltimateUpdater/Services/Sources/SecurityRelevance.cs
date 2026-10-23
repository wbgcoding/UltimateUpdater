namespace UltimateUpdater.Services.Sources;

/// <summary>
/// What earns an update the "critical" mark, and therefore what the critical-only filter shows.
/// Two independent verdicts, never mixed:
/// * Windows Update rates its own updates (MSRC severity): that classification is taken as it comes;
/// * every other source is matched against the explicit table below.
/// The table lists software whose holes are attacked in the wild within days and which sits directly
/// on the network or on downloaded input: browsers, the tools that hold secrets or guard the machine,
/// and the runtimes that execute code from elsewhere. It is a table rather than a similarity search
/// for the same reason BrandIcons is one: a badge on the wrong row teaches the user to ignore the
/// badge, which is worse than a row that never gets one. Archivers, media players, editors and games
/// are deliberately absent; 7-Zip being a version behind is not what this filter is for.
/// To extend it: add the identifier NORMALIZED (letters and digits only, lower case): the id a
/// package manager uses, or the product half of a winget id. Never add a word that is also an
/// ordinary part of some other product's name: matching is exact, and it must stay that way.
/// </summary>
public static class SecurityRelevance
{
    private static readonly HashSet<string> Identifiers = new(StringComparer.Ordinal)
    {
        // Browsers: the biggest attack surface on a desktop, and patched on their own fast schedule.
        "firefox", "mozillafirefox", "firefoxesr", "firefoxdeveloperedition",
        "chrome", "googlechrome", "chromium",
        "microsoftedge",
        "brave", "bravebrowser",
        "opera", "operagx",
        "vivaldi",
        "torbrowser",

        // VPN, secret and anti-malware tools; stale, they leak exactly what they were installed to guard.
        "openvpn", "openvpnconnect",
        "wireguard",
        "malwarebytes",
        "keepass", "keepassxc",
        "bitwarden",

        // Runtimes that execute code from elsewhere.
        "java", "javaruntime", "javaruntimeenvironment", "jre", "jdk", "openjdk", "temurin",
        "openssl",
        "node", "nodejs", "nodejslts",
        "python", "python3",
        "dotnet", "dotnetsdk", "dotnetruntime", "dotnetdesktopruntime",
        "php",
    };

    /// <summary>Words that name the maker, not the product.</summary>
    private static readonly string[] VendorWords = ["mozilla", "google", "microsoft", "oracle", "apache", "the"];

    /// <summary>
    /// True when the update is for software from the table. Conservative by design: an identifier that
    /// is not listed is ordinary software, and ordinary software gets no badge.
    /// </summary>
    public static bool IsSecurityRelevant(string? itemId, string? name)
    {
        foreach (var candidate in Candidates(itemId, name))
        {
            if (candidate.Length > 0 && Identifiers.Contains(candidate))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Windows Update's own classification. MSRC rates a security update Critical, Important, Moderate
    /// or Low; the top two are the ones Microsoft itself means by "install this now".
    /// </summary>
    public static bool IsCriticalSeverity(string? msrcSeverity) => msrcSeverity is "Critical" or "Important";

    private static IEnumerable<string> Candidates(string? itemId, string? name)
    {
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            // winget ids are Publisher.Product, sometimes with a version tail. Each part is compared on
            // its own, so "Oracle.JDK.21" is recognised by "jdk" without the table listing versions.
            yield return TextNormalization.AlphanumericLower(itemId);

            foreach (var segment in itemId.Split('.', StringSplitOptions.RemoveEmptyEntries))
                yield return TextNormalization.AlphanumericLower(segment);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            yield return TextNormalization.AlphanumericLower(name);

            // "Mozilla Firefox" and "Mozilla Firefox 129.0.1" both have to reach "firefox"
            var words = Words(name);
            yield return TextNormalization.AlphanumericLower(string.Concat(WithoutVendor(words)));
            yield return TextNormalization.AlphanumericLower(string.Concat(WithoutVersion(WithoutVendor(words))));
        }
    }

    private static string[] Words(string value) =>
        value.Split([' ', '\t', '-', '_'], StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<string> WithoutVendor(string[] words) =>
        words.Length > 1 && VendorWords.Contains(words[0].ToLowerInvariant())
            ? words.Skip(1)
            : words;

    private static IEnumerable<string> WithoutVersion(IEnumerable<string> words) =>
        words.Where(word => !IsVersion(word));

    private static bool IsVersion(string word) =>
        char.IsDigit(word[0]) || (word.Length > 1 && word[0] is 'v' or 'V' && char.IsDigit(word[1]));
}
