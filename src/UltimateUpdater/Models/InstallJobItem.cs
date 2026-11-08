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

    /// <summary>A fresh install's scope, "user" or "machine" (<see cref="InstallScopes"/>); null = none named.
    /// The worker checks it again and refuses anything else.</summary>
    public string? Scope { get; set; }

    /// <summary>The winget source the app's scan found the package in, so the worker, which rebuilds the item
    /// from this job without a scan of its own, pins the install to it too. Only a plain name is ever passed
    /// on; the worker checks it again and drops anything else.</summary>
    public string? PackageSource { get; set; }
}
