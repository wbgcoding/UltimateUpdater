namespace UltimateUpdater.Services.Sources;

/// <summary>
/// A source that can finish a scan with a caveat that is not a failure (rows it had to leave out). Read
/// by the scan orchestrator once the scan ended and shown in the source's pill tooltip; the pill stays
/// Done.
/// </summary>
public interface IScanNoteSource
{
    /// <summary>Localized one-liner about the scan that just ended, or null when there is nothing to add.</summary>
    string? ScanNote { get; }
}
