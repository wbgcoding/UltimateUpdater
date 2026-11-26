namespace UltimateUpdater.Services.App;

/// <summary>
/// The once-only startup hardware auto-tune for both parallelism sliders. Pure: the actual probing
/// (core count, RAM, battery presence) happens at the call site, this only turns the three raw numbers
/// into the two settings values, so it can be tested without touching real hardware.
/// </summary>
public static class ParallelismAutoTune
{
    public readonly record struct Result(int MaxParallelSources, int MaxParallelInstalls);

    /// <summary>The three named machine shapes, cheapest first. A shape that matches neither of the two
    /// higher rows falls through to this one, which is why it is also the safe default.</summary>
    private static readonly (int Sources, int Installs)[] Steps =
    [
        (4, 1),
        (8, 2),
        (12, 3),
    ];

    private const long GigaByte = 1024L * 1024 * 1024;

    /// <summary>
    /// The memory to judge the machine by. Windows shows a little less than is fitted (firmware and graphics
    /// reserve some), which would put every 8 GB and 16 GB machine one shape too low: the fitted amount wins
    /// when Windows knows it, otherwise the visible amount is rounded up to whole gigabytes. Zero stays zero.
    /// </summary>
    public static long RamForTuning(long installedKilobytes, long visibleBytes)
    {
        if (installedKilobytes > 0)
            return installedKilobytes * 1024;

        return visibleBytes <= 0 ? 0 : (visibleBytes + GigaByte - 1) / GigaByte * GigaByte;
    }

    public static Result Compute(int processorCount, long totalRamBytes, bool hasBattery)
    {
        var step = 0;
        if (processorCount > 8 && totalRamBytes >= 16 * GigaByte)
            step = 2;
        else if (processorCount >= 5 && totalRamBytes >= 8 * GigaByte)
            step = 1;

        // A battery-powered machine drops one step in both numbers; a machine already at the lowest
        // step has nowhere further down to go and stays there.
        if (hasBattery)
            step = Math.Max(0, step - 1);

        var (sources, installs) = Steps[step];
        return new Result(sources, installs);
    }
}
