using System.IO;
using System.Security.Principal;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>One recycled item: the content file/folder, its index file, and the original item it stands for.</summary>
public sealed record RecycleBinItem(
    string ContentPath, string IndexPath, string OriginalPath, long SizeBytes, DateTimeOffset DeletedAt);

/// <summary>
/// The impure edge around <see cref="RecycleBinIndexParser"/>: walks one drive's recycle bin and pairs every
/// <c>$I</c> index file with the <c>$R</c> content it describes. ONLY the current user's own SID folder is
/// ever opened: another account's deleted files are none of this app's business, and staying out of them
/// keeps that true even when the caller happens to run elevated.
/// </summary>
public static class RecycleBinScanner
{
    private const string RecycleBinFolder = "$Recycle.Bin";

    /// <summary>An index file is a few hundred bytes; anything larger is not one and is not read.</summary>
    private const int MaxIndexFileBytes = 64 * 1024;

    /// <summary>The SID whose recycle-bin folder belongs to this user, or null when it cannot be
    /// established; in which case nothing is enumerated and nothing is deleted.</summary>
    public static string? TryGetOwnSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>The folder this user's recycled items live in on <paramref name="driveRoot"/>.</summary>
    public static string OwnBinFolder(string driveRoot, string ownSid) =>
        Path.Combine(driveRoot, RecycleBinFolder, ownSid);

    /// <summary>
    /// Every item this user has in <paramref name="driveRoot"/>'s recycle bin. False means the enumeration
    /// itself failed and the caller must fall back to the shell's own total for this drive rather than
    /// showing a list that silently misses items; a drive with no bin folder at all is a successful empty
    /// result, not a failure. An index file that cannot be read or parsed, and an index whose <c>$R</c>
    /// twin is missing (an orphan Windows left behind), are skipped individually.
    /// </summary>
    public static bool TryEnumerate(
        string driveRoot, string ownSid, CancellationToken cancellationToken, out IReadOnlyList<RecycleBinItem> items) =>
        TryEnumerate(driveRoot, ownSid, cancellationToken, out items, out _);

    /// <summary>
    /// As above, and <paramref name="skippedIndexCount"/> is how many index files were skipped because
    /// they could not be read or parsed. Those may describe real items that are not on the list but that
    /// the shell's whole-bin call would still delete. An orphan without its content twin is not counted:
    /// there is nothing behind it to protect.
    /// </summary>
    public static bool TryEnumerate(
        string driveRoot, string ownSid, CancellationToken cancellationToken, out IReadOnlyList<RecycleBinItem> items,
        out int skippedIndexCount)
    {
        items = [];
        skippedIndexCount = 0;
        if (string.IsNullOrWhiteSpace(driveRoot) || string.IsNullOrWhiteSpace(ownSid))
            return false;

        var folder = OwnBinFolder(driveRoot, ownSid);
        var found = new List<RecycleBinItem>();
        var skipped = 0;

        try
        {
            if (!Directory.Exists(folder))
                return true;

            foreach (var indexPath in Directory.EnumerateFiles(folder, "$I*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (TryReadItem(indexPath, out var item))
                {
                    case IndexRead.Read:
                        found.Add(item!);
                        break;
                    case IndexRead.Unreadable:
                        skipped++;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        items = found;
        skippedIndexCount = skipped;
        return true;
    }

    private enum IndexRead
    {
        Read,

        /// <summary>No content twin (or the index vanished): nothing real is hidden behind it.</summary>
        Orphan,

        /// <summary>Present but not readable or not parseable: it may describe an item left off the list.</summary>
        Unreadable,
    }

    private static IndexRead TryReadItem(string indexPath, out RecycleBinItem? item)
    {
        item = null;
        try
        {
            var info = new FileInfo(indexPath);
            if (!info.Exists)
                return IndexRead.Orphan;

            if (info.Length is 0 or > MaxIndexFileBytes)
                return IndexRead.Unreadable;

            if (!RecycleBinIndexParser.TryParse(File.ReadAllBytes(indexPath), out var parsed) || parsed is null)
                return IndexRead.Unreadable;

            var contentPath = ContentPathFor(indexPath);
            if (contentPath is null || !(File.Exists(contentPath) || Directory.Exists(contentPath)))
                return IndexRead.Orphan;

            item = new RecycleBinItem(contentPath, indexPath, parsed.OriginalPath, parsed.SizeBytes, parsed.DeletedAt);
            return IndexRead.Read;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return IndexRead.Unreadable;
        }
    }

    /// <summary>The <c>$R</c> file beside a <c>$I</c> file: same folder, same name past the two-character
    /// prefix. Null for a name that is not an index file at all.</summary>
    public static string? ContentPathFor(string indexPath)
    {
        var name = Path.GetFileName(indexPath);
        if (name.Length < 2 || !name.StartsWith("$I", StringComparison.OrdinalIgnoreCase))
            return null;

        var folder = Path.GetDirectoryName(indexPath);
        return folder is null ? null : Path.Combine(folder, "$R" + name[2..]);
    }

    /// <summary>The <c>$I</c> file beside a <c>$R</c> file: the same pairing read the other way round, so
    /// deleting an item can remove its index without having carried that path along.</summary>
    public static string? IndexPathFor(string contentPath)
    {
        var name = Path.GetFileName(contentPath);
        if (name.Length < 2 || !name.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
            return null;

        var folder = Path.GetDirectoryName(contentPath);
        return folder is null ? null : Path.Combine(folder, "$I" + name[2..]);
    }
}
