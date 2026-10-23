using UltimateUpdater.Models;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// The row-level actions that need the app's sources and install queue (Start and Uninstall) handed
/// to the row so its context menu can offer them without the row itself knowing how they work. The
/// main view model implements it; a row built without one (tests) simply offers neither.
/// </summary>
public interface IRowActions
{
    bool CanStart(UpdateItem item);

    bool CanUninstall(UpdateItem item);

    void Start(UpdateRowViewModel row);

    void Uninstall(UpdateRowViewModel row);

    /// <summary>Queues just this one row for installation (joining any batch already running).</summary>
    void InstallSingle(UpdateRowViewModel row)
    {
    }

    /// <summary>Stops just this one row while it is running; the rest of the queue, and the rest
    /// of an admin batch it might be part of, keeps going.</summary>
    void CancelSingle(UpdateRowViewModel row)
    {
    }

    /// <summary>The narrow exception to "never --interactive", reachable only from the row's own gated
    /// command, which re-checks the same gates itself.</summary>
    void RetryInteractive(UpdateRowViewModel row)
    {
    }

    /// <summary>Ignores every version of this one package (not just the current one).</summary>
    void IgnoreAllVersions(UpdateRowViewModel row)
    {
    }

    /// <summary>"Remind me later" for the given duration ("day"/"week"/"month"). Clears any Ignore on
    /// the same row, the two are mutually exclusive.</summary>
    void Snooze(UpdateRowViewModel row, string duration)
    {
    }

    /// <summary>Clears a snooze early: the "show again now" entry on an already-snoozed row.</summary>
    void Unsnooze(UpdateRowViewModel row)
    {
    }

    /// <summary>"Try normally again": clears the persisted failure tally and the row's own
    /// <see cref="RowInstallState.NeedsManualWork"/> flag, back to plain Idle.</summary>
    void ResetPersistentFailure(UpdateRowViewModel row)
    {
    }

    /// <summary>"Back to the previous version": confirms first, then runs the pinned install
    /// through the exact same path as any other install (elevation, restore point, history entry).
    /// Reachable only when <see cref="UpdateRowViewModel.ShowDowngradeButton"/> is true.</summary>
    void Downgrade(UpdateRowViewModel row)
    {
    }

    /// <summary>Retry of a failed rollback: the same pinned version again, never a plain upgrade.</summary>
    void RetryDowngrade(UpdateRowViewModel row, string targetVersion)
    {
    }

    /// <summary>
    /// Whether a row's own collapsible sub-list is open. Kept by the main view model rather than the
    /// row, so it survives the table being rebuilt mid-scan. Default (no actions wired, e.g. in tests):
    /// open.
    /// </summary>
    bool IsSubListExpanded(string key) => true;

    void SetSubListExpanded(string key, bool expanded)
    {
    }
}
