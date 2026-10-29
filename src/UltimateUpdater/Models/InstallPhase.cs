namespace UltimateUpdater.Models;

/// <summary>
/// Which stage of an install a progress report belongs to, as far as the tool's own output shows it.
/// Unknown is the default and what an older worker copy sends, so the value is safe to leave out of the
/// pipe message: the row then falls back to reading any measurable figure as a download.
/// </summary>
public enum InstallPhase
{
    Unknown = 0,
    Downloading = 1,
    Installing = 2,
}
