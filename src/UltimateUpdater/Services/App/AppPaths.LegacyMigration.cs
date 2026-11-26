using System.IO;

namespace UltimateUpdater.Services.App;

/// <summary>The pre-rename "WindowsUpdater" data folder and the move of logs, caches and reports into the local folder.</summary>
public static partial class AppPaths
{
    /// <summary>The folders that moved from the data folder to the local one. A property, not a field: it is read
    /// from the static initializer, before any later field would have been assigned.</summary>
    internal static string[] LocalFolderNames => ["logs", "cache", "reports"];

    /// <summary>Set when the one-time move of the logs, cache and reports carried something over; deferred
    /// logging like <see cref="PointerFallbackReason"/>.</summary>
    public static string? LocalFolderMigrationMessage { get; private set; }

    /// <summary>Reads and clears <see cref="LocalFolderMigrationMessage"/> in one step.</summary>
    public static string? ConsumeLocalFolderMigrationMessage()
    {
        var message = LocalFolderMigrationMessage;
        LocalFolderMigrationMessage = null;
        return message;
    }

    /// <summary>Set when part of the move had to be skipped; those files stay where they were.</summary>
    public static string? LocalFolderMigrationFailure { get; private set; }

    /// <summary>Reads and clears <see cref="LocalFolderMigrationFailure"/> in one step.</summary>
    public static string? ConsumeLocalFolderMigrationFailure()
    {
        var message = LocalFolderMigrationFailure;
        LocalFolderMigrationFailure = null;
        return message;
    }

    /// <summary>
    /// Moves the logs, cache and reports folders from <paramref name="dataDirectory"/> into
    /// <paramref name="localDirectory"/>, file by file. Nothing is ever lost or blocked: a file that cannot be
    /// moved (in use, a name that already exists there, a link) stays where it was, any error only skips that
    /// entry, and a folder with nothing left in it is removed. It runs on every start but finds nothing to do
    /// once the old folders are gone. Never throws; returns how many files moved and how many entries were skipped.
    /// </summary>
    internal static (int Moved, int Skipped) MigrateToLocalDirectory(string dataDirectory, string localDirectory)
    {
        var moved = 0;
        var skipped = 0;

        foreach (var name in LocalFolderNames)
        {
            try
            {
                MoveFolderContents(Path.Combine(dataDirectory, name), Path.Combine(localDirectory, name), ref moved, ref skipped);
            }
            catch (Exception)
            {
                skipped++;
            }
        }

        return (moved, skipped);
    }

    private static void MoveFolderContents(string from, string to, ref int moved, ref int skipped)
    {
        // Gone: nothing to do. A link is never followed, in the source or at the target.
        var kind = ClassifyEntry(from);
        if (kind == EntryKind.Gone || !Directory.Exists(from))
            return;

        if (kind == EntryKind.Link || ClassifyEntry(to) == EntryKind.Link)
        {
            skipped++;
            return;
        }

        Directory.CreateDirectory(to);

        foreach (var entry in Directory.EnumerateFileSystemEntries(from).ToList())
        {
            var target = Path.Combine(to, Path.GetFileName(entry));
            try
            {
                if (ClassifyEntry(entry) != EntryKind.Plain)
                {
                    skipped++;
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    MoveFolderContents(entry, target, ref moved, ref skipped);
                    continue;
                }

                if (File.Exists(target) || Directory.Exists(target))
                {
                    skipped++;
                    continue;
                }

                File.Move(entry, target);
                moved++;
            }
            catch (Exception)
            {
                skipped++;
            }
        }

        try
        {
            // only an empty folder goes; Directory.Delete without recursion refuses anything else
            Directory.Delete(from, recursive: false);
        }
        catch (Exception)
        {
            // still in use or not empty: stays
        }
    }

    /// <summary>
    /// Copies an old <c>%AppData%\WindowsUpdater</c> folder into the new <c>%AppData%\UltimateUpdater</c>
    /// one, exactly once: only when the new folder does not exist yet and the old one does. The old folder is
    /// left untouched (a copy, never a move), so a problem with the new folder never loses the user's data.
    /// The internal overload takes explicit paths so a test does not touch the real <c>%AppData%</c>.
    /// </summary>
    private static void TryMigrateLegacyDataFolder(string roaming) =>
        TryMigrateLegacyDataFolder(
            roaming,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyProductFolderName));

    internal static void TryMigrateLegacyDataFolder(string roaming, string legacy)
    {
        // An existing new folder normally ends the migration for good. Only the marker of a failed copy
        // reopens it. The legacy copy then wins, and each file it replaces is kept with a suffix.
        var retry = Directory.Exists(roaming);
        if (retry && !IsMigrationPending(Path.Combine(roaming, MigrationPendingFileName)))
            return;

        if (!Directory.Exists(legacy))
            return;

        // The legacy folder can itself carry a "moved elsewhere" pointer from a pre-rename build; that pointer,
        // not the legacy default folder, is where the data lives. An unusable target falls back to the legacy
        // folder itself, logged.
        var source = legacy;
        string? fallbackReason = null;

        if (TryReadPointer(Path.Combine(legacy, PointerFileName)) is { } pointedTo)
        {
            // The whole target gets copied, so only a dedicated app folder qualifies: a pointer at a user's own
            // folder (or a relative one) must not make the first start copy arbitrary data.
            if (!IsDedicatedLegacyDataFolder(pointedTo))
            {
                fallbackReason =
                    $"The data folder recorded in the old installation ('{pointedTo}') is not a dedicated app folder; migrating the old default folder '{legacy}' instead.";
            }
            else if (Directory.Exists(pointedTo))
            {
                source = pointedTo;
            }
            else
            {
                fallbackReason =
                    $"The data folder recorded in the old installation ('{pointedTo}') no longer exists; migrating the old default folder '{legacy}' instead.";
            }
        }

        if (!TryMigrateInto(source, roaming, out var failure))
        {
            LegacyDataFolderMigrationFailure =
                $"Could not copy data from the old '{source}' folder to '{roaming}' ({failure}); the next start tries again.";
            MarkMigrationPending(roaming);
            return;
        }

        if (retry)
            CloseMigrationMarker(Path.Combine(roaming, MigrationPendingFileName));

        LegacyDataFolderMigrationMessage =
            (fallbackReason is null ? string.Empty : fallbackReason + " ")
            + $"Copied data from the old '{legacy}' folder to '{roaming}' after the app was renamed; the old folder was left in place.";
    }

    /// <summary>
    /// A fully qualified path whose last segment is the current or the pre-rename dedicated folder name.
    /// </summary>
    private static bool IsDedicatedLegacyDataFolder(string path)
    {
        if (IsDedicatedDataFolder(path))
            return true;

        try
        {
            return Path.IsPathFullyQualified(path)
                && string.Equals(
                    Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    LegacyProductFolderName,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> into a temp sibling of <paramref name="destination"/> and renames it
    /// into place only once the copy succeeded. A copy that fails midway never leaves
    /// <paramref name="destination"/> existing, so the next launch retries instead of being stuck on a
    /// half-migrated folder.
    /// </summary>
    internal static bool TryMigrateInto(string source, string destination) =>
        TryMigrateInto(source, destination, out _);

    /// <summary>When <paramref name="destination"/> already exists (a retry after a failed copy) the staged
    /// copy is laid over it: the failed start only wrote defaults there, while the legacy folder holds the
    /// user's real settings and history.</summary>
    internal static bool TryMigrateInto(string source, string destination, out string failure)
    {
        var staging = destination + ".migrating-" + Guid.NewGuid().ToString("N");

        try
        {
            // A skipped log costs nothing; any other skipped file would be lost for good, since the new
            // folder existing stops every later retry.
            if (!TryCopyDirectory(source, staging, out var skipped))
            {
                failure = "the copy failed part way";
                TryDeleteBestEffort(staging);
                return false;
            }

            if (skipped.FirstOrDefault(file => !IsUnder(file, Path.Combine(source, LogsFolderName))) is { } left)
            {
                failure = $"'{Path.GetFileName(left)}' could not be read";
                TryDeleteBestEffort(staging);
                return false;
            }

            if (Directory.Exists(destination))
            {
                MergeOver(staging, destination);
                TryDeleteBestEffort(staging);
            }
            else
            {
                Directory.Move(staging, destination);
            }

            failure = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex.Message;
            TryDeleteBestEffort(staging);
            return false;
        }
    }

    private static void MergeOver(string from, string into)
    {
        // The app may have run for a while on the new folder before this retry, so what the legacy copy
        // replaces is kept beside it instead of being lost. A backup that already exists holds what the user
        // had before the FIRST attempt; a later attempt finds the earlier legacy copy in the target and must
        // not push that over the backup.
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var target = Path.Combine(into, Path.GetFileName(file));
            var backup = target + MigrationReplacedSuffix;
            if (File.Exists(target) && !File.Exists(backup))
                File.Move(target, backup);
            File.Move(file, target, overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(from))
        {
            var target = Path.Combine(into, Path.GetFileName(directory));
            Directory.CreateDirectory(target);
            MergeOver(directory, target);
        }
    }

    /// <summary>Best effort: a folder that cannot even take the marker is retried anyway while it does not
    /// exist, and the failure is already reported.</summary>
    private static void MarkMigrationPending(string roaming)
    {
        try
        {
            Directory.CreateDirectory(roaming);
            File.WriteAllText(Path.Combine(roaming, MigrationPendingFileName), string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsMigrationPending(string marker)
    {
        try
        {
            return File.Exists(marker) && File.ReadAllText(marker).Trim() != MigrationDoneContent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable marker must not reopen a merge that may already have run.
            return false;
        }
    }

    private static void CloseMigrationMarker(string marker)
    {
        FileDeletion.TryDelete(marker);
        if (!File.Exists(marker))
            return;

        try
        {
            File.WriteAllText(marker, MigrationDoneContent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LegacyDataFolderMigrationFailure =
                $"The migration marker '{marker}' could not be removed ({ex.Message}); delete it so the next start does not copy the old data again.";
        }
    }
}
