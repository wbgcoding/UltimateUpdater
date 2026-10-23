namespace UltimateUpdater.Models;

/// <summary>One row in the update table. Immutable snapshot from a scan; re-fetched by Id at install time, never held as a live handle.</summary>
public sealed class UpdateItem
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required UpdateCategory Category { get; init; }
    public required string Name { get; init; }
    public string? InstalledVersion { get; init; }
    public string? AvailableVersion { get; init; }
    public bool IsCritical { get; init; }
    public SourceTier Tier { get; init; } = SourceTier.A;

    /// <summary>
    /// Tier B/C only: what the row's "Open in …" button launches; a vendor URI where one exists,
    /// otherwise the launcher executable. Null means the row is not openable.
    /// </summary>
    public string? OpenTarget { get; init; }

    /// <summary>
    /// Rows that belong UNDER this one and are shown as a collapsible sub-list on the parent row rather
    /// than as table rows of their own; Steam's pending game updates hanging under the Steam launcher
    /// row. Empty for every other source.
    /// </summary>
    public IReadOnlyList<UpdateItem> Children { get; init; } = [];

    public IReadOnlyDictionary<string, string> Meta { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Download size in bytes, where the source reports it up front. Windows Update does; most package
    /// managers do not (the size is only known once the manager resolves the installer). Null = unknown,
    /// and no size is shown for the row.
    /// </summary>
    public long? DownloadSize { get; init; }

    /// <summary>When the update was published, where the source exposes it (Windows Update, Steam). Null = unknown.</summary>
    public DateTimeOffset? PublishDate { get; init; }

    /// <summary>Tier C rows carry no installable action; they can only be opened in their vendor app.</summary>
    public bool CanInstall => Tier != SourceTier.C;

    /// <summary>
    /// When set, the source must install THIS exact version rather than whatever it itself
    /// would otherwise resolve as "the" upgrade (a rollback). Only sources whose
    /// <see cref="Sources.IUpdateSource.SupportsVersionPinning"/> is true ever see this filled in.
    /// </summary>
    public string? PinnedVersion { get; init; }

    /// <summary>This run is a deliberate downgrade, so the history entry it produces records
    /// itself as a rollback rather than an ordinary update.</summary>
    public bool IsRollback { get; init; }

    /// <summary>
    /// A rollback's own copy: every field this run needs carried over, plus the target OLDER version pinned
    /// both as the install target and as what the history entry should show as "installed to". A plain
    /// class, not a record, so this is the one place that copy is written rather than a `with` expression
    /// at every call site.
    /// </summary>
    public UpdateItem WithPinnedVersion(string version) => new()
    {
        Id = Id,
        SourceId = SourceId,
        Category = Category,
        Name = Name,
        InstalledVersion = InstalledVersion,
        AvailableVersion = version,
        IsCritical = IsCritical,
        Tier = Tier,
        OpenTarget = OpenTarget,
        Children = Children,
        Meta = Meta,
        DownloadSize = DownloadSize,
        PublishDate = PublishDate,
        PinnedVersion = version,
        IsRollback = true,
    };
}
