using System.Windows.Forms;
using UltimateUpdater.Services.Install;

namespace UltimateUpdater.Tests;

/// <summary>Pure function, no live power state read: every reading is handed in directly.</summary>
public class PowerPreconditionTests
{
    [Fact]
    public void On_AC_power_never_warns_no_matter_how_low_the_battery_reads()
    {
        Assert.False(PowerPrecondition.ShouldWarn(PowerLineStatus.Online, 0.05f));
    }

    [Fact]
    public void Below_20_percent_on_battery_warns()
    {
        Assert.True(PowerPrecondition.ShouldWarn(PowerLineStatus.Offline, 0.19f));
    }

    [Fact]
    public void Above_20_percent_on_battery_does_not_warn()
    {
        Assert.False(PowerPrecondition.ShouldWarn(PowerLineStatus.Offline, 0.20f));
        Assert.False(PowerPrecondition.ShouldWarn(PowerLineStatus.Offline, 0.80f));
    }

    /// <summary>The sentinel for "not reported" is any value above 1.0 (full charge); never treated as a
    /// real, dangerously-low reading.</summary>
    [Fact]
    public void An_unreported_percent_never_warns()
    {
        Assert.False(PowerPrecondition.ShouldWarn(PowerLineStatus.Offline, 255f));
    }

    [Fact]
    public void An_unknown_line_status_never_warns()
    {
        Assert.False(PowerPrecondition.ShouldWarn(PowerLineStatus.Unknown, 0.05f));
    }
}
