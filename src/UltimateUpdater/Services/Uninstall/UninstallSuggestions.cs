using System.Globalization;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>Why a program is suggested for removal.</summary>
public enum UninstallSuggestionKind
{
    /// <summary>The same program installed through two or more launchers, both with files on disk.</summary>
    DuplicateInstall,

    /// <summary>A launcher entry whose install folder holds no file (the game is most likely gone already).</summary>
    EmptyEntry,

    /// <summary>A large program that has not been used for a long time.</summary>
    UnusedLarge,
}

/// <summary>One program of a suggestion: the row it points to, where it came from and how much space it takes.</summary>
public sealed record UninstallSuggestionCopy(string Key, string Source, long? Bytes);

/// <summary>One suggested clean-up: what, why, how much space it frees and which rows it concerns.</summary>
/// <param name="DismissKey">Stable key kept in the settings once the user hides the suggestion; never suggested again.</param>
/// <param name="Entries">The rows concerned: one for an empty or unused entry, every copy for a duplicate.</param>
/// <param name="ReclaimableBytes">What removing the suggested row frees; for a duplicate the smaller copy.</param>
/// <param name="LastUsed">The last use (UTC) for an unused program, otherwise null.</param>
public sealed record UninstallSuggestion(
    UninstallSuggestionKind Kind,
    string DismissKey,
    string Name,
    IReadOnlyList<UninstallSuggestionCopy> Entries,
    long ReclaimableBytes,
    DateTime? LastUsed);

/// <summary>
/// The "suggestions" of the uninstaller: a few entries worth a look, found only from what the list and the local usage
/// data already say. Pure: no disk, no registry, no clock. Never suggests anything the window would not let the user
/// remove, nothing that is not a plain Safe program, and nothing the user dismissed.
/// </summary>
public static class UninstallSuggestions
{
    /// <summary>The card shows at most this many lines.</summary>
    public const int MaxSuggestions = 5;

    /// <summary>A program counts as unused when its last use is older than this many days.</summary>
    public const int UnusedDays = 180;

    /// <summary>...and only when it takes at least this much space (500 MB).</summary>
    public const long UnusedMinBytes = 500L * 1024 * 1024;

    private const string DuplicatePrefix = "dup:";
    private const string EmptyPrefix = "empty:";
    private const string UnusedPrefix = "unused:";

    /// <summary>The suggestions, largest reclaimable space first, at most <see cref="MaxSuggestions"/>.</summary>
    /// <param name="lastUsedByKey">Last use (UTC) per entry key; an entry missing here has no usage data and is never "unused".</param>
    /// <param name="dismissed">Dismiss keys the user hid.</param>
    public static IReadOnlyList<UninstallSuggestion> Build(
        IReadOnlyList<UninstallListing> listings,
        IReadOnlyDictionary<string, DateTime> lastUsedByKey,
        IReadOnlyCollection<string> dismissed,
        DateTime nowUtc)
    {
        var hidden = new HashSet<string>(dismissed, StringComparer.OrdinalIgnoreCase);
        var eligible = listings.Where(IsEligible).ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<UninstallSuggestion>();

        foreach (var suggestion in EmptyEntries(eligible).Concat(Duplicates(eligible)).Concat(Unused(eligible, lastUsedByKey, nowUtc)))
        {
            if (hidden.Contains(suggestion.DismissKey) || suggestion.Entries.Any(copy => used.Contains(copy.Key)))
                continue;

            foreach (var copy in suggestion.Entries)
                used.Add(copy.Key);

            found.Add(suggestion);
        }

        return found
            .OrderByDescending(suggestion => suggestion.ReclaimableBytes)
            .ThenBy(suggestion => suggestion.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxSuggestions)
            .ToList();
    }

    /// <summary>Only a plain program the window lets the user remove, and only a Safe one: nothing risky, no driver, no update, no package.</summary>
    private static bool IsEligible(UninstallListing listing) =>
        listing.Entry.Kind == UninstallEntryKind.Program
        && listing.Risk.Level == CleanupRiskLevel.Safe
        && listing.Risk.Selectable
        && listing.Plan.IsRunnable;

    private static IEnumerable<UninstallSuggestion> EmptyEntries(List<UninstallListing> eligible)
    {
        foreach (var listing in eligible.Where(listing => listing.Entry.InstallFolderEmpty))
        {
            var entry = listing.Entry;
            yield return new UninstallSuggestion(
                UninstallSuggestionKind.EmptyEntry,
                EmptyPrefix + entry.Key,
                entry.Name,
                [Copy(entry)],
                0,
                null);
        }
    }

    private static IEnumerable<UninstallSuggestion> Duplicates(List<UninstallListing> eligible)
    {
        var groups = eligible
            .Where(listing => !listing.Entry.InstallFolderEmpty && listing.Entry.SizeBytes is not 0 && UninstallEntryOrigin.Label(listing.Entry) is not null)
            .GroupBy(listing => NormalisedName(listing.Entry.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            // One copy per launcher; two entries of the same launcher are no duplicate install.
            var copies = group
                .GroupBy(listing => UninstallEntryOrigin.Label(listing.Entry)!, StringComparer.OrdinalIgnoreCase)
                .Select(perLauncher => perLauncher.First().Entry)
                .ToList();
            if (copies.Count < 2)
                continue;

            var reclaim = copies.Min(copy => copy.SizeBytes ?? 0);
            yield return new UninstallSuggestion(
                UninstallSuggestionKind.DuplicateInstall,
                DuplicatePrefix + group.Key,
                copies[0].Name.Trim(),
                [.. copies.Select(Copy)],
                reclaim,
                null);
        }
    }

    private static IEnumerable<UninstallSuggestion> Unused(
        List<UninstallListing> eligible, IReadOnlyDictionary<string, DateTime> lastUsedByKey, DateTime nowUtc)
    {
        var limit = nowUtc - TimeSpan.FromDays(UnusedDays);
        foreach (var listing in eligible)
        {
            var entry = listing.Entry;
            if (entry.InstallFolderEmpty || entry.SizeBytes is not { } bytes || bytes < UnusedMinBytes)
                continue;

            // No usage data: the program may well be in daily use, so it is never called unused.
            if (!lastUsedByKey.TryGetValue(entry.Key, out var lastUsed) || lastUsed >= limit)
                continue;

            yield return new UninstallSuggestion(
                UninstallSuggestionKind.UnusedLarge,
                UnusedPrefix + entry.Key,
                entry.Name,
                [Copy(entry)],
                bytes,
                lastUsed);
        }
    }

    private static UninstallSuggestionCopy Copy(InstalledProgramEntry entry) =>
        new(entry.Key, UninstallEntryOrigin.Label(entry) ?? string.Empty, entry.SizeBytes);

    /// <summary>The name that decides "the same program": trimmed, inner whitespace collapsed, case ignored by the grouping.</summary>
    private static string NormalisedName(string name) =>
        string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLower(CultureInfo.InvariantCulture);
}
