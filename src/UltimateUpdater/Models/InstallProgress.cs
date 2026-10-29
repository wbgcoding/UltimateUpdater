namespace UltimateUpdater.Models;

public sealed class InstallProgress
{
    public double PercentComplete { get; init; }
    public string? StatusText { get; init; }

    /// <summary>
    /// The item installed but a reboot is needed to finish (winget passes the installer's 3010/1641
    /// through). The update IS applied; this is information for the user, not a failure.
    /// </summary>
    public bool RebootRequired { get; init; }

    /// <summary>
    /// Bytes, where the tool driving the install actually reports them. Left null otherwise, and the
    /// progress window then shows no throughput rather than a made-up figure.
    /// </summary>
    public long? BytesReceived { get; init; }

    public long? BytesTotal { get; init; }

    /// <summary>
    /// Download or install stage, where the tool's output says so. Unknown for a source that reports
    /// only a bare figure; the row then treats any measurable reading as a download.
    /// </summary>
    public InstallPhase Phase { get; init; }
}
