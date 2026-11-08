namespace UltimateUpdater.Models;

/// <summary>What a queue run does with its items, the whole batch is one kind.</summary>
public enum InstallAction
{
    Install,
    Uninstall,

    /// <summary>
    /// A plain install of a package that was just removed, without the upgrade-first ladder
    /// (<see cref="Services.Sources.IFreshInstallSource"/>). Only the "reinstall" flow queues it, after its
    /// own confirmed uninstall; the install half never removes anything.
    /// </summary>
    FreshInstall,
}
