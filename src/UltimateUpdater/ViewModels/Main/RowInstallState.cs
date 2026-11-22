namespace UltimateUpdater.ViewModels.Main;

public enum RowInstallState
{
    Idle,
    Queued,
    Running,
    Succeeded,
    Failed,

    /// <summary>The source could only open a vendor's own confirmation dialog (Steam's uninstall) and cannot see the outcome: neither success nor failure.</summary>
    HandedOff,

    /// <summary>The package manager knows a newer version but none fits this installation (winget 0x8A15002B: architecture, install type or scope). Nothing was changed and nothing went wrong, so the row says it is stuck instead of showing a red failure.</summary>
    Stuck,

    /// <summary>Set once the same classified failure (see <see cref="Models.Settings.PersistentFailureRecord"/>) has happened three times in a row for this package. Persists across restarts until the row's "try normally again" clears it.</summary>
    NeedsManualWork,

    /// <summary>The user stopped just this one row with its own stop button while the rest of the queue kept going. Terminal, not a failure; read like <see cref="HandedOff"/> and <see cref="Stuck"/>.</summary>
    Cancelled,
}
