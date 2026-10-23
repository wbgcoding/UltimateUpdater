namespace UltimateUpdater.ViewModels;

public enum RowInstallState
{
    Idle,
    Queued,
    Running,
    Succeeded,
    Failed,

    /// <summary>
    /// The source could only open a vendor's own confirmation dialog (Steam's uninstall) and cannot see
    /// what happened next; genuinely neither success nor failure, so it gets its own terminal state
    /// rather than being folded into one of the other two.
    /// </summary>
    HandedOff,

    /// <summary>
    /// The package manager knows a newer version but has none that fits this installation (winget's
    /// 0x8A15002B; architecture, install type or scope). Nothing was changed and nothing went wrong, so
    /// the row says it is stuck instead of showing the red failure of an install the user could have
    /// caused (the Microsoft.OpenJDK.17 case: winget reported no applicable upgrade).
    /// </summary>
    Stuck,

    /// <summary>
    /// Set once the SAME classified failure (see <see cref="Models.Settings.PersistentFailureRecord"/>) has now
    /// happened three times in a row for this package, whatever version was on offer each time: a
    /// crashing vendor setup or a permanently inapplicable winget upgrade otherwise just keeps failing
    /// (or staying <see cref="Stuck"/>) forever, once per attempt, never actually telling the user that
    /// retrying will not help. Persists across restarts until the row's own "try normally again" clears
    /// it, unlike every other state here, which the next install attempt simply overwrites.
    /// </summary>
    NeedsManualWork,

    /// <summary>
    /// The user stopped just this one row with its own stop button while it was running; the
    /// rest of the queue kept going. Terminal, but not a failure, read like <see cref="HandedOff"/> and
    /// <see cref="Stuck"/>.
    /// </summary>
    Cancelled,
}
