using System.Globalization;
using System.Text.RegularExpressions;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>Why an uninstaller entry got its level. Each value has a text under "UninstallRisk" + the value's name.</summary>
public enum UninstallRiskReason
{
    Own,
    Driver,
    Update,
    PackageManagerItself,
    Accessibility,
    DataLoss,
    Security,
    CompanyAccess,
    Runtime,
    Hardware,
    Virtualization,
    Microsoft,
    Remote,
    Sync,
    GamePlatform,
    InputMethod,
    DevRuntime,
    OemTool,
    NeededByOthers,
    StoreData,
    Interactive,
    Unknown,
    Incomplete,
    Ordinary,
}

/// <summary>
/// Every fact the rating may look at. All of it is untrusted text from the registry, a certificate or a tool, and
/// every field may be missing: a missing fact never lowers the level.
/// </summary>
public sealed record UninstallRiskInput
{
    public UninstallEntryKind Kind { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>Programs: the registry Publisher. Store apps: the certificate subject (the O= value, else CN=, is used).</summary>
    public string? Publisher { get; init; }

    public string? PackageFamilyName { get; init; }

    public string? SourceId { get; init; }

    public string? PackageId { get; init; }

    public UninstallScope Scope { get; init; } = UninstallScope.Machine;

    /// <summary>How the removal runs; null while it is not known yet.</summary>
    public UninstallMethod? Method { get; init; }

    /// <summary>The registry key's own name (the last segment of its path).</summary>
    public string? UninstallKeyName { get; init; }

    /// <summary>Optional; never required.</summary>
    public string? InstallLocation { get; init; }

    /// <summary>The Windows folder of this PC, passed in so the rating needs no I/O.</summary>
    public string? WindowsDirectory { get; init; }

    /// <summary>Packages only: true, false, or null when the manager cannot tell.</summary>
    public bool? RequiredByOthers { get; init; }
}

/// <summary>The rating of one entry: how much care it deserves, why, and whether "select all" may tick it.</summary>
public sealed record UninstallRisk(CleanupRiskLevel Level, UninstallRiskReason Reason, bool Selectable)
{
    /// <summary>The resource key of the reason text.</summary>
    public string ReasonKey => "UninstallRisk" + Reason;
}

/// <summary>
/// Rates an uninstaller entry. A pure function: no registry, no file system. The principle is fail safe, a dangerous
/// entry is never Safe and any doubt (a missing fact, an unknown publisher, an unclear package) raises the level.
/// The ordered rule tables live in <see cref="UninstallRiskRules"/>; the rule order and the vendor and name lists are
/// specified in docs/DESIGN.md, "Uninstaller risk rating".
/// </summary>
public static class UninstallRiskClassifier
{
    internal const string OwnUninstallKeyName = "{8C4F2A91-3B7D-4E5A-9C21-6D8E4F1B2A73}_is1";

    private const int MaxNameLength = 1024;

    private static readonly string[] MicrosoftPublisherIds = ["8wekyb3d8bbwe", "cw5n1h2txyewy"];

    public static UninstallRisk Classify(UninstallRiskInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var rated = Rate(input);
        return input.Method == UninstallMethod.Refused ? rated with { Selectable = false } : rated;
    }

    private static UninstallRisk Rate(UninstallRiskInput input)
    {
        // 1. This app's own entry (the installer's key, machine or user hive).
        if (IsOwnEntry(input.UninstallKeyName))
            return new UninstallRisk(CleanupRiskLevel.Considered, UninstallRiskReason.Own, false);

        // 2 and 3.
        if (input.Kind == UninstallEntryKind.Driver)
            return Risky(UninstallRiskReason.Driver);

        if (input.Kind == UninstallEntryKind.WindowsUpdate)
            return Risky(UninstallRiskReason.Update);

        // 4. The package id table.
        if (input.Kind == UninstallEntryKind.Package && UninstallRiskRules.RatePackage(input.SourceId, input.PackageId) is { } byPackage)
            return byPackage;

        var subject = Subject.From(input);

        // 5 to 21, first match wins.
        foreach (var step in UninstallRiskRules.Steps)
        {
            if (step(subject) is { } matched)
                return matched;
        }

        // 22.
        if (input.Kind == UninstallEntryKind.Package)
        {
            if (input.RequiredByOthers == true)
                return Considered(UninstallRiskReason.NeededByOthers);

            if (input.RequiredByOthers is null)
                return Considered(UninstallRiskReason.Incomplete);
        }

        // 23.
        if (input.Kind == UninstallEntryKind.StoreApp)
            return Considered(UninstallRiskReason.StoreData);

        if (input.Kind == UninstallEntryKind.Program)
        {
            // 24.
            if (input.Scope == UninstallScope.Machine && input.Method == UninstallMethod.Interactive)
                return Considered(UninstallRiskReason.Interactive);

            // 25.
            if ((input.Scope == UninstallScope.Machine && subject.PublisherTokens.Length == 0) || IsUnclearLocation(input))
                return Considered(UninstallRiskReason.Unknown);
        }

        // 26.
        if (!HasCompleteFacts(input, subject))
            return Considered(UninstallRiskReason.Incomplete);

        // 27.
        return new UninstallRisk(CleanupRiskLevel.Safe, UninstallRiskReason.Ordinary, true);
    }

    private static UninstallRisk Risky(UninstallRiskReason reason) => new(CleanupRiskLevel.Risky, reason, true);

    private static UninstallRisk Considered(UninstallRiskReason reason) => new(CleanupRiskLevel.Considered, reason, true);

    private static bool IsOwnEntry(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName))
            return false;

        var name = keyName.Trim();
        var cut = name.LastIndexOf('\\');
        return string.Equals(cut >= 0 ? name[(cut + 1)..] : name, OwnUninstallKeyName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Complete means: a program has a name, a publisher, a scope and a method; a Store app a name and a family name; a
    /// package a source, an id and a known dependent state. Anything else is rated for what is missing, never for Safe.
    /// </summary>
    private static bool HasCompleteFacts(UninstallRiskInput input, Subject subject)
    {
        if (subject.Name.Length == 0)
            return false;

        return input.Kind switch
        {
            UninstallEntryKind.Program => subject.PublisherTokens.Length > 0 && input.Method is not null,
            UninstallEntryKind.StoreApp => subject.Family.Length > 0,
            UninstallEntryKind.Package => !string.IsNullOrWhiteSpace(input.SourceId) && !string.IsNullOrWhiteSpace(input.PackageId) && input.RequiredByOthers is not null,
            _ => false,
        };
    }

    /// <summary>
    /// True when an install location is given and either sits inside the Windows folder or cannot be judged (relative,
    /// unexpanded variable, no Windows folder to compare with). No location at all is no evidence either way.
    /// </summary>
    private static bool IsUnclearLocation(UninstallRiskInput input)
    {
        var location = input.InstallLocation?.Trim().Trim('"');
        if (string.IsNullOrEmpty(location))
            return false;

        var windows = input.WindowsDirectory?.Trim().Trim('"');
        // A "~" may be an 8.3 short name ("WINDOW~1") that no string comparison can resolve: not judgeable, so raised.
        if (string.IsNullOrEmpty(windows) || location.Contains('%') || windows.Contains('%') || location.Contains('~') || windows.Contains('~'))
            return true;

        var folder = Canonical(location);
        var root = Canonical(windows);
        if (folder is null || root is null || root.Length == 0)
            return true;

        return string.Equals(folder, root, StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The absolute path without a trailing separator or a "\\?\" prefix; null when it is not an absolute path.</summary>
    private static string? Canonical(string path)
    {
        var text = path;
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal))
            text = text[4..];

        if (text.Contains('\0') || !System.IO.Path.IsPathFullyQualified(text))
            return null;

        try
        {
            return System.IO.Path.GetFullPath(text).TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The text normalization for names and publishers before every match: NFKC, the registered, trademark and copyright
    /// marks and their "(R)" and "(TM)" spellings dropped, non-breaking spaces made plain, white space collapsed, no
    /// distinction of case.
    /// </summary>
    public static string NormalizeName(string? text)
    {
        var normalized = InstalledPackageDuplicates.Normalize(text);
        return normalized.Length > MaxNameLength ? normalized[..MaxNameLength] : normalized;
    }

    /// <summary>
    /// The publisher as lower-case tokens with the trailing legal forms stripped, repeatedly ("realtek semiconductor
    /// corp" becomes "realtek"). One token always stays, so a publisher that is nothing but a legal form keeps its text.
    /// </summary>
    public static string NormalizePublisher(string? publisher) => string.Join(' ', PublisherTokens(publisher));

    /// <summary>
    /// True when the publisher's leading tokens equal the vendor's tokens: "realtek semiconductor corp" matches Realtek,
    /// "intellij" never matches Intel (whole tokens, never a text prefix).
    /// </summary>
    public static bool PublisherMatchesVendor(string? publisher, string vendor) =>
        UninstallRiskRules.HasTokenPrefix(PublisherTokens(publisher), UninstallRiskRules.VendorTokens(vendor));

    internal static string[] PublisherTokens(string? publisher)
    {
        var tokens = Tokens(NormalizeName(publisher));
        return UninstallRiskRules.StripLegalForms(tokens);
    }

    /// <summary>Lower-case letter and digit runs; everything else (spaces, dots, commas, slashes, hyphens, "&amp;") separates.</summary>
    internal static string[] Tokens(string normalized)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i <= normalized.Length; i++)
        {
            var isPart = i < normalized.Length && char.IsLetterOrDigit(normalized[i]);
            if (isPart && start < 0)
            {
                start = i;
            }
            else if (!isPart && start >= 0)
            {
                tokens.Add(normalized[start..i]);
                start = -1;
            }
        }

        return [.. tokens];
    }

    /// <summary>
    /// The organization out of a certificate subject ("CN=Name, O=Org, L=City, C=US"): the O= value, else the CN= value.
    /// Text that is not a subject comes back unchanged.
    /// </summary>
    internal static string StorePublisher(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject) || !subject.Contains('='))
            return subject ?? string.Empty;

        string? organization = null;
        string? commonName = null;
        foreach (var part in SplitSubject(subject))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
                continue;

            var key = part[..equals].Trim();
            var value = part[(equals + 1)..].Trim().Trim('"');
            if (key.Equals("O", StringComparison.OrdinalIgnoreCase))
                organization ??= value;
            else if (key.Equals("CN", StringComparison.OrdinalIgnoreCase))
                commonName ??= value;
        }

        return !string.IsNullOrWhiteSpace(organization) ? organization : commonName ?? string.Empty;
    }

    private static IEnumerable<string> SplitSubject(string subject)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in subject)
        {
            if (c == '"')
                quoted = !quoted;

            if (c == ',' && !quoted)
            {
                yield return current.ToString();
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        yield return current.ToString();
    }

    internal static bool HasMicrosoftPublisherId(string family)
    {
        var cut = family.LastIndexOf('_');
        if (cut < 0 || cut == family.Length - 1)
            return false;

        var id = family[(cut + 1)..];
        return MicrosoftPublisherIds.Any(known => string.Equals(known, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The normalized facts the rule tables match against.</summary>
    internal sealed class Subject
    {
        public required UninstallEntryKind Kind { get; init; }

        public required string Name { get; init; }

        public required string[] PublisherTokens { get; init; }

        public required string Family { get; init; }

        public static Subject From(UninstallRiskInput input)
        {
            var publisher = input.Kind == UninstallEntryKind.StoreApp ? StorePublisher(input.Publisher) : input.Publisher;
            return new Subject
            {
                Kind = input.Kind,
                Name = NormalizeName(input.DisplayName),
                PublisherTokens = UninstallRiskClassifier.PublisherTokens(publisher),
                Family = (input.PackageFamilyName ?? string.Empty).Trim(),
            };
        }
    }

    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    internal static string Lower(string text) => text.ToLower(CultureInfo.InvariantCulture);

    internal static Regex NameBoundary(string normalizedPattern, bool trailingBoundary) =>
        new(@"(?<![\p{L}\p{N}])" + Regex.Escape(normalizedPattern) + (trailingBoundary ? @"(?![\p{L}\p{N}])" : string.Empty),
            RegexOptions.CultureInvariant, MatchTimeout);
}
