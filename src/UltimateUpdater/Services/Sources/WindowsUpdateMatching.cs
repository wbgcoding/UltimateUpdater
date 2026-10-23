namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Pulled out of WindowsUpdateSource.Install as a pure, testable seam: there is no WUA fake to run the
/// real COM code against, so the actual decision lives here instead. Windows Update re-evaluates
/// continuously, so a revision bump between the scan that offered a row and the install a moment later is
/// normal, not a sign the update is gone. Matching stays on the update's GUID alone; only when that GUID
/// itself is missing from a fresh search is the update genuinely gone from the offer.
/// </summary>
internal static class WindowsUpdateMatching
{
    /// <summary>
    /// Among a fresh search's results, picks the revision to install for the update identified by
    /// <paramref name="wantedId"/>: the highest revision number found under that same GUID, whatever
    /// revision the original scan happened to see. Returns null only when the GUID itself is not present
    /// at all in <paramref name="found"/> - the one case that means the update is gone from the offer.
    /// </summary>
    public static int? PickInstallTarget(string wantedId, IReadOnlyList<(string UpdateId, int Revision)> found)
    {
        var best = (int?)null;

        foreach (var candidate in found)
        {
            if (!string.Equals(candidate.UpdateId, wantedId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (best is null || candidate.Revision > best)
                best = candidate.Revision;
        }

        return best;
    }
}
