namespace UltimateUpdater.Models;

/// <summary>One line of the worker→app named-pipe protocol (JSON per line).</summary>
public sealed class InstallProgressMessage
{
    public required string Type { get; set; }   // "progress" | "result" | "done" | "log"
    public string? SourceId { get; set; }
    public string? ItemId { get; set; }
    public double Percent { get; set; }
    public string? Status { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>Bytes, where the driving tool reports them, so the app can show live throughput.</summary>
    public long? BytesReceived { get; set; }

    public long? BytesTotal { get; set; }

    /// <summary>Raw, unlocalized diagnostic detail for a failed item; shown only on request (SECURITY.md).</summary>
    public string? Detail { get; set; }

    /// <summary>The item installed but needs a reboot to finish (winget's 3010/1641).</summary>
    public bool RebootRequired { get; set; }

    /// <summary>
    /// The two terminal outcomes that are neither success nor failure, carried across the pipe so an
    /// elevated install reaches the row in the same shape an in-process one does, without them every
    /// admin-side handoff arrives as a red failure. Both default to false, so a message from a copy
    /// that does not know them degrades to a plain failure result rather than throwing; see
    /// <see cref="Sources.ManualHandoffException"/> and <see cref="Sources.NotApplicableException"/>.
    /// </summary>
    public bool IsManualHandoff { get; set; }

    public bool IsNotApplicable { get; set; }

    /// <summary>The user stopped just this one item; the rest of the batch kept running.
    /// A third terminal non-failure, same degrade-to-plain-failure default as the two above.</summary>
    public bool IsCancelled { get; set; }

    /// <summary>
    /// Set only on a message with <c>Type == "restorePointCreated"</c>: the moment the elevated
    /// worker actually created one, so the app, which owns every file this program ever writes, can record
    /// it for its own at-most-once-per-24-hours decision on the NEXT batch. The worker decides nothing from
    /// this itself, it only reports the fact; null on every other message type.
    /// </summary>
    public DateTimeOffset? RestorePointCreatedAt { get; set; }

    /// <summary>
    /// Set on a successful "result" only, when the source's own post-install re-check found the
    /// SAME version that was installed before the run started. Not a failure, just a quiet note; a
    /// message from a copy that does not know it degrades to the plain success it already carries.
    /// </summary>
    public bool VersionUnchanged { get; set; }
}
