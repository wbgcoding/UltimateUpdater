namespace UltimateUpdater.Models;

public sealed class InstallJobItem
{
    public required string SourceId { get; set; }
    public required string ItemId { get; set; }
    public required string Name { get; set; }
    public string? InstalledVersion { get; set; }
    public string? AvailableVersion { get; set; }

    /// <summary>A rollback's exact target version, carried across the pipe so the elevated
    /// worker's own reconstructed <see cref="Models.UpdateItem"/> can honour it too.</summary>
    public string? PinnedVersion { get; set; }
}
