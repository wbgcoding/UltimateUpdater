using UltimateUpdater.Models.Settings;

namespace UltimateUpdater.Tests;

/// <summary>
/// The pure hardware-to-defaults table the startup auto-tune reads from: no I/O, so every named machine
/// shape and the battery step-down can be proven without touching real hardware.
/// </summary>
public class ParallelismAutoTuneTests
{
    private const long GigaByte = 1024L * 1024 * 1024;

    [Fact]
    public void A_modest_machine_gets_the_lowest_shape()
    {
        var result = ParallelismAutoTune.Compute(processorCount: 4, totalRamBytes: 4 * GigaByte, hasBattery: false);

        Assert.Equal(4, result.MaxParallelSources);
        Assert.Equal(1, result.MaxParallelInstalls);
    }

    [Fact]
    public void A_mid_range_machine_gets_the_middle_shape()
    {
        var result = ParallelismAutoTune.Compute(processorCount: 6, totalRamBytes: 8 * GigaByte, hasBattery: false);

        Assert.Equal(8, result.MaxParallelSources);
        Assert.Equal(2, result.MaxParallelInstalls);
    }

    [Fact]
    public void A_high_end_machine_gets_the_top_shape()
    {
        var result = ParallelismAutoTune.Compute(processorCount: 16, totalRamBytes: 32 * GigaByte, hasBattery: false);

        Assert.Equal(12, result.MaxParallelSources);
        Assert.Equal(3, result.MaxParallelInstalls);
    }

    /// <summary>Plenty of cores but not enough RAM to earn the top shape falls back to the lowest one,
    /// the same safe default a genuinely low-end machine gets, rather than landing on the middle shape by
    /// accident.</summary>
    [Fact]
    public void Cores_without_enough_ram_falls_back_to_the_lowest_shape()
    {
        var result = ParallelismAutoTune.Compute(processorCount: 12, totalRamBytes: 4 * GigaByte, hasBattery: false);

        Assert.Equal(4, result.MaxParallelSources);
        Assert.Equal(1, result.MaxParallelInstalls);
    }

    [Theory]
    [InlineData(16, 32, 12, 3, 8, 2)]
    [InlineData(6, 8, 8, 2, 4, 1)]
    public void A_battery_drops_one_step_in_both_numbers(
        int cores, int ramGb, int sourcesWithoutBattery, int installsWithoutBattery,
        int sourcesWithBattery, int installsWithBattery)
    {
        var withoutBattery = ParallelismAutoTune.Compute(cores, ramGb * GigaByte, hasBattery: false);
        var withBattery = ParallelismAutoTune.Compute(cores, ramGb * GigaByte, hasBattery: true);

        Assert.Equal((sourcesWithoutBattery, installsWithoutBattery),
            (withoutBattery.MaxParallelSources, withoutBattery.MaxParallelInstalls));
        Assert.Equal((sourcesWithBattery, installsWithBattery),
            (withBattery.MaxParallelSources, withBattery.MaxParallelInstalls));
    }

    /// <summary>The lowest shape has nowhere further down to go: a battery does not push it below its
    /// own floor.</summary>
    [Fact]
    public void A_battery_on_the_lowest_shape_machine_stays_at_the_floor()
    {
        var result = ParallelismAutoTune.Compute(processorCount: 4, totalRamBytes: 4 * GigaByte, hasBattery: true);

        Assert.Equal(4, result.MaxParallelSources);
        Assert.Equal(1, result.MaxParallelInstalls);
    }
}
