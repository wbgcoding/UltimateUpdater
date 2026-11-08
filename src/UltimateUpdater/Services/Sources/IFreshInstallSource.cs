using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// A source that can install a package from scratch: the second half of the "reinstall" flow (winget's
/// different-install-technology refusal), after the old version was removed. A separate capability so a
/// source that cannot do it is never offered the action and the elevated worker can refuse it by type.
/// </summary>
public interface IFreshInstallSource : IUpdateSource
{
    /// <summary>
    /// Whether the source can fetch this package right now. Asked BEFORE anything is removed: a false (or a
    /// failure of any kind) must stop the flow with nothing changed.
    /// </summary>
    Task<bool> CanFetchAsync(UpdateItem item, CancellationToken cancellationToken);

    /// <summary>
    /// The scope the package is installed in right now, <see cref="InstallScopes.User"/> or
    /// <see cref="InstallScopes.Machine"/>; null when that cannot be told (not found, both, any failure).
    /// Asked BEFORE the removal, because afterwards there is nothing left to ask. Read-only.
    /// </summary>
    Task<string?> GetInstalledScopeAsync(UpdateItem item, CancellationToken cancellationToken);

    /// <summary>Installs the package fresh, one attempt, no upgrade ladder, in <see cref="UpdateItem.InstallScope"/>
    /// when that is set. Throws on failure.</summary>
    Task FreshInstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken);
}
