namespace UltimateUpdater.Services.Install;

/// <summary>
/// What <see cref="DiskSpaceCheck.Evaluate"/> found: how much a batch is likely to need, how much is
/// actually free, and whether that is short. <see cref="UnknownCount"/> is reported separately rather
/// than folded into <see cref="RequiredBytes"/>, since a package with no known size contributes nothing to
/// that number, not a zero.
/// </summary>
public sealed record DiskSpaceVerdict(long RequiredBytes, int UnknownCount, long FreeBytes, bool IsShort);

/// <summary>
/// Pre-install check: does the machine have room for the batch? An installer needs the download PLUS the
/// unpacked copy at the same time, so <see cref="DiskSpaceVerdict.RequiredBytes"/> doubles the known sizes
/// rather than reporting the raw download total. Checked against the smaller of the system drive and the
/// temp drive's free space, since either one running out stops an install (most installers land their
/// download in %TEMP% before unpacking to Program Files).
/// </summary>
public static class DiskSpaceCheck
{
    public static DiskSpaceVerdict Evaluate(IReadOnlyList<long?> sizes, long freeSystemBytes, long freeTempBytes)
    {
        // A size at or below zero is not a size (a negative one would cancel a real figure out), so it
        // counts as unknown like a missing one.
        var known = sizes.Where(size => size is > 0).Select(size => size!.Value).ToList();
        var unknownCount = sizes.Count - known.Count;
        var knownTotal = SaturatingSum(known);
        var requiredBytes = SaturatingAdd(knownTotal, knownTotal);
        var freeBytes = Math.Min(freeSystemBytes, freeTempBytes);

        // Never warn on a guess: with no known size at all there is nothing to compare against, so a
        // batch of packages that simply do not report a size must never be flagged as short.
        var isShort = known.Count > 0 && requiredBytes > freeBytes;

        return new DiskSpaceVerdict(requiredBytes, unknownCount, freeBytes, isShort);
    }

    /// <summary>Sizes come from third-party metadata, so the sum and the doubling cap at long.MaxValue
    /// instead of wrapping around to a small or negative number that would read as "fits".</summary>
    private static long SaturatingSum(IEnumerable<long> values)
    {
        var total = 0L;
        foreach (var value in values)
            total = SaturatingAdd(total, value);

        return total;
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}
