namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// What one drive's recycle bin holds, beside the rows shown for it. Emptying can only take the shell's fast
/// whole-bin path when a drive is FULLY selected AND nothing of it was left out of the list. Otherwise that
/// call would remove files past the per-drive budget that the user never saw, which is the very thing the
/// budget exists to prevent. <paramref name="DetailsAvailable"/> is false for a drive whose enumeration
/// failed and which therefore shows the shell's aggregate figure as a single row.
/// <paramref name="AgeFilterHidItems"/> is true when the recycle-bin age filter left something on this drive
/// out of the list, or could not prove it did not (an aggregate row carries no per-item date): the shell's
/// whole-bin call would then also delete the younger files the filter promised to keep.
/// <paramref name="SkippedIndexCount"/> counts index files that could not be read or parsed: each may stand
/// for an item that is missing from the list, so the whole-bin call is not taken when it is above zero.
/// </summary>
public readonly record struct RecycleBinDriveSummary(
    string Root,
    string Label,
    int FoundCount,
    int ListedCount,
    long FoundBytes,
    long ListedBytes,
    bool DetailsAvailable,
    bool AgeFilterHidItems = false,
    int SkippedIndexCount = 0)
{
    public int OmittedCount => Math.Max(0, FoundCount - ListedCount);

    public long OmittedBytes => Math.Max(0, FoundBytes - ListedBytes);
}
