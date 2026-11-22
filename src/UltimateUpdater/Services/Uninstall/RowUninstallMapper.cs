using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// Decides whether a row of the update table whose source has no removal of its own can be removed through the
/// uninstaller, and which installed entry that is. The answer is "yes" only for one unique entry; every doubt
/// (no match, two matches, a name too common to identify a program, an entry the uninstaller cannot remove on
/// its own) means no action, never a guess.
/// </summary>
public static class RowUninstallMapper
{
    private const string GitHubReleaseSourceId = "github-release";

    private static readonly Lazy<HashSet<string>> LauncherSourceIds = new(() =>
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { "epic", "gog" };
        foreach (var launcher in LauncherCatalog.Known())
            ids.Add(launcher.Id);

        return ids;
    });

    /// <summary>
    /// True for the rows that may map to an installed program: a program found through its GitHub repository and
    /// the client of a launcher. Windows updates (nothing installed yet), driver rows and every other source stay
    /// out, so does anything below a launcher client (a game): only the client itself is a program of its own.
    /// </summary>
    public static bool IsEligible(UpdateItem item)
    {
        if (item.SourceId == GitHubReleaseSourceId)
            return true;

        return LauncherSourceIds.Value.Contains(item.SourceId)
               && string.Equals(item.Id, item.SourceId, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one installed entry the row stands for, or null. The name must be equal after normalising (case,
    /// spaces, punctuation), the publisher too when both sides know one. Drivers, Windows updates and package
    /// manager entries are never taken, and an entry that cannot be removed on its own is no answer either.
    /// </summary>
    public static UninstallListing? Match(UpdateItem item, string? publisher, IReadOnlyList<UninstallListing> listings)
    {
        if (!IsEligible(item))
            return null;

        var name = LeftoverNames.Normalize(item.Name);
        if (!LeftoverNames.IsUsable(name))
            return null;

        var wantedPublisher = LeftoverNames.Normalize(publisher);
        UninstallListing? found = null;
        foreach (var listing in listings)
        {
            if (listing.Entry.Kind is not (UninstallEntryKind.Program or UninstallEntryKind.StoreApp))
                continue;

            if (LeftoverNames.Normalize(listing.Entry.Name) != name)
                continue;

            var entryPublisher = LeftoverNames.Normalize(listing.Entry.Publisher);
            if (wantedPublisher.Length > 0 && entryPublisher.Length > 0 && wantedPublisher != entryPublisher)
                continue;

            // A second entry that fits makes the answer unclear, whatever it is.
            if (found is not null)
                return null;

            found = listing;
        }

        return found is { Plan.IsRunnable: true, Risk.Selectable: true } ? found : null;
    }
}
