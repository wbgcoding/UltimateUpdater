using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>The exit-code judgment behind pnputil calls; the process itself cannot run unelevated in a test.</summary>
public class DriverCleanupExecutorTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(3010, true)]
    [InlineData(1641, true)]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(259, false)]
    [InlineData(-1, false)]
    public void IsPnputilSuccess_treats_a_pending_restart_as_done(int exitCode, bool expected) =>
        Assert.Equal(expected, DriverCleanupExecutor.IsPnputilSuccess(exitCode));
}
