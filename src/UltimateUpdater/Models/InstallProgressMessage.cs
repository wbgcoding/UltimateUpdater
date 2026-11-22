namespace UltimateUpdater.Models;

/// <summary>One line of the worker→app named-pipe protocol (JSON per line).</summary>
public sealed class InstallProgressMessage
{
    public required string Type { get; set; }   // "progress" | "result" | "done" | "log" | "restorePointCreated"
    public string? SourceId { get; set; }
    public string? ItemId { get; set; }
    public double Percent { get; set; }
    public string? Status { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>Bytes, where the driving tool reports them, so the app can show live throughput.</summary>
    public long? BytesReceived { get; set; }

    public long? BytesTotal { get; set; }

    /// <summary>Raw, unlocalized diagnostic detail for a failed item; shown only on request.</summary>
    public string? Detail { get; set; }

    /// <summary>The item installed but needs a reboot to finish (winget's 3010/1641).</summary>
    public bool RebootRequired { get; set; }

    /// <summary>
    /// The two terminal outcomes that are neither success nor failure, so an elevated install reaches the row in the
    /// same shape as an in-process one. Both default to false, so a message from a copy that does not know them
    /// reads as a plain failure; see <see cref="Sources.ManualHandoffException"/> and
    /// <see cref="Sources.NotApplicableException"/>.
    /// </summary>
    public bool IsManualHandoff { get; set; }

    public bool IsNotApplicable { get; set; }

    /// <summary>The user stopped just this one item; the rest of the batch kept running. Same
    /// degrade-to-plain-failure default as the two above.</summary>
    public bool IsCancelled { get; set; }

    /// <summary>
    /// Set only on a <c>"restorePointCreated"</c> message: the worker created a restore point, so the app (which owns
    /// every file this program writes) can record it for its once-per-24-hours decision. Null on every other type.
    /// </summary>
    public DateTimeOffset? RestorePointCreatedAt { get; set; }

    /// <summary>
    /// Set on a successful "result" only, when the post-install re-check still found the version installed before the
    /// run. A quiet note, not a failure; a copy that does not know it reads the plain success.
    /// </summary>
    public bool VersionUnchanged { get; set; }

    /// <summary>
    /// Download or install stage. Missing in a message from a copy that does not know it, which reads as
    /// <see cref="InstallPhase.Unknown"/>.
    /// </summary>
    public InstallPhase Phase { get; set; }

    /// <summary>The exit code of the program a removal ran, on the "result" of an uninstall-program job; null
    /// on every other message (a copy that does not know it reads no code).</summary>
    public int? ExitCode { get; set; }
}
