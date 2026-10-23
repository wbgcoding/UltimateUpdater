namespace UltimateUpdater.Services.Cleanup;

/// <summary>What kind of thing one <see cref="CleanupEntry"/> is, for the detail list's "by type" grouping;
/// derived from a folder's name and its files' extensions, never asked of the user.</summary>
public enum CleanupEntryType
{
    Cache,
    Logs,
    CrashReports,
    InstallLeftovers,
    Thumbnails,
    Other,
}

/// <summary>
/// One row in a target's detail list; a folder (grouped "by program", never file by file), a registry
/// finding, or a leftover candidate. <see cref="Bytes"/>/<see cref="Files"/>
/// are 0 for kinds with no meaningful size (a registry finding is one broken reference, not a file).
/// The recycle bin is the one deliberate exception to the grouping rule: its rows ARE the
/// individual deleted files, so each can be kept or removed on its own; everything else stays grouped.
/// </summary>
/// <param name="OriginalPath">Where a recycled file was before it was deleted, for the rows that have one; <see
/// cref="Path"/> is then the <c>$R</c> file inside the bin, which means nothing to the user and is not
/// worth remembering anywhere. Null for every other kind of entry.</param>
public sealed record CleanupEntry(
    string Path,
    string DisplayName,
    long Bytes,
    int Files,
    DateTimeOffset OldestWriteTime,
    DateTimeOffset NewestWriteTime,
    CleanupEntryType Type,
    string? OriginalPath = null);
