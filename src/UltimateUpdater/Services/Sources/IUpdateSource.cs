using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Extra facts about an already-found update, fetched lazily after the scan (a package registry knows
/// the release date, download size and project/changelog page that the outdated listing itself never
/// carries). Any field may stay null when it is not available.
/// <paramref name="AvailableVersion"/> is for the sources whose own listing cannot say what the newest
/// version is (pipx, VS Code extensions, gh extensions all only report what is installed). It is applied
/// only when the source left the version blank, so a real up-front value always wins.
/// </summary>
public readonly record struct UpdateEnrichment(
    DateTimeOffset? PublishDate,
    long? DownloadSize,
    string? ProjectUrl = null,
    string? AvailableVersion = null,
    string? ChangelogBody = null,
    string? ChangelogUrl = null,
    string? Publisher = null);

/// <summary>
/// Thrown by <see cref="IUpdateSource.InstallAsync"/>/<see cref="IUpdateSource.UninstallAsync"/> when a
/// source could only hand the action off to an external program (opening a vendor's own confirmation
/// dialog, e.g. Steam's) and genuinely cannot observe whether it completed. <see cref="InstallQueue"/>
/// catches this specifically and reports it as its own outcome; distinct from both success and
/// failure, neither of which this app can honestly claim.
/// </summary>
public sealed class ManualHandoffException(string message) : Exception(message);

/// <summary>
/// Thrown when the package manager knows a newer version but refuses it for THIS installation; winget's
/// 0x8A15002B ("no applicable upgrade"): wrong architecture, wrong install type, wrong scope, or an
/// installed version it cannot compare. Nothing went wrong and nothing was changed, so the row must not
/// read like a failure the user caused (the Microsoft.OpenJDK.17 case: winget reported no applicable
/// upgrade). Reported as
/// its own terminal outcome, exactly like <see cref="ManualHandoffException"/>, on both the in-process and
/// the elevated path.
/// </summary>
public sealed class NotApplicableException(string message) : Exception(message);

/// <summary>One provider per source. ScanAsync streams results so the table grows live; InstallAsync only applies to Tier A/B.</summary>
public interface IUpdateSource
{
    string Id { get; }
    UpdateCategory Category { get; }
    SourceTier Tier { get; }

    /// <summary>Is the underlying manager/launcher present on this machine? Providers set to "auto" in settings only scan when this is true.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken);

    /// <summary>Tier A/B only; throws NotSupportedException for Tier C (detect+open only).</summary>
    Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this source can remove the given item. Default: no. Removal is a deliberate, per-source
    /// capability; a wrong "yes" would offer to uninstall something the source cannot actually touch.
    /// </summary>
    bool CanUninstall(UpdateItem item) => false;

    /// <summary>Removes the item. Routed like an install (elevated where the source needs admin). Default throws.</summary>
    Task UninstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Id} cannot uninstall '{item.Id}'.");

    /// <summary>
    /// A shell target that STARTS the installed application/game, or null when none can be resolved
    /// reliably. Kept deliberately conservative: launching the wrong executable is worse than offering
    /// no "Start" at all, so a source returns a target only where it truly knows one.
    /// </summary>
    string? LaunchTarget(UpdateItem item) => null;

    /// <summary>
    /// Best-effort extra facts (release date, download size) for an item this source already reported,
    /// fetched off the scan's critical path so the table fills in progressively. Default: nothing to
    /// add. Implementations must fail closed; a network or parse error returns null, never throws.
    /// </summary>
    Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken) =>
        Task.FromResult<UpdateEnrichment?>(null);

    /// <summary>
    /// Re-checks the installed version right after a successful install, for the sources that
    /// can answer cheaply, one local call, no network: winget, Chocolatey, npm, pip. Default: null, which
    /// means "no statement", not "unchanged" - a source that cannot answer says so instead of guessing,
    /// and the caller must never read null as a real version.
    /// </summary>
    Task<string?> GetInstalledVersionAsync(UpdateItem item, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// Can this source install an EXACT, older version on request (a rollback), rather than
    /// whatever it itself considers the latest upgrade? Default: no. Where true, <see cref="InstallAsync"/>
    /// must honour <see cref="UpdateItem.PinnedVersion"/> when the caller sets it.
    /// </summary>
    bool SupportsVersionPinning => false;
}
