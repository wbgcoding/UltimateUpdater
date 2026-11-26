using System.Globalization;
using System.Text.RegularExpressions;
using UltimateUpdater.Services.Sources.Windows;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>One line of <c>dism /online /get-packages /format:table</c>.</summary>
public sealed record DismPackage(string Identity, string State, string ReleaseType, DateTime? InstallTime);

/// <summary>An installed Windows update: the package identity, the date and whether Windows lets it be removed. The app reads
/// it from the registry (<see cref="WindowsUpdateRegistryReader"/>) to show it; the elevated worker works it out again
/// from DISM before it removes anything.</summary>
public sealed record WindowsUpdateRow
{
    public string Identity { get; init; } = string.Empty;

    public string ReleaseType { get; init; } = string.Empty;

    public DateOnly? InstalledOn { get; init; }

    public bool Removable { get; init; }
}

/// <summary>One line of <c>Win32_QuickFixEngineering</c>.</summary>
public sealed record QuickFix(string HotFixId, string Description, DateOnly? InstalledOn);

/// <summary>What <c>dism /online /get-packageinfo</c> says about a package's permanence.</summary>
public enum DismPermanence
{
    /// <summary>No permanence line (or one this parser does not know): the caller decides conservatively.</summary>
    Unknown,
    Removable,
    NotRemovable,
}

/// <summary>
/// The pure parts of listing installed Windows updates: DISM's table and package info, which packages count as
/// updates, which of them Windows lets be removed, and the join with the installed-hotfix list for KB numbers and
/// dates. No process runs here, so every rule is tested on fabricated output.
/// </summary>
public static partial class WindowsUpdateDiscovery
{
    /// <summary>The key of an update entry in the uninstaller list.</summary>
    internal const string KeyPrefix = "update:";

    internal const int InstalledPackageState = 0x70;

    internal const string CbsPackagesKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages";

    /// <summary>A package identity a Windows update wraps itself in: the KB package, the cumulative rollup, the
    /// .NET Framework rollup and the servicing stack. Nothing else is ever listed (a language pack, a feature or a driver
    /// package is not an update).</summary>
    [GeneratedRegex(@"^Package_for_(?<kind>KB(?<kb>\d{5,8})|RollupFix|DotNetRollup(_\d+)?|ServicingStack(_\d+)?)~[A-Za-z0-9._~\-]+$")]
    private static partial Regex WrapperRegex();

    /// <summary>The characters a package identity may have, so it can be put into an argument and a registry path as it is.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._~\-]{1,256}$")]
    private static partial Regex SafeIdentityRegex();

    [GeneratedRegex(@"^Package_for_RollupFix~[^~]*~[^~]*~[^~]*~(?<build>\d+\.\d+)")]
    private static partial Regex RollupBuildRegex();

    public static bool IsSafeIdentity(string? identity) => identity is not null && SafeIdentityRegex().IsMatch(identity);

    /// <summary>The identity of an update entry key, or null when the key is not <c>update:</c> and a wrapper identity.</summary>
    internal static string? IdentityOf(string? key) =>
        key is not null && key.StartsWith(KeyPrefix, StringComparison.Ordinal)
        && IsWrapper(key[KeyPrefix.Length..])
            ? key[KeyPrefix.Length..]
            : null;

    internal static bool IsWrapper(string identity) => IsSafeIdentity(identity) && WrapperRegex().IsMatch(identity);

    /// <summary>"KB5043080" for a KB package, else null.</summary>
    internal static string? KbNumberOf(string identity) =>
        WrapperRegex().Match(identity) is { Success: true } match && match.Groups["kb"].Success ? "KB" + match.Groups["kb"].Value : null;

    // ---- the DISM table ----

    /// <summary>
    /// The rows of the package table. DISM translates its headers and the state word with the Windows language,
    /// so rows are found by what they are, not by a header: the first cell is a package identity (a word with a tilde),
    /// the second the state, and of the last two the one with digits is the install time. The release type is not
    /// translated. A line that is not such a row (banner, header, rule) is skipped.
    /// </summary>
    public static IReadOnlyList<DismPackage> ParsePackages(string? output)
    {
        var packages = new List<DismPackage>();
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var cells = raw.TrimEnd('\r').Split('|');
            if (cells.Length != 4)
                continue;

            var identity = cells[0].Trim();
            if (!identity.Contains('~') || !IsSafeIdentity(identity))
                continue;

            var third = cells[2].Trim();
            var fourth = cells[3].Trim();
            var (release, time) = fourth.Any(char.IsDigit) && !third.Any(char.IsDigit) ? (third, fourth)
                : third.Any(char.IsDigit) && !fourth.Any(char.IsDigit) ? (fourth, third)
                : (third, fourth);
            packages.Add(new DismPackage(identity, cells[1].Trim(), release, ParseTime(time)));
        }

        return packages;
    }

    private static DateTime? ParseTime(string text)
    {
        // DISM prints the time in the system's own format, with a 12 or 24 hour clock.
        foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
        {
            if (DateTime.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces, out var time))
                return time;
        }

        return null;
    }

    /// <summary>
    /// True when DISM's state word means "Installed". DISM prints it in the Windows language, so the word is compared
    /// after its letters are reduced to plain ASCII, and a package whose state the registry says is installed counts
    /// as well. A state this does not recognise is not installed: the package is not listed.
    /// </summary>
    internal static bool IsInstalledState(string state)
    {
        var plain = new string([.. state.Where(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')]).ToLowerInvariant();
        return plain is "installed" or "installiert" or "instalado" or "installato" or "instalat" or "install"
            or "zainstalowano";
    }

    /// <summary>A package DISM lists as installed, or whose registry state says it is.</summary>
    internal static bool IsInstalled(DismPackage package, IRegistryView? registry) =>
        IsInstalledState(package.State)
        || registry?.DwordValue(CbsPackagesKey + "\\" + package.Identity, "CurrentState") == InstalledPackageState;

    /// <summary>Update and Security Update are the release types of what Windows Update delivers.</summary>
    internal static bool IsUpdateReleaseType(string releaseType) =>
        releaseType.Equals("Update", StringComparison.OrdinalIgnoreCase)
        || releaseType.Equals("Security Update", StringComparison.OrdinalIgnoreCase);

    // ---- get-packageinfo ----

    /// <summary>
    /// The permanence line of a package's info, in any language: a line whose label starts with "Permanen" and whose
    /// value is "Removable" (or its translation) reads Removable; any other value reads NotRemovable. No such line is
    /// Unknown.
    /// </summary>
    public static DismPermanence ParsePermanence(string? output)
    {
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var split = raw.IndexOf(':');
            if (split <= 0)
                continue;

            var label = raw[..split].Trim();
            if (!label.StartsWith("Permanen", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = raw[(split + 1)..].Trim();
            return value.Equals("Removable", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("Entfernbar", StringComparison.OrdinalIgnoreCase)
                ? DismPermanence.Removable
                : DismPermanence.NotRemovable;
        }

        return DismPermanence.Unknown;
    }

    // ---- which packages are listed, which are removable ----

    /// <summary>
    /// The installed Windows updates of a DISM package table. Only a wrapper package (KB, rollup, servicing stack) of
    /// state Installed with the release type Update or Security Update is listed. Removable only for a KB package whose
    /// info does not say otherwise: the cumulative rollup and the servicing stack are listed but never removable, and a
    /// KB package with no permanence line in its info stays removable only because Windows itself refuses the rest
    /// (a permanent package fails with its own error).
    /// </summary>
    public static IReadOnlyList<WindowsUpdateRow> Classify(
        IReadOnlyList<DismPackage> packages, IRegistryView? registry, Func<string, DismPermanence> permanence)
    {
        var rows = new List<WindowsUpdateRow>();
        foreach (var package in packages)
        {
            if (!IsWrapper(package.Identity) || !IsUpdateReleaseType(package.ReleaseType))
                continue;

            if (!IsInstalled(package, registry))
                continue;

            var removable = KbNumberOf(package.Identity) is not null && permanence(package.Identity) != DismPermanence.NotRemovable;
            rows.Add(new WindowsUpdateRow
            {
                Identity = package.Identity,
                ReleaseType = package.ReleaseType,
                InstalledOn = package.InstallTime is { } time ? DateOnly.FromDateTime(time) : null,
                Removable = removable,
            });
        }

        return rows;
    }

    /// <summary>
    /// True when the package is no longer installed. Fails safe: an unreadable package list (no subkeys at all) is
    /// never "gone", a package absent from a readable list is gone, and a listed one is gone only when its state is
    /// readable and differs from Installed.
    /// </summary>
    public static bool IsGone(IRegistryView registry, string identity)
    {
        var names = registry.SubKeyNames(CbsPackagesKey);
        if (names.Count == 0)
            return false;

        if (!names.Contains(identity, StringComparer.OrdinalIgnoreCase))
            return true;

        var state = registry.DwordValue(CbsPackagesKey + "\\" + identity, "CurrentState");
        return state is { } value && value != InstalledPackageState;
    }

    // ---- the join with the hotfix list ----

    /// <summary>The uninstaller entries of the worker's rows, with the KB description and date from the hotfix list.</summary>
    public static IReadOnlyList<InstalledProgramEntry> ToEntries(
        IReadOnlyList<WindowsUpdateRow> rows, IReadOnlyList<QuickFix> quickFixes, Func<string, string> text)
    {
        var byKb = new Dictionary<string, QuickFix>(StringComparer.OrdinalIgnoreCase);
        foreach (var fix in quickFixes)
            byKb.TryAdd(fix.HotFixId.Trim(), fix);

        var entries = new List<InstalledProgramEntry>(rows.Count);
        foreach (var row in rows.Where(r => IsWrapper(r.Identity)))
        {
            QuickFix? fix = null;
            var kb = KbNumberOf(row.Identity);
            if (kb is not null)
                byKb.TryGetValue(kb, out fix);

            var name = kb is not null
                ? (string.IsNullOrWhiteSpace(fix?.Description) ? kb : $"{kb} ({fix!.Description.Trim()})")
                : row.Identity.StartsWith("Package_for_RollupFix", StringComparison.Ordinal)
                    ? string.Format(text("UninstallerUpdateCumulative"), RollupBuildRegex().Match(row.Identity) is { Success: true } build ? build.Groups["build"].Value : "?")
                    : row.Identity.StartsWith("Package_for_DotNetRollup", StringComparison.Ordinal)
                        ? text("UninstallerUpdateDotNetRollup")
                        : text("UninstallerUpdateServicingStack");

            entries.Add(new InstalledProgramEntry
            {
                Key = KeyPrefix + row.Identity,
                Kind = UninstallEntryKind.WindowsUpdate,
                Name = name,
                Publisher = "Microsoft",
                Version = string.Empty,
                InstallDate = fix?.InstalledOn ?? row.InstalledOn,
                Scope = UninstallScope.Machine,
                WindowsAllowsRemoval = row.Removable,
            });
        }

        return entries;
    }

    /// <summary>The date of a <c>Win32_QuickFixEngineering.InstalledOn</c> value: a date in the system's format, the
    /// invariant format, or the hexadecimal FILETIME some Windows versions store. Null when it is none of them.</summary>
    public static DateOnly? ParseQuickFixDate(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;

        foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
        {
            if (DateTime.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out var date))
                return DateOnly.FromDateTime(date);
        }

        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fileTime)
            && fileTime is > 0 and < 2650467743999999999)
            return DateOnly.FromDateTime(DateTime.FromFileTime(fileTime));

        return null;
    }
}
