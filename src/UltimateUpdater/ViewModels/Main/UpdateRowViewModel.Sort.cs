using UltimateUpdater.Models;

namespace UltimateUpdater.ViewModels.Main;

public partial class UpdateRowViewModel
{
    /// <summary>Order of the groups in the table: applications and store first, then system, developer tooling, games last.</summary>
    public int CategoryOrder
    {
        get
        {
            // A GitHub-release row gets its own group above the generic one: it carries a real version comparison and size.
            if (IsGitHubManual)
                return 98;

            // Every other "open it yourself" row (store, launcher, Steam, vendor driver page) goes into one "Manual update" category at the very bottom.
            if (IsManualOnly)
                return 99;

            return Item.Category switch
            {
                UpdateCategory.Software => 0,
                UpdateCategory.Store => 1,
                UpdateCategory.SystemAndDrivers => 2,
                UpdateCategory.DeveloperTools => 3,
                UpdateCategory.Games => 4,
                _ => 8,
            };
        }
    }

    /// <summary>Download size in bytes (source or enrichment), for the pre-install batch preview.</summary>
    public long? DownloadSizeBytes => EnrichedDownloadSize ?? Item.DownloadSize;

    // Sort keys for the column headers. The table sorts by HasPublishDate/HasDownloadSize/HasVersions first, so rows without a value stay at the bottom.
    public DateTimeOffset SortDate => EffectivePublishDate ?? DateTimeOffset.MinValue;

    public long SortSize => EffectiveDownloadSize ?? 0L;

    public string SortVersion => VersionSortKey.For(AvailableVersion);
}
